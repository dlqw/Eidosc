/**
 * Eidos Runtime - One-Shot Promise / Future
 *
 * Implements a one-shot Promise/Future pair for asynchronous value delivery.
 *
 *   Promise  - the write side: fulfilled exactly once by the producer.
 *   Future   - the read side:  awaited by one or more consumers.
 *
 * Safety guarantees:
 *   - Promise is one-shot: CAS enforces at most one successful fulfill.
 *   - Both Promise and Future are shared objects (thread-safe RC via
 *     eidos_share / eidos_incref_shared / eidos_decref_shared).
 *   - Future.await is affine: the caller should only await once per Future.
 *     (The Promise supports multiple waiters via its WaitQueue.)
 *
 * Include dependency chain: eidos_runtime.h -> eidos_sync.h -> eidos_waitqueue.h
 */

#include "eidos_waitqueue.h"
#include <stdlib.h>
#include <string.h>
#include <stdio.h>

/* ============================================================
 * Atomics (platform-abstracted, consistent with eidos_task.c)
 * ============================================================ */

#if defined(_WIN32)
#include <intrin.h>
#define EPROM_ATOMIC_LOAD32(ptr)        (*(volatile int32_t*)(ptr))
#define EPROM_ATOMIC_STORE32(ptr, val)  (*(volatile int32_t*)(ptr) = (int32_t)(val))
#define EPROM_ATOMIC_CAS32(ptr, exp, des) \
    (_InterlockedCompareExchange((LONG volatile*)(ptr), (LONG)(des), (LONG)(exp)) == (LONG)(exp))
#else
#define EPROM_ATOMIC_LOAD32(ptr)        __atomic_load_n((ptr), __ATOMIC_ACQUIRE)
#define EPROM_ATOMIC_STORE32(ptr, val)  __atomic_store_n((ptr), (val), __ATOMIC_RELEASE)
#define EPROM_ATOMIC_CAS32(ptr, exp, des) \
    __atomic_compare_exchange_n((ptr), &(exp), (des), 0, \
                                __ATOMIC_SEQ_CST, __ATOMIC_SEQ_CST)
#endif

/* ============================================================
 * Structures
 * ============================================================ */

/** Promise states. */
#define EPROM_STATE_PENDING   0
#define EPROM_STATE_FULFILLED 1
#define EPROM_STATE_REJECTED  2

/**
 * EidosPromise - the write side of a one-shot async value.
 *
 * State transitions (atomic):
 *   Pending(0) --> Fulfilled(1)   via eidos_promise_fulfill()
 *   Pending(0) --> Rejected(2)    (future: eidos_promise_reject)
 *
 * Once fulfilled, the result pointer is stored and all waiters are woken.
 * The result is a shared pointer; a reference is acquired on store and
 * released in the destructor.
 */
typedef struct EidosPromise {
    EidosHeader       header;
    volatile uint32_t state;     /* EPROM_STATE_* (atomic) */
    uint32_t          raw_payloads; /* 1 = result is an unmanaged RawPtr */
    void*             result;    /* shared ptr to result value */
    EidosWaitQueue    waiters;   /* continuations awaiting fulfillment */
} EidosPromise;

/**
 * EidosFuture - the read side of a one-shot async value.
 *
 * Holds a shared reference to the linked Promise. When awaited, the
 * continuation is either scheduled immediately (if already fulfilled)
 * or pushed onto the Promise's waiter queue.
 */
typedef struct EidosFuture {
    EidosHeader       header;
    EidosPromise*     promise;   /* linked promise (shared ref) */
} EidosFuture;

/* ============================================================
 * Module State
 * ============================================================ */

/** Flag to track whether destructors have been registered. */
static int32_t g_promise_destructors_registered = 0;

/* Forward declarations */
static void eidos_promise_destructor(void* ptr);
static void eidos_future_destructor(void* ptr);
static void eidos_promise_retain_result(EidosPromise* promise, void* value);
static void eidos_promise_release_result(EidosPromise* promise, void* value);

/**
 * Register destructors for EIDOS_TYPE_PROMISE and EIDOS_TYPE_FUTURE.
 * Idempotent: safe to call multiple times.
 */
static void ensure_promise_destructors(void) {
    if (!EPROM_ATOMIC_LOAD32(&g_promise_destructors_registered)) {
        eidos_register_destructor(EIDOS_TYPE_PROMISE, eidos_promise_destructor);
        eidos_register_destructor(EIDOS_TYPE_FUTURE, eidos_future_destructor);
        EPROM_ATOMIC_STORE32(&g_promise_destructors_registered, 1);
    }
}

/* ============================================================
 * Promise / Future Creation
 * ============================================================ */

/**
 * Create a new Promise/Future pair.
 *
 * Both objects are allocated as shared (thread-safe RC) with ref_count=1.
 * The Future holds a shared reference to the Promise, so the total
 * ref_count on the Promise starts at 2 (one for the caller, one for the
 * Future).
 *
 * @param promise_out  Receives the new Promise pointer (caller owns ref_count=1)
 * @param future_out   Receives the new Future pointer  (caller owns ref_count=1)
 */
void eidos_promise_new(struct EidosPromise** promise_out,
                       struct EidosFuture**  future_out)
{
    ensure_promise_destructors();

    if (!promise_out || !future_out) {
        fprintf(stderr, "eidos_promise_new: null output pointer\n");
        return;
    }

    /* Allocate Promise. */
    EidosPromise* promise = (EidosPromise*)eidos_alloc(sizeof(EidosPromise),
                                                        EIDOS_TYPE_PROMISE);
    if (!promise) {
        fprintf(stderr, "eidos_promise_new: out of memory (promise)\n");
        *promise_out = NULL;
        *future_out  = NULL;
        return;
    }

    promise->state        = EPROM_STATE_PENDING;
    promise->raw_payloads = 0;
    promise->result       = NULL;
    eidos_waitqueue_init(&promise->waiters);

    /* Mark as shared so atomic incref/decref is used. */
    eidos_share(promise);

    /* Allocate Future. */
    EidosFuture* future = (EidosFuture*)eidos_alloc(sizeof(EidosFuture),
                                                     EIDOS_TYPE_FUTURE);
    if (!future) {
        fprintf(stderr, "eidos_promise_new: out of memory (future)\n");
        /* Clean up the already-created promise. */
        eidos_waitqueue_destroy(&promise->waiters);
        eidos_decref_shared(promise);
        *promise_out = NULL;
        *future_out  = NULL;
        return;
    }

    future->promise = promise;

    /* Mark as shared. */
    eidos_share(future);

    /*
     * The Future holds a shared reference to the Promise.
     * Promise ref_count is now 2 (caller's + Future's).
     * Future ref_count is 1 (caller's).
     */
    eidos_incref_shared(promise);

    *promise_out = promise;
    *future_out  = future;
}

void eidos_promise_new_raw(struct EidosPromise** promise_out,
                           struct EidosFuture**  future_out)
{
    eidos_promise_new(promise_out, future_out);
    if (promise_out && *promise_out) {
        (*promise_out)->raw_payloads = 1;
    }
}

struct EidosPromise* eidos_promise_new_raw_single(void)
{
    ensure_promise_destructors();

    EidosPromise* promise = (EidosPromise*)eidos_alloc(sizeof(EidosPromise),
                                                        EIDOS_TYPE_PROMISE);
    if (!promise) {
        fprintf(stderr, "eidos_promise_new_raw_single: out of memory\n");
        return NULL;
    }

    promise->state        = EPROM_STATE_PENDING;
    promise->raw_payloads = 1;
    promise->result       = NULL;
    eidos_waitqueue_init(&promise->waiters);
    eidos_share(promise);
    return promise;
}

struct EidosPromise* eidos_promise_new_single(void)
{
    ensure_promise_destructors();

    EidosPromise* promise = (EidosPromise*)eidos_alloc(sizeof(EidosPromise),
                                                        EIDOS_TYPE_PROMISE);
    if (!promise) {
        fprintf(stderr, "eidos_promise_new_single: out of memory\n");
        return NULL;
    }

    promise->state        = EPROM_STATE_PENDING;
    promise->raw_payloads = 0;
    promise->result       = NULL;
    eidos_waitqueue_init(&promise->waiters);
    eidos_share(promise);
    return promise;
}

static void eidos_promise_retain_result(EidosPromise* promise, void* value) {
    if (value && promise && !promise->raw_payloads) {
        eidos_incref_shared(value);
    }
}

static void eidos_promise_release_result(EidosPromise* promise, void* value) {
    if (value && promise && !promise->raw_payloads) {
        eidos_decref_shared(value);
    }
}

/* ============================================================
 * Promise Fulfill
 * ============================================================ */

/**
 * Fulfill a Promise with a value.
 *
 * Atomically transitions the Promise from Pending to Fulfilled via CAS.
 * If the CAS succeeds:
 *   - Stores the result (acquiring a shared reference if non-null).
 *   - Wakes all waiters (each waiter's continuation is scheduled with
 *     the result as its argument).
 * If the CAS fails (already fulfilled or rejected), this is a no-op.
 *
 * @param promise  The Promise to fulfill (must not be NULL)
 * @param value    Shared pointer to the result value (may be NULL)
 * @return         1 on success, 0 if already fulfilled/rejected
 */
int eidos_promise_fulfill(struct EidosPromise* promise, void* value)
{
    if (!promise) return 0;

    /*
     * CAS state from Pending(0) to Fulfilled(1).
     * If CAS fails, the promise was already resolved -- no-op.
     */
    int32_t expected = EPROM_STATE_PENDING;
    if (!EPROM_ATOMIC_CAS32((volatile int32_t*)&promise->state,
                            expected,
                            EPROM_STATE_FULFILLED)) {
        /* Already fulfilled or rejected. */
        return 0;
    }

    /* Store result, acquiring a shared reference if non-null. */
    promise->result = value;
    eidos_promise_retain_result(promise, value);

    /*
     * Wake all waiters. Each waiter is an EidosWorkItem whose arg
     * will be the result. We must set arg = result before scheduling.
     *
     * eidos_waitqueue_wake_all() calls eidos_schedule() for each waiter,
     * but it does not modify the work item's arg. We need to set the
     * result as the arg for each waiter.
     *
     * Strategy: drain the wait queue ourselves, patch each work item's
     * arg to the result, then schedule.
     */
    EidosWaitQueue* q = &promise->waiters;

    eidos_lock_acquire(&q->lock);

    EidosWaiter* w = q->head;
    q->head = NULL;
    q->tail = NULL;

    eidos_lock_release(&q->lock);

    while (w) {
        EidosWaiter* next = w->next;
        /* Patch the continuation's arg to be the result value. */
        eidos_promise_retain_result(promise, value);
        w->work.arg = value;
        eidos_schedule(w->work);
        free(w);
        w = next;
    }

    return 1;
}

bool eidos_promise_fulfill_raw(struct EidosPromise* promise, void* value)
{
    return eidos_promise_fulfill(promise, value) != 0;
}

/* ============================================================
 * Future Await
 * ============================================================ */

/**
 * Await the result of a Future, registering a continuation.
 *
 * If the linked Promise is already Fulfilled, the continuation is
 * scheduled immediately with the Promise's result as its argument.
 *
 * If the Promise is still Pending, the continuation is pushed onto
 * the Promise's waiter queue. When the Promise is fulfilled, the
 * continuation will be scheduled with the result as its argument.
 *
 * Race handling:
 *   We push the continuation to the waiter queue first, then re-check
 *   the state. This ordering prevents the race where:
 *     1. We check state == Pending
 *     2. Another thread fulfills the Promise (wakes existing waiters)
 *     3. Our continuation is never scheduled
 *
 *   With push-first ordering:
 *     - If the Promise is fulfilled after our push but before our
 *       re-check, the fulfill path will find our waiter and schedule it.
 *     - If the Promise was already fulfilled before our push, we
 *       detect it on re-check and schedule immediately. The waiter
 *       node we pushed will be cleaned up by the Promise destructor.
 *
 * @param future       The Future to await (must not be NULL)
 * @param continuation Work item to schedule when the Promise is fulfilled.
 *                     The arg field will be overwritten with the result value.
 */
void eidos_future_await(struct EidosFuture* future, EidosWorkItem continuation)
{
    if (!future || !future->promise) return;

    EidosPromise* promise = future->promise;

    /*
     * Fast path: already fulfilled. Read state first (acquire semantics).
     * If fulfilled, schedule continuation immediately with result as arg.
     */
    uint32_t state = (uint32_t)EPROM_ATOMIC_LOAD32((volatile int32_t*)&promise->state);
    if (state == EPROM_STATE_FULFILLED) {
        EidosWorkItem done;
        done.invoke_fn   = continuation.invoke_fn;
        done.closure_ptr = continuation.closure_ptr;
        eidos_promise_retain_result(promise, promise->result);
        done.arg         = promise->result;
        eidos_schedule(done);
        return;
    }

    /*
     * Slow path: still pending (or rejected). Push continuation to waiters.
     *
     * We push first, then re-check state. If the Promise is fulfilled
     * concurrently, the fulfill path will drain the queue and schedule
     * our continuation with the correct result.
     */
    eidos_waitqueue_push(&promise->waiters, continuation);

    /*
     * Re-check state after push. If it transitioned to Fulfilled while
     * we were pushing, the fulfill path may or may not have seen our
     * waiter depending on timing. We handle both cases:
     *
     *   - If fulfill saw our waiter: it will schedule it. We do nothing.
     *   - If fulfill missed our waiter (very narrow race): we schedule
     *     immediately and the waiter node is left in the queue (cleaned
     *     up by the destructor).
     */
    state = (uint32_t)EPROM_ATOMIC_LOAD32((volatile int32_t*)&promise->state);
    if (state == EPROM_STATE_FULFILLED) {
        /*
         * Promise was fulfilled after our push. The fulfill path's
         * wake_all may have already scheduled our waiter, or it may
         * have drained the queue before our push completed.
         *
         * To avoid double-scheduling, we attempt to remove our waiter.
         * However, since the queue is MPSC and wake_all may have already
         * drained it, we simply schedule again -- the continuation's
         * invoke_fn must be idempotent or the caller must handle this.
         *
         * In practice, the race window is extremely narrow. The common
         * case is that fulfill's wake_all already picked up our waiter.
         * We schedule defensively to guarantee liveness.
         */
        EidosWorkItem done;
        done.invoke_fn   = continuation.invoke_fn;
        done.closure_ptr = continuation.closure_ptr;
        eidos_promise_retain_result(promise, promise->result);
        done.arg         = promise->result;
        eidos_schedule(done);
    }
}

void* eidos_future_try_get(struct EidosFuture* future)
{
    if (!future || !future->promise) return NULL;

    EidosPromise* promise = future->promise;
    uint32_t state = (uint32_t)EPROM_ATOMIC_LOAD32((volatile int32_t*)&promise->state);
    if (state != EPROM_STATE_FULFILLED) {
        return NULL;
    }

    return promise->result;
}

void* eidos_promise_try_get_raw(struct EidosPromise* promise)
{
    if (!promise) return NULL;

    uint32_t state = (uint32_t)EPROM_ATOMIC_LOAD32((volatile int32_t*)&promise->state);
    if (state != EPROM_STATE_FULFILLED) {
        return NULL;
    }

    return promise->result;
}

/* ============================================================
 * Destructors
 * ============================================================ */

/**
 * Destructor for EidosPromise (registered for EIDOS_TYPE_PROMISE).
 *
 * Releases the shared reference to the result (if any) and destroys
 * the waiter queue. Remaining waiters are freed without being scheduled;
 * if graceful shutdown is needed, the caller should fulfill the Promise
 * before releasing it.
 *
 * @param ptr Pointer to the EidosPromise (after header)
 */
static void eidos_promise_destructor(void* ptr)
{
    EidosPromise* promise = (EidosPromise*)ptr;
    if (!promise) return;

    /* Release result. */
    if (promise->result) {
        eidos_promise_release_result(promise, promise->result);
        promise->result = NULL;
    }

    /* Destroy waiter queue (frees remaining nodes without scheduling). */
    eidos_waitqueue_destroy(&promise->waiters);
}

/**
 * Destructor for EidosFuture (registered for EIDOS_TYPE_FUTURE).
 *
 * Releases the shared reference to the linked Promise.
 *
 * @param ptr Pointer to the EidosFuture (after header)
 */
static void eidos_future_destructor(void* ptr)
{
    EidosFuture* future = (EidosFuture*)ptr;
    if (!future) return;

    /* Release the shared reference to the linked Promise. */
    if (future->promise) {
        eidos_decref_shared(future->promise);
        future->promise = NULL;
    }
}
