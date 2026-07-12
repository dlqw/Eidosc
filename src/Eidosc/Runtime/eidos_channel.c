/**
 * Eidos Runtime - Async Channel (CSP-style)
 *
 * Implements unbuffered (rendezvous) and buffered channels for task-based
 * message passing. All operations are fully asynchronous: they never block
 * OS threads. Instead, tasks register continuations via WaitQueue and yield
 * back to the work-stealing scheduler.
 *
 * Buffered channels (capacity > 0) use a ring buffer. When the buffer is
 * full, senders suspend; when empty, receivers suspend.
 *
 * Rendezvous channels (capacity == 0) have no buffer. A sender and receiver
 * must meet simultaneously: if a receiver is already waiting, the value is
 * handed off directly; otherwise the sender suspends until a receiver arrives.
 *
 * Key design decisions:
 *   - A single channel-level lock (ch_lock) protects all mutable state.
 *     Contention is low because operations are brief (no blocking inside).
 *   - Pending sends are stored in a parallel linked list alongside send_waiters.
 *     They are always pushed and popped in lockstep.
 *   - When a receiver suspends, a retry trampoline is stored in recv_waiters.
 *     When woken (either by a rendezvous send or a buffered send), the
 *     trampoline re-invokes recv, which finds the data and delivers it.
 *   - Reference counting: each value stored in the buffer or pending list
 *     gets an extra shared incref. The consumer (receiver) is responsible
 *     for the corresponding decref.
 *
 * Include dependency chain: eidos_runtime.h -> eidos_sync.h -> eidos_waitqueue.h
 */

#include "eidos_waitqueue.h"
#include <stdlib.h>
#include <string.h>
#include <stdio.h>

/* ============================================================
 * Atomics (consistent with eidos_scheduler.c / eidos_task.c)
 * ============================================================ */

#if defined(_WIN32)
#include <intrin.h>
#define ECH_ATOMIC_LOAD32(ptr)        (*(volatile int32_t*)(ptr))
#define ECH_ATOMIC_STORE32(ptr, val)  (*(volatile int32_t*)(ptr) = (int32_t)(val))
#else
#define ECH_ATOMIC_LOAD32(ptr)        __atomic_load_n((ptr), __ATOMIC_ACQUIRE)
#define ECH_ATOMIC_STORE32(ptr, val)  __atomic_store_n((ptr), (val), __ATOMIC_RELEASE)
#endif

/* ============================================================
 * Pending Send Node
 *
 * When a sender blocks (buffer full or rendezvous), we store the
 * value in a linked list parallel to send_waiters. The two are
 * always pushed and popped together under ch_lock.
 * ============================================================ */

typedef struct EidosPendingSend {
    void*                    value;   /* shared pointer being sent */
    struct EidosPendingSend* next;
} EidosPendingSend;

/* ============================================================
 * Recv Retry Trampoline
 *
 * When a receiver suspends on an empty channel, we cannot simply
 * store the receiver's continuation in recv_waiters because
 * eidos_waitqueue_wake_one() schedules the stored work item with
 * its original arg -- but we need the arg to be the received value.
 *
 * Instead, we store a trampoline work item whose closure_ptr holds
 * a heap-allocated EidosRecvRetry. When woken, the trampoline
 * re-invokes eidos_channel_recv with the real continuation, which
 * then finds data in the buffer/pending list and delivers it.
 *
 * Optimization: for rendezvous sends, the sender directly dequeues
 * the waiter, extracts the real continuation from the trampoline,
 * and schedules it with the value -- bypassing the retry entirely.
 * ============================================================ */

typedef struct EidosRecvRetry {
    struct EidosChannel* ch;
    EidosWorkItem        continuation;  /* the real receiver continuation */
} EidosRecvRetry;

/* ============================================================
 * Channel Structure
 * ============================================================ */

typedef struct EidosChannel {
    EidosHeader       header;
    volatile uint32_t closed;       /* 0 = open, 1 = closed */
    uint32_t          capacity;     /* buffer size (0 = rendezvous) */
    uint32_t          raw_payloads; /* 1 = payloads are unmanaged RawPtr values */
    void**            buffer;       /* ring buffer (shared pointers), NULL when capacity==0 */
    volatile uint32_t buf_head;     /* index of next read position */
    volatile uint32_t buf_tail;     /* index of next write position */
    volatile uint32_t buf_count;    /* number of items currently in buffer */
    EidosNativeLock   ch_lock;      /* protects all mutable state below */
    EidosWaitQueue    send_waiters; /* sender continuations waiting for space */
    EidosWaitQueue    recv_waiters; /* recv retry trampolines waiting for data */
    EidosPendingSend* pending_head; /* head of pending-sends linked list */
    EidosPendingSend* pending_tail; /* tail of pending-sends linked list */
} EidosChannel;

/* ============================================================
 * Module State
 * ============================================================ */

static int32_t g_channel_destructors_registered = 0;

static void eidos_channel_destructor(void* ptr);

/**
 * Register destructor for EIDOS_TYPE_CHANNEL.
 * Idempotent: safe to call multiple times.
 */
static void ensure_channel_destructors(void) {
    if (!ECH_ATOMIC_LOAD32(&g_channel_destructors_registered)) {
        eidos_register_destructor(EIDOS_TYPE_CHANNEL, eidos_channel_destructor);
        ECH_ATOMIC_STORE32(&g_channel_destructors_registered, 1);
    }
}

/* ============================================================
 * Closed-channel Sentinel
 *
 * When a send is attempted on a closed channel, or a recv on a
 * closed+empty channel, the continuation is scheduled with this
 * sentinel as the arg. The caller checks against
 * eidos_channel_closed_sentinel() to detect the closed condition.
 * ============================================================ */

static int g_eidos_channel_closed_sentinel_marker = 0;

void* eidos_channel_closed_sentinel(void) {
    return &g_eidos_channel_closed_sentinel_marker;
}

bool eidos_channel_is_closed_value(void* value) {
    return value == eidos_channel_closed_sentinel();
}

/* ============================================================
 * Internal Helpers - Pending Send List
 *
 * All pending_* functions require ch_lock to be held.
 * ============================================================ */

static EidosPendingSend* pending_send_alloc(void) {
    EidosPendingSend* ps = (EidosPendingSend*)malloc(sizeof(EidosPendingSend));
    if (!ps) {
        fprintf(stderr, "eidos_channel: pending send alloc failed\n");
    }
    return ps;
}

static void pending_push(EidosChannel* ch, void* value) {
    EidosPendingSend* ps = pending_send_alloc();
    if (!ps) return;
    ps->value = value;
    ps->next  = NULL;

    if (ch->pending_tail) {
        ch->pending_tail->next = ps;
    } else {
        ch->pending_head = ps;
    }
    ch->pending_tail = ps;
}

static void channel_retain_value(EidosChannel* ch, void* value) {
    if (value && !ch->raw_payloads) {
        eidos_incref_shared(value);
    }
}

static void channel_release_value(EidosChannel* ch, void* value) {
    if (value && !ch->raw_payloads) {
        eidos_decref_shared(value);
    }
}

/**
 * Pop the oldest pending send. Returns the value, or NULL if empty.
 * The EidosPendingSend node is freed.
 */
static void* pending_pop(EidosChannel* ch) {
    EidosPendingSend* ps = ch->pending_head;
    if (!ps) return NULL;

    void* value = ps->value;
    ch->pending_head = ps->next;
    if (!ch->pending_head) {
        ch->pending_tail = NULL;
    }
    free(ps);
    return value;
}

/* ============================================================
 * Internal Helpers - Buffer
 * ============================================================ */

/**
 * Store a value into the ring buffer.
 * Caller must hold ch_lock and verify buf_count < capacity.
 */
static void buffer_put(EidosChannel* ch, void* value) {
    ch->buffer[ch->buf_tail] = value;
    ch->buf_tail = (ch->buf_tail + 1) % ch->capacity;
    ch->buf_count++;
}

/**
 * Take a value from the ring buffer.
 * Caller must hold ch_lock and verify buf_count > 0.
 * Returns the value (shared pointer; caller inherits the buffer's ref).
 */
static void* buffer_take(EidosChannel* ch) {
    void* value = ch->buffer[ch->buf_head];
    ch->buffer[ch->buf_head] = NULL;
    ch->buf_head = (ch->buf_head + 1) % ch->capacity;
    ch->buf_count--;
    return value;
}

/* ============================================================
 * Recv Retry Trampoline
 * ============================================================ */

/**
 * Trampoline function for suspended receivers.
 *
 * When a receiver is woken from recv_waiters, this function is
 * scheduled. It re-invokes eidos_channel_recv with the original
 * continuation, which will find data in the buffer or pending list.
 */
static void* recv_retry_trampoline(void* closure, void* arg) {
    EidosRecvRetry* retry = (EidosRecvRetry*)closure;
    (void)arg;

    struct EidosChannel* ch = retry->ch;
    EidosWorkItem cont      = retry->continuation;

    free(retry);

    /*
     * Decref the channel reference acquired when the retry was created.
     * This balances the incref in eidos_channel_recv's suspend path.
     */
    eidos_decref_shared(ch);

    /* Re-attempt the recv. This time, data should be available. */
    eidos_channel_recv(ch, cont);

    return NULL;
}

/* ============================================================
 * Drain Helpers (for destructor)
 * ============================================================ */

/**
 * Free all pending send nodes, releasing shared references on values.
 * Caller must hold ch_lock.
 */
static void pending_drain_all_locked(EidosChannel* ch) {
    EidosPendingSend* ps = ch->pending_head;
    while (ps) {
        EidosPendingSend* next = ps->next;
        channel_release_value(ch, ps->value);
        free(ps);
        ps = next;
    }
    ch->pending_head = NULL;
    ch->pending_tail = NULL;
}

/**
 * Drain all items from the ring buffer, releasing shared references.
 * Caller must hold ch_lock.
 */
static void buffer_drain_all_locked(EidosChannel* ch) {
    if (!ch->buffer) return;

    uint32_t head  = ch->buf_head;
    uint32_t count = ch->buf_count;
    for (uint32_t i = 0; i < count; i++) {
        void* val = ch->buffer[head];
        channel_release_value(ch, val);
        head = (head + 1) % ch->capacity;
    }
    ch->buf_head  = 0;
    ch->buf_tail  = 0;
    ch->buf_count = 0;
}

/* ============================================================
 * Public API
 * ============================================================ */

/**
 * Create a new async channel.
 *
 * @param capacity  Buffer capacity. 0 = rendezvous (unbuffered).
 *                  Values > 0 create a buffered channel.
 * @return Pointer to the new EidosChannel (shared, ref_count=1)
 */
struct EidosChannel* eidos_channel_new(uint32_t capacity) {
    ensure_channel_destructors();

    EidosChannel* ch = (EidosChannel*)eidos_alloc(sizeof(EidosChannel),
                                                   EIDOS_TYPE_CHANNEL);
    if (!ch) {
        fprintf(stderr, "eidos_channel_new: out of memory\n");
        return NULL;
    }

    /* Initialize fields */
    ch->closed        = 0;
    ch->capacity      = capacity;
    ch->raw_payloads  = 0;
    ch->buffer        = NULL;
    ch->buf_head      = 0;
    ch->buf_tail      = 0;
    ch->buf_count     = 0;
    ch->pending_head  = NULL;
    ch->pending_tail  = NULL;

    /* Allocate ring buffer if capacity > 0 */
    if (capacity > 0) {
        ch->buffer = (void**)calloc(capacity, sizeof(void*));
        if (!ch->buffer) {
            fprintf(stderr, "eidos_channel_new: buffer alloc failed\n");
            eidos_free(ch);
            return NULL;
        }
    }

    /* Initialize synchronization primitives */
    eidos_lock_init(&ch->ch_lock);
    eidos_waitqueue_init(&ch->send_waiters);
    eidos_waitqueue_init(&ch->recv_waiters);

    /* Mark as shared for thread-safe reference counting */
    eidos_share(ch);

    return ch;
}

struct EidosChannel* eidos_channel_new_capacity(int64_t capacity) {
    if (capacity <= 0) {
        return eidos_channel_new(0);
    }

    if ((uint64_t)capacity > UINT32_MAX) {
        return eidos_channel_new(UINT32_MAX);
    }

    return eidos_channel_new((uint32_t)capacity);
}

struct EidosChannel* eidos_channel_new_raw_capacity(int64_t capacity) {
    EidosChannel* ch = eidos_channel_new_capacity(capacity);
    if (ch) {
        ch->raw_payloads = 1;
    }
    return ch;
}

/**
 * Async send: enqueue a value onto the channel.
 *
 * Outcomes (all schedule the continuation immediately or via waiters):
 *   1. Closed: continuation(arg = closed_sentinel)
 *   2. Receiver waiting (rendezvous): value delivered directly,
 *      continuation(arg = NULL) = success
 *   3. Buffer has space: value stored in ring buffer,
 *      wake one recv_waiter, continuation(arg = NULL) = success
 *   4. No room: value stored in pending_sends, continuation pushed
 *      to send_waiters. Sender is suspended until a receiver takes it.
 *
 * Reference counting for value:
 *   - Paths 2/3 (rendezvous/buffered): incref_shared(value) for the
 *     receiver/buffer's ownership. The sender retains its own reference.
 *   - Path 4 (suspend): incref_shared(value) for the pending list's
 *     ownership. When consumed, the receiver inherits this reference.
 *
 * @param ch           The channel (must not be NULL)
 * @param value        Shared pointer to send (may be NULL)
 * @param continuation Sender's continuation
 */
void eidos_channel_send(struct EidosChannel* ch, void* value,
                         EidosWorkItem continuation) {
    if (!ch) return;

    /* ---- Acquire channel lock ---- */
    eidos_lock_acquire(&ch->ch_lock);

    /* Check closed */
    if (ECH_ATOMIC_LOAD32((volatile int32_t*)&ch->closed)) {
        eidos_lock_release(&ch->ch_lock);

        EidosWorkItem done;
        done.invoke_fn   = continuation.invoke_fn;
        done.closure_ptr = continuation.closure_ptr;
        done.arg         = eidos_channel_closed_sentinel();
        eidos_schedule(done);
        return;
    }

    /* ---- Fast path: rendezvous with waiting receiver ---- */
    if (!eidos_waitqueue_is_empty(&ch->recv_waiters)) {
        /*
         * A receiver is suspended. Dequeue its retry trampoline from
         * recv_waiters. Extract the real continuation from the trampoline
         * and schedule it with our value directly (bypass retry).
         *
         * Incref the value for the receiver's ownership.
         */
        channel_retain_value(ch, value);

        eidos_lock_release(&ch->ch_lock);

        /* Dequeue one recv waiter directly. */
        eidos_lock_acquire(&ch->recv_waiters.lock);
        EidosWaiter* w = ch->recv_waiters.head;
        if (w) {
            ch->recv_waiters.head = w->next;
            if (!ch->recv_waiters.head) {
                ch->recv_waiters.tail = NULL;
            }
        }
        eidos_lock_release(&ch->recv_waiters.lock);

        if (w) {
            /* Extract real continuation from the retry trampoline. */
            EidosRecvRetry* retry = (EidosRecvRetry*)w->work.closure_ptr;
            EidosWorkItem real_cont = retry->continuation;

            /* Balance the incref done in recv's suspend path. */
            eidos_decref_shared(ch);
            free(retry);
            free(w);

            /* Schedule receiver with value */
            EidosWorkItem recv_done;
            recv_done.invoke_fn   = real_cont.invoke_fn;
            recv_done.closure_ptr = real_cont.closure_ptr;
            recv_done.arg         = value;
            eidos_schedule(recv_done);

            /* Sender succeeds */
            EidosWorkItem done;
            done.invoke_fn   = continuation.invoke_fn;
            done.closure_ptr = continuation.closure_ptr;
            done.arg         = NULL;
            eidos_schedule(done);
            return;
        }

        /*
         * Race: waiter was consumed between our is_empty check and
         * acquiring recv_waiters.lock. Undo the incref and fall through
         * to buffered/suspend path. Re-acquire ch_lock.
         */
        channel_release_value(ch, value);
        eidos_lock_acquire(&ch->ch_lock);

        /* Fall through to buffered / suspend path below. */
    }

    /* ---- Buffered path: try ring buffer ---- */
    if (ch->capacity > 0 && ch->buf_count < ch->capacity) {
        /* Store value, acquiring a shared reference for buffer ownership. */
        channel_retain_value(ch, value);
        buffer_put(ch, value);

        eidos_lock_release(&ch->ch_lock);

        /* Wake one suspended receiver (retry trampoline finds data in buffer). */
        eidos_waitqueue_wake_one(&ch->recv_waiters);

        /* Sender succeeds */
        EidosWorkItem done;
        done.invoke_fn   = continuation.invoke_fn;
        done.closure_ptr = continuation.closure_ptr;
        done.arg         = NULL;
        eidos_schedule(done);
        return;
    }

    /* ---- Suspend: no room, buffer full or rendezvous ---- */
    /* Store value in pending sends (with shared incref). */
    channel_retain_value(ch, value);
    pending_push(ch, value);

    eidos_lock_release(&ch->ch_lock);

    /* Push sender continuation to send_waiters. */
    eidos_waitqueue_push(&ch->send_waiters, continuation);

    /* Sender is now suspended. A receiver will wake it. */
}

bool eidos_channel_try_send(struct EidosChannel* ch, void* value) {
    if (!ch) return false;

    eidos_lock_acquire(&ch->ch_lock);

    if (ECH_ATOMIC_LOAD32((volatile int32_t*)&ch->closed)) {
        eidos_lock_release(&ch->ch_lock);
        return false;
    }

    if (!eidos_waitqueue_is_empty(&ch->recv_waiters)) {
        channel_retain_value(ch, value);

        eidos_lock_release(&ch->ch_lock);

        eidos_lock_acquire(&ch->recv_waiters.lock);
        EidosWaiter* w = ch->recv_waiters.head;
        if (w) {
            ch->recv_waiters.head = w->next;
            if (!ch->recv_waiters.head) {
                ch->recv_waiters.tail = NULL;
            }
        }
        eidos_lock_release(&ch->recv_waiters.lock);

        if (w) {
            EidosRecvRetry* retry = (EidosRecvRetry*)w->work.closure_ptr;
            EidosWorkItem real_cont = retry->continuation;

            eidos_decref_shared(ch);
            free(retry);
            free(w);

            EidosWorkItem recv_done;
            recv_done.invoke_fn   = real_cont.invoke_fn;
            recv_done.closure_ptr = real_cont.closure_ptr;
            recv_done.arg         = value;
            eidos_schedule(recv_done);
            return true;
        }

        channel_release_value(ch, value);

        eidos_lock_acquire(&ch->ch_lock);
        if (ECH_ATOMIC_LOAD32((volatile int32_t*)&ch->closed)) {
            eidos_lock_release(&ch->ch_lock);
            return false;
        }
    }

    if (ch->capacity > 0 && ch->buf_count < ch->capacity) {
        channel_retain_value(ch, value);
        buffer_put(ch, value);

        eidos_lock_release(&ch->ch_lock);
        eidos_waitqueue_wake_one(&ch->recv_waiters);
        return true;
    }

    eidos_lock_release(&ch->ch_lock);
    return false;
}

/**
 * Async receive: dequeue a value from the channel.
 *
 * Outcomes (all schedule the continuation):
 *   1. Pending sender exists: take pending value, wake sender,
 *      continuation(arg = value)
 *   2. Buffer has data: take from buffer, wake one sender,
 *      continuation(arg = value)
 *   3. Closed and empty: continuation(arg = closed_sentinel)
 *   4. Open but empty: create retry trampoline, push to recv_waiters.
 *      Receiver is suspended until a sender provides data.
 *
 * @param ch           The channel (must not be NULL)
 * @param continuation Receiver's continuation (scheduled with value or sentinel)
 */
void eidos_channel_recv(struct EidosChannel* ch, EidosWorkItem continuation) {
    if (!ch) return;

    eidos_lock_acquire(&ch->ch_lock);

    /* ---- Check pending sends first (suspended senders) ---- */
    if (ch->pending_head) {
        void* value = pending_pop(ch);

        eidos_lock_release(&ch->ch_lock);

        /* Wake one sender (their value was consumed). */
        eidos_waitqueue_wake_one(&ch->send_waiters);

        /* Schedule receiver with value (receiver inherits the pending ref). */
        EidosWorkItem done;
        done.invoke_fn   = continuation.invoke_fn;
        done.closure_ptr = continuation.closure_ptr;
        done.arg         = value;
        eidos_schedule(done);
        return;
    }

    /* ---- Check ring buffer ---- */
    if (ch->buf_count > 0) {
        /* buffer_take returns the value; receiver inherits buffer's ref. */
        void* value = buffer_take(ch);

        eidos_lock_release(&ch->ch_lock);

        /* Wake one sender (buffer now has a free slot). */
        eidos_waitqueue_wake_one(&ch->send_waiters);

        /* Schedule receiver with value. */
        EidosWorkItem done;
        done.invoke_fn   = continuation.invoke_fn;
        done.closure_ptr = continuation.closure_ptr;
        done.arg         = value;
        eidos_schedule(done);
        return;
    }

    /* ---- Buffer empty, no pending sends ---- */
    if (ECH_ATOMIC_LOAD32((volatile int32_t*)&ch->closed)) {
        eidos_lock_release(&ch->ch_lock);

        /* Channel closed and empty: return closed sentinel. */
        EidosWorkItem done;
        done.invoke_fn   = continuation.invoke_fn;
        done.closure_ptr = continuation.closure_ptr;
        done.arg         = eidos_channel_closed_sentinel();
        eidos_schedule(done);
        return;
    }

    eidos_lock_release(&ch->ch_lock);

    /* ---- Suspend receiver ----
     *
     * Allocate a retry trampoline. When a sender wakes this receiver,
     * the trampoline re-invokes eidos_channel_recv, which finds the data.
     *
     * For rendezvous sends: the sender pops the waiter directly and
     * extracts the real continuation from the trampoline, bypassing the
     * retry entirely.
     *
     * For buffered sends: wake_one schedules the trampoline, which
     * re-enters recv and picks up the value from the buffer.
     */
    EidosRecvRetry* retry = (EidosRecvRetry*)malloc(sizeof(EidosRecvRetry));
    if (!retry) {
        fprintf(stderr, "eidos_channel_recv: retry alloc failed\n");
        EidosWorkItem done;
        done.invoke_fn   = continuation.invoke_fn;
        done.closure_ptr = continuation.closure_ptr;
        done.arg         = eidos_channel_closed_sentinel();
        eidos_schedule(done);
        return;
    }
    retry->ch           = ch;
    retry->continuation = continuation;

    EidosWorkItem retry_work;
    retry_work.invoke_fn   = recv_retry_trampoline;
    retry_work.closure_ptr = retry;
    retry_work.arg         = NULL;

    /*
     * Incref channel to keep it alive until the retry fires.
     * Balanced by decref in recv_retry_trampoline or when the sender
     * directly extracts the retry (in eidos_channel_send rendezvous path).
     */
    eidos_incref_shared(ch);

    eidos_waitqueue_push(&ch->recv_waiters, retry_work);

    eidos_lock_acquire(&ch->ch_lock);
    if (ch->pending_head) {
        EidosWorkItem removed;
        if (eidos_waitqueue_try_remove(&ch->recv_waiters, retry_work, &removed)) {
            void* value = pending_pop(ch);
            eidos_lock_release(&ch->ch_lock);

            eidos_decref_shared(ch);
            free(retry);
            eidos_waitqueue_wake_one(&ch->send_waiters);

            EidosWorkItem done;
            done.invoke_fn   = continuation.invoke_fn;
            done.closure_ptr = continuation.closure_ptr;
            done.arg         = value;
            eidos_schedule(done);
            return;
        }
    }
    eidos_lock_release(&ch->ch_lock);
}

void* eidos_channel_try_recv(struct EidosChannel* ch) {
    if (!ch) return NULL;

    eidos_lock_acquire(&ch->ch_lock);

    if (ch->pending_head) {
        void* value = pending_pop(ch);

        eidos_lock_release(&ch->ch_lock);
        eidos_waitqueue_wake_one(&ch->send_waiters);
        return value;
    }

    if (ch->buf_count > 0) {
        void* value = buffer_take(ch);

        eidos_lock_release(&ch->ch_lock);
        eidos_waitqueue_wake_one(&ch->send_waiters);
        return value;
    }

    if (ECH_ATOMIC_LOAD32((volatile int32_t*)&ch->closed)) {
        eidos_lock_release(&ch->ch_lock);
        return eidos_channel_closed_sentinel();
    }

    eidos_lock_release(&ch->ch_lock);
    return NULL;
}

/**
 * Close a channel.
 *
 * Sets the closed flag atomically. Wakes all suspended senders and receivers:
 *   - Senders are scheduled with the closed sentinel as their arg,
 *     indicating the send failed.
 *   - Receivers' retry trampolines are scheduled normally. When the
 *     trampoline re-enters recv, it finds the channel closed and delivers
 *     the sentinel.
 *
 * If the channel has buffered data or pending sends, those values are
 * released (shared decref). Subsequent recv calls on a closed channel
 * with remaining data can still succeed until all data is consumed.
 *
 * @param ch  The channel to close (must not be NULL)
 */
void eidos_channel_close(struct EidosChannel* ch) {
    if (!ch) return;

    eidos_lock_acquire(&ch->ch_lock);

    /* Set closed flag. */
    ECH_ATOMIC_STORE32((volatile int32_t*)&ch->closed, 1);

    eidos_lock_release(&ch->ch_lock);

    /*
     * Wake all suspended senders with the closed sentinel.
     * We directly dequeue from send_waiters and schedule each
     * continuation with the sentinel as arg.
     */
    eidos_lock_acquire(&ch->send_waiters.lock);
    EidosWaiter* sw = ch->send_waiters.head;
    ch->send_waiters.head = NULL;
    ch->send_waiters.tail = NULL;
    eidos_lock_release(&ch->send_waiters.lock);

    while (sw) {
        EidosWaiter* next = sw->next;
        EidosWorkItem done;
        done.invoke_fn   = sw->work.invoke_fn;
        done.closure_ptr = sw->work.closure_ptr;
        done.arg         = eidos_channel_closed_sentinel();
        eidos_schedule(done);
        free(sw);
        sw = next;
    }

    /*
     * Release pending send values. The senders have been woken with
     * the closed sentinel, so their values are orphaned. Release the
     * shared references acquired when the pending sends were created.
     */
    eidos_lock_acquire(&ch->ch_lock);
    pending_drain_all_locked(ch);
    eidos_lock_release(&ch->ch_lock);

    /*
     * Wake all suspended receivers (retry trampolines).
     * The trampolines will re-enter recv, find the channel closed
     * and empty, and deliver the closed sentinel.
     */
    eidos_waitqueue_wake_all(&ch->recv_waiters);
}

/**
 * Destructor for EidosChannel (registered for EIDOS_TYPE_CHANNEL).
 *
 * Releases all resources owned by the channel:
 *   - Pending send values (shared decref)
 *   - Ring buffer contents (shared decref)
 *   - Ring buffer array (free)
 *   - Wait queues (destroy, freeing remaining waiter nodes)
 *
 * Called by the memory system when the channel's ref_count reaches zero.
 * The channel should already be closed before destruction.
 *
 * @param ptr  Pointer to the EidosChannel (after header)
 */
static void eidos_channel_destructor(void* ptr) {
    EidosChannel* ch = (EidosChannel*)ptr;
    if (!ch) return;

    /* Drain pending sends (releases shared refs on values). */
    eidos_lock_acquire(&ch->ch_lock);
    pending_drain_all_locked(ch);
    buffer_drain_all_locked(ch);
    eidos_lock_release(&ch->ch_lock);

    /* Free ring buffer array. */
    if (ch->buffer) {
        free(ch->buffer);
        ch->buffer = NULL;
    }

    /* Destroy wait queues (frees remaining waiter nodes). */
    eidos_waitqueue_destroy(&ch->send_waiters);
    eidos_waitqueue_destroy(&ch->recv_waiters);

    /* Note: SRWLOCK on Windows does not need explicit destruction.
     * pthread_mutex_t would need pthread_mutex_destroy, but we skip
     * it for simplicity since the entire struct is about to be freed. */
}
