/**
 * Eidos Scheduler Unit Tests
 *
 * Tests the work-stealing scheduler (eidos_scheduler.c) using a simple
 * hand-rolled assertion framework. Compile together with eidos_scheduler.c.
 *
 * Build (MSVC):
 *   cl /Fe:test_scheduler.exe test_scheduler.c ../eidos_scheduler.c /I..
 *
 * Build (gcc/clang on Windows):
 *   gcc -o test_scheduler.exe test_scheduler.c ../eidos_scheduler.c -I..
 */

#include "../eidos_sync.h"

#include <stdio.h>
#include <stdlib.h>

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
            /* already printed FAIL inside fn */                        \
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
 * Test Helpers
 * ============================================================ */

/**
 * Work function: increments an atomic counter pointed to by closure_ptr.
 * arg is unused.
 */
static void* test_increment_fn(void* closure, void* arg) {
    (void)arg;
    TEST_ATOMIC_INC((TEST_ATOMIC_LONG*)closure);
    return NULL;
}

/**
 * Busy-wait poll until a counter reaches the expected value,
 * or timeout_ms elapses. Returns 1 if the value was reached, 0 on timeout.
 */
static int wait_for_counter(volatile TEST_ATOMIC_LONG* counter,
                            TEST_ATOMIC_LONG expected,
                            int timeout_ms)
{
    int elapsed = 0;
    const int poll_ms = 10;
    while (elapsed < timeout_ms) {
        if (TEST_ATOMIC_READ(counter) >= expected) {
            return 1;
        }
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
 * Test 1: Init and Shutdown
 * ============================================================ */

static int test_init_shutdown(void) {
    eidos_scheduler_init(2);
    /* Let workers spin up briefly. */
#if defined(_WIN32)
    Sleep(50);
#else
    struct timespec ts = { 0, 50000000 };
    nanosleep(&ts, NULL);
#endif
    eidos_scheduler_shutdown();

    /* Should not hang or crash. */
    ASSERT(1, "init+shutdown completed without crash");
    return 0;
}

/* ============================================================
 * Test 2: Single Item
 * ============================================================ */

static TEST_ATOMIC_LONG g_single_counter = 0;

static int test_single_item(void) {
    TEST_ATOMIC_SET(&g_single_counter, 0);

    eidos_scheduler_init(2);

    EidosWorkItem item = {0};
    item.invoke_fn   = test_increment_fn;
    item.closure_ptr = &g_single_counter;
    item.arg         = NULL;
    eidos_schedule(item);

    int ok = wait_for_counter(&g_single_counter, 1, 2000);
    eidos_scheduler_shutdown();

    ASSERT(ok, "counter reached 1");
    ASSERT(TEST_ATOMIC_READ(&g_single_counter) == 1,
           "counter exactly 1 after single item");
    return 0;
}

/* ============================================================
 * Test 3: Many Items (1000)
 * ============================================================ */

static TEST_ATOMIC_LONG g_many_counter = 0;

static int test_many_items(void) {
    const int N = 1000;
    TEST_ATOMIC_SET(&g_many_counter, 0);

    eidos_scheduler_init(4);

    for (int i = 0; i < N; i++) {
        EidosWorkItem item = {0};
        item.invoke_fn   = test_increment_fn;
        item.closure_ptr = &g_many_counter;
        item.arg         = NULL;
        eidos_schedule(item);
    }

    int ok = wait_for_counter(&g_many_counter, N, 5000);
    eidos_scheduler_shutdown();

    ASSERT(ok, "counter reached 1000");
    ASSERT(TEST_ATOMIC_READ(&g_many_counter) == N,
           "counter exactly 1000 after many items");
    return 0;
}

/* ============================================================
 * Test 4: Worker Index
 * ============================================================ */

/**
 * Work function that checks eidos_worker_index() and records
 * whether it was valid (not UINT32_MAX) into a flag.
 * closure_ptr points to a TEST_ATOMIC_LONG used as a boolean flag.
 */
static void* test_check_worker_index_fn(void* closure, void* arg) {
    (void)arg;
    uint32_t idx = eidos_worker_index();
    if (idx != UINT32_MAX) {
        TEST_ATOMIC_SET((TEST_ATOMIC_LONG*)closure, 1);
    }
    return NULL;
}

static int test_worker_index(void) {
    TEST_ATOMIC_LONG valid_flag = 0;

    eidos_scheduler_init(2);

    EidosWorkItem item = {0};
    item.invoke_fn   = test_check_worker_index_fn;
    item.closure_ptr = &valid_flag;
    item.arg         = NULL;
    eidos_schedule(item);

    int ok = wait_for_counter(&valid_flag, 1, 2000);
    eidos_scheduler_shutdown();

    ASSERT(ok, "worker index flag was set");
    ASSERT(TEST_ATOMIC_READ(&valid_flag) == 1,
           "eidos_worker_index() returned valid index inside work item");
    return 0;
}

/* ============================================================
 * Test 5: Work Stealing
 *
 * Schedule 100 items from a non-worker thread (the main thread).
 * Since the main thread is not a worker, all items go to the
 * global queue. Workers must pull from the global queue and
 * steal from each other. Verifies all 100 items execute.
 * ============================================================ */

static TEST_ATOMIC_LONG g_steal_counter = 0;

static int test_work_stealing(void) {
    const int N = 100;
    TEST_ATOMIC_SET(&g_steal_counter, 0);

    eidos_scheduler_init(4);

    /* Main thread is not a worker, so all items go to global queue. */
    for (int i = 0; i < N; i++) {
        EidosWorkItem item = {0};
        item.invoke_fn   = test_increment_fn;
        item.closure_ptr = &g_steal_counter;
        item.arg         = NULL;
        eidos_schedule(item);
    }

    int ok = wait_for_counter(&g_steal_counter, N, 5000);
    eidos_scheduler_shutdown();

    ASSERT(ok, "all 100 global-queue items executed");
    ASSERT(TEST_ATOMIC_READ(&g_steal_counter) == N,
           "counter exactly 100 after work-stealing test");
    return 0;
}

/* ============================================================
 * Test 6: Shutdown Drains Remaining Work
 *
 * Schedule 50 items and immediately call shutdown.
 * eidos_scheduler_shutdown() joins all workers (which drain their
 * local queues) and then drains the global queue. All items
 * should execute.
 * ============================================================ */

static TEST_ATOMIC_LONG g_drain_counter = 0;

static int test_shutdown_drains(void) {
    const int N = 50;
    TEST_ATOMIC_SET(&g_drain_counter, 0);

    eidos_scheduler_init(4);

    for (int i = 0; i < N; i++) {
        EidosWorkItem item = {0};
        item.invoke_fn   = test_increment_fn;
        item.closure_ptr = &g_drain_counter;
        item.arg         = NULL;
        eidos_schedule(item);
    }

    /* Immediately shut down -- no sleep, no waiting. */
    eidos_scheduler_shutdown();

    /* After shutdown returns, workers have drained their local queues
     * and the global queue has been drained as well. */
    ASSERT(TEST_ATOMIC_READ(&g_drain_counter) == N,
           "all 50 items executed after shutdown drain");
    return 0;
}

/* ============================================================
 * Main
 * ============================================================ */

int main(void) {
    printf("=== Eidos Scheduler Tests ===\n\n");

    RUN_TEST(test_init_shutdown);
    RUN_TEST(test_single_item);
    RUN_TEST(test_many_items);
    RUN_TEST(test_worker_index);
    RUN_TEST(test_work_stealing);
    RUN_TEST(test_shutdown_drains);

    printf("\n--- Results: %d run, %d passed, %d failed ---\n",
           g_tests_run, g_tests_passed, g_tests_failed);

    return (g_tests_failed > 0) ? 1 : 0;
}
