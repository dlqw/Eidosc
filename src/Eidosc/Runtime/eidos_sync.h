/**
 * Eidos Runtime - Platform Synchronization Abstraction
 *
 * Provides platform-abstracted primitives for multithreading:
 *   - Native lock (SRWLOCK / pthread_mutex_t)
 *   - Native condition variable (CONDITION_VARIABLE / pthread_cond_t)
 *   - Thread handle (HANDLE / pthread_t)
 *   - Atomic CAS32
 *
 * All wrapper functions are static inline (header-only).
 * Include this after eidos_runtime.h.
 */

#ifndef EIDOS_SYNC_H
#define EIDOS_SYNC_H

#include "eidos_runtime.h"

#ifdef __cplusplus
extern "C" {
#endif

/* ============================================================
 * Platform Detection
 * ============================================================ */

#if defined(_WIN32) || defined(_WIN64)
    #define EIDOS_WIN  1
    #define EIDOS_POSIX 0
#elif defined(__unix__) || defined(__APPLE__) || defined(__linux__)
    #define EIDOS_WIN  0
    #define EIDOS_POSIX 1
#else
    #error "Eidos sync: unsupported platform"
#endif

/* ============================================================
 * Platform Headers
 * ============================================================ */

#if EIDOS_WIN
    #define WIN32_LEAN_AND_MEAN
    #include <windows.h>
#elif EIDOS_POSIX
    #include <pthread.h>
#endif

/* ============================================================
 * Native Lock
 * ============================================================ */

#if EIDOS_WIN
    typedef SRWLOCK EidosNativeLock;
#elif EIDOS_POSIX
    typedef pthread_mutex_t EidosNativeLock;
#endif

static inline void eidos_lock_init(EidosNativeLock* lock)
{
#if EIDOS_WIN
    InitializeSRWLock(lock);
#elif EIDOS_POSIX
    pthread_mutex_init(lock, NULL);
#endif
}

static inline void eidos_lock_destroy(EidosNativeLock* lock)
{
#if EIDOS_WIN
    (void)lock;
#elif EIDOS_POSIX
    pthread_mutex_destroy(lock);
#endif
}

/** Acquire exclusive (writer) ownership. */
static inline void eidos_lock_acquire(EidosNativeLock* lock)
{
#if EIDOS_WIN
    AcquireSRWLockExclusive(lock);
#elif EIDOS_POSIX
    pthread_mutex_lock(lock);
#endif
}

/** Release exclusive (writer) ownership. */
static inline void eidos_lock_release(EidosNativeLock* lock)
{
#if EIDOS_WIN
    ReleaseSRWLockExclusive(lock);
#elif EIDOS_POSIX
    pthread_mutex_unlock(lock);
#endif
}

/** Acquire shared (reader) ownership. Only meaningful on SRWLOCK. */
static inline void eidos_lock_acquire_shared(EidosNativeLock* lock)
{
#if EIDOS_WIN
    AcquireSRWLockShared(lock);
#elif EIDOS_POSIX
    /* POSIX mutex has no shared mode; fall back to exclusive. */
    pthread_mutex_lock(lock);
#endif
}

/** Release shared (reader) ownership. */
static inline void eidos_lock_release_shared(EidosNativeLock* lock)
{
#if EIDOS_WIN
    ReleaseSRWLockShared(lock);
#elif EIDOS_POSIX
    pthread_mutex_unlock(lock);
#endif
}

/* ============================================================
 * Native Condition Variable
 * ============================================================ */

#if EIDOS_WIN
    typedef CONDITION_VARIABLE EidosNativeCond;
#elif EIDOS_POSIX
    typedef pthread_cond_t EidosNativeCond;
#endif

static inline void eidos_cond_init(EidosNativeCond* cond)
{
#if EIDOS_WIN
    InitializeConditionVariable(cond);
#elif EIDOS_POSIX
    pthread_cond_init(cond, NULL);
#endif
}

/** Wake one waiting thread. */
static inline void eidos_cond_signal(EidosNativeCond* cond)
{
#if EIDOS_WIN
    WakeConditionVariable(cond);
#elif EIDOS_POSIX
    pthread_cond_signal(cond);
#endif
}

/** Wake all waiting threads. */
static inline void eidos_cond_broadcast(EidosNativeCond* cond)
{
#if EIDOS_WIN
    WakeAllConditionVariable(cond);
#elif EIDOS_POSIX
    pthread_cond_broadcast(cond);
#endif
}

/**
 * Wait on cond, protected by lock (exclusive).
 * Spurious wakeups are possible; callers must re-check their predicate.
 */
static inline void eidos_cond_wait(EidosNativeCond* cond, EidosNativeLock* lock)
{
#if EIDOS_WIN
    SleepConditionVariableSRW(cond, lock, INFINITE, 0);
#elif EIDOS_POSIX
    pthread_cond_wait(cond, lock);
#endif
}

/* ============================================================
 * Thread
 * ============================================================ */

#if EIDOS_WIN
    typedef HANDLE EidosThread;
    typedef DWORD  EidosThreadId;
#elif EIDOS_POSIX
    typedef pthread_t EidosThread;
    typedef pthread_t EidosThreadId;
#endif

/** Thread entry-point signature. */
typedef void* (*EidosThreadFn)(void* arg);

static inline int eidos_thread_create(EidosThread*   thread,
                                      EidosThreadFn  fn,
                                      void*          arg)
{
#if EIDOS_WIN
    *thread = CreateThread(NULL, 0, (LPTHREAD_START_ROUTINE)fn, arg, 0, NULL);
    return (*thread != NULL) ? 0 : (int)GetLastError();
#elif EIDOS_POSIX
    return pthread_create(thread, NULL, fn, arg);
#endif
}

static inline int eidos_thread_join(EidosThread thread)
{
#if EIDOS_WIN
    DWORD rc = WaitForSingleObject(thread, INFINITE);
    CloseHandle(thread);
    return (rc == WAIT_OBJECT_0) ? 0 : (int)rc;
#elif EIDOS_POSIX
    return pthread_join(thread, NULL);
#endif
}

static inline EidosThreadId eidos_thread_self_id(void)
{
#if EIDOS_WIN
    return GetCurrentThreadId();
#elif EIDOS_POSIX
    return pthread_self();
#endif
}

/* ============================================================
 * Atomic CAS32
 *
 * Performs: if *ptr == expected, *ptr = desired, returns true.
 *           otherwise returns false (and writes the actual value
 *           into *expected).
 * ============================================================ */

static inline bool eidos_cas32(volatile int32_t* ptr,
                               int32_t*         expected,
                               int32_t          desired)
{
#if EIDOS_WIN
    LONG old = (LONG)*expected;
    LONG prev = InterlockedCompareExchange((LONG volatile*)ptr,
                                           (LONG)desired, old);
    if (prev == old) return true;
    *expected = (int32_t)prev;
    return false;
#elif EIDOS_POSIX
    return __atomic_compare_exchange_n(ptr, expected, desired,
                                       0 /* strong */,
                                       __ATOMIC_SEQ_CST,
                                       __ATOMIC_SEQ_CST);
#endif
}

#ifdef __cplusplus
}
#endif

#endif /* EIDOS_SYNC_H */
