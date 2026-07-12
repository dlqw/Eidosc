/**
 * Eidos Channel Unit Tests
 *
 * Tests the CSP-style async channel (eidos_channel.c) with buffered,
 * rendezvous, multiple send/recv, and close scenarios.
 *
 * Channel values are passed as shared Eidos-allocated objects (e.g. int64_t
 * allocated via eidos_alloc) because the channel calls eidos_incref_shared /
 * eidos_decref_shared on every value that passes through.
 *
 * The scheduler is initialized once in main() and shutdown once at the end.
 * Individual tests do not init/shutdown the scheduler.
 *
 * Build (gcc/clang on Windows):
 *   gcc -o test_channel.exe test_channel.c ../eidos_channel.c ../eidos_task.c ../eidos_scheduler.c ../eidos_memory.c -I.. -Wall -Wextra
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
 * Channels call eidos_incref_shared / eidos_decref_shared on values,
 * so every value sent through a channel must be an Eidos-allocated
 * shared object. We allocate a simple int64_t via eidos_alloc and
 * mark it shared.
 * ============================================================ */

static void* make_test_value(int64_t v) {
    int64_t* obj = (int64_t*)eidos_alloc(sizeof(int64_t), EIDOS_TYPE_INT);
    if (!obj) return NULL;
    *obj = v;
    eidos_share(obj);
    return obj;
}

/* ============================================================
 * Test 1: Buffered Channel Send + Recv
 *
 * Create a buffered channel (cap=4). Send one value, recv it.
 * Verify the receiver continuation receives the correct value.
 * ============================================================ */

static volatile TEST_ATOMIC_LONG g_value_received = 0;
static void* g_received_ptr = NULL;

static void* recv_cont_fn(void* closure, void* arg) {
    (void)closure;
    g_received_ptr = arg;
    TEST_ATOMIC_INC(&g_value_received);
    return NULL;
}

static void* send_done_fn(void* closure, void* arg) {
    (void)closure;
    (void)arg;
    return NULL;
}

/* Channels that need to be released after scheduler shutdown. */
static struct EidosChannel* g_ch_buffered = NULL;
static void* g_val_buffered = NULL;

static int test_channel_send_recv_buffered(void) {
    TEST_ATOMIC_SET(&g_value_received, 0);
    g_received_ptr = NULL;

    g_ch_buffered = eidos_channel_new(4);
    ASSERT(g_ch_buffered != NULL, "eidos_channel_new(4) returned non-NULL");

    g_val_buffered = make_test_value(42);
    ASSERT(g_val_buffered != NULL, "make_test_value returned non-NULL");

    EidosWorkItem send_cont = {0};
    send_cont.invoke_fn   = send_done_fn;
    send_cont.closure_ptr = NULL;
    send_cont.arg         = NULL;
    eidos_channel_send(g_ch_buffered, g_val_buffered, send_cont);

    EidosWorkItem recv_cont = {0};
    recv_cont.invoke_fn   = recv_cont_fn;
    recv_cont.closure_ptr = NULL;
    recv_cont.arg         = NULL;
    eidos_channel_recv(g_ch_buffered, recv_cont);

    int ok = wait_for_counter(&g_value_received, 1, 3000);
    ASSERT(ok, "receiver continuation fired within timeout");
    ASSERT(g_received_ptr == g_val_buffered, "received value matches sent value");
    return 0;
}

/* ============================================================
 * Test 2: Rendezvous Channel Send + Recv
 *
 * Create a rendezvous channel (cap=0). Register a receiver first
 * (it suspends), then send. The sender finds the waiting receiver
 * and delivers the value directly via rendezvous.
 * ============================================================ */

static volatile TEST_ATOMIC_LONG g_rendezvous_received = 0;
static void* g_rendezvous_ptr = NULL;

static void* rendezvous_recv_fn(void* closure, void* arg) {
    (void)closure;
    g_rendezvous_ptr = arg;
    TEST_ATOMIC_INC(&g_rendezvous_received);
    return NULL;
}

static void* rendezvous_send_fn(void* closure, void* arg) {
    (void)closure;
    (void)arg;
    return NULL;
}

static struct EidosChannel* g_ch_rendezvous = NULL;
static void* g_val_rendezvous = NULL;

static int test_channel_send_recv_rendezvous(void) {
    TEST_ATOMIC_SET(&g_rendezvous_received, 0);
    g_rendezvous_ptr = NULL;

    g_ch_rendezvous = eidos_channel_new(0);
    ASSERT(g_ch_rendezvous != NULL, "eidos_channel_new(0) returned non-NULL");

    g_val_rendezvous = make_test_value(99);
    ASSERT(g_val_rendezvous != NULL, "make_test_value returned non-NULL");

    EidosWorkItem recv_cont = {0};
    recv_cont.invoke_fn   = rendezvous_recv_fn;
    recv_cont.closure_ptr = NULL;
    recv_cont.arg         = NULL;
    eidos_channel_recv(g_ch_rendezvous, recv_cont);

    /* Brief pause to ensure the recv trampoline is fully queued. */
#if defined(_WIN32)
    Sleep(100);
#else
    struct timespec ts = { 0, 100 * 1000000L };
    nanosleep(&ts, NULL);
#endif

    EidosWorkItem send_cont = {0};
    send_cont.invoke_fn   = rendezvous_send_fn;
    send_cont.closure_ptr = NULL;
    send_cont.arg         = NULL;
    eidos_channel_send(g_ch_rendezvous, g_val_rendezvous, send_cont);

    int ok = wait_for_counter(&g_rendezvous_received, 1, 3000);
    ASSERT(ok, "rendezvous receiver continuation fired");
    ASSERT(g_rendezvous_ptr == g_val_rendezvous,
           "rendezvous received value matches sent value");
    return 0;
}

/* ============================================================
 * Test 3: Multiple Send + Recv (10 values)
 *
 * Send 10 distinct values through a buffered channel (cap=16),
 * then recv all 10. Verify all received values are correct.
 * ============================================================ */

static volatile TEST_ATOMIC_LONG g_multi_recv_count = 0;
static volatile void* g_multi_recv_ptrs[10];
static void* g_multi_values[10];

static void* multi_recv_fn(void* closure, void* arg) {
    int idx = (int)(intptr_t)closure;
    g_multi_recv_ptrs[idx] = arg;
    TEST_ATOMIC_INC(&g_multi_recv_count);
    return NULL;
}

static void* multi_send_done_fn(void* closure, void* arg) {
    (void)closure;
    (void)arg;
    return NULL;
}

static struct EidosChannel* g_ch_multi = NULL;

static int test_channel_multiple(void) {
    TEST_ATOMIC_SET(&g_multi_recv_count, 0);
    for (int i = 0; i < 10; i++) {
        g_multi_recv_ptrs[i] = NULL;
        g_multi_values[i]    = NULL;
    }

    g_ch_multi = eidos_channel_new(16);
    ASSERT(g_ch_multi != NULL, "eidos_channel_new(16) returned non-NULL");

    for (int i = 0; i < 10; i++) {
        g_multi_values[i] = make_test_value(100 + i);
        ASSERT(g_multi_values[i] != NULL, "make_test_value returned non-NULL");

        EidosWorkItem send_cont = {0};
        send_cont.invoke_fn   = multi_send_done_fn;
        send_cont.closure_ptr = NULL;
        send_cont.arg         = NULL;
        eidos_channel_send(g_ch_multi, g_multi_values[i], send_cont);
    }

    for (int i = 0; i < 10; i++) {
        EidosWorkItem recv_cont = {0};
        recv_cont.invoke_fn   = multi_recv_fn;
        recv_cont.closure_ptr = (void*)(intptr_t)i;
        recv_cont.arg         = NULL;
        eidos_channel_recv(g_ch_multi, recv_cont);
    }

    int ok = wait_for_counter(&g_multi_recv_count, 10, 5000);
    ASSERT(ok, "all 10 receiver continuations fired");

    int all_match = 1;
    for (int i = 0; i < 10; i++) {
        if (g_multi_recv_ptrs[i] != g_multi_values[i]) {
            all_match = 0;
        }
    }
    ASSERT(all_match, "all 10 received values match their originals");
    return 0;
}

/* ============================================================
 * Test 4: Close Channel
 *
 * Close an empty channel. Verify that recv returns the closed
 * sentinel.
 * ============================================================ */

static volatile TEST_ATOMIC_LONG g_close_recv_fired = 0;
static void* g_close_recv_arg = NULL;

static void* close_recv_fn(void* closure, void* arg) {
    (void)closure;
    g_close_recv_arg = arg;
    TEST_ATOMIC_INC(&g_close_recv_fired);
    return NULL;
}

static struct EidosChannel* g_ch_close = NULL;

static int test_channel_close(void) {
    TEST_ATOMIC_SET(&g_close_recv_fired, 0);
    g_close_recv_arg = NULL;

    g_ch_close = eidos_channel_new(4);
    ASSERT(g_ch_close != NULL, "eidos_channel_new(4) returned non-NULL");

    eidos_channel_close(g_ch_close);

    EidosWorkItem recv_cont = {0};
    recv_cont.invoke_fn   = close_recv_fn;
    recv_cont.closure_ptr = NULL;
    recv_cont.arg         = NULL;
    eidos_channel_recv(g_ch_close, recv_cont);

    int ok = wait_for_counter(&g_close_recv_fired, 1, 3000);
    ASSERT(ok, "recv on closed channel fired");
    ASSERT(g_close_recv_arg == eidos_channel_closed_sentinel(),
           "recv on closed channel returned closed sentinel");
    return 0;
}

/* ============================================================
 * Main
 * ============================================================ */

int main(void) {
    printf("=== Eidos Channel Tests ===\n\n");

    eidos_scheduler_init(2);

    RUN_TEST(test_channel_send_recv_buffered);
    RUN_TEST(test_channel_send_recv_rendezvous);
    RUN_TEST(test_channel_multiple);
    RUN_TEST(test_channel_close);

    /* Shutdown scheduler first, then release shared objects.
     * This avoids a race where worker threads execute continuations
     * that reference channels/objects being freed by eidos_decref_shared. */
    eidos_scheduler_shutdown();

    /* Now safe to release shared objects (no worker threads running). */
    if (g_val_buffered)   eidos_decref_shared(g_val_buffered);
    if (g_ch_buffered)    eidos_decref_shared(g_ch_buffered);
    if (g_val_rendezvous) eidos_decref_shared(g_val_rendezvous);
    if (g_ch_rendezvous)  eidos_decref_shared(g_ch_rendezvous);
    for (int i = 0; i < 10; i++) {
        if (g_multi_values[i]) eidos_decref_shared(g_multi_values[i]);
    }
    if (g_ch_multi)       eidos_decref_shared(g_ch_multi);
    if (g_ch_close)       eidos_decref_shared(g_ch_close);

    printf("\n--- Results: %d run, %d passed, %d failed ---\n",
           g_tests_run, g_tests_passed, g_tests_failed);

    return (g_tests_failed > 0) ? 1 : 0;
}
