/**
 * Eidos Runtime - Task Lifecycle & TaskGroup Structured Concurrency
 *
 * Implements Tasks (lightweight units of concurrent work) and TaskGroups
 * (structured concurrency: groups of tasks that can be joined collectively).
 *
 * Tasks are created via eidos_task_spawn() which posts a trampoline work item
 * to the work-stealing scheduler. When the user's function completes, the
 * trampoline calls eidos_task_complete() which transitions the task state and
 * schedules any registered awaiter continuation.
 *
 * TaskGroups track pending_count and invoke an on_complete continuation when
 * all spawned tasks finish. Cancellation is supported via an error_flag.
 *
 * Include dependency chain: eidos_runtime.h -> eidos_sync.h -> this file
 */

#include "eidos_sync.h"
#include <stdlib.h>
#include <string.h>
#include <stdio.h>

#if EIDOS_POSIX
#include <sched.h>
#endif

/* ============================================================
 * Atomics (same macros as eidos_scheduler.c / eidos_memory.c)
 * ============================================================ */

#if defined(_WIN32)
#include <intrin.h>
#define ETASK_ATOMIC_LOAD32(ptr)        (*(volatile int32_t*)(ptr))
#define ETASK_ATOMIC_STORE32(ptr, val)  (*(volatile int32_t*)(ptr) = (int32_t)(val))
#define ETASK_ATOMIC_CAS32(ptr, exp, des) \
    (_InterlockedCompareExchange((LONG volatile*)(ptr), (LONG)(des), (LONG)(exp)) == (LONG)(exp))
#define ETASK_ATOMIC_INC32(ptr)         InterlockedIncrement((LONG volatile*)(ptr))
#define ETASK_ATOMIC_DEC32(ptr)         InterlockedDecrement((LONG volatile*)(ptr))
#define ETASK_ATOMIC_OR32(ptr, val)     InterlockedOr((LONG volatile*)(ptr), (LONG)(val))
#else
#define ETASK_ATOMIC_LOAD32(ptr)        __atomic_load_n((ptr), __ATOMIC_ACQUIRE)
#define ETASK_ATOMIC_STORE32(ptr, val)  __atomic_store_n((ptr), (val), __ATOMIC_RELEASE)
#define ETASK_ATOMIC_CAS32(ptr, exp, des) \
    __atomic_compare_exchange_n((ptr), &(exp), (des), 0, \
                                __ATOMIC_SEQ_CST, __ATOMIC_SEQ_CST)
#define ETASK_ATOMIC_INC32(ptr)         __atomic_add_fetch((ptr), 1, __ATOMIC_SEQ_CST)
#define ETASK_ATOMIC_DEC32(ptr)         __atomic_sub_fetch((ptr), 1, __ATOMIC_SEQ_CST)
#define ETASK_ATOMIC_OR32(ptr, val)     __atomic_or_fetch((ptr), (val), __ATOMIC_SEQ_CST)
#endif

/* ============================================================
 * Structures
 * ============================================================ */

/**
 * A Task represents a unit of concurrent work running on the scheduler.
 *
 * Lifecycle: CREATED -> SCHEDULED -> RUNNING -> COMPLETED
 *                                      |
 *                                      v
 *                                  SUSPENDED (waiting for await)
 *                                      |
 *                                      v
 *                                  COMPLETED (via eidos_task_complete)
 *
 * All fields accessed from multiple threads use atomic operations.
 * The task is allocated as a shared object (SHARED bit set) so
 * eidos_incref_shared / eidos_decref_shared must be used.
 */
typedef struct EidosTask {
    EidosHeader       header;
    volatile uint32_t state;         /* EidosTaskState enum (atomic) */
    uint32_t          raw_payloads;  /* 1 = result is an unmanaged RawPtr */
    uint32_t          release_result_after_complete;
    EidosNativeLock   completion_lock;
    EidosWorkItem     start;         /* task body scheduled by spawn */
    EidosWorkItem     completion;    /* continuation scheduled on completion (awaiter) */
    void*             result;        /* shared ptr to computation result */
    struct EidosTaskGroup* group;    /* owning TaskGroup (nullable) */
} EidosTask;

/**
 * A TaskGroup implements structured concurrency: a collection of tasks
 * whose completion can be awaited collectively via eidos_taskgroup_join().
 *
 * pending_count tracks how many tasks are still running. When it reaches
 * zero, the on_complete continuation is scheduled.
 *
 * error_flag enables cancellation: once set via eidos_taskgroup_cancel(),
 * remaining tasks are expected to check and short-circuit (future: actual
 * cooperative cancellation).
 */
typedef struct EidosTaskGroup {
    EidosHeader       header;
    volatile uint32_t pending_count; /* atomic: number of unfinished tasks */
    volatile uint32_t error_flag;    /* atomic: non-zero if cancelled */
    EidosWorkItem     on_complete;   /* continuation scheduled when pending -> 0 */
} EidosTaskGroup;

/* ============================================================
 * Module State & Auto-initialization
 * ============================================================ */

/** Flag to track whether the task runtime has initialized the scheduler. */
static int32_t g_task_runtime_initialized = 0;

#define EIDOS_TASK_RUNTIME_UNINIT       0
#define EIDOS_TASK_RUNTIME_INITIALIZING 1
#define EIDOS_TASK_RUNTIME_READY        2

/** Flag to track whether the task runtime shutdown hook has been registered. */
static int32_t g_task_runtime_atexit_registered = 0;

/** Flag to track whether destructors have been registered. */
static int32_t g_task_destructors_registered = 0;

static void task_runtime_yield(void)
{
#if EIDOS_WIN
    Sleep(0);
#else
    sched_yield();
#endif
}

/**
 * Ensure the scheduler is running. Auto-initializes with hardware
 * concurrency if not already initialized. Makes tests simpler.
 */
static void ensure_scheduler(void) {
    int32_t expected = 0;
    if (ETASK_ATOMIC_CAS32(&g_task_runtime_initialized, expected, EIDOS_TASK_RUNTIME_INITIALIZING)) {
        expected = 0;
        if (ETASK_ATOMIC_CAS32(&g_task_runtime_atexit_registered, expected, 1)) {
            atexit(eidos_task_runtime_shutdown);
        }

        eidos_scheduler_init(0);
        ETASK_ATOMIC_STORE32(&g_task_runtime_initialized, EIDOS_TASK_RUNTIME_READY);
        return;
    }

    while (ETASK_ATOMIC_LOAD32(&g_task_runtime_initialized) == EIDOS_TASK_RUNTIME_INITIALIZING) {
        task_runtime_yield();
    }

    if (ETASK_ATOMIC_LOAD32(&g_task_runtime_initialized) != EIDOS_TASK_RUNTIME_READY) {
        ensure_scheduler();
    }
}

/* Forward declarations */
static void  eidos_task_destructor(void* ptr);
static void  eidos_taskgroup_destructor(void* ptr);
static void  eidos_taskgroup_on_task_done(EidosTaskGroup* group);
static void  eidos_task_retain_result(EidosTask* task, void* result);
static void  eidos_task_release_result(EidosTask* task, void* result);
static EidosTask* eidos_task_spawn_with_payload_mode(void* closure,
                                                     void* (*invoke_fn)(void*, void*),
                                                     void* arg,
                                                     uint32_t raw_payloads,
                                                     uint32_t release_result_after_complete);
static EidosTask* eidos_task_spawn_with_payload_mode_and_group(void* closure,
                                                               void* (*invoke_fn)(void*, void*),
                                                               void* arg,
                                                               uint32_t raw_payloads,
                                                               uint32_t release_result_after_complete,
                                                               EidosTaskGroup* group);
static EidosTask* eidos_taskgroup_spawn_with_payload_mode(EidosTaskGroup* group,
                                                          void* closure,
                                                          void* (*invoke_fn)(void*, void*),
                                                          void* arg,
                                                          uint32_t raw_payloads,
                                                          uint32_t release_result_after_complete);

/**
 * Register destructors for EIDOS_TYPE_TASK and EIDOS_TYPE_TASKGROUP.
 * Idempotent: safe to call multiple times.
 */
static void ensure_destructors(void) {
    if (!ETASK_ATOMIC_LOAD32(&g_task_destructors_registered)) {
        eidos_register_destructor(EIDOS_TYPE_TASK, eidos_task_destructor);
        eidos_register_destructor(EIDOS_TYPE_TASKGROUP, eidos_taskgroup_destructor);
        ETASK_ATOMIC_STORE32(&g_task_destructors_registered, 1);
    }
}

/* ============================================================
 * Task Lifecycle - Internal Helpers
 * ============================================================ */

/**
 * Trampoline executed by the scheduler to run a task's user function.
 *
 * This is the bridge between the scheduler's EidosWorkItem and the Task
 * completion protocol. The Task itself serves as the WorkItem closure,
 * and the user's invoke_fn/closure are temporarily stored in the task's
 * completion field during spawn.
 *
 * Lifecycle within this function:
 *   1. Extract user's invoke_fn and closure from task->start
 *   2. Call user function, obtaining result
 *   3. Call eidos_task_complete() to transition state and notify awaiter
 *   4. Release the reference we acquired in eidos_task_spawn()
 *
 * @param closure  Points to the EidosTask (cast from void*)
 * @param arg      Unused (NULL)
 * @return         Always NULL
 */
static void* task_trampoline(void* closure, void* arg) {
    EidosTask* task = (EidosTask*)closure;
    (void)arg;

    /* Extract the user's work from the start field (stashed in spawn). */
    void* (*user_fn)(void*, void*) = task->start.invoke_fn;
    void* user_closure = task->start.closure_ptr;
    void* user_arg     = task->start.arg;

    /* Clear the start slot; completion is owned by awaiters. */
    memset(&task->start, 0, sizeof(EidosWorkItem));

    /* Transition state to RUNNING. */
    ETASK_ATOMIC_STORE32(&task->state, EIDOS_TASK_RUNNING);

    /* Execute the user's function. */
    void* user_result = user_fn(user_closure, user_arg);

    /* Complete the task: store result, transition to COMPLETED, notify. */
    eidos_task_complete(task, user_result);

    if (user_result && task->release_result_after_complete && !task->raw_payloads) {
        eidos_decref_shared(user_result);
    }

    /* Release the reference we acquired in eidos_task_spawn(). */
    eidos_decref_shared(task);

    return NULL;
}

typedef void* (*EidosClosureUnitRawInvokeFn)(void* closure, bool unit);
typedef void  (*EidosClosureRawUnitInvokeFn)(void* closure, void* arg);
typedef void  (*EidosClosureUnitUnitInvokeFn)(void* closure, bool unit);

static void* closure_task_raw_invoke(void* closure, void* arg) {
    EidosClosure* thunk = (EidosClosure*)closure;
    (void)arg;

    if (!thunk || !thunk->invoke_fn) {
        return NULL;
    }

    void* result = ((EidosClosureUnitRawInvokeFn)thunk->invoke_fn)(thunk, false);
    eidos_decref_shared(thunk);
    return result;
}

static void* closure_raw_continuation_invoke(void* closure, void* arg) {
    EidosClosure* continuation = (EidosClosure*)closure;

    if (continuation && continuation->invoke_fn) {
        ((EidosClosureRawUnitInvokeFn)continuation->invoke_fn)(continuation, arg);
        eidos_decref_shared(continuation);
    }

    return NULL;
}

static void* closure_unit_continuation_invoke(void* closure, void* arg) {
    EidosClosure* continuation = (EidosClosure*)closure;
    (void)arg;

    if (continuation && continuation->invoke_fn) {
        ((EidosClosureUnitUnitInvokeFn)continuation->invoke_fn)(continuation, false);
        eidos_decref_shared(continuation);
    }

    return NULL;
}

/* ============================================================
 * Task Lifecycle - Public API
 * ============================================================ */

/**
 * Allocate a new Task object.
 *
 * The task is heap-allocated with ref_count=1, marked as shared
 * (thread-safe), and initialized to the CREATED state.
 *
 * @return Pointer to the new EidosTask (shared, ref_count=1)
 */
static EidosTask* eidos_task_alloc(void) {
    EidosTask* task = (EidosTask*)eidos_alloc(sizeof(EidosTask), EIDOS_TYPE_TASK);
    if (!task) {
        fprintf(stderr, "eidos_task_alloc: out of memory\n");
        return NULL;
    }

    /* Initialize fields. eidos_alloc sets ref_count=1 and type_id. */
    task->state     = EIDOS_TASK_CREATED;
    task->raw_payloads = 0;
    task->release_result_after_complete = 0;
    task->result    = NULL;
    task->group     = NULL;
    eidos_lock_init(&task->completion_lock);
    memset(&task->start, 0, sizeof(EidosWorkItem));
    memset(&task->completion, 0, sizeof(EidosWorkItem));

    /* Mark as shared so atomic incref/decref is used. */
    eidos_share(task);

    return task;
}

struct EidosTask* eidos_task_new_completed_raw(void* result)
{
    ensure_destructors();

    EidosTask* task = eidos_task_alloc();
    if (!task) {
        return NULL;
    }

    task->raw_payloads = 1;
    task->release_result_after_complete = 0;
    task->result = result;
    ETASK_ATOMIC_STORE32(&task->state, EIDOS_TASK_COMPLETED);
    return task;
}

struct EidosTask* eidos_task_new_completed_value(void* result)
{
    ensure_destructors();

    EidosTask* task = eidos_task_alloc();
    if (!task) {
        return NULL;
    }

    task->raw_payloads = 0;
    task->release_result_after_complete = 0;
    eidos_task_complete(task, result);
    return task;
}

bool eidos_task_is_completed(struct EidosTask* task)
{
    if (!task) {
        return false;
    }

    return ETASK_ATOMIC_LOAD32((volatile int32_t*)&task->state) == EIDOS_TASK_COMPLETED;
}

void* eidos_task_try_get_raw(struct EidosTask* task)
{
    if (!task || !task->raw_payloads || !eidos_task_is_completed(task)) {
        return NULL;
    }

    return task->result;
}

void* eidos_task_try_get_value(struct EidosTask* task)
{
    if (!task || task->raw_payloads || !eidos_task_is_completed(task)) {
        return NULL;
    }

    return task->result;
}

static void eidos_task_retain_result(EidosTask* task, void* result)
{
    if (result && task && !task->raw_payloads) {
        eidos_incref_shared(result);
    }
}

static void eidos_task_release_result(EidosTask* task, void* result)
{
    if (result && task && !task->raw_payloads) {
        eidos_decref_shared(result);
    }
}

/**
 * Spawn a task that runs invoke_fn(closure, arg) on the scheduler.
 *
 * Creates a Task, wraps the user's function in a trampoline work item,
 * and posts it to the scheduler. The caller receives a reference to the
 * task (ref_count=1) which they can eidos_task_await() or decref_shared.
 *
 * The trampoline runs the user's function and then calls
 * eidos_task_complete() to transition the task and notify any awaiter.
 *
 * Auto-initializes the scheduler if not already running.
 *
 * @param closure   User's closure pointer (passed to invoke_fn)
 * @param invoke_fn User's function to execute
 * @param arg       Argument passed to invoke_fn
 * @return Pointer to the spawned EidosTask (shared, ref_count=1)
 */
EidosTask* eidos_task_spawn(void* closure, void* (*invoke_fn)(void*, void*), void* arg) {
    return eidos_task_spawn_with_payload_mode(closure, invoke_fn, arg, 0, 0);
}

static EidosTask* eidos_task_spawn_with_payload_mode(void* closure,
                                                     void* (*invoke_fn)(void*, void*),
                                                     void* arg,
                                                     uint32_t raw_payloads,
                                                     uint32_t release_result_after_complete) {
    return eidos_task_spawn_with_payload_mode_and_group(
        closure,
        invoke_fn,
        arg,
        raw_payloads,
        release_result_after_complete,
        NULL);
}

static EidosTask* eidos_task_spawn_with_payload_mode_and_group(void* closure,
                                                               void* (*invoke_fn)(void*, void*),
                                                               void* arg,
                                                               uint32_t raw_payloads,
                                                               uint32_t release_result_after_complete,
                                                               EidosTaskGroup* group) {
    ensure_scheduler();
    ensure_destructors();

    EidosTask* task = eidos_task_alloc();
    if (!task) {
        return NULL;
    }

    task->raw_payloads = raw_payloads ? 1u : 0u;
    task->release_result_after_complete = release_result_after_complete ? 1u : 0u;
    if (group) {
        task->group = group;
        eidos_incref_shared(group);
    }

    /* Transition to SCHEDULED before posting. */
    ETASK_ATOMIC_STORE32(&task->state, EIDOS_TASK_SCHEDULED);

    /*
     * Stash the user's invoke_fn/closure/arg in the start field. Awaiter
     * continuations use completion, so spawn/await cannot race on one slot.
     */
    task->start.invoke_fn   = invoke_fn;
    task->start.closure_ptr = closure;
    task->start.arg         = arg;

    /*
     * Acquire a reference for the trampoline. The spawn caller retains
     * their own reference (from eidos_task_alloc). The trampoline will
     * decref_shared after completing the task.
     */
    eidos_incref_shared(task);

    /* Create the trampoline work item and post to scheduler. */
    EidosWorkItem work;
    work.invoke_fn   = task_trampoline;
    work.closure_ptr = task;
    work.arg         = NULL;

    eidos_schedule(work);

    return task;
}

EidosTask* eidos_task_spawn_closure_raw(EidosClosure* thunk) {
    if (!thunk) {
        return NULL;
    }

    eidos_share(thunk);
    eidos_incref_shared(thunk);
    EidosTask* task = eidos_task_spawn_with_payload_mode(
        thunk,
        closure_task_raw_invoke,
        NULL,
        1,
        0);
    if (!task) {
        eidos_decref_shared(thunk);
    }

    return task;
}

EidosTask* eidos_task_spawn_closure_value(EidosClosure* thunk) {
    if (!thunk) {
        return NULL;
    }

    eidos_share(thunk);
    eidos_incref_shared(thunk);
    EidosTask* task = eidos_task_spawn_with_payload_mode(
        thunk,
        closure_task_raw_invoke,
        NULL,
        0,
        1);
    if (!task) {
        eidos_decref_shared(thunk);
    }

    return task;
}

void eidos_task_await_closure_raw(EidosTask* task, EidosClosure* continuation) {
    if (!task || !continuation) {
        return;
    }

    eidos_share(continuation);
    eidos_incref_shared(continuation);
    EidosWorkItem work;
    work.invoke_fn = closure_raw_continuation_invoke;
    work.closure_ptr = continuation;
    work.arg = NULL;
    eidos_task_await(task, work);
}

void eidos_task_await_closure_value(EidosTask* task, EidosClosure* continuation) {
    eidos_task_await_closure_raw(task, continuation);
}

/**
 * Await completion of a task, registering a continuation.
 *
 * If the task is already COMPLETED, the continuation is scheduled
 * immediately with the task's result as its argument.
 *
 * If the task is still running, the continuation is stored in the
 * task's completion slot. When the task completes, the continuation
 * will be scheduled with the result.
 *
 * Race handling: completion_lock serializes continuation installation
 * with completion, so the completer cannot observe a half-installed
 * continuation.
 *
 * @param task         The task to await (must not be NULL)
 * @param continuation Work item to schedule when the task completes
 */
void eidos_task_await(EidosTask* task, EidosWorkItem continuation) {
    if (!task) return;

    EidosWorkItem done;
    memset(&done, 0, sizeof(EidosWorkItem));

    eidos_lock_acquire(&task->completion_lock);

    int32_t cur_state = ETASK_ATOMIC_LOAD32((volatile int32_t*)&task->state);
    if (cur_state == EIDOS_TASK_COMPLETED) {
        done.invoke_fn   = continuation.invoke_fn;
        done.closure_ptr = continuation.closure_ptr;
        done.arg         = task->result;
    } else if (cur_state == EIDOS_TASK_CANCELLED) {
        done.invoke_fn   = continuation.invoke_fn;
        done.closure_ptr = continuation.closure_ptr;
        done.arg         = NULL;
    } else if (!task->completion.invoke_fn) {
        task->completion = continuation;
        ETASK_ATOMIC_STORE32((volatile int32_t*)&task->state, EIDOS_TASK_SUSPENDED);
    } else {
        eidos_lock_release(&task->completion_lock);
        eidos_panic("eidos_task_await: task already has a pending continuation");
        return;
    }

    eidos_lock_release(&task->completion_lock);

    if (done.invoke_fn) {
        eidos_schedule(done);
    }
}

/**
 * Complete a task with a result value.
 *
 * Called by the task trampoline (or manually for externally-driven tasks).
 * Stores the result, transitions state to COMPLETED atomically, and
 * schedules any registered awaiter continuation. Also notifies the
 * owning TaskGroup if present.
 *
 * @param task   The task to complete (must not be NULL)
 * @param result Shared pointer to the result (may be NULL). If non-NULL,
 *               a shared reference is acquired (eidos_incref_shared).
 */
void eidos_task_complete(EidosTask* task, void* result) {
    if (!task) return;

    EidosWorkItem done;
    memset(&done, 0, sizeof(EidosWorkItem));
    bool completed = false;

    eidos_lock_acquire(&task->completion_lock);

    int32_t previous_state = ETASK_ATOMIC_LOAD32((volatile int32_t*)&task->state);
    if (previous_state != EIDOS_TASK_COMPLETED && previous_state != EIDOS_TASK_CANCELLED) {
        task->result = result;
        eidos_task_retain_result(task, result);
        ETASK_ATOMIC_STORE32((volatile int32_t*)&task->state, EIDOS_TASK_COMPLETED);
        completed = true;

        if (task->completion.invoke_fn) {
            done.invoke_fn   = task->completion.invoke_fn;
            done.closure_ptr = task->completion.closure_ptr;
            done.arg         = result;
            memset(&task->completion, 0, sizeof(EidosWorkItem));
        }
    }

    eidos_lock_release(&task->completion_lock);

    if (!completed) {
        return;
    }

    if (done.invoke_fn) {
        eidos_schedule(done);
    }

    /* Notify owning TaskGroup, if any. */
    if (task->group) {
        eidos_taskgroup_on_task_done(task->group);
    }
}

/**
 * Destructor for EidosTask (registered for EIDOS_TYPE_TASK).
 *
 * Releases shared references to the result and group, if present.
 * Called by the memory system when the task's ref_count reaches zero.
 *
 * @param ptr Pointer to the EidosTask (after header)
 */
static void eidos_task_destructor(void* ptr) {
    EidosTask* task = (EidosTask*)ptr;
    if (!task) return;

    /* Release result. */
    if (task->result) {
        eidos_task_release_result(task, task->result);
        task->result = NULL;
    }

    /* Release group reference. */
    if (task->group) {
        eidos_decref_shared(task->group);
        task->group = NULL;
    }

    eidos_lock_destroy(&task->completion_lock);
}

/* ============================================================
 * TaskGroup - Public API
 * ============================================================ */

/**
 * Allocate a new TaskGroup.
 *
 * The group is heap-allocated with ref_count=1, marked as shared,
 * pending_count=0, and error_flag=0.
 *
 * @return Pointer to the new EidosTaskGroup (shared, ref_count=1)
 */
EidosTaskGroup* eidos_taskgroup_new(void) {
    ensure_destructors();

    EidosTaskGroup* group = (EidosTaskGroup*)eidos_alloc(
        sizeof(EidosTaskGroup), EIDOS_TYPE_TASKGROUP);
    if (!group) {
        fprintf(stderr, "eidos_taskgroup_new: out of memory\n");
        return NULL;
    }

    /* Initialize fields. eidos_alloc sets ref_count=1 and type_id. */
    group->pending_count = 0;
    group->error_flag    = 0;
    memset(&group->on_complete, 0, sizeof(EidosWorkItem));

    /* Mark as shared so atomic incref/decref is used. */
    eidos_share(group);

    return group;
}

/**
 * Spawn a task within a TaskGroup.
 *
 * Atomically increments the group's pending_count, then spawns a task
 * via eidos_task_spawn(). The task is linked to the group so that
 * eidos_task_complete() will call eidos_taskgroup_on_task_done().
 *
 * @param group      The TaskGroup to spawn within (must not be NULL)
 * @param closure    User's closure pointer
 * @param invoke_fn  User's function to execute
 * @param arg        Argument passed to invoke_fn
 * @return Pointer to the spawned EidosTask, or NULL on failure
 */
EidosTask* eidos_taskgroup_spawn(EidosTaskGroup* group,
                                  void* closure,
                                  void* (*invoke_fn)(void*, void*),
                                  void* arg) {
    return eidos_taskgroup_spawn_with_payload_mode(group, closure, invoke_fn, arg, 0, 0);
}

static EidosTask* eidos_taskgroup_spawn_with_payload_mode(EidosTaskGroup* group,
                                                          void* closure,
                                                          void* (*invoke_fn)(void*, void*),
                                                          void* arg,
                                                          uint32_t raw_payloads,
                                                          uint32_t release_result_after_complete) {
    if (!group) return NULL;

    ensure_scheduler();

    /* Increment pending count BEFORE spawning (ensures join sees the count). */
    ETASK_ATOMIC_INC32((volatile int32_t*)&group->pending_count);

    EidosTask* task = eidos_task_spawn_with_payload_mode_and_group(
        closure,
        invoke_fn,
        arg,
        raw_payloads,
        release_result_after_complete,
        group);
    if (!task) {
        /* Spawn failed: undo the pending increment. */
        ETASK_ATOMIC_DEC32((volatile int32_t*)&group->pending_count);
        return NULL;
    }

    return task;
}

EidosTask* eidos_taskgroup_spawn_closure_raw(EidosTaskGroup* group, EidosClosure* thunk) {
    if (!group || !thunk) {
        return NULL;
    }

    eidos_share(thunk);
    eidos_incref_shared(thunk);
    EidosTask* task = eidos_taskgroup_spawn_with_payload_mode(
        group,
        thunk,
        closure_task_raw_invoke,
        NULL,
        1,
        0);
    if (!task) {
        eidos_decref_shared(thunk);
    }

    return task;
}

EidosTask* eidos_taskgroup_spawn_closure_value(EidosTaskGroup* group, EidosClosure* thunk) {
    if (!group || !thunk) {
        return NULL;
    }

    eidos_share(thunk);
    eidos_incref_shared(thunk);
    EidosTask* task = eidos_taskgroup_spawn_with_payload_mode(
        group,
        thunk,
        closure_task_raw_invoke,
        NULL,
        0,
        1);
    if (!task) {
        eidos_decref_shared(thunk);
    }

    return task;
}

void eidos_taskgroup_join_closure_raw(EidosTaskGroup* group, EidosClosure* continuation) {
    if (!group || !continuation) {
        return;
    }

    eidos_share(continuation);
    eidos_incref_shared(continuation);
    EidosWorkItem work;
    work.invoke_fn = closure_unit_continuation_invoke;
    work.closure_ptr = continuation;
    work.arg = NULL;
    eidos_taskgroup_join(group, work);
}

/**
 * Join a TaskGroup: register a continuation for when all tasks complete.
 *
 * If all tasks have already finished (pending_count == 0), the
 * continuation is scheduled immediately. Otherwise, the continuation
 * is stored and will be scheduled when the last task calls
 * eidos_taskgroup_on_task_done().
 *
 * Race handling: we store the continuation first, then re-check
 * pending_count. If it is zero, we schedule immediately. This
 * avoids the race where tasks complete between checking and storing.
 *
 * @param group        The TaskGroup to join (must not be NULL)
 * @param continuation Work item to schedule when all tasks complete
 */
void eidos_taskgroup_join(EidosTaskGroup* group, EidosWorkItem continuation) {
    if (!group) return;

    /*
     * Store the continuation first, then check pending_count.
     * This ordering prevents the race where:
     *   1. We check pending == 0
     *   2. A new task is spawned (pending -> 1)
     *   3. The task completes and on_task_done sees no continuation
     *
     * With store-first ordering:
     *   - If pending is 0, we schedule immediately (safe, no tasks remain).
     *   - If pending is > 0, on_task_done will see our continuation when
     *     the last task finishes.
     *   - If a new task is spawned after our check, on_task_done won't fire
     *     until that task also finishes, so the continuation waits correctly.
     */
    group->on_complete = continuation;

    /* Re-check: if all tasks are already done, schedule immediately. */
    if (ETASK_ATOMIC_LOAD32((volatile int32_t*)&group->pending_count) == 0) {
        /*
         * Double-check: clear on_complete to prevent on_task_done from
         * also scheduling. Since pending is 0, no more on_task_done calls
         * are coming (no tasks are running), so this is safe.
         */
        EidosWorkItem done = group->on_complete;
        memset(&group->on_complete, 0, sizeof(EidosWorkItem));
        eidos_schedule(done);
    }
}

/**
 * Called by eidos_task_complete() when a task with a group finishes.
 *
 * Atomically decrements pending_count. If it reaches zero and an
 * on_complete continuation is registered, schedules it.
 *
 * @param group The TaskGroup to notify (must not be NULL)
 */
static void eidos_taskgroup_on_task_done(EidosTaskGroup* group) {
    if (!group) return;

    /*
     * Atomic decrement of pending_count. The return value is the NEW count
     * (after decrement). When it reaches 0, all tasks in the group are done.
     */
    int32_t new_count = ETASK_ATOMIC_DEC32((volatile int32_t*)&group->pending_count);

    if (new_count == 0) {
        /* All tasks finished. Schedule on_complete if registered. */
        if (group->on_complete.invoke_fn) {
            EidosWorkItem done = group->on_complete;
            memset(&group->on_complete, 0, sizeof(EidosWorkItem));
            eidos_schedule(done);
        }
    }
}

/**
 * Cancel a TaskGroup.
 *
 * Sets the error_flag atomically. Currently this is a flag only --
 * actual cooperative cancellation of running tasks is a future feature.
 * Tasks can check group->error_flag to implement voluntary cancellation.
 *
 * @param group The TaskGroup to cancel (must not be NULL)
 */
void eidos_taskgroup_cancel(EidosTaskGroup* group) {
    if (!group) return;
    ETASK_ATOMIC_STORE32((volatile int32_t*)&group->error_flag, 1);
}

bool eidos_taskgroup_is_cancelled(struct EidosTaskGroup* group)
{
    if (!group) {
        return false;
    }

    return ETASK_ATOMIC_LOAD32((volatile int32_t*)&group->error_flag) != 0;
}

int64_t eidos_taskgroup_pending_count(struct EidosTaskGroup* group)
{
    if (!group) {
        return 0;
    }

    return (int64_t)ETASK_ATOMIC_LOAD32((volatile int32_t*)&group->pending_count);
}

/**
 * Destructor for EidosTaskGroup (registered for EIDOS_TYPE_TASKGROUP).
 *
 * No internal heap allocations to free -- the group's memory is released
 * by eidos_free() after this destructor runs.
 *
 * @param ptr Pointer to the EidosTaskGroup (after header)
 */
static void eidos_taskgroup_destructor(void* ptr) {
    EidosTaskGroup* group = (EidosTaskGroup*)ptr;
    if (!group) return;

    /* No owned resources to release beyond the struct itself. */
    memset(&group->on_complete, 0, sizeof(EidosWorkItem));
}

/* ============================================================
 * Task Runtime Shutdown
 * ============================================================ */

/**
 * Shut down the task runtime and the underlying scheduler.
 *
 * Idempotent: safe to call multiple times. Resets the auto-initialization
 * flag so a subsequent spawn will re-initialize if needed.
 */
void eidos_task_runtime_shutdown(void) {
    if (ETASK_ATOMIC_LOAD32(&g_task_runtime_initialized) == EIDOS_TASK_RUNTIME_READY) {
        eidos_scheduler_shutdown();
        ETASK_ATOMIC_STORE32(&g_task_runtime_initialized, EIDOS_TASK_RUNTIME_UNINIT);
    }
}
