/**
 * Eidos Multithreading Integration Tests
 *
 * End-to-end tests that exercise multiple runtime subsystems together:
 *   - Scheduler work-stealing
 *   - Task spawn/complete lifecycle
 *   - TaskGroup structured concurrency
 *   - Channel CSP-style communication
 *   - Promise/Future one-shot async
 *   - Mutex / Barrier task-aware synchronization
 *   - Shared reference counting (eidos_share + _shared RC variants)
 *
 * Build (gcc/clang on Windows):
 *   gcc -o test_integration.exe test_integration.c ../eidos_sync.c ../eidos_task.c ../eidos_scheduler.c ../eidos_channel.c ../eidos_promise.c ../eidos_memory.c -I.. -Wall -Wextra
 *
 * Build (MSVC):
 *   cl /Fe:test_integration.exe test_integration.c ../eidos_sync.c ../eidos_task.c ../eidos_scheduler.c ../eidos_channel.c ../eidos_promise.c ../eidos_memory.c /I..
 */

#include "../eidos_sync.h"

#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>

#ifdef _WIN32
#   include <windows.h>
#   define sleep_ms(ms) Sleep((ms))
#else
#   include <unistd.h>
#   define sleep_ms(ms) usleep((ms) * 1000)
#endif

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
        printf("[%d] %-50s", g_tests_run, #fn);                        \
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
 * Atomics
 * ============================================================ */

#ifdef _WIN32
#   define ATOMIC_INC32(ptr)    InterlockedIncrement((LONG*)(ptr))
#   define ATOMIC_ADD64(ptr,v)  InterlockedAdd64((LONGLONG*)(ptr),(LONGLONG)(v))
#   define ATOMIC_LOAD32(ptr)   InterlockedOr((LONG*)(ptr), 0)
#else
#   include <stdatomic.h>
#   define ATOMIC_INC32(ptr)    __atomic_add_fetch((ptr), 1, __ATOMIC_SEQ_CST)
#   define ATOMIC_ADD64(ptr,v)  __atomic_add_fetch((ptr), (v), __ATOMIC_SEQ_CST)
#   define ATOMIC_LOAD32(ptr)   __atomic_load_n((ptr), __ATOMIC_SEQ_CST)
#endif

/* No-op continuation for fire-and-forget operations. */
static void* noop_fn(void* closure, void* arg)
{
    (void)closure; (void)arg;
    return NULL;
}

static EidosWorkItem noop_cont(void)
{
    EidosWorkItem c = { noop_fn, NULL, NULL };
    return c;
}

/* ============================================================
 * Test 1: spawn many tasks incrementing a global counter
 * ============================================================ */

static int64_t g_test1_counter = 0;

static void* test1_increment_fn(void* closure, void* arg)
{
    (void)closure; (void)arg;
    ATOMIC_ADD64(&g_test1_counter, 1);
    return NULL;
}

static int test_spawn_many_tasks(void)
{
    g_test1_counter = 0;
    eidos_scheduler_init(4);

    const int N = 20;
    struct EidosTask** tasks = (struct EidosTask**)malloc(N * sizeof(struct EidosTask*));

    for (int i = 0; i < N; i++) {
        tasks[i] = eidos_task_spawn(NULL, test1_increment_fn, NULL);
    }

    sleep_ms(500);

    ASSERT(g_test1_counter == N, "Counter should equal N after all tasks");

    for (int i = 0; i < N; i++) {
        eidos_decref_shared(tasks[i]);
    }
    free(tasks);
    eidos_scheduler_shutdown();
    return 0;
}

/* ============================================================
 * Test 2: Shared RC auto-forwarding
 * ============================================================ */

#define TYPE_TEST_SHARED  9001

static int test_shared_rc_autoforward(void)
{
    void* obj = eidos_alloc(16, TYPE_TEST_SHARED);

    EidosHeader* hdr = ((EidosHeader*)obj) - 1;
    ASSERT(hdr->ref_count == 1, "Initial ref_count should be 1");
    ASSERT((hdr->ref_count & EIDOS_SHARED_BIT) == 0,
           "Should not be shared initially");

    eidos_share(obj);
    ASSERT(hdr->ref_count & EIDOS_SHARED_BIT,
           "Should be shared after eidos_share");
    ASSERT((hdr->ref_count & EIDOS_COUNT_MASK) == 1,
           "Count portion should still be 1");

    eidos_incref_local(obj);
    ASSERT((hdr->ref_count & EIDOS_COUNT_MASK) == 2,
           "Count should be 2 after incref_local on shared obj");

    eidos_decref_local(obj);
    ASSERT((hdr->ref_count & EIDOS_COUNT_MASK) == 1,
           "Count should be 1 after decref_local on shared obj");

    eidos_decref_local(obj);
    return 0;
}

/* ============================================================
 * Test 3: Scheduler lifecycle — init/shutdown/reinit
 * ============================================================ */

static int test_scheduler_lifecycle(void)
{
    eidos_scheduler_init(2);
    sleep_ms(50);
    eidos_scheduler_shutdown();

    eidos_scheduler_init(2);

    g_test1_counter = 0;
    eidos_task_spawn(NULL, test1_increment_fn, NULL);
    sleep_ms(200);

    ASSERT(g_test1_counter == 1, "Task should have run in second lifecycle");

    eidos_scheduler_shutdown();
    return 0;
}

/* ============================================================
 * Test 4: TaskGroup — spawn multiple tasks, wait for all
 * ============================================================ */

static int g_test4_counter = 0;

static void* test4_increment_fn(void* closure, void* arg)
{
    (void)closure; (void)arg;
    ATOMIC_INC32(&g_test4_counter);
    return NULL;
}

static int test_taskgroup_basic(void)
{
    g_test4_counter = 0;
    eidos_scheduler_init(4);

    struct EidosTaskGroup* group = eidos_taskgroup_new();

    const int N = 10;
    for (int i = 0; i < N; i++) {
        eidos_taskgroup_spawn(group, NULL, test4_increment_fn, NULL);
    }

    sleep_ms(500);

    ASSERT(ATOMIC_LOAD32(&g_test4_counter) == N,
           "All TaskGroup tasks should have completed");

    eidos_decref_shared(group);
    eidos_scheduler_shutdown();
    return 0;
}

/* ============================================================
 * Test 5: Channel buffered send + recv (simple)
 *
 * Channel values must be eidos_alloc'd objects (shared pointers).
 * We allocate a small box to carry our integer value.
 * ============================================================ */

#define TYPE_TEST_BOX 9002

static int64_t* box_new(int64_t val)
{
    int64_t* p = (int64_t*)eidos_alloc(sizeof(int64_t), TYPE_TEST_BOX);
    *p = val;
    return p;
}

static int64_t g_test5_received = 0;

typedef struct SimpleChannelCtx {
    struct EidosChannel* ch;
    int64_t              value;
} SimpleChannelCtx;

static void* simple_send_fn(void* closure, void* arg)
{
    (void)arg;
    SimpleChannelCtx* ctx = (SimpleChannelCtx*)closure;
    int64_t* box = box_new(ctx->value);
    eidos_share(box);
    eidos_channel_send(ctx->ch, box, noop_cont());
    /* Channel incref's the value, we drop our ref. */
    eidos_decref_shared(box);
    return NULL;
}

static void* simple_recv_done_fn(void* closure, void* arg)
{
    SimpleChannelCtx* ctx = (SimpleChannelCtx*)closure;
    if (arg && arg != eidos_channel_closed_sentinel()) {
        int64_t* box = (int64_t*)arg;
        if (*box == ctx->value) {
            ATOMIC_ADD64(&g_test5_received, *box);
        }
        eidos_decref_shared(box);
    }
    return NULL;
}

static void* simple_recv_start_fn(void* closure, void* arg)
{
    (void)arg;
    SimpleChannelCtx* ctx = (SimpleChannelCtx*)closure;
    EidosWorkItem cont = { simple_recv_done_fn, ctx, NULL };
    eidos_channel_recv(ctx->ch, cont);
    return NULL;
}

static int test_channel_buffered_simple(void)
{
    g_test5_received = 0;
    eidos_scheduler_init(4);

    static struct EidosChannel* s_ch5;
    s_ch5 = eidos_channel_new(4);
    static SimpleChannelCtx s_ctx5;
    s_ctx5.ch = s_ch5;
    s_ctx5.value = 42;

    /* Send first, then recv — buffered channel can absorb. */
    EidosWorkItem send_work = { simple_send_fn, &s_ctx5, NULL };
    eidos_schedule(send_work);

    sleep_ms(100);

    EidosWorkItem recv_work = { simple_recv_start_fn, &s_ctx5, NULL };
    eidos_schedule(recv_work);

    sleep_ms(1000);

    ASSERT(g_test5_received == 42, "Should have received 42");

    eidos_decref_shared(s_ch5);
    eidos_scheduler_shutdown();
    return 0;
}

/* ============================================================
 * Test 6: Channel rendezvous (capacity=0)
 * ============================================================ */

static int g_test6_received = 0;

typedef struct RendezvousCtx {
    struct EidosChannel* ch;
    int64_t expected;
} RendezvousCtx;

static void* rendezvous_sender_fn(void* closure, void* arg)
{
    (void)arg;
    RendezvousCtx* ctx = (RendezvousCtx*)closure;
    int64_t* box = box_new(ctx->expected);
    eidos_share(box);
    eidos_channel_send(ctx->ch, box, noop_cont());
    eidos_decref_shared(box);
    return NULL;
}

static void* rendezvous_recv_done_fn(void* closure, void* arg)
{
    RendezvousCtx* ctx = (RendezvousCtx*)closure;
    if (arg && arg != eidos_channel_closed_sentinel()) {
        int64_t* box = (int64_t*)arg;
        if (*box == ctx->expected) {
            ATOMIC_INC32(&g_test6_received);
        }
        eidos_decref_shared(box);
    }
    return NULL;
}

static void* rendezvous_recv_start_fn(void* closure, void* arg)
{
    (void)arg;
    RendezvousCtx* ctx = (RendezvousCtx*)closure;
    EidosWorkItem cont = { rendezvous_recv_done_fn, ctx, NULL };
    eidos_channel_recv(ctx->ch, cont);
    return NULL;
}

static int test_channel_rendezvous(void)
{
    g_test6_received = 0;
    eidos_scheduler_init(4);

    struct EidosChannel* ch = eidos_channel_new(0);
    RendezvousCtx ctx = { ch, 99 };

    EidosWorkItem recv_work = { rendezvous_recv_start_fn, &ctx, NULL };
    EidosWorkItem send_work = { rendezvous_sender_fn, &ctx, NULL };

    eidos_schedule(recv_work);
    eidos_schedule(send_work);

    sleep_ms(500);

    ASSERT(ATOMIC_LOAD32(&g_test6_received) == 1,
           "Receiver should have received 99");

    eidos_decref_shared(ch);
    eidos_scheduler_shutdown();
    return 0;
}

/* ============================================================
 * Test 7: Promise/Future
 * ============================================================ */

static int g_test7_verified = 0;

typedef struct PromiseTestCtx {
    struct EidosPromise* promise;
    struct EidosFuture*  future;
    int64_t              value;
} PromiseTestCtx;

static void* promise_fulfill_fn(void* closure, void* arg)
{
    (void)arg;
    PromiseTestCtx* ctx = (PromiseTestCtx*)closure;
    int64_t* box = box_new(ctx->value);
    eidos_share(box);
    eidos_promise_fulfill(ctx->promise, box);
    eidos_decref_shared(box);
    return NULL;
}

static void* future_await_done_fn(void* closure, void* arg)
{
    PromiseTestCtx* ctx = (PromiseTestCtx*)closure;
    if (arg) {
        int64_t* box = (int64_t*)arg;
        if (*box == ctx->value) {
            ATOMIC_INC32(&g_test7_verified);
        }
        eidos_decref_shared(box);
    }
    return NULL;
}

static void* future_await_start_fn(void* closure, void* arg)
{
    (void)arg;
    PromiseTestCtx* ctx = (PromiseTestCtx*)closure;
    EidosWorkItem cont = { future_await_done_fn, ctx, NULL };
    eidos_future_await(ctx->future, cont);
    return NULL;
}

static int test_promise_future(void)
{
    g_test7_verified = 0;
    eidos_scheduler_init(4);

    PromiseTestCtx ctx;
    ctx.value = 42;
    eidos_promise_new(&ctx.promise, &ctx.future);

    EidosWorkItem await_work = { future_await_start_fn, &ctx, NULL };
    EidosWorkItem fill_work  = { promise_fulfill_fn, &ctx, NULL };

    eidos_schedule(await_work);
    sleep_ms(100);
    eidos_schedule(fill_work);

    sleep_ms(500);

    ASSERT(ATOMIC_LOAD32(&g_test7_verified) == 1,
           "Future should resolve to 42");

    eidos_decref_shared(ctx.promise);
    eidos_decref_shared(ctx.future);
    eidos_scheduler_shutdown();
    return 0;
}

/* ============================================================
 * Test 8: Mutex contention
 * ============================================================ */

static int64_t g_test8_counter = 0;

typedef struct MutexTestCtx {
    struct EidosMutex* mutex;
    int64_t            increment;
    int                iterations;
} MutexTestCtx;

static void* mutex_step_fn(void* closure, void* arg);

typedef struct MutexStep {
    MutexTestCtx* ctx;
    int remaining;
} MutexStep;

static void* mutex_step_fn(void* closure, void* arg)
{
    (void)arg;
    MutexStep* step = (MutexStep*)closure;

    ATOMIC_ADD64(&g_test8_counter, step->ctx->increment);
    eidos_mutex_guard_release(step->ctx->mutex);

    step->remaining--;
    if (step->remaining > 0) {
        EidosWorkItem cont = { mutex_step_fn, step, NULL };
        eidos_mutex_lock(step->ctx->mutex, cont);
    } else {
        free(step);
    }
    return NULL;
}

static void* mutex_worker_start_fn(void* closure, void* arg)
{
    (void)arg;
    MutexTestCtx* ctx = (MutexTestCtx*)closure;

    MutexStep* step = (MutexStep*)malloc(sizeof(MutexStep));
    step->ctx       = ctx;
    step->remaining = ctx->iterations;

    EidosWorkItem cont = { mutex_step_fn, step, NULL };
    eidos_mutex_lock(ctx->mutex, cont);
    return NULL;
}

static int test_mutex_contention(void)
{
    g_test8_counter = 0;
    eidos_scheduler_init(4);

    struct EidosMutex* mutex = eidos_mutex_new(NULL);

    const int TASKS = 4;
    const int ITERS = 25;

    MutexTestCtx workers[4];
    for (int i = 0; i < TASKS; i++) {
        workers[i].mutex      = mutex;
        workers[i].increment  = 1;
        workers[i].iterations = ITERS;

        EidosWorkItem work = { mutex_worker_start_fn, &workers[i], NULL };
        eidos_schedule(work);
    }

    sleep_ms(2000);

    int64_t expected = (int64_t)TASKS * ITERS;
    ASSERT(g_test8_counter == expected,
           "Mutex-protected counter should match expected");

    eidos_decref_shared(mutex);
    eidos_scheduler_shutdown();
    return 0;
}

/* ============================================================
 * Test 9: Barrier synchronization
 * ============================================================ */

static int g_test9_arrived = 0;

typedef struct BarrierTestCtx {
    struct EidosBarrier* barrier;
} BarrierTestCtx;

static void* barrier_trip_fn(void* closure, void* arg)
{
    (void)arg;
    BarrierTestCtx* ctx = (BarrierTestCtx*)closure;
    (void)ctx;
    ATOMIC_INC32(&g_test9_arrived);
    return NULL;
}

static void* barrier_wait_fn(void* closure, void* arg)
{
    (void)arg;
    BarrierTestCtx* ctx = (BarrierTestCtx*)closure;
    EidosWorkItem cont = { barrier_trip_fn, ctx, NULL };
    eidos_barrier_wait(ctx->barrier, cont);
    return NULL;
}

static int test_barrier_synchronization(void)
{
    g_test9_arrived = 0;
    eidos_scheduler_init(4);

    struct EidosBarrier* barrier = eidos_barrier_new(4);

    BarrierTestCtx ctx = { barrier };
    for (int i = 0; i < 4; i++) {
        EidosWorkItem work = { barrier_wait_fn, &ctx, NULL };
        eidos_schedule(work);
    }

    sleep_ms(1000);

    ASSERT(ATOMIC_LOAD32(&g_test9_arrived) == 4,
           "All 4 barrier workers should have arrived");

    eidos_decref_shared(barrier);
    eidos_scheduler_shutdown();
    return 0;
}

/* ============================================================
 * Test 10: Channel close wakes pending receivers
 * ============================================================ */

static int g_test10_got_closed = 0;

typedef struct CloseTestCtx {
    struct EidosChannel* ch;
} CloseTestCtx;

static void* close_recv_done_fn(void* closure, void* arg)
{
    CloseTestCtx* ctx = (CloseTestCtx*)closure;
    (void)ctx;
    if (arg == eidos_channel_closed_sentinel()) {
        ATOMIC_INC32(&g_test10_got_closed);
    }
    return NULL;
}

static void* close_recv_start_fn(void* closure, void* arg)
{
    (void)arg;
    CloseTestCtx* ctx = (CloseTestCtx*)closure;
    EidosWorkItem cont = { close_recv_done_fn, ctx, NULL };
    eidos_channel_recv(ctx->ch, cont);
    return NULL;
}

static int test_channel_close_wakes_receivers(void)
{
    g_test10_got_closed = 0;
    eidos_scheduler_init(4);

    struct EidosChannel* ch = eidos_channel_new(0);
    CloseTestCtx ctx = { ch };

    EidosWorkItem recv_work = { close_recv_start_fn, &ctx, NULL };
    eidos_schedule(recv_work);

    sleep_ms(200);
    eidos_channel_close(ch);
    sleep_ms(500);

    ASSERT(ATOMIC_LOAD32(&g_test10_got_closed) == 1,
           "Receiver should get closed sentinel");

    eidos_decref_shared(ch);
    eidos_scheduler_shutdown();
    return 0;
}

/* ============================================================
 * Main
 * ============================================================ */

int main(void)
{
    printf("=== Eidos Multithreading Integration Tests ===\n\n");

    RUN_TEST(test_spawn_many_tasks);
    RUN_TEST(test_shared_rc_autoforward);
    RUN_TEST(test_scheduler_lifecycle);
    RUN_TEST(test_taskgroup_basic);
    RUN_TEST(test_channel_buffered_simple);
    /* Channel/Promise/Barrier/Mutex async integration tests covered by unit tests. */
    RUN_TEST(test_channel_rendezvous);
    RUN_TEST(test_promise_future);
    RUN_TEST(test_mutex_contention);
    RUN_TEST(test_barrier_synchronization);
    RUN_TEST(test_channel_close_wakes_receivers);

    printf("\n=== Results: %d/%d passed", g_tests_passed, g_tests_run);
    if (g_tests_failed > 0) {
        printf(", %d FAILED", g_tests_failed);
    }
    printf(" ===\n");

    return g_tests_failed > 0 ? 1 : 0;
}
