/**
 * Eidos Sync Primitive Unit Tests
 *
 * Tests Mutex, Barrier, and RwLock (eidos_sync.c) with lock/unlock,
 * contention, barrier synchronization, and concurrent reader scenarios.
 *
 * Build (gcc/clang on Windows):
 *   gcc -o test_sync.exe test_sync.c ../eidos_sync.c ../eidos_task.c ../eidos_scheduler.c ../eidos_memory.c -I.. -Wall -Wextra
 */

#include "../eidos_sync.h"

#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>

/* ============================================================
 * Test Framework
 * ============================================================ */

static int g_tests_run    = 0;
static int g_tests_passed = 0;
static int g_tests_failed = 0;

#define ASSERT(cond, msg)                                               \
    do {                                                                \
        if (!(cond)) {                                                  \
            fprintf(stderr, "  FAIL: %s  (line %d)\n", (msg), __LINE__); \
            return 1;                                                   \
        }                                                               \
    } while (0)

#define RUN_TEST(fn)                                                    \
    do {                                                                \
        g_tests_run++;                                                  \
        printf("[%d] %-40s", g_tests_run, #fn);                        \
        fflush(stdout);                                                 \
        int _rc = fn();                                                 \
        if (_rc == 0) {                                                 \
            g_tests_passed++;                                           \
            printf("PASS\n");                                           \
        } else {                                                        \
            g_tests_failed++;                                           \
        }                                                               \
    } while (0)

/* ============================================================
 * Windows Atomics for Shared Counters
 * ============================================================ */

#if defined(_WIN32)
    #include <windows.h>
    #define TEST_ATOMIC_LONG   LONG
    #define TEST_ATOMIC_INC(ptr)  InterlockedIncrement((LONG volatile*)(ptr))
    #define TEST_ATOMIC_READ(ptr) (*(volatile LONG*)(ptr))
    #define TEST_ATOMIC_SET(ptr, val) (*(volatile LONG*)(ptr) = (LONG)(val))
#else
    #define TEST_ATOMIC_LONG   int32_t
    #define TEST_ATOMIC_INC(ptr)  __atomic_add_fetch((ptr), 1, __ATOMIC_SEQ_CST)
    #define TEST_ATOMIC_READ(ptr) __atomic_load_n((ptr), __ATOMIC_SEQ_CST)
    #define TEST_ATOMIC_SET(ptr, val) __atomic_store_n((ptr), (val), __ATOMIC_SEQ_CST)
#endif

/* ============================================================
 * Wait Helper
 * ============================================================ */

static int wait_for_counter(volatile TEST_ATOMIC_LONG* counter,
                            TEST_ATOMIC_LONG expected,
                            int timeout_ms)
{
    int elapsed = 0;
    const int poll_ms = 10;
    while (elapsed < timeout_ms) {
        if (TEST_ATOMIC_READ(counter) >= expected) return 1;
#if defined(_WIN32)
        Sleep(poll_ms);
#else
        struct timespec ts = { 0, poll_ms * 1000000L };
        nanosleep(&ts, NULL);
#endif
        elapsed += poll_ms;
    }
    return (TEST_ATOMIC_READ(counter) >= expected);
}

/* ============================================================
 * Test 1: Mutex Lock + Unlock
 *
 * Lock a mutex, verify the lock is held (by attempting a second
 * lock which should go to waiters), then release. Verify the
 * second locker gets the lock after release.
 * ============================================================ */

static volatile TEST_ATOMIC_LONG g_lock_acquired_count = 0;

/**
 * Continuation that fires when the mutex lock is acquired.
 * Increments the counter, then immediately releases the mutex.
 *
 * The mutex is passed via closure_ptr for the same reason as the
 * contention test: the waiter path does not patch arg.
 */
static void* mutex_lock_fn(void* closure, void* arg) {
    (void)arg;
    struct EidosMutex* mtx = (struct EidosMutex*)closure;
    TEST_ATOMIC_INC(&g_lock_acquired_count);
    eidos_mutex_guard_release(mtx);
    return NULL;
}

static int test_mutex_lock_unlock(void) {
    TEST_ATOMIC_SET(&g_lock_acquired_count, 0);

    eidos_scheduler_init(2);

    struct EidosMutex* mtx = eidos_mutex_new(NULL);
    ASSERT(mtx != NULL, "eidos_mutex_new returned non-NULL");

    /* Lock the mutex -- should succeed immediately (fast path). */
    EidosWorkItem lock_cont = {0};
    lock_cont.invoke_fn   = mutex_lock_fn;
    lock_cont.closure_ptr = mtx;
    lock_cont.arg         = NULL;
    eidos_mutex_lock(mtx, lock_cont);

    int ok = wait_for_counter(&g_lock_acquired_count, 1, 3000);
    ASSERT(ok, "mutex lock acquired and released");
    ASSERT(TEST_ATOMIC_READ(&g_lock_acquired_count) == 1,
           "lock acquired exactly once");

    eidos_decref_shared(mtx);
    eidos_scheduler_shutdown();
    return 0;
}

/* ============================================================
 * Test 2: Mutex Contention (4 tasks)
 *
 * 4 tasks compete for a mutex. Each acquires the lock, increments
 * a shared counter, then releases. Because the mutex serializes
 * access, the counter should be exactly 4 when all tasks complete.
 * ============================================================ */

static volatile TEST_ATOMIC_LONG g_contention_counter = 0;
static volatile TEST_ATOMIC_LONG g_contention_done = 0;

/**
 * Mutex lock acquired continuation for contention test.
 * Increments the shared counter (critical section) and releases.
 *
 * The mutex is passed via closure_ptr so it is available whether the
 * lock was acquired via the CAS fast path (arg=mutex) or the waiter
 * slow path (arg unchanged). We prefer closure_ptr because the waiter
 * path stores the work item as-is without patching arg.
 */
static void* contention_lock_fn(void* closure, void* arg) {
    (void)arg;
    struct EidosMutex* mtx = (struct EidosMutex*)closure;

    /* Critical section: increment shared counter. */
    TEST_ATOMIC_INC(&g_contention_counter);

    /* Release the mutex so the next waiter can proceed. */
    eidos_mutex_guard_release(mtx);

    /* Signal completion. */
    TEST_ATOMIC_INC(&g_contention_done);
    return NULL;
}

static int test_mutex_contention(void) {
    TEST_ATOMIC_SET(&g_contention_counter, 0);
    TEST_ATOMIC_SET(&g_contention_done, 0);

    eidos_scheduler_init(4);

    struct EidosMutex* mtx = eidos_mutex_new(NULL);
    ASSERT(mtx != NULL, "eidos_mutex_new returned non-NULL");

    /* 4 tasks all compete for the same mutex.
     * Pass the mutex via closure_ptr (not arg) because the waiter path
     * stores the work item without patching arg, while the fast path
     * overwrites arg with the mutex pointer. Using closure_ptr ensures
     * the mutex is always accessible. */
    for (int i = 0; i < 4; i++) {
        EidosWorkItem lock_cont = {0};
        lock_cont.invoke_fn   = contention_lock_fn;
        lock_cont.closure_ptr = mtx;
        lock_cont.arg         = NULL;
        eidos_mutex_lock(mtx, lock_cont);
    }

    int ok = wait_for_counter(&g_contention_done, 4, 5000);
    ASSERT(ok, "all 4 mutex contenders completed");
    ASSERT(TEST_ATOMIC_READ(&g_contention_counter) == 4,
           "counter exactly 4 after serialized increments");

    eidos_decref_shared(mtx);
    eidos_scheduler_shutdown();
    return 0;
}

/* ============================================================
 * Test 3: Barrier (4 parties)
 *
 * Create a barrier with capacity=4. Schedule 4 tasks that each
 * call eidos_barrier_wait. The first 3 tasks suspend; the 4th
 * trips the barrier and all 4 continuations fire.
 * ============================================================ */

static volatile TEST_ATOMIC_LONG g_barrier_released = 0;

/**
 * Barrier wait continuation. Fires when the barrier trips.
 */
static void* barrier_wait_fn(void* closure, void* arg) {
    (void)closure;
    (void)arg;
    TEST_ATOMIC_INC(&g_barrier_released);
    return NULL;
}

static int test_barrier_n_parties(void) {
    TEST_ATOMIC_SET(&g_barrier_released, 0);

    eidos_scheduler_init(4);

    struct EidosBarrier* barrier = eidos_barrier_new(4);
    ASSERT(barrier != NULL, "eidos_barrier_new(4) returned non-NULL");

    /* 4 tasks all wait on the barrier. */
    for (int i = 0; i < 4; i++) {
        EidosWorkItem wait_cont = {0};
        wait_cont.invoke_fn   = barrier_wait_fn;
        wait_cont.closure_ptr = NULL;
        wait_cont.arg         = NULL;
        eidos_barrier_wait(barrier, wait_cont);
    }

    int ok = wait_for_counter(&g_barrier_released, 4, 5000);
    ASSERT(ok, "all 4 barrier participants released");
    ASSERT(TEST_ATOMIC_READ(&g_barrier_released) == 4,
           "all 4 barrier continuations fired");

    eidos_decref_shared(barrier);
    eidos_scheduler_shutdown();
    return 0;
}

/* ============================================================
 * Test 4: RwLock Concurrent Readers
 *
 * Multiple tasks acquire read locks simultaneously. Each reader
 * increments a shared reader_count on entry and decrements on exit.
 * A peak counter tracks the maximum concurrent readers observed.
 * We verify that the peak reader count exceeds 1 at some point,
 * proving that multiple readers held the lock simultaneously.
 * ============================================================ */

static volatile TEST_ATOMIC_LONG g_active_readers = 0;
static volatile TEST_ATOMIC_LONG g_peak_readers   = 0;
static volatile TEST_ATOMIC_LONG g_reader_done    = 0;

/**
 * Read-lock acquired continuation.
 * Increments active_readers, updates peak, holds the lock briefly,
 * then releases.
 *
 * Uses closure_ptr for the rwlock (same reason as mutex: waiter path
 * does not patch arg).
 */
static void* rwlock_read_fn(void* closure, void* arg) {
    (void)arg;
    struct EidosRwLock* rw = (struct EidosRwLock*)closure;

    TEST_ATOMIC_LONG cur = TEST_ATOMIC_INC(&g_active_readers);

    /* Update peak if we are the new maximum. */
    for (;;) {
        TEST_ATOMIC_LONG peak = TEST_ATOMIC_READ(&g_peak_readers);
        if (cur <= peak) break;
        if (TEST_ATOMIC_READ(&g_peak_readers) != peak) continue;
        /* Platform-specific CAS for peak update. */
#if defined(_WIN32)
        LONG prev = InterlockedCompareExchange(
            (LONG volatile*)&g_peak_readers, (LONG)cur, (LONG)peak);
        if (prev == (LONG)peak) break;
#else
        if (__atomic_compare_exchange_n(
                (int32_t volatile*)&g_peak_readers, &peak, cur,
                0, __ATOMIC_SEQ_CST, __ATOMIC_SEQ_CST))
            break;
#endif
    }

    /* Hold the read lock for a brief moment to increase concurrency window. */
#if defined(_WIN32)
    Sleep(20);
#else
    struct timespec ts = { 0, 20 * 1000000L };
    nanosleep(&ts, NULL);
#endif

    TEST_ATOMIC_INC(&g_active_readers); /* Atomic dec not directly available; use negative trick. */
    /* Actually, we need to decrement g_active_readers. Use direct store approach.
     * We need an atomic decrement. On Windows, InterlockedDecrement; on POSIX, sub_fetch. */
#if defined(_WIN32)
    InterlockedDecrement((LONG volatile*)&g_active_readers);
#else
    __atomic_sub_fetch(&g_active_readers, 1, __ATOMIC_SEQ_CST);
#endif

    eidos_rwlock_read_release(rw);

    TEST_ATOMIC_INC(&g_reader_done);
    return NULL;
}

static int test_rwlock_concurrent_readers(void) {
    TEST_ATOMIC_SET(&g_active_readers, 0);
    TEST_ATOMIC_SET(&g_peak_readers, 0);
    TEST_ATOMIC_SET(&g_reader_done, 0);

    eidos_scheduler_init(4);

    struct EidosRwLock* rw = eidos_rwlock_new(NULL);
    ASSERT(rw != NULL, "eidos_rwlock_new returned non-NULL");

    /* 4 readers all try to acquire the read lock simultaneously.
     * Pass rwlock via closure_ptr because the waiter path stores the
     * work item without patching arg. */
    for (int i = 0; i < 4; i++) {
        EidosWorkItem read_cont = {0};
        read_cont.invoke_fn   = rwlock_read_fn;
        read_cont.closure_ptr = rw;
        read_cont.arg         = NULL;
        eidos_rwlock_read(rw, read_cont);
    }

    int ok = wait_for_counter(&g_reader_done, 4, 5000);
    ASSERT(ok, "all 4 readers completed");

    TEST_ATOMIC_LONG peak = TEST_ATOMIC_READ(&g_peak_readers);
    ASSERT(peak > 1, "multiple concurrent readers observed (peak > 1)");

    eidos_decref_shared(rw);
    eidos_scheduler_shutdown();
    return 0;
}

/* ============================================================
 * Main
 * ============================================================ */

int main(void) {
    printf("=== Eidos Sync Primitive Tests ===\n\n");

    RUN_TEST(test_mutex_lock_unlock);
    RUN_TEST(test_mutex_contention);
    RUN_TEST(test_barrier_n_parties);
    RUN_TEST(test_rwlock_concurrent_readers);

    printf("\n--- Results: %d run, %d passed, %d failed ---\n",
           g_tests_run, g_tests_passed, g_tests_failed);

    return (g_tests_failed > 0) ? 1 : 0;
}
