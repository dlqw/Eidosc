/**
 * Eidos Runtime - Task-Aware Wait Queue
 *
 * A lock-free wait queue that suspends Tasks by registering continuations
 * (EidosWorkItems) and later scheduling them via eidos_schedule().
 *
 * Unlike traditional thread-blocking wait queues, this never blocks OS threads.
 * Instead, Tasks register their continuation and yield; when the condition is
 * met, the continuation is scheduled back onto the work-stealing thread pool.
 *
 * Used by: Channel, Promise/Future, Mutex, RwLock, Barrier.
 *
 * Include after eidos_sync.h (which includes eidos_runtime.h).
 */

#ifndef EIDOS_WAITQUEUE_H
#define EIDOS_WAITQUEUE_H

#include "eidos_sync.h"
#include <stdio.h>
#include <stdlib.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ============================================================
 * Waiter Node
 * ============================================================ */

/**
 * A single waiter: a work item to be scheduled plus a next pointer.
 * Waiter nodes are allocated with malloc() and freed when scheduled.
 * A per-thread freelist optimization can be added later.
 */
typedef struct EidosWaiter {
    EidosWorkItem        work;   /* continuation to schedule on wake */
    struct EidosWaiter*  next;   /* linked-list pointer */
} EidosWaiter;

/* ============================================================
 * Wait Queue
 * ============================================================ */

/**
 * A lock-free MPSC (multi-producer, single-consumer) wait queue.
 *
 * Producers push to the tail via CAS.
 * Consumer pops from the head.
 *
 * For simplicity, we use a lock to protect push/pop since contention
 * is low (pushes happen on task suspend, pops on resource release).
 * Lock-free optimization can be added later if profiling shows contention.
 */
typedef struct EidosWaitQueue {
    EidosWaiter*  head;
    EidosWaiter*  tail;
    EidosNativeLock lock;
} EidosWaitQueue;

/* ============================================================
 * API
 * ============================================================ */

/**
 * Initialize a wait queue.
 */
static inline void eidos_waitqueue_init(EidosWaitQueue* q) {
    q->head = NULL;
    q->tail = NULL;
    eidos_lock_init(&q->lock);
}

/**
 * Enqueue a waiter.
 *
 * Allocates a new EidosWaiter node and appends it to the queue.
 * The caller's continuation will be scheduled when the resource
 * becomes available (via eidos_waitqueue_wake_one or wake_all).
 *
 * @param q    The wait queue
 * @param work The work item to schedule when woken
 */
static inline void eidos_waitqueue_push(EidosWaitQueue* q, EidosWorkItem work) {
    EidosWaiter* w = (EidosWaiter*)malloc(sizeof(EidosWaiter));
    if (!w) {
        fprintf(stderr, "eidos_waitqueue_push: out of memory\n");
        return;
    }
    w->work = work;
    w->next = NULL;

    eidos_lock_acquire(&q->lock);

    if (q->tail) {
        q->tail->next = w;
    } else {
        q->head = w;
    }
    q->tail = w;

    eidos_lock_release(&q->lock);
}

/**
 * Dequeue one waiter and schedule its work item.
 *
 * @param q  The wait queue
 * @return   1 if a waiter was woken, 0 if the queue was empty
 */
static inline int eidos_waitqueue_wake_one(EidosWaitQueue* q) {
    eidos_lock_acquire(&q->lock);

    EidosWaiter* w = q->head;
    if (!w) {
        eidos_lock_release(&q->lock);
        return 0;
    }

    q->head = w->next;
    if (!q->head) {
        q->tail = NULL;
    }

    eidos_lock_release(&q->lock);

    /* Schedule the waiter's continuation onto the thread pool. */
    eidos_schedule(w->work);
    free(w);
    return 1;
}

/**
 * Dequeue all waiters and schedule their work items.
 *
 * @param q  The wait queue
 * @return   Number of waiters woken
 */
static inline int eidos_waitqueue_wake_all(EidosWaitQueue* q) {
    int count = 0;

    eidos_lock_acquire(&q->lock);

    EidosWaiter* w = q->head;
    q->head = NULL;
    q->tail = NULL;

    eidos_lock_release(&q->lock);

    while (w) {
        EidosWaiter* next = w->next;
        eidos_schedule(w->work);
        free(w);
        w = next;
        count++;
    }

    return count;
}

static inline int eidos_waitqueue_try_remove(EidosWaitQueue* q, EidosWorkItem work, EidosWorkItem* removed) {
    int found = 0;
    eidos_lock_acquire(&q->lock);

    EidosWaiter* prev = NULL;
    EidosWaiter* cur = q->head;
    while (cur) {
        if (cur->work.invoke_fn == work.invoke_fn &&
            cur->work.closure_ptr == work.closure_ptr &&
            cur->work.arg == work.arg) {
            if (prev) {
                prev->next = cur->next;
            } else {
                q->head = cur->next;
            }
            if (q->tail == cur) {
                q->tail = prev;
            }
            if (removed) {
                *removed = cur->work;
            }
            free(cur);
            found = 1;
            break;
        }
        prev = cur;
        cur = cur->next;
    }

    eidos_lock_release(&q->lock);
    return found;
}

/**
 * Check if the wait queue is empty (no lock, approximate).
 *
 * @param q  The wait queue
 * @return   Non-zero if empty, 0 if non-empty
 */
static inline int eidos_waitqueue_is_empty(EidosWaitQueue* q) {
    return q->head == NULL;
}

/**
 * Destroy a wait queue, freeing any remaining waiter nodes.
 * Does NOT schedule remaining waiters — call wake_all first if needed.
 *
 * @param q  The wait queue
 */
static inline void eidos_waitqueue_destroy(EidosWaitQueue* q) {
    EidosWaiter* w = q->head;
    while (w) {
        EidosWaiter* next = w->next;
        free(w);
        w = next;
    }
    q->head = NULL;
    q->tail = NULL;
}

#ifdef __cplusplus
}
#endif

#endif /* EIDOS_WAITQUEUE_H */
