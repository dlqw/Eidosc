/**
 * Eidos Runtime - Mutex, RwLock, and Barrier Synchronization Primitives
 *
 * Implements task-aware synchronization primitives that never block OS threads.
 * Instead, tasks register continuations (EidosWorkItems) and yield; when the
 * synchronization condition is met, the continuation is scheduled back onto
 * the work-stealing thread pool.
 *
 * Primitives:
 *   - Mutex:  Mutual exclusion lock with FIFO waiter queue
 *   - RwLock: Reader-writer lock allowing concurrent readers or exclusive writer
 *   - Barrier: N-party synchronization point (all must arrive before any proceed)
 *
 * All primitives are shared objects (SHARED bit set) and use atomic operations
 * for thread-safe state transitions.
 *
 * Include dependency chain: eidos_runtime.h -> eidos_sync.h -> eidos_waitqueue.h
 */

#include "eidos_waitqueue.h"
#include <stdlib.h>
#include <string.h>
#include <stdio.h>

/* ============================================================
 * Atomics (same macros as eidos_task.c / eidos_scheduler.c)
 * ============================================================ */

#if defined(_WIN32)
#include <intrin.h>
#define ESYNC_ATOMIC_LOAD32(ptr)        (*(volatile int32_t*)(ptr))
#define ESYNC_ATOMIC_STORE32(ptr, val)  (*(volatile int32_t*)(ptr) = (int32_t)(val))
#define ESYNC_ATOMIC_CAS32(ptr, exp, des) \
    (_InterlockedCompareExchange((LONG volatile*)(ptr), (LONG)(des), (LONG)(exp)) == (LONG)(exp))
#define ESYNC_ATOMIC_INC32(ptr)         InterlockedIncrement((LONG volatile*)(ptr))
#define ESYNC_ATOMIC_DEC32(ptr)         InterlockedDecrement((LONG volatile*)(ptr))
#else
#define ESYNC_ATOMIC_LOAD32(ptr)        __atomic_load_n((ptr), __ATOMIC_ACQUIRE)
#define ESYNC_ATOMIC_STORE32(ptr, val)  __atomic_store_n((ptr), (val), __ATOMIC_RELEASE)
#define ESYNC_ATOMIC_CAS32(ptr, exp, des) \
    __atomic_compare_exchange_n((ptr), &(exp), (des), 0, \
                                __ATOMIC_SEQ_CST, __ATOMIC_SEQ_CST)
#define ESYNC_ATOMIC_INC32(ptr)         __atomic_add_fetch((ptr), 1, __ATOMIC_SEQ_CST)
#define ESYNC_ATOMIC_DEC32(ptr)         __atomic_sub_fetch((ptr), 1, __ATOMIC_SEQ_CST)
#endif

/* ============================================================
 * Structures
 * ============================================================ */

/**
 * Mutex: mutual exclusion lock.
 *
 * locked == 0 means unlocked; locked == 1 means a task holds the lock.
 * When lock acquisition fails, the task's continuation is enqueued in
 * waiters and will be scheduled when the holder releases.
 *
 * inner holds the protected data as a shared pointer.
 */
typedef struct EidosMutex {
    EidosHeader       header;
    void*             inner;       /* protected data (shared ptr) */
    volatile int32_t  locked;      /* 0 = unlocked, 1 = locked */
    EidosWaitQueue    waiters;     /* tasks waiting for lock */
} EidosMutex;

/**
 * RwLock: reader-writer lock.
 *
 * Allows concurrent readers OR a single exclusive writer.
 *
 * State transitions:
 *   - read:  if no writer active, increment reader_count and proceed
 *   - write: if no readers and no writer, set writer_active and proceed
 *
 * Read release: decrement reader_count; if 0, wake one writer.
 * Write release: clear writer_active; wake all readers, then one writer.
 */
typedef struct EidosRwLock {
    EidosHeader       header;
    void*             inner;
    volatile int32_t  reader_count;  /* number of active readers */
    volatile int32_t  writer_active; /* 1 if a writer holds the lock */
    EidosWaitQueue    read_waiters;
    EidosWaitQueue    write_waiters;
} EidosRwLock;

/**
 * Barrier: N-party synchronization point.
 *
 * All participants must call eidos_barrier_wait(). The first (capacity-1)
 * tasks are suspended in the waiters queue. The final task wakes all
 * waiters and all participants' continuations are scheduled.
 *
 * generation is incremented on each cycle, allowing the barrier to be
 * reused. arrived is reset to 0 when the barrier trips.
 */
typedef struct EidosBarrier {
    EidosHeader       header;
    uint32_t          capacity;      /* number of participants */
    volatile uint32_t arrived;       /* how many have arrived */
    volatile uint32_t generation;    /* incremented on each cycle */
    EidosWaitQueue    waiters;
} EidosBarrier;

/* ============================================================
 * Module State & Destructor Registration
 * ============================================================ */

/** Flag to track whether sync destructors have been registered. */
static int32_t g_sync_destructors_registered = 0;

/** Flag to track whether sync wait APIs initialized the scheduler. */
static int32_t g_sync_scheduler_initialized = 0;

/** Flag to track whether sync scheduler shutdown has been registered. */
static int32_t g_sync_scheduler_atexit_registered = 0;

/* Forward declarations for destructors */
static void eidos_mutex_destructor(void* ptr);
static void eidos_rwlock_destructor(void* ptr);
static void eidos_barrier_destructor(void* ptr);
static bool eidos_barrier_arrive_and_check_trip(EidosBarrier* barrier);
static void eidos_waitqueue_schedule_list(EidosWaiter* waiters);

static void ensure_sync_scheduler(void) {
    int32_t expected = 0;
    if (ESYNC_ATOMIC_CAS32(&g_sync_scheduler_initialized, expected, 1)) {
        expected = 0;
        if (ESYNC_ATOMIC_CAS32(&g_sync_scheduler_atexit_registered, expected, 1)) {
            atexit(eidos_scheduler_shutdown);
        }

        eidos_scheduler_init(0);
    }
}

/**
 * Register destructors for Mutex, RwLock, and Barrier type IDs.
 * Idempotent: safe to call multiple times.
 */
static void ensure_sync_destructors(void) {
    if (!ESYNC_ATOMIC_LOAD32(&g_sync_destructors_registered)) {
        eidos_register_destructor(EIDOS_TYPE_MUTEX, eidos_mutex_destructor);
        eidos_register_destructor(EIDOS_TYPE_RWLOCK, eidos_rwlock_destructor);
        eidos_register_destructor(EIDOS_TYPE_BARRIER, eidos_barrier_destructor);
        ESYNC_ATOMIC_STORE32(&g_sync_destructors_registered, 1);
    }
}

/* ============================================================
 * Mutex - Implementation
 * ============================================================ */

/**
 * Create a new Mutex protecting the given value.
 *
 * Allocates a shared Mutex object. The protected value is shared
 * (eidos_share) and its reference count incremented so the Mutex
 * owns a reference. Callers should decref their original reference
 * after construction.
 *
 * @param value  The protected data (shared ptr, may be NULL)
 * @return Pointer to the new EidosMutex (shared, ref_count=1)
 */
EidosMutex* eidos_mutex_new(void* value) {
    ensure_sync_destructors();

    EidosMutex* mutex = (EidosMutex*)eidos_alloc(sizeof(EidosMutex), EIDOS_TYPE_MUTEX);
    if (!mutex) {
        fprintf(stderr, "eidos_mutex_new: out of memory\n");
        return NULL;
    }

    /* Initialize the protected value. Share + incref so the mutex owns a reference. */
    if (value) {
        eidos_share(value);
        eidos_incref_shared(value);
    }
    mutex->inner = value;
    mutex->locked = 0;
    eidos_waitqueue_init(&mutex->waiters);

    /* Mark as shared so atomic incref/decref is used. */
    eidos_share(mutex);

    return mutex;
}

/**
 * Acquire the Mutex, registering a continuation for when the lock is held.
 *
 * Attempts an atomic CAS from 0 (unlocked) to 1 (locked). If successful,
 * the continuation is scheduled immediately with the mutex as its argument.
 * If the lock is already held, the continuation is enqueued in the waiters
 * queue and will be scheduled when the current holder releases.
 *
 * The continuation receives the EidosMutex pointer as its argument.
 * When done with the protected data, the codegen must call
 * eidos_mutex_guard_release() to release the lock.
 *
 * @param mutex        The mutex to acquire (must not be NULL)
 * @param continuation Work item to schedule when the lock is acquired
 */
void eidos_mutex_lock(EidosMutex* mutex, EidosWorkItem continuation) {
    if (!mutex) return;

    /* Try to acquire the lock via CAS. */
    int32_t expected = 0;
    if (ESYNC_ATOMIC_CAS32(&mutex->locked, expected, 1)) {
        /* Fast path: lock acquired. Schedule continuation with mutex as arg. */
        EidosWorkItem ready;
        ready.invoke_fn   = continuation.invoke_fn;
        ready.closure_ptr = continuation.closure_ptr;
        ready.arg         = mutex;
        eidos_schedule(ready);
    } else {
        /*
         * Slow path: lock is held.  We must hold the waitqueue spinlock
         * while pushing, because eidos_mutex_guard_release checks the
         * waiters list and conditionally unlocks under the same lock.
         * Without this serialization, release can observe an empty
         * waiters list, unlock, and then our push arrives too late —
         * a lost wakeup deadlock.
         *
         * After acquiring the spinlock we retry the CAS: the mutex may
         * have been released while we were waiting for the spinlock.
         */
        eidos_lock_acquire(&mutex->waiters.lock);
        expected = 0;
        if (ESYNC_ATOMIC_CAS32(&mutex->locked, expected, 1)) {
            eidos_lock_release(&mutex->waiters.lock);
            EidosWorkItem ready;
            ready.invoke_fn   = continuation.invoke_fn;
            ready.closure_ptr = continuation.closure_ptr;
            ready.arg         = mutex;
            eidos_schedule(ready);
        } else {
            /* Still locked — enqueue while holding the spinlock. */
            EidosWaiter* w = (EidosWaiter*)malloc(sizeof(EidosWaiter));
            w->work = continuation;
            w->next = NULL;
            if (mutex->waiters.tail) {
                mutex->waiters.tail->next = w;
            } else {
                mutex->waiters.head = w;
            }
            mutex->waiters.tail = w;
            eidos_lock_release(&mutex->waiters.lock);
        }
    }
}

bool eidos_mutex_try_lock(EidosMutex* mutex) {
    if (!mutex) return false;

    int32_t expected = 0;
    return ESYNC_ATOMIC_CAS32(&mutex->locked, expected, 1);
}

void* eidos_mutex_get_inner(EidosMutex* mutex) {
    return mutex ? mutex->inner : NULL;
}

void eidos_mutex_replace_inner(EidosMutex* mutex, void* value) {
    if (!mutex) return;

    if (value) {
        eidos_share(value);
        eidos_incref_shared(value);
    }

    void* old_value = mutex->inner;
    mutex->inner = value;

    if (old_value) {
        eidos_decref_shared(old_value);
    }
}

/**
 * Release the Mutex (called by the affine drop of the "guard").
 *
 * Sets locked to 0 and wakes one waiting task. If no tasks are waiting,
 * the lock simply becomes available for the next acquirer.
 *
 * @param mutex The mutex to release (must not be NULL)
 */
void eidos_mutex_guard_release(EidosMutex* mutex) {
    if (!mutex) return;

    /*
     * Hold the waitqueue spinlock for the entire release to close the
     * TOCTOU window: if we check waiters (empty) and then unlock, a
     * concurrent eidos_mutex_lock slow-path can push between those two
     * steps, creating a lost wakeup.  By holding the spinlock, the
     * slow-path either pushes before our check (we'll see it) or
     * retries CAS after we unlock (it'll succeed).
     */
    eidos_lock_acquire(&mutex->waiters.lock);
    EidosWaiter* w = mutex->waiters.head;
    if (w) {
        /* Transfer ownership directly to the waiter. */
        mutex->waiters.head = w->next;
        if (!mutex->waiters.head) {
            mutex->waiters.tail = NULL;
        }
        eidos_lock_release(&mutex->waiters.lock);
        EidosWorkItem ready;
        ready.invoke_fn = w->work.invoke_fn;
        ready.closure_ptr = w->work.closure_ptr;
        ready.arg = mutex;
        eidos_schedule(ready);
        free(w);
        /* locked stays 1 — ownership transferred. */
    } else {
        /* No waiter — unlock while holding the spinlock. */
        ESYNC_ATOMIC_STORE32(&mutex->locked, 0);
        eidos_lock_release(&mutex->waiters.lock);
    }
}

void eidos_mutex_unlock(EidosMutex* mutex) {
    eidos_mutex_guard_release(mutex);
}

/**
 * Destructor for EidosMutex (registered for EIDOS_TYPE_MUTEX).
 *
 * Releases the shared reference to the protected inner value,
 * then destroys the waiters queue (freeing any remaining nodes).
 *
 * @param ptr Pointer to the EidosMutex (after header)
 */
static void eidos_mutex_destructor(void* ptr) {
    EidosMutex* mutex = (EidosMutex*)ptr;
    if (!mutex) return;

    if (mutex->inner) {
        eidos_decref_shared(mutex->inner);
        mutex->inner = NULL;
    }

    eidos_waitqueue_destroy(&mutex->waiters);
}

/* ============================================================
 * RwLock - Implementation
 * ============================================================ */

/**
 * Create a new RwLock protecting the given value.
 *
 * Allocates a shared RwLock object. The protected value is shared
 * and its reference count incremented so the RwLock owns a reference.
 *
 * @param value  The protected data (shared ptr, may be NULL)
 * @return Pointer to the new EidosRwLock (shared, ref_count=1)
 */
EidosRwLock* eidos_rwlock_new(void* value) {
    ensure_sync_destructors();

    EidosRwLock* rwlock = (EidosRwLock*)eidos_alloc(sizeof(EidosRwLock), EIDOS_TYPE_RWLOCK);
    if (!rwlock) {
        fprintf(stderr, "eidos_rwlock_new: out of memory\n");
        return NULL;
    }

    /* Initialize the protected value. Share + incref so the rwlock owns a reference. */
    if (value) {
        eidos_share(value);
        eidos_incref_shared(value);
    }
    rwlock->inner = value;
    rwlock->reader_count = 0;
    rwlock->writer_active = 0;
    eidos_waitqueue_init(&rwlock->read_waiters);
    eidos_waitqueue_init(&rwlock->write_waiters);

    /* Mark as shared so atomic incref/decref is used. */
    eidos_share(rwlock);

    return rwlock;
}

/**
 * Acquire a read lock, registering a continuation for when it is held.
 *
 * If no writer is active (writer_active == 0), atomically increments
 * reader_count and schedules the continuation immediately.
 * Otherwise, enqueues the continuation in the read waiters queue.
 *
 * Multiple readers can hold the lock simultaneously as long as no
 * writer is active.
 *
 * The continuation receives the EidosRwLock pointer as its argument.
 * When done, call eidos_rwlock_read_release().
 *
 * @param rwlock       The RwLock to read-lock (must not be NULL)
 * @param continuation Work item to schedule when the read lock is acquired
 */
void eidos_rwlock_read(EidosRwLock* rwlock, EidosWorkItem continuation) {
    if (!rwlock) return;

    /*
     * Check if a writer is active. If not, we can enter a read section
     * by atomically incrementing reader_count.
     *
     * We use a CAS loop to ensure atomicity of the check-and-increment:
     * another writer could start between our check and the increment.
     */
    for (;;) {
        if (ESYNC_ATOMIC_LOAD32(&rwlock->writer_active) != 0) {
            /* Writer is active: must wait. */
            eidos_waitqueue_push(&rwlock->read_waiters, continuation);
            return;
        }

        /* Attempt to increment reader_count atomically. */
        int32_t cur_readers = ESYNC_ATOMIC_LOAD32(&rwlock->reader_count);
        if (ESYNC_ATOMIC_CAS32(&rwlock->reader_count, cur_readers, cur_readers + 1)) {
            /* Re-check that no writer became active during our CAS. */
            if (ESYNC_ATOMIC_LOAD32(&rwlock->writer_active) == 0) {
                /* Success: schedule continuation with rwlock as arg. */
                EidosWorkItem ready;
                ready.invoke_fn   = continuation.invoke_fn;
                ready.closure_ptr = continuation.closure_ptr;
                ready.arg         = rwlock;
                eidos_schedule(ready);
                return;
            }
            /*
             * A writer became active between our CAS and the re-check.
             * Undo the reader_count increment and fall through to wait.
             * This is safe because the writer will see the decremented
             * count when it checks on release.
             */
            ESYNC_ATOMIC_DEC32(&rwlock->reader_count);
            eidos_waitqueue_push(&rwlock->read_waiters, continuation);
            return;
        }
        /* CAS failed (concurrent reader changed reader_count). Retry. */
    }
}

bool eidos_rwlock_try_read(EidosRwLock* rwlock) {
    if (!rwlock) return false;

    for (;;) {
        if (ESYNC_ATOMIC_LOAD32(&rwlock->writer_active) != 0) {
            return false;
        }

        int32_t cur_readers = ESYNC_ATOMIC_LOAD32(&rwlock->reader_count);
        if (ESYNC_ATOMIC_CAS32(&rwlock->reader_count, cur_readers, cur_readers + 1)) {
            if (ESYNC_ATOMIC_LOAD32(&rwlock->writer_active) == 0) {
                return true;
            }

            ESYNC_ATOMIC_DEC32(&rwlock->reader_count);
            return false;
        }
    }
}

/**
 * Acquire a write lock, registering a continuation for when it is held.
 *
 * If no readers are active (reader_count == 0) and no writer is active
 * (writer_active == 0), atomically sets writer_active to 1 and schedules
 * the continuation immediately.
 * Otherwise, enqueues the continuation in the write waiters queue.
 *
 * The continuation receives the EidosRwLock pointer as its argument.
 * When done, call eidos_rwlock_write_release().
 *
 * @param rwlock       The RwLock to write-lock (must not be NULL)
 * @param continuation Work item to schedule when the write lock is acquired
 */
void eidos_rwlock_write(EidosRwLock* rwlock, EidosWorkItem continuation) {
    if (!rwlock) return;

    /*
     * Try to acquire exclusive write access.
     * We need both reader_count == 0 and writer_active == 0.
     *
     * Strategy: CAS writer_active from 0 to 1, then re-check reader_count.
     * If readers are present, undo the writer_active and enqueue.
     */
    int32_t expected = 0;
    if (ESYNC_ATOMIC_CAS32(&rwlock->writer_active, expected, 1)) {
        /* We set writer_active. Now check if any readers are active. */
        if (ESYNC_ATOMIC_LOAD32(&rwlock->reader_count) == 0) {
            /* No readers: write lock acquired. */
            EidosWorkItem ready;
            ready.invoke_fn   = continuation.invoke_fn;
            ready.closure_ptr = continuation.closure_ptr;
            ready.arg         = rwlock;
            eidos_schedule(ready);
            return;
        }
        /*
         * Readers are still active. We have writer_active set, so no new
         * readers will enter (they check writer_active first). However,
         * we must wait for existing readers to finish. Undo writer_active
         * and enqueue as a writer waiter.
         *
         * Note: a more sophisticated approach would keep writer_active set
         * and enqueue the continuation, waking on the last reader release.
         * For simplicity, we release writer_active and enqueue normally.
         */
        ESYNC_ATOMIC_STORE32(&rwlock->writer_active, 0);
        eidos_waitqueue_push(&rwlock->write_waiters, continuation);
        return;
    }

    /* writer_active was already 1: another writer holds the lock. */
    eidos_waitqueue_push(&rwlock->write_waiters, continuation);
}

bool eidos_rwlock_try_write(EidosRwLock* rwlock) {
    if (!rwlock) return false;

    int32_t expected = 0;
    if (!ESYNC_ATOMIC_CAS32(&rwlock->writer_active, expected, 1)) {
        return false;
    }

    if (ESYNC_ATOMIC_LOAD32(&rwlock->reader_count) == 0) {
        return true;
    }

    ESYNC_ATOMIC_STORE32(&rwlock->writer_active, 0);
    return false;
}

void* eidos_rwlock_get_inner(EidosRwLock* rwlock) {
    return rwlock ? rwlock->inner : NULL;
}

void eidos_rwlock_replace_inner(EidosRwLock* rwlock, void* value) {
    if (!rwlock) return;

    if (value) {
        eidos_share(value);
        eidos_incref_shared(value);
    }

    void* old_value = rwlock->inner;
    rwlock->inner = value;

    if (old_value) {
        eidos_decref_shared(old_value);
    }
}

/**
 * Release a read lock.
 *
 * Atomically decrements reader_count. If it reaches 0 and there are
 * waiting writers, wakes one writer.
 *
 * @param rwlock The RwLock to release the read lock on (must not be NULL)
 */
void eidos_rwlock_read_release(EidosRwLock* rwlock) {
    if (!rwlock) return;

    int32_t new_count = ESYNC_ATOMIC_DEC32(&rwlock->reader_count);

    if (new_count == 0) {
        /* No more readers. Try to wake a waiting writer. */
        eidos_waitqueue_wake_one(&rwlock->write_waiters);
    }
}

/**
 * Release a write lock.
 *
 * Sets writer_active to 0, then wakes all waiting readers (they can
 * now proceed concurrently) and one waiting writer (if any).
 *
 * Readers are woken first to favor concurrent read throughput. Then
 * one writer is woken to compete for the next write acquisition.
 *
 * @param rwlock The RwLock to release the write lock on (must not be NULL)
 */
void eidos_rwlock_write_release(EidosRwLock* rwlock) {
    if (!rwlock) return;

    /* Release the write lock. */
    ESYNC_ATOMIC_STORE32(&rwlock->writer_active, 0);

    /* Wake all waiting readers first (they can proceed concurrently). */
    eidos_waitqueue_wake_all(&rwlock->read_waiters);

    /* Then try to wake one waiting writer. */
    eidos_waitqueue_wake_one(&rwlock->write_waiters);
}

/**
 * Destructor for EidosRwLock (registered for EIDOS_TYPE_RWLOCK).
 *
 * Releases the shared reference to the protected inner value,
 * then destroys both waiters queues.
 *
 * @param ptr Pointer to the EidosRwLock (after header)
 */
static void eidos_rwlock_destructor(void* ptr) {
    EidosRwLock* rwlock = (EidosRwLock*)ptr;
    if (!rwlock) return;

    if (rwlock->inner) {
        eidos_decref_shared(rwlock->inner);
        rwlock->inner = NULL;
    }

    eidos_waitqueue_destroy(&rwlock->read_waiters);
    eidos_waitqueue_destroy(&rwlock->write_waiters);
}

/* ============================================================
 * Barrier - Implementation
 * ============================================================ */

/**
 * Create a new Barrier with the given number of participants.
 *
 * All participants must call eidos_barrier_wait(). The barrier trips
 * when the last participant arrives, at which point all waiting
 * continuations (plus the last one) are scheduled.
 *
 * @param capacity  Number of participants that must arrive before the barrier trips
 * @return Pointer to the new EidosBarrier (shared, ref_count=1)
 */
EidosBarrier* eidos_barrier_new(uint32_t capacity) {
    ensure_sync_destructors();

    if (capacity == 0) {
        fprintf(stderr, "eidos_barrier_new: capacity must be > 0\n");
        return NULL;
    }

    EidosBarrier* barrier = (EidosBarrier*)eidos_alloc(sizeof(EidosBarrier), EIDOS_TYPE_BARRIER);
    if (!barrier) {
        fprintf(stderr, "eidos_barrier_new: out of memory\n");
        return NULL;
    }

    barrier->capacity   = capacity;
    barrier->arrived    = 0;
    barrier->generation = 0;
    eidos_waitqueue_init(&barrier->waiters);

    /* Mark as shared so atomic incref/decref is used. */
    eidos_share(barrier);

    return barrier;
}

static bool eidos_barrier_arrive_and_check_trip(EidosBarrier* barrier) {
    if (!barrier) return false;

    EidosWaiter* waiters = NULL;

    eidos_lock_acquire(&barrier->waiters.lock);
    uint32_t my_slot = barrier->arrived + 1;
    barrier->arrived = my_slot;

    if (my_slot != barrier->capacity) {
        eidos_lock_release(&barrier->waiters.lock);
        return false;
    }

    barrier->generation += 1;
    barrier->arrived = 0;
    waiters = barrier->waiters.head;
    barrier->waiters.head = NULL;
    barrier->waiters.tail = NULL;
    eidos_lock_release(&barrier->waiters.lock);

    eidos_waitqueue_schedule_list(waiters);
    return true;
}

bool eidos_barrier_arrive(EidosBarrier* barrier) {
    return eidos_barrier_arrive_and_check_trip(barrier);
}

/**
 * Wait at the Barrier, registering a continuation for when all participants arrive.
 *
 * Atomically increments the arrived counter. If this task is the last
 * one (arrived == capacity), the barrier trips: generation is incremented,
 * arrived is reset to 0, all waiting continuations are woken, and this
 * task's continuation is also scheduled immediately.
 *
 * If not the last participant, the continuation is enqueued in the
 * waiters queue and will be woken when the barrier trips.
 *
 * The barrier can be reused after it trips: the generation counter
 * distinguishes different cycles.
 *
 * @param barrier      The barrier to wait at (must not be NULL)
 * @param continuation Work item to schedule when the barrier trips
 */
void eidos_barrier_wait(EidosBarrier* barrier, EidosWorkItem continuation) {
    if (!barrier) return;

    ensure_sync_scheduler();

    EidosWaiter* waiter = (EidosWaiter*)malloc(sizeof(EidosWaiter));
    if (!waiter) {
        fprintf(stderr, "eidos_barrier_wait: out of memory\n");
        return;
    }
    waiter->work = continuation;
    waiter->next = NULL;

    EidosWaiter* waiters = NULL;
    bool tripped = false;

    eidos_lock_acquire(&barrier->waiters.lock);
    uint32_t my_slot = barrier->arrived + 1;
    barrier->arrived = my_slot;

    if (my_slot == barrier->capacity) {
        barrier->generation += 1;
        barrier->arrived = 0;
        waiters = barrier->waiters.head;
        barrier->waiters.head = NULL;
        barrier->waiters.tail = NULL;
        tripped = true;
    } else if (barrier->waiters.tail) {
        barrier->waiters.tail->next = waiter;
        barrier->waiters.tail = waiter;
        waiter = NULL;
    } else {
        barrier->waiters.head = waiter;
        barrier->waiters.tail = waiter;
        waiter = NULL;
    }
    eidos_lock_release(&barrier->waiters.lock);

    if (waiter) {
        free(waiter);
    }

    if (tripped) {
        /*
         * We are the last participant. The barrier trips.
         *
         * The locked section increments generation,
         * resets arrived to 0, and wakes queued waiters. Schedule this
         * caller's continuation immediately.
         */
        eidos_waitqueue_schedule_list(waiters);

        EidosWorkItem ready;
        ready.invoke_fn   = continuation.invoke_fn;
        ready.closure_ptr = continuation.closure_ptr;
        ready.arg         = barrier;
        eidos_schedule(ready);
    }
}

typedef void (*EidosClosureUnitUnitInvokeFn)(void* closure, bool unit);

static void* barrier_closure_unit_continuation_invoke(void* closure, void* arg) {
    EidosClosure* continuation = (EidosClosure*)closure;
    (void)arg;

    if (continuation && continuation->invoke_fn) {
        ((EidosClosureUnitUnitInvokeFn)continuation->invoke_fn)(continuation, false);
        eidos_decref_shared(continuation);
    }

    return NULL;
}

void eidos_barrier_wait_closure_raw(EidosBarrier* barrier, EidosClosure* continuation) {
    if (!barrier || !continuation) {
        return;
    }

    eidos_share(continuation);
    eidos_incref_shared(continuation);

    EidosWorkItem work;
    work.invoke_fn = barrier_closure_unit_continuation_invoke;
    work.closure_ptr = continuation;
    work.arg = NULL;
    eidos_barrier_wait(barrier, work);
}

static void eidos_waitqueue_schedule_list(EidosWaiter* waiters) {
    while (waiters) {
        EidosWaiter* next = waiters->next;
        eidos_schedule(waiters->work);
        free(waiters);
        waiters = next;
    }
}

/**
 * Destructor for EidosBarrier (registered for EIDOS_TYPE_BARRIER).
 *
 * Destroys the waiters queue, freeing any remaining waiter nodes.
 *
 * @param ptr Pointer to the EidosBarrier (after header)
 */
static void eidos_barrier_destructor(void* ptr) {
    EidosBarrier* barrier = (EidosBarrier*)ptr;
    if (!barrier) return;

    eidos_waitqueue_destroy(&barrier->waiters);
}
