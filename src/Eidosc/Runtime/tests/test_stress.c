/**
 * Eidos Runtime Bounded Stress Tests
 *
 * Exercises scheduler/task throughput, channel backpressure, and mutex
 * serialization with fixed iteration counts. This is a regression gate, not a
 * long-running soak test.
 */

#include "../eidos_sync.h"

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

#if defined(_WIN32)
#   include <windows.h>
#   define sleep_ms(ms) Sleep((ms))
#   define ATOMIC_INC32(ptr) InterlockedIncrement((LONG volatile*)(ptr))
#   define ATOMIC_ADD64(ptr, val) InterlockedAdd64((LONGLONG volatile*)(ptr), (LONGLONG)(val))
#   define ATOMIC_FETCH_SUB32(ptr, val) InterlockedExchangeAdd((LONG volatile*)(ptr), (LONG)(val))
#   define ATOMIC_LOAD32(ptr) InterlockedOr((LONG volatile*)(ptr), 0)
#   define ATOMIC_LOAD64(ptr) InterlockedAdd64((LONGLONG volatile*)(ptr), 0)
#else
#   include <time.h>
#   define ATOMIC_INC32(ptr) __atomic_add_fetch((ptr), 1, __ATOMIC_SEQ_CST)
#   define ATOMIC_ADD64(ptr, val) __atomic_add_fetch((ptr), (val), __ATOMIC_SEQ_CST)
#   define ATOMIC_FETCH_SUB32(ptr, val) __atomic_fetch_sub((ptr), (val), __ATOMIC_SEQ_CST)
#   define ATOMIC_LOAD32(ptr) __atomic_load_n((ptr), __ATOMIC_SEQ_CST)
#   define ATOMIC_LOAD64(ptr) __atomic_load_n((ptr), __ATOMIC_SEQ_CST)
static void sleep_ms(int ms)
{
    struct timespec ts = { 0, ms * 1000000L };
    nanosleep(&ts, NULL);
}
#endif

static int g_tests_run = 0;
static int g_tests_passed = 0;
static int g_tests_failed = 0;

#define ASSERT(cond, msg)                                                \
    do {                                                                 \
        if (!(cond)) {                                                   \
            fprintf(stderr, "  FAIL: %s (line %d)\n", (msg), __LINE__); \
            return 1;                                                    \
        }                                                                \
    } while (0)

#define RUN_TEST(fn)                             \
    do {                                         \
        g_tests_run++;                           \
        printf("[%d] %-44s", g_tests_run, #fn); \
        fflush(stdout);                          \
        int rc = fn();                           \
        if (rc == 0) {                           \
            g_tests_passed++;                    \
            printf("PASS\n");                   \
        } else {                                 \
            g_tests_failed++;                    \
        }                                        \
    } while (0)

static int wait_for_i32(volatile int32_t* counter, int32_t expected, int timeout_ms)
{
    int elapsed = 0;
    while (elapsed < timeout_ms) {
        if (ATOMIC_LOAD32(counter) >= expected) {
            return 1;
        }

        sleep_ms(10);
        elapsed += 10;
    }

    return ATOMIC_LOAD32(counter) >= expected;
}

static int wait_for_i64(volatile int64_t* counter, int64_t expected, int timeout_ms)
{
    int elapsed = 0;
    while (elapsed < timeout_ms) {
        if (ATOMIC_LOAD64(counter) >= expected) {
            return 1;
        }

        sleep_ms(10);
        elapsed += 10;
    }

    return ATOMIC_LOAD64(counter) >= expected;
}

static void* noop_fn(void* closure, void* arg)
{
    (void)closure;
    (void)arg;
    return NULL;
}

static EidosWorkItem noop_cont(void)
{
    EidosWorkItem item = { noop_fn, NULL, NULL };
    return item;
}

static volatile int64_t g_task_counter = 0;

static void* task_stress_fn(void* closure, void* arg)
{
    (void)closure;
    int iterations = (int)(intptr_t)arg;
    for (int i = 0; i < iterations; i++) {
        ATOMIC_ADD64(&g_task_counter, 1);
    }
    return NULL;
}

static int test_task_scheduler_stress(void)
{
    const int task_count = 128;
    const int iterations = 64;
    const int timeout_ms = 15000;
    struct EidosTask* tasks[128];

    g_task_counter = 0;

    for (int i = 0; i < task_count; i++) {
        tasks[i] = eidos_task_spawn(NULL, task_stress_fn, (void*)(intptr_t)iterations);
        ASSERT(tasks[i] != NULL, "task spawn returned non-NULL");
    }

    int64_t expected = (int64_t)task_count * iterations;
    ASSERT(wait_for_i64(&g_task_counter, expected, timeout_ms), "all task increments completed");
    ASSERT(ATOMIC_LOAD64(&g_task_counter) == expected, "task counter matched expected total");

    for (int i = 0; i < task_count; i++) {
        eidos_decref_shared(tasks[i]);
    }

    eidos_task_runtime_shutdown();
    return 0;
}

static volatile int32_t g_channel_received = 0;
static volatile int32_t g_channel_send_done = 0;
static volatile int64_t g_channel_sum = 0;

static int64_t* box_new(int64_t value)
{
    int64_t* box = (int64_t*)eidos_alloc(sizeof(int64_t), EIDOS_TYPE_INT);
    if (!box) {
        return NULL;
    }

    *box = value;
    eidos_share(box);
    return box;
}

static void* channel_send_done_fn(void* closure, void* arg)
{
    (void)closure;
    (void)arg;
    ATOMIC_INC32(&g_channel_send_done);
    return NULL;
}

static void* channel_recv_done_fn(void* closure, void* arg)
{
    (void)closure;
    if (arg && arg != eidos_channel_closed_sentinel()) {
        int64_t* box = (int64_t*)arg;
        ATOMIC_ADD64(&g_channel_sum, *box);
        ATOMIC_INC32(&g_channel_received);
        eidos_decref_shared(box);
    }

    return NULL;
}

static int test_channel_backpressure_stress(void)
{
    const int value_count = 64;
    const int timeout_ms = 15000;
    struct EidosChannel* channel = eidos_channel_new(8);
    ASSERT(channel != NULL, "channel allocation returned non-NULL");

    g_channel_received = 0;
    g_channel_send_done = 0;
    g_channel_sum = 0;

    eidos_scheduler_init(4);

    for (int i = 0; i < value_count; i++) {
        int64_t* box = box_new(i);
        ASSERT(box != NULL, "box allocation returned non-NULL");

        EidosWorkItem send_done = { channel_send_done_fn, NULL, NULL };
        eidos_channel_send(channel, box, send_done);
        eidos_decref_shared(box);
    }

    for (int i = 0; i < value_count; i++) {
        EidosWorkItem recv_done = { channel_recv_done_fn, NULL, NULL };
        eidos_channel_recv(channel, recv_done);
    }

    int64_t expected_sum = ((int64_t)value_count * (value_count - 1)) / 2;
    ASSERT(wait_for_i32(&g_channel_received, value_count, timeout_ms), "all channel receives completed");
    ASSERT(wait_for_i32(&g_channel_send_done, value_count, timeout_ms), "all channel sends completed");
    ASSERT(ATOMIC_LOAD64(&g_channel_sum) == expected_sum, "channel sum matched expected total");

    eidos_scheduler_shutdown();
    eidos_decref_shared(channel);
    return 0;
}

static volatile int64_t g_mutex_counter = 0;
static volatile int32_t g_mutex_done = 0;

static void* mutex_contention_once_fn(void* closure, void* arg)
{
    (void)arg;
    struct EidosMutex* mutex = (struct EidosMutex*)closure;

    ATOMIC_ADD64(&g_mutex_counter, 1);
    eidos_mutex_guard_release(mutex);
    ATOMIC_INC32(&g_mutex_done);

    return NULL;
}

static int test_mutex_contention_stress(void)
{
    const int scheduler_worker_count = 1;
    const int task_count = 512;
    const int timeout_ms = 15000;
    struct EidosMutex* mutex = eidos_mutex_new(NULL);
    ASSERT(mutex != NULL, "mutex allocation returned non-NULL");

    g_mutex_counter = 0;
    g_mutex_done = 0;
    eidos_scheduler_init(scheduler_worker_count);

    for (int i = 0; i < task_count; i++) {
        EidosWorkItem work = { mutex_contention_once_fn, mutex, NULL };
        eidos_mutex_lock(mutex, work);
    }

    int64_t expected = task_count;
    ASSERT(wait_for_i32(&g_mutex_done, task_count, timeout_ms), "all mutex contenders completed");

    /* Shutdown before checking: in-flight steps may push counter past expected. */
    eidos_scheduler_shutdown();
    eidos_decref_shared(mutex);

    ASSERT(ATOMIC_LOAD32(&g_mutex_done) == task_count, "mutex completion count matched expected total");
    ASSERT(ATOMIC_LOAD64(&g_mutex_counter) == expected, "mutex counter matched expected total");
    return 0;
}

int main(void)
{
    printf("=== Eidos Runtime Bounded Stress Tests ===\n\n");

    RUN_TEST(test_task_scheduler_stress);
    RUN_TEST(test_channel_backpressure_stress);
    RUN_TEST(test_mutex_contention_stress);

    printf("\n=== Results: %d/%d passed", g_tests_passed, g_tests_run);
    if (g_tests_failed > 0) {
        printf(", %d FAILED", g_tests_failed);
    }
    printf(" ===\n");

    return g_tests_failed > 0 ? 1 : 0;
}
