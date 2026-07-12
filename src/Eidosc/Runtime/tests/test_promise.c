/**
 * Eidos Promise/Future Unit Tests
 *
 * Tests the one-shot Promise/Future pair (eidos_promise.c) with
 * fulfill-then-await (fast path), await-then-fulfill (slow path),
 * and double-fulfill rejection scenarios.
 *
 * Build (gcc/clang on Windows):
 *   gcc -o test_promise.exe test_promise.c ../eidos_promise.c ../eidos_task.c ../eidos_scheduler.c ../eidos_memory.c -I.. -Wall -Wextra
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
 * Value Factory
 *
 * Promise stores values via eidos_incref_shared, so every fulfilled
 * value must be an Eidos-allocated shared object.
 * ============================================================ */

static void* make_test_value(int64_t v) {
    int64_t* obj = (int64_t*)eidos_alloc(sizeof(int64_t), EIDOS_TYPE_INT);
    if (!obj) return NULL;
    *obj = v;
    eidos_share(obj);
    return obj;
}

/* ============================================================
 * Test 1: Fulfill then Await (Fast Path)
 *
 * Create promise/future. Fulfill with a value. Then await the
 * future. The await should hit the fast path (already fulfilled)
 * and schedule the continuation immediately with the value.
 * ============================================================ */

static volatile TEST_ATOMIC_LONG g_fast_await_fired = 0;
static void* g_fast_await_arg = NULL;

static void* fast_await_cont_fn(void* closure, void* arg) {
    (void)closure;
    g_fast_await_arg = arg;
    TEST_ATOMIC_INC(&g_fast_await_fired);
    return NULL;
}

static int test_promise_fulfill_then_await(void) {
    TEST_ATOMIC_SET(&g_fast_await_fired, 0);
    g_fast_await_arg = NULL;

    eidos_scheduler_init(2);

    struct EidosPromise* promise = NULL;
    struct EidosFuture*  future  = NULL;
    eidos_promise_new(&promise, &future);
    ASSERT(promise != NULL, "promise is non-NULL");
    ASSERT(future  != NULL, "future is non-NULL");

    /* Create a shared value. */
    void* val = make_test_value(42);
    ASSERT(val != NULL, "make_test_value returned non-NULL");

    /* Fulfill the promise first. */
    int rc = eidos_promise_fulfill(promise, val);
    ASSERT(rc == 1, "first fulfill returns 1 (success)");

    /* Now await the future -- should hit fast path. */
    EidosWorkItem cont = {0};
    cont.invoke_fn   = fast_await_cont_fn;
    cont.closure_ptr = NULL;
    cont.arg         = NULL;
    eidos_future_await(future, cont);

    int ok = wait_for_counter(&g_fast_await_fired, 1, 3000);
    ASSERT(ok, "await continuation fired (fast path)");
    ASSERT(g_fast_await_arg == val,
           "await received the fulfilled value");

    eidos_decref_shared(val);
    eidos_decref_shared(promise);
    eidos_decref_shared(future);
    eidos_scheduler_shutdown();
    return 0;
}

/* ============================================================
 * Test 2: Await then Fulfill (Slow Path)
 *
 * Create promise/future. Await the future first (it will suspend
 * because the promise is still pending). Then fulfill the promise
 * from a scheduled task. The suspended awaiter should be woken
 * and receive the value.
 * ============================================================ */

static volatile TEST_ATOMIC_LONG g_slow_await_fired = 0;
static void* g_slow_await_arg = NULL;

static void* slow_await_cont_fn(void* closure, void* arg) {
    (void)closure;
    g_slow_await_arg = arg;
    TEST_ATOMIC_INC(&g_slow_await_fired);
    return NULL;
}

/**
 * Scheduled task that fulfills the promise.
 * closure_ptr points to a struct containing promise + value.
 */
typedef struct {
    struct EidosPromise* promise;
    void*                value;
} FulfillPayload;

static void* fulfill_task_fn(void* closure, void* arg) {
    (void)arg;
    FulfillPayload* p = (FulfillPayload*)closure;
    eidos_promise_fulfill(p->promise, p->value);
    return NULL;
}

static int test_promise_await_then_fulfill(void) {
    TEST_ATOMIC_SET(&g_slow_await_fired, 0);
    g_slow_await_arg = NULL;

    eidos_scheduler_init(2);

    struct EidosPromise* promise = NULL;
    struct EidosFuture*  future  = NULL;
    eidos_promise_new(&promise, &future);
    ASSERT(promise != NULL, "promise is non-NULL");
    ASSERT(future  != NULL, "future is non-NULL");

    void* val = make_test_value(77);
    ASSERT(val != NULL, "make_test_value returned non-NULL");

    /* Await the future first -- it will suspend. */
    EidosWorkItem cont = {0};
    cont.invoke_fn   = slow_await_cont_fn;
    cont.closure_ptr = NULL;
    cont.arg         = NULL;
    eidos_future_await(future, cont);

    /* Schedule a task to fulfill the promise. */
    static FulfillPayload payload;
    payload.promise = promise;
    payload.value   = val;

    EidosWorkItem fulfill_work = {0};
    fulfill_work.invoke_fn   = fulfill_task_fn;
    fulfill_work.closure_ptr = &payload;
    fulfill_work.arg         = NULL;
    eidos_schedule(fulfill_work);

    int ok = wait_for_counter(&g_slow_await_fired, 1, 3000);
    ASSERT(ok, "await continuation fired after fulfill (slow path)");
    ASSERT(g_slow_await_arg == val,
           "await received the fulfilled value");

    eidos_decref_shared(val);
    eidos_decref_shared(promise);
    eidos_decref_shared(future);
    eidos_scheduler_shutdown();
    return 0;
}

/* ============================================================
 * Test 3: Double Fulfill
 *
 * Fulfill a promise twice. The first call should return 1 (success),
 * the second should return 0 (already fulfilled).
 * ============================================================ */

static int test_promise_double_fulfill(void) {
    eidos_scheduler_init(2);

    struct EidosPromise* promise = NULL;
    struct EidosFuture*  future  = NULL;
    eidos_promise_new(&promise, &future);
    ASSERT(promise != NULL, "promise is non-NULL");

    void* val1 = make_test_value(10);
    void* val2 = make_test_value(20);
    ASSERT(val1 != NULL && val2 != NULL, "make_test_value returned non-NULL");

    int rc1 = eidos_promise_fulfill(promise, val1);
    ASSERT(rc1 == 1, "first fulfill returns 1");

    int rc2 = eidos_promise_fulfill(promise, val2);
    ASSERT(rc2 == 0, "second fulfill returns 0 (already fulfilled)");

    eidos_decref_shared(val1);
    eidos_decref_shared(val2);
    eidos_decref_shared(promise);
    eidos_decref_shared(future);
    eidos_scheduler_shutdown();
    return 0;
}

/* ============================================================
 * Main
 * ============================================================ */

int main(void) {
    printf("=== Eidos Promise/Future Tests ===\n\n");

    RUN_TEST(test_promise_fulfill_then_await);
    RUN_TEST(test_promise_await_then_fulfill);
    RUN_TEST(test_promise_double_fulfill);

    printf("\n--- Results: %d run, %d passed, %d failed ---\n",
           g_tests_run, g_tests_passed, g_tests_failed);

    return (g_tests_failed > 0) ? 1 : 0;
}
