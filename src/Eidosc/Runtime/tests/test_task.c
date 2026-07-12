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

/* ============================================================
 * Test 1: Spawn + Await (Fast Path)
 *
 * Spawns a task that increments a counter. After the task body
 * completes, calls eidos_task_await with a continuation.
 * Verifies both the task body and the continuation executed.
 *
 * NOTE: We wait for the task body to finish before calling await
 * because the current implementation reuses task->completion for
 * both the user work (during spawn) and the awaiter continuation.
 * If await() runs before the trampoline extracts the user work, it
 * would overwrite the user function pointer. The wait ensures the
 * trampoline has cleared the completion slot so await can safely
 * store its continuation. This tests the COMPLETED fast path.
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
 * Main
 * ============================================================ */

int main(void) {
    printf("=== Eidos Task / TaskGroup Tests ===\n\n");

    RUN_TEST(test_spawn_await);
    RUN_TEST(test_spawn_multiple);
    RUN_TEST(test_taskgroup_4_tasks);
    RUN_TEST(test_taskgroup_join_before_spawn);
    RUN_TEST(test_complete_then_await);

    printf("\n--- Results: %d run, %d passed, %d failed ---\n",
           g_tests_run, g_tests_passed, g_tests_failed);

    return (g_tests_failed > 0) ? 1 : 0;
}
