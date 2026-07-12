/**
 * Eidos Runtime - Work-Stealing Thread Pool Scheduler
 *
 * Implements a work-stealing scheduler with:
 *   - Per-worker local deques (owner: LIFO push/pop, stealers: FIFO steal)
 *   - Global FIFO queue for external submissions
 *   - Sleep/wake via condition variable when no work is found
 *
 * Include dependency chain: eidos_runtime.h -> eidos_sync.h -> this file
 */

#include "eidos_sync.h"
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <time.h>

#if EIDOS_POSIX
#include <unistd.h>
#include <sched.h>
#endif

/* ============================================================
 * Atomics (platform-abstracted, consistent with eidos_memory.c)
 * ============================================================ */

#if defined(_WIN32)
#include <intrin.h>
#define EIDOS_ATOMIC_LOAD32(ptr)        (*(volatile int32_t*)(ptr))
#define EIDOS_ATOMIC_STORE32(ptr, val)  (*(volatile int32_t*)(ptr) = (int32_t)(val))
#define EIDOS_ATOMIC_CAS32(ptr, exp, des) \
    (_InterlockedCompareExchange((LONG volatile*)(ptr), (LONG)(des), (LONG)(exp)) == (LONG)(exp))
#define EIDOS_ATOMIC_INC32(ptr)         InterlockedIncrement((LONG volatile*)(ptr))
#define EIDOS_ATOMIC_DEC32(ptr)         InterlockedDecrement((LONG volatile*)(ptr))
#else
#define EIDOS_ATOMIC_LOAD32(ptr)        __atomic_load_n((ptr), __ATOMIC_ACQUIRE)
#define EIDOS_ATOMIC_STORE32(ptr, val)  __atomic_store_n((ptr), (val), __ATOMIC_RELEASE)
#define EIDOS_ATOMIC_CAS32(ptr, exp, des) \
    __atomic_compare_exchange_n((ptr), &(exp), (des), 0, \
                                __ATOMIC_SEQ_CST, __ATOMIC_SEQ_CST)
#define EIDOS_ATOMIC_INC32(ptr)         __atomic_add_fetch((ptr), 1, __ATOMIC_SEQ_CST)
#define EIDOS_ATOMIC_DEC32(ptr)         __atomic_sub_fetch((ptr), 1, __ATOMIC_SEQ_CST)
#endif

/* ============================================================
 * Constants
 * ============================================================ */

#define EIDOS_LOCAL_QUEUE_CAP  256
#define EIDOS_MAX_WORKERS      64
#define EIDOS_GLOBAL_INIT_CAP  1024
#define EIDOS_SLEEP_TIMEOUT_MS 100

/* ============================================================
 * Internal Structures
 * ============================================================ */

/**
 * Per-worker Chase-Lev-style deque.
 * Owner pushes/pops from tail (LIFO). Stealers pop from head (FIFO).
 * steal_lock serialises concurrent stealers.
 */
typedef struct EidosLocalQueue {
    EidosWorkItem    items[EIDOS_LOCAL_QUEUE_CAP];
    volatile int32_t head;                /* consumer index (stealers) */
    volatile int32_t tail;                /* producer index (owner)    */
    EidosNativeLock  steal_lock;          /* serialises stealers       */
} EidosLocalQueue;

/** Global FIFO queue with lock-based enqueue/dequeue. */
typedef struct EidosGlobalQueue {
    EidosWorkItem*   items;
    int32_t          capacity;
    volatile int32_t head;
    volatile int32_t tail;
    volatile int32_t count;
    EidosNativeLock  lock;
    EidosNativeCond  not_empty;
} EidosGlobalQueue;

/** Worker thread context. */
typedef struct EidosWorker {
    EidosLocalQueue  local;
    uint32_t         index;
    EidosThread      thread;
    volatile int32_t sleeping;
} EidosWorker;

/** The scheduler singleton. */
typedef struct EidosScheduler {
    EidosWorker*     workers;
    uint32_t         worker_count;
    EidosGlobalQueue global;
    volatile int32_t running;
} EidosScheduler;

/* ============================================================
 * Module State
 * ============================================================ */

static EidosScheduler g_scheduler;
static int32_t        g_scheduler_initialized = 0;

#define EIDOS_SCHED_UNINIT       0
#define EIDOS_SCHED_INITIALIZING 1
#define EIDOS_SCHED_READY        2

/** Thread-local worker index. UINT32_MAX when not a worker thread. */
static _Thread_local uint32_t g_worker_index = UINT32_MAX;

static void scheduler_yield(void)
{
#if EIDOS_WIN
    Sleep(0);
#else
    sched_yield();
#endif
}

/* ============================================================
 * Local Queue Operations
 * ============================================================ */

static void local_queue_init(EidosLocalQueue* q)
{
    memset(q->items, 0, sizeof(q->items));
    q->head = 0;
    q->tail = 0;
    eidos_lock_init(&q->steal_lock);
}

/**
 * Owner pushes to tail. Returns true on success, false if full.
 * Only the owner thread may call this.
 */
static int local_push(EidosLocalQueue* q, EidosWorkItem item)
{
    int32_t t = EIDOS_ATOMIC_LOAD32(&q->tail);
    int32_t h = EIDOS_ATOMIC_LOAD32(&q->head);

    if (t - h >= EIDOS_LOCAL_QUEUE_CAP) {
        return 0;  /* full */
    }

    q->items[t % EIDOS_LOCAL_QUEUE_CAP] = item;
    /* Store-release so stealers see the item after seeing the new tail. */
    EIDOS_ATOMIC_STORE32(&q->tail, t + 1);
    return 1;
}

/**
 * Owner pops from tail (LIFO). Returns true on success, false if empty.
 * Only the owner thread may call this.
 */
static int local_pop(EidosLocalQueue* q, EidosWorkItem* out)
{
    int32_t t = EIDOS_ATOMIC_LOAD32(&q->tail);

    if (t == 0) {
        return 0;
    }

    /* Optimistically decrement tail. */
    int32_t new_t = t - 1;
    EIDOS_ATOMIC_STORE32(&q->tail, new_t);

    /* Re-read head to check we did not race with a steal. */
    int32_t h = EIDOS_ATOMIC_LOAD32(&q->head);

    if (h <= new_t) {
        /* We own the slot. */
        *out = q->items[new_t % EIDOS_LOCAL_QUEUE_CAP];
        if (h == new_t) {
            /* Last item -- must sync with potential stealers. */
            EIDOS_ATOMIC_STORE32(&q->tail, t);
            int32_t h2 = h + 1;
            if (!EIDOS_ATOMIC_CAS32(&q->head, h, h2)) {
                /* A stealer took it; restore tail. */
                EIDOS_ATOMIC_STORE32(&q->tail, t);
                return 0;
            }
            EIDOS_ATOMIC_STORE32(&q->tail, t);
        }
        return 1;
    }

    /* We decremented below head -- queue was empty or stolen. */
    EIDOS_ATOMIC_STORE32(&q->tail, t);
    return 0;
}

/**
 * Stealer pops from head (FIFO). Returns true on success, false if empty.
 * Multiple stealers are serialised by steal_lock.
 */
static int local_steal(EidosLocalQueue* q, EidosWorkItem* out)
{
    eidos_lock_acquire(&q->steal_lock);

    int32_t h = EIDOS_ATOMIC_LOAD32(&q->head);
    int32_t t = EIDOS_ATOMIC_LOAD32(&q->tail);

    if (h >= t) {
        eidos_lock_release(&q->steal_lock);
        return 0;  /* empty */
    }

    *out = q->items[h % EIDOS_LOCAL_QUEUE_CAP];
    int32_t h1 = h + 1;
    EIDOS_ATOMIC_STORE32(&q->head, h1);

    eidos_lock_release(&q->steal_lock);
    return 1;
}

/* ============================================================
 * Global Queue Operations
 * ============================================================ */

static void global_queue_init(EidosGlobalQueue* q)
{
    q->capacity = EIDOS_GLOBAL_INIT_CAP;
    q->items    = (EidosWorkItem*)malloc((size_t)q->capacity * sizeof(EidosWorkItem));
    q->head     = 0;
    q->tail     = 0;
    q->count    = 0;
    eidos_lock_init(&q->lock);
    eidos_cond_init(&q->not_empty);
}

static void global_queue_destroy(EidosGlobalQueue* q)
{
    free(q->items);
    q->items    = NULL;
    q->capacity = 0;
}

/**
 * Enqueue to global queue (lock-protected).
 * Grows the backing array when full.
 */
static void global_push(EidosGlobalQueue* q, EidosWorkItem item)
{
    eidos_lock_acquire(&q->lock);

    int32_t c = EIDOS_ATOMIC_LOAD32(&q->count);

    if (c >= q->capacity) {
        /* Grow: double the ring buffer. */
        int32_t new_cap = q->capacity * 2;
        EidosWorkItem* new_buf = (EidosWorkItem*)realloc(q->items,
                                    (size_t)new_cap * sizeof(EidosWorkItem));
        if (!new_buf) {
            eidos_lock_release(&q->lock);
            fprintf(stderr, "eidos_scheduler: global_push out of memory\n");
            return;
        }
        q->items    = new_buf;
        q->capacity = new_cap;
    }

    int32_t t = q->tail;
    q->items[t % q->capacity] = item;
    q->tail = t + 1;
    EIDOS_ATOMIC_STORE32(&q->count, c + 1);

    eidos_cond_signal(&q->not_empty);
    eidos_lock_release(&q->lock);
}

/**
 * Dequeue from global queue (lock-protected). Returns true if an item was obtained.
 */
static int global_pop(EidosGlobalQueue* q, EidosWorkItem* out)
{
    eidos_lock_acquire(&q->lock);

    int32_t c = EIDOS_ATOMIC_LOAD32(&q->count);
    if (c <= 0) {
        eidos_lock_release(&q->lock);
        return 0;
    }

    int32_t h = q->head;
    *out = q->items[h % q->capacity];
    q->head = h + 1;
    EIDOS_ATOMIC_STORE32(&q->count, c - 1);

    eidos_lock_release(&q->lock);
    return 1;
}

/* ============================================================
 * Worker Sleep / Wake
 * ============================================================ */

static void sleep_worker(EidosWorker* worker)
{
    EidosGlobalQueue* gq = &g_scheduler.global;
    EIDOS_ATOMIC_STORE32(&worker->sleeping, 1);

    eidos_lock_acquire(&gq->lock);
    /* Double-check: a schedule call may have pushed after our last check. */
    if (EIDOS_ATOMIC_LOAD32(&gq->count) > 0) {
        eidos_lock_release(&gq->lock);
        EIDOS_ATOMIC_STORE32(&worker->sleeping, 0);
        return;
    }
#if EIDOS_WIN
    SleepConditionVariableSRW(&gq->not_empty, &gq->lock, EIDOS_SLEEP_TIMEOUT_MS, 0);
#else
    {
        struct timespec ts;
        clock_gettime(CLOCK_REALTIME, &ts);
        ts.tv_nsec += (long)EIDOS_SLEEP_TIMEOUT_MS * 1000000L;
        if (ts.tv_nsec >= 1000000000L) {
            ts.tv_sec  += 1;
            ts.tv_nsec -= 1000000000L;
        }
        pthread_cond_timedwait(&gq->not_empty, &gq->lock, &ts);
    }
#endif
    eidos_lock_release(&gq->lock);
    EIDOS_ATOMIC_STORE32(&worker->sleeping, 0);
}

/** Wake one sleeping worker (called after pushing work). */
static void wake_one_worker(void)
{
    uint32_t wc = g_scheduler.worker_count;
    for (uint32_t i = 0; i < wc; i++) {
        if (EIDOS_ATOMIC_LOAD32(&g_scheduler.workers[i].sleeping)) {
            /* Signal the condvar. The sleeping worker will see it. */
            eidos_cond_signal(&g_scheduler.global.not_empty);
            return;
        }
    }
}

/* ============================================================
 * Work Execution
 * ============================================================ */

static void execute_item(EidosWorkItem item)
{
    if (item.invoke_fn) {
        item.invoke_fn(item.closure_ptr, item.arg);
    }
}

/* ============================================================
 * Steal Attempt
 * ============================================================ */

static int try_steal(EidosScheduler* sched, uint32_t self, EidosWorkItem* out)
{
    if (sched->worker_count <= 1) {
        return 0;
    }

    /* Simple random victim selection: start from (self+1) and scan. */
    for (uint32_t i = 1; i < sched->worker_count; i++) {
        uint32_t victim = (self + i) % sched->worker_count;
        if (local_steal(&sched->workers[victim].local, out)) {
            return 1;
        }
    }
    return 0;
}

/* ============================================================
 * Worker Thread Entry
 * ============================================================ */

static void* worker_main(void* arg)
{
    EidosWorker* worker = (EidosWorker*)arg;
    g_worker_index = worker->index;

    EidosWorkItem item;

    while (EIDOS_ATOMIC_LOAD32(&g_scheduler.running)) {
        /* 1. Try local queue (LIFO pop -- cache-hot). */
        if (local_pop(&worker->local, &item)) {
            execute_item(item);
            continue;
        }

        /* 2. Try global queue (FIFO pop). */
        if (global_pop(&g_scheduler.global, &item)) {
            execute_item(item);
            continue;
        }

        /* 3. Try steal from another worker (FIFO steal). */
        if (try_steal(&g_scheduler, worker->index, &item)) {
            execute_item(item);
            continue;
        }

        /* 4. No work found -- sleep on global condvar. */
        sleep_worker(worker);
    }

    /* Drain remaining work in local queue after shutdown signal. */
    while (local_pop(&worker->local, &item)) {
        execute_item(item);
    }

    g_worker_index = UINT32_MAX;
    return NULL;
}

/* ============================================================
 * Public API
 * ============================================================ */

/**
 * Initialise the work-stealing scheduler.
 * @param worker_count Number of worker threads. 0 = hardware concurrency.
 */
void eidos_scheduler_init(uint32_t worker_count)
{
    int32_t expected = EIDOS_SCHED_UNINIT;
    if (!EIDOS_ATOMIC_CAS32(&g_scheduler_initialized, expected, EIDOS_SCHED_INITIALIZING)) {
        while (EIDOS_ATOMIC_LOAD32(&g_scheduler_initialized) == EIDOS_SCHED_INITIALIZING) {
            scheduler_yield();
        }
        return;  /* already initialised */
    }

    if (worker_count == 0) {
#if EIDOS_WIN
        SYSTEM_INFO si;
        GetSystemInfo(&si);
        worker_count = (uint32_t)si.dwNumberOfProcessors;
#else
        long n = sysconf(_SC_NPROCESSORS_ONLN);
        worker_count = (n > 0) ? (uint32_t)n : 4;
#endif
    }

    if (worker_count > EIDOS_MAX_WORKERS) {
        worker_count = EIDOS_MAX_WORKERS;
    }
    if (worker_count == 0) {
        worker_count = 1;
    }

    /* Initialise global queue. */
    global_queue_init(&g_scheduler.global);

    /* Allocate worker array. */
    g_scheduler.workers = (EidosWorker*)calloc(worker_count, sizeof(EidosWorker));
    if (!g_scheduler.workers) {
        fprintf(stderr, "eidos_scheduler: failed to allocate workers\n");
        global_queue_destroy(&g_scheduler.global);
        EIDOS_ATOMIC_STORE32(&g_scheduler_initialized, EIDOS_SCHED_UNINIT);
        return;
    }
    g_scheduler.worker_count = worker_count;

    /* Initialise each worker. */
    for (uint32_t i = 0; i < worker_count; i++) {
        g_scheduler.workers[i].index   = i;
        g_scheduler.workers[i].sleeping = 0;
        local_queue_init(&g_scheduler.workers[i].local);
    }

    /* Mark running before spawning threads. */
    EIDOS_ATOMIC_STORE32(&g_scheduler.running, 1);

    /* Spawn worker threads. */
    for (uint32_t i = 0; i < worker_count; i++) {
        int rc = eidos_thread_create(&g_scheduler.workers[i].thread,
                                     worker_main,
                                     &g_scheduler.workers[i]);
        if (rc != 0) {
            fprintf(stderr, "eidos_scheduler: failed to create worker %u (rc=%d)\n", i, rc);
        }
    }

    EIDOS_ATOMIC_STORE32(&g_scheduler_initialized, EIDOS_SCHED_READY);
}

/**
 * Shut down the scheduler.
 * Sets running=0, wakes all sleeping workers, joins threads, frees resources.
 */
void eidos_scheduler_shutdown(void)
{
    if (EIDOS_ATOMIC_LOAD32(&g_scheduler_initialized) != EIDOS_SCHED_READY) {
        return;
    }

    /* Signal shutdown. */
    EIDOS_ATOMIC_STORE32(&g_scheduler.running, 0);

    /* Wake all sleeping workers so they exit their loop. */
    eidos_cond_broadcast(&g_scheduler.global.not_empty);

    /* Join all worker threads. */
    for (uint32_t i = 0; i < g_scheduler.worker_count; i++) {
        eidos_thread_join(g_scheduler.workers[i].thread);
    }

    /* Drain remaining global work. */
    EidosWorkItem item;
    while (global_pop(&g_scheduler.global, &item)) {
        execute_item(item);
    }

    /* Free resources. */
    free(g_scheduler.workers);
    g_scheduler.workers = NULL;
    global_queue_destroy(&g_scheduler.global);
    g_scheduler.worker_count = 0;

    EIDOS_ATOMIC_STORE32(&g_scheduler_initialized, EIDOS_SCHED_UNINIT);
}

/**
 * Post a work item. Prefers the current worker's local queue, then the global queue.
 * Wakes a sleeping worker when work is submitted.
 */
void eidos_schedule(EidosWorkItem item)
{
    /* Prefer local queue if we are on a worker thread. */
    uint32_t idx = g_worker_index;
    if (idx != UINT32_MAX && idx < g_scheduler.worker_count) {
        if (local_push(&g_scheduler.workers[idx].local, item)) {
            wake_one_worker();
            return;
        }
        /* Local queue full -- fall through to global. */
    }

    global_push(&g_scheduler.global, item);
    wake_one_worker();
}

/**
 * Return the current thread's worker index.
 * Returns UINT32_MAX when called from a non-worker thread.
 */
uint32_t eidos_worker_index(void)
{
    return g_worker_index;
}
