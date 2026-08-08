/**
 * Eidos Task / TaskGroup Unit Tests
 *
 * Tests the task lifecycle (eidos_task.c) and TaskGroup structured concurrency
 * using the same hand-rolled assertion framework as test_scheduler.c.
 *
 * Build (gcc/clang on Windows):
 *   gcc -o test_task.exe test_task.c ../eidos_task.c ../eidos_scheduler.c ../eidos_memory.c -I.. -Wall -Wextra
 *
 * Build (MSVC):
 *   cl /Fe:test_task.exe test_task.c ../eidos_task.c ../eidos_scheduler.c ../eidos_memory.c /I..
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
 * Wait Helper
 * ============================================================ */

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

static int wait_for_task_waiter_free_count(int64_t expected, int timeout_ms)
{
    int elapsed = 0;
    const int poll_ms = 10;
    while (elapsed < timeout_ms) {
        if (eidos_task_waiter_free_count() >= expected) {
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
    return eidos_task_waiter_free_count() >= expected;
}

/* ============================================================
 * Test 1: Spawn + Await (Fast Path)
 *
 * Spawns a task that increments a counter. After the task body
 * completes, calls eidos_task_await with a continuation.
 * Verifies both the task body and the continuation executed.
 *
 * NOTE: We wait for the task body to finish before calling await so
 * this case specifically exercises the COMPLETED fast path. Pending
 * registration and completion races are covered by later tests.
 * ============================================================ */

static TEST_ATOMIC_LONG g_spawn_await_counter = 0;
static TEST_ATOMIC_LONG g_spawn_await_cont = 0;

static void* spawn_await_task_fn(void* closure, void* arg) {
    (void)arg;
    TEST_ATOMIC_INC((TEST_ATOMIC_LONG*)closure);
    return NULL;
}

static void* spawn_await_cont_fn(void* closure, void* arg) {
    (void)closure;
    (void)arg;
    TEST_ATOMIC_INC(&g_spawn_await_cont);
    return NULL;
}

static int test_spawn_await(void) {
    TEST_ATOMIC_SET(&g_spawn_await_counter, 0);
    TEST_ATOMIC_SET(&g_spawn_await_cont, 0);

    struct EidosTask* task = eidos_task_spawn(&g_spawn_await_counter,
                                              spawn_await_task_fn, NULL);
    ASSERT(task != NULL, "eidos_task_spawn returned non-NULL");

    /* Wait for the task body to execute before calling await. */
    int task_ok = wait_for_counter(&g_spawn_await_counter, 1, 2000);
    ASSERT(task_ok, "task body executed before await");

    /* Brief sleep to let the trampoline finish and transition state. */
#if defined(_WIN32)
    Sleep(100);
#else
    struct timespec ts = { 0, 100 * 1000000L };
    nanosleep(&ts, NULL);
#endif

    EidosWorkItem cont;
    cont.invoke_fn   = spawn_await_cont_fn;
    cont.closure_ptr = NULL;
    cont.arg         = NULL;

    eidos_task_await(task, cont);

    int cont_ok = wait_for_counter(&g_spawn_await_cont, 1, 2000);
    ASSERT(cont_ok, "continuation was invoked within timeout");

    eidos_decref_shared(task);
    eidos_task_runtime_shutdown();
    return 0;
}

/* ============================================================
 * Test 2: Spawn Multiple Tasks
 *
 * Spawns 10 tasks, each incrementing a shared atomic counter.
 * Waits for all 10 to complete. Verifies counter == 10.
 * ============================================================ */

static TEST_ATOMIC_LONG g_multi_counter = 0;

static void* multi_task_fn(void* closure, void* arg) {
    (void)arg;
    TEST_ATOMIC_INC((TEST_ATOMIC_LONG*)closure);
    return NULL;
}

static int test_spawn_multiple(void) {
    const int N = 10;
    TEST_ATOMIC_SET(&g_multi_counter, 0);

    struct EidosTask* tasks[10];

    for (int i = 0; i < N; i++) {
        tasks[i] = eidos_task_spawn(&g_multi_counter, multi_task_fn, NULL);
        ASSERT(tasks[i] != NULL, "eidos_task_spawn returned non-NULL");
    }

    int ok = wait_for_counter(&g_multi_counter, N, 2000);
    ASSERT(ok, "all 10 tasks completed within timeout");
    ASSERT(TEST_ATOMIC_READ(&g_multi_counter) == N,
           "counter exactly 10 after all tasks");

    for (int i = 0; i < N; i++) {
        eidos_decref_shared(tasks[i]);
    }
    eidos_task_runtime_shutdown();
    return 0;
}

/* ============================================================
 * Test 3: TaskGroup Spawn (4 Tasks)
 *
 * Creates a TaskGroup, spawns 4 tasks (each increments an atomic
 * counter). Verifies counter == 4, confirming that TaskGroup
 * correctly tracks and runs spawned tasks.
 * ============================================================ */

static TEST_ATOMIC_LONG g_tg_counter = 0;

static void* tg_task_fn(void* closure, void* arg) {
    (void)arg;
    TEST_ATOMIC_INC((TEST_ATOMIC_LONG*)closure);
    return NULL;
}

static int test_taskgroup_4_tasks(void) {
    const int N = 4;
    TEST_ATOMIC_SET(&g_tg_counter, 0);

    struct EidosTaskGroup* group = eidos_taskgroup_new();
    ASSERT(group != NULL, "eidos_taskgroup_new returned non-NULL");

    struct EidosTask* tasks[4];
    for (int i = 0; i < N; i++) {
        tasks[i] = eidos_taskgroup_spawn(group, &g_tg_counter,
                                         tg_task_fn, NULL);
        ASSERT(tasks[i] != NULL, "eidos_taskgroup_spawn returned non-NULL");
    }

    /* Wait for all tasks to complete. */
    int counter_ok = wait_for_counter(&g_tg_counter, N, 2000);
    ASSERT(counter_ok, "all 4 tasks completed within timeout");
    ASSERT(TEST_ATOMIC_READ(&g_tg_counter) == N,
           "counter exactly 4 after group tasks");

    /* Release task references. */
    for (int i = 0; i < N; i++) {
        eidos_decref_shared(tasks[i]);
    }
    eidos_decref_shared(group);
    eidos_task_runtime_shutdown();
    return 0;
}

/* ============================================================
 * Test 4: TaskGroup Join Before Spawn (Empty Group Fast Path)
 *
 * Creates a TaskGroup, joins immediately (pending_count == 0),
 * then verifies the continuation fires immediately without any
 * spawns. This tests the fast-path in eidos_taskgroup_join.
 *
 * Since we don't spawn any tasks (which would auto-init the
 * scheduler), we must init the scheduler manually so that
 * eidos_taskgroup_join can call eidos_schedule.
 * ============================================================ */

static TEST_ATOMIC_LONG g_join_empty_called = 0;

static void* join_empty_fn(void* closure, void* arg) {
    (void)closure;
    (void)arg;
    TEST_ATOMIC_INC(&g_join_empty_called);
    return NULL;
}

static int test_taskgroup_join_before_spawn(void) {
    TEST_ATOMIC_SET(&g_join_empty_called, 0);

    /*
     * eidos_taskgroup_join() calls eidos_schedule() when pending_count == 0.
     * The scheduler must be running for that to work.
     */
    eidos_scheduler_init(0);

    struct EidosTaskGroup* group = eidos_taskgroup_new();
    ASSERT(group != NULL, "eidos_taskgroup_new returned non-NULL");

    EidosWorkItem join_cont;
    join_cont.invoke_fn   = join_empty_fn;
    join_cont.closure_ptr = NULL;
    join_cont.arg         = NULL;

    /* Join with no tasks spawned -- should schedule continuation immediately. */
    eidos_taskgroup_join(group, join_cont);

    int ok = wait_for_counter(&g_join_empty_called, 1, 2000);
    ASSERT(ok, "join continuation fired immediately (pending=0)");

    eidos_decref_shared(group);
    eidos_task_runtime_shutdown();
    return 0;
}

/* ============================================================
 * Test 5: Complete Then Await (Fast Path)
 *
 * Spawns a task that increments an atomic counter, waits for the
 * counter to be incremented (proving the task body ran), sleeps
 * briefly to let the trampoline finish, then calls eidos_task_await.
 * The await should hit the COMPLETED fast path and schedule the
 * continuation immediately.
 * ============================================================ */

static TEST_ATOMIC_LONG g_fastpath_task_done = 0;
static TEST_ATOMIC_LONG g_fastpath_cont_called = 0;

static void* fastpath_task_fn(void* closure, void* arg) {
    (void)closure;
    (void)arg;
    TEST_ATOMIC_INC(&g_fastpath_task_done);
    return NULL;
}

static void* fastpath_cont_fn(void* closure, void* arg) {
    (void)closure;
    (void)arg;
    TEST_ATOMIC_INC(&g_fastpath_cont_called);
    return NULL;
}

static int test_complete_then_await(void) {
    TEST_ATOMIC_SET(&g_fastpath_task_done, 0);
    TEST_ATOMIC_SET(&g_fastpath_cont_called, 0);

    /* Spawn a task that completes quickly. */
    struct EidosTask* task = eidos_task_spawn(NULL, fastpath_task_fn, NULL);
    ASSERT(task != NULL, "eidos_task_spawn returned non-NULL");

    /* Wait for the task body to execute. */
    int task_done = wait_for_counter(&g_fastpath_task_done, 1, 2000);
    ASSERT(task_done, "task body executed before await");

    /*
     * Give the scheduler a brief moment to finish the trampoline and
     * transition the task to COMPLETED state.
     */
#if defined(_WIN32)
    Sleep(200);
#else
    struct timespec ts = { 0, 200 * 1000000L };
    nanosleep(&ts, NULL);
#endif

    /* Now await -- should hit the COMPLETED fast path. */
    EidosWorkItem cont;
    cont.invoke_fn   = fastpath_cont_fn;
    cont.closure_ptr = NULL;
    cont.arg         = NULL;

    eidos_task_await(task, cont);

    int ok = wait_for_counter(&g_fastpath_cont_called, 1, 2000);
    ASSERT(ok, "await continuation fired (fast path after completion)");

    eidos_decref_shared(task);
    eidos_task_runtime_shutdown();
    return 0;
}

/* ============================================================
 * Test 6: Pending Task Completion Ownership
 *
 * A pending value task is retained for the continuation callback. The
 * callback releases that continuation-owned reference after observing
 * completion, while the caller releases its original reference after
 * the callback has run.
 * ============================================================ */

static TEST_ATOMIC_LONG g_pending_cont_called = 0;
static TEST_ATOMIC_LONG g_pending_cont_failed = 0;

static void* pending_cont_fn(void* closure, void* arg) {
    (void)arg;
    struct EidosTask* task = (struct EidosTask*)closure;
    TEST_ATOMIC_INC(&g_pending_cont_called);
    if (!eidos_task_is_completed(task)) {
        TEST_ATOMIC_INC(&g_pending_cont_failed);
    }
    eidos_task_release_pending(task);
    return NULL;
}

static int test_pending_task_completion(void) {
    TEST_ATOMIC_SET(&g_pending_cont_called, 0);
    TEST_ATOMIC_SET(&g_pending_cont_failed, 0);
    eidos_scheduler_init(0);

    struct EidosTask* task = eidos_task_new_pending_value();
    ASSERT(task != NULL, "eidos_task_new_pending_value returned non-NULL");
    ASSERT(!eidos_task_is_completed(task), "new pending task starts incomplete");

    eidos_task_retain_pending(task);
    EidosWorkItem continuation;
    continuation.invoke_fn = pending_cont_fn;
    continuation.closure_ptr = task;
    continuation.arg = NULL;
    eidos_task_await(task, continuation);

    eidos_task_complete(task, NULL);
    int callback_ok = wait_for_counter(&g_pending_cont_called, 1, 2000);
    ASSERT(callback_ok, "pending continuation fired after completion");
    ASSERT(TEST_ATOMIC_READ(&g_pending_cont_failed) == 0,
           "pending continuation observed completed task");

    eidos_task_release_pending(task);
    eidos_task_runtime_shutdown();
    return 0;
}

/* ============================================================
 * Test 7: Concurrent Multi-Awaiter Completion
 * ============================================================ */

#define MULTI_AWAITER_COUNT 16

typedef struct MultiAwaiterContext {
    struct EidosTask* task;
    int expected_terminal_state;
} MultiAwaiterContext;

static TEST_ATOMIC_LONG g_multi_await_called = 0;
static TEST_ATOMIC_LONG g_multi_await_failed = 0;

static void* multi_await_cont_fn(void* closure, void* arg) {
    MultiAwaiterContext* context = (MultiAwaiterContext*)closure;
    if (arg != NULL) {
        TEST_ATOMIC_INC(&g_multi_await_failed);
    }
    if (context->expected_terminal_state == EIDOS_TASK_COMPLETED &&
        !eidos_task_is_completed(context->task)) {
        TEST_ATOMIC_INC(&g_multi_await_failed);
    }
    if (context->expected_terminal_state == EIDOS_TASK_CANCELLED &&
        !eidos_task_is_cancelled(context->task)) {
        TEST_ATOMIC_INC(&g_multi_await_failed);
    }
    if (context->expected_terminal_state == EIDOS_TASK_FAILED &&
        !eidos_task_is_failed(context->task)) {
        TEST_ATOMIC_INC(&g_multi_await_failed);
    }
    TEST_ATOMIC_INC(&g_multi_await_called);
    eidos_task_release_pending(context->task);
    return NULL;
}

static void* register_multi_awaiter(void* arg) {
    MultiAwaiterContext* context = (MultiAwaiterContext*)arg;
    EidosWorkItem continuation;
    continuation.invoke_fn = multi_await_cont_fn;
    continuation.closure_ptr = context;
    continuation.arg = NULL;
    eidos_task_retain_pending(context->task);
    eidos_task_await(context->task, continuation);
    return NULL;
}

static int test_concurrent_multi_awaiter_completion(void) {
    TEST_ATOMIC_SET(&g_multi_await_called, 0);
    TEST_ATOMIC_SET(&g_multi_await_failed, 0);
    eidos_task_waiter_counters_reset();

    struct EidosTask* task = eidos_task_new_pending_value();
    ASSERT(task != NULL, "pending task allocated for multi-await test");

    EidosThread threads[MULTI_AWAITER_COUNT];
    MultiAwaiterContext contexts[MULTI_AWAITER_COUNT + 1];
    for (int i = 0; i < MULTI_AWAITER_COUNT; i++) {
        contexts[i].task = task;
        contexts[i].expected_terminal_state = EIDOS_TASK_COMPLETED;
        ASSERT(eidos_thread_create(&threads[i], register_multi_awaiter, &contexts[i]) == 0,
               "multi-await registration thread started");
    }
    for (int i = 0; i < MULTI_AWAITER_COUNT; i++) {
        ASSERT(eidos_thread_join(threads[i]) == 0,
               "multi-await registration thread joined");
    }

    eidos_task_complete(task, NULL);
    ASSERT(wait_for_counter(&g_multi_await_called, MULTI_AWAITER_COUNT, 2000),
           "every pending awaiter ran after completion");
    ASSERT(TEST_ATOMIC_READ(&g_multi_await_failed) == 0,
           "every pending awaiter observed completed state exactly once");
    ASSERT(eidos_task_waiter_alloc_count() == MULTI_AWAITER_COUNT,
           "one queue node allocated per pending awaiter");
    ASSERT(wait_for_task_waiter_free_count(MULTI_AWAITER_COUNT, 2000),
           "all pending awaiter dispatch nodes released");
    ASSERT(eidos_task_waiter_free_count() == MULTI_AWAITER_COUNT,
           "all pending awaiter nodes freed after completion");

    contexts[MULTI_AWAITER_COUNT].task = task;
    contexts[MULTI_AWAITER_COUNT].expected_terminal_state = EIDOS_TASK_COMPLETED;
    register_multi_awaiter(&contexts[MULTI_AWAITER_COUNT]);
    ASSERT(wait_for_counter(&g_multi_await_called, MULTI_AWAITER_COUNT + 1, 2000),
           "late awaiter ran on completed fast path");
    ASSERT(eidos_task_waiter_alloc_count() == MULTI_AWAITER_COUNT + 1,
           "completed fast path allocated one ownership dispatch node");
    ASSERT(wait_for_task_waiter_free_count(MULTI_AWAITER_COUNT + 1, 2000),
           "completed fast-path dispatch node released");

    eidos_task_release_pending(task);
    eidos_task_runtime_shutdown();
    return 0;
}

/* ============================================================
 * Test 8: Pending Cancellation Drains Awaiters Once
 * ============================================================ */

static int test_pending_cancellation(void) {
    const int waiter_count = 4;
    TEST_ATOMIC_SET(&g_multi_await_called, 0);
    TEST_ATOMIC_SET(&g_multi_await_failed, 0);
    eidos_task_waiter_counters_reset();

    struct EidosTask* task = eidos_task_new_pending_value();
    ASSERT(task != NULL, "pending task allocated for cancellation");
    MultiAwaiterContext contexts[4];
    for (int i = 0; i < waiter_count; i++) {
        contexts[i].task = task;
        contexts[i].expected_terminal_state = EIDOS_TASK_CANCELLED;
        register_multi_awaiter(&contexts[i]);
    }

    ASSERT(eidos_task_cancel(task), "first pending cancellation succeeds");
    ASSERT(!eidos_task_cancel(task), "repeated pending cancellation is rejected");
    eidos_task_complete(task, NULL);
    ASSERT(wait_for_counter(&g_multi_await_called, waiter_count, 2000),
           "cancellation scheduled every pending awaiter");
    ASSERT(TEST_ATOMIC_READ(&g_multi_await_failed) == 0,
           "cancelled awaiters observed cancellation");
    ASSERT(eidos_task_waiter_alloc_count() == waiter_count,
           "cancellation allocated one node per pending awaiter");
    ASSERT(wait_for_task_waiter_free_count(waiter_count, 2000),
           "cancellation released every dispatch node");
    ASSERT(eidos_task_waiter_free_count() == waiter_count,
           "cancellation freed every pending awaiter node");
    ASSERT(!eidos_task_is_completed(task), "completion cannot overwrite cancellation");

    eidos_task_release_pending(task);
    eidos_task_runtime_shutdown();
    return 0;
}

/* ============================================================
 * Test 9: Pending Failure Retains Error And Drains Awaiters
 * ============================================================ */

static int test_pending_failure(void) {
    const int waiter_count = 4;
    TEST_ATOMIC_SET(&g_multi_await_called, 0);
    TEST_ATOMIC_SET(&g_multi_await_failed, 0);
    eidos_task_waiter_counters_reset();

    struct EidosTask* task = eidos_task_new_pending_value();
    ASSERT(task != NULL, "pending task allocated for failure");
    MultiAwaiterContext contexts[4];
    for (int i = 0; i < waiter_count; i++) {
        contexts[i].task = task;
        contexts[i].expected_terminal_state = EIDOS_TASK_FAILED;
        register_multi_awaiter(&contexts[i]);
    }

    void* error = eidos_alloc(sizeof(int64_t), 9001);
    ASSERT(error != NULL, "managed error payload allocated");
    eidos_share(error);
    ASSERT(eidos_task_fail(task, error), "first pending failure succeeds");
    eidos_decref_shared(error);
    ASSERT(!eidos_task_fail(task, NULL), "repeated pending failure is rejected");
    ASSERT(!eidos_task_cancel(task), "failure cannot be overwritten by cancellation");
    eidos_task_complete(task, NULL);

    ASSERT(wait_for_counter(&g_multi_await_called, waiter_count, 2000),
           "failure scheduled every pending awaiter");
    ASSERT(TEST_ATOMIC_READ(&g_multi_await_failed) == 0,
           "failed awaiters observed failed state");
    ASSERT(eidos_task_try_get_error(task) == error,
           "failed task retained the managed error payload");
    ASSERT(eidos_task_waiter_alloc_count() == waiter_count,
           "failure allocated one node per pending awaiter");
    ASSERT(wait_for_task_waiter_free_count(waiter_count, 2000),
           "failure released every dispatch node");
    ASSERT(eidos_task_waiter_free_count() == waiter_count,
           "failure freed every pending awaiter node");

    eidos_task_release_pending(task);
    eidos_task_runtime_shutdown();
    return 0;
}

/* ============================================================
 * Main
 * ============================================================ */

int main(void) {
    printf("=== Eidos Task / TaskGroup Tests ===\n\n");

    RUN_TEST(test_spawn_await);
    RUN_TEST(test_spawn_multiple);
    RUN_TEST(test_taskgroup_4_tasks);
    RUN_TEST(test_taskgroup_join_before_spawn);
    RUN_TEST(test_complete_then_await);
    RUN_TEST(test_pending_task_completion);
    RUN_TEST(test_concurrent_multi_awaiter_completion);
    RUN_TEST(test_pending_cancellation);
    RUN_TEST(test_pending_failure);

    printf("\n--- Results: %d run, %d passed, %d failed ---\n",
           g_tests_run, g_tests_passed, g_tests_failed);

    return (g_tests_failed > 0) ? 1 : 0;
}
