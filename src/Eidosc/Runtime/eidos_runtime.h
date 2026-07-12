/**
 * Eidos Runtime - C Header
 *
 * Provides memory management and runtime support for Eidos programs.
 * This header defines the interface between generated LLVM IR and the runtime system.
 */

#ifndef EIDOS_RUNTIME_H
#define EIDOS_RUNTIME_H

#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ============================================================
 * Memory Management (Perceus Reference Counting)
 * ============================================================ */

/**
 * Eidos object header - prepended to every heap-allocated object
 */
typedef struct EidosHeader {
    int32_t ref_count;     /* Reference count (32-bit, accessed atomically via EIDOS_ATOMIC_INC32/DEC32) */
    uint32_t type_id;      /* Type identifier for RTTI */
} EidosHeader;

/* Shared-bit reference counting constants.
 * Bit 31 = SHARED flag. Bits 0-30 = actual count.
 * See eidos_memory.c for full documentation. */
#define EIDOS_SHARED_BIT  0x80000000
#define EIDOS_COUNT_MASK  0x7FFFFFFF

/**
 * Allocate memory with Eidos header
 * @param size Size of the object (not including header)
 * @param type_id Type identifier
 * @return Pointer to the object (after header)
 */
void* eidos_alloc(size_t size, uint32_t type_id);

/**
 * Free memory allocated by eidos_alloc
 * @param ptr Pointer to the object
 */
void eidos_free(void* ptr);

/**
 * Increment reference count
 * @param ptr Pointer to the object
 * @return The same pointer (for chaining)
 */
void* eidos_incref(void* ptr);

/**
 * Decrement reference count and free if zero
 * @param ptr Pointer to the object
 */
void eidos_decref(void* ptr);

/**
 * Backward-compatible alias for eidos_incref
 * @param ptr Pointer to the object
 * @return The same pointer (for chaining)
 */
void* eidos_dup(void* ptr);

/**
 * Backward-compatible alias for eidos_decref
 * @param ptr Pointer to the object
 */
void eidos_drop(void* ptr);

/**
 * Mark an object as thread-shared (sticky reference count).
 * Shared objects use atomic reference counting and are not freed
 * through normal decref. This enables the non-atomic fast path
 * for single-threaded objects (positive ref_count).
 * @param ptr Pointer to the object
 */
void eidos_share(void* ptr);

/**
 * Non-atomic increment for single-threaded objects.
 * Only safe when the object is guaranteed to be accessed by one thread.
 * @param ptr Pointer to the object
 * @return The same pointer (for chaining)
 */
void* eidos_incref_local(void* ptr);

/**
 * Non-atomic decrement for single-threaded objects.
 * Only safe when the object is guaranteed to be accessed by one thread.
 * @param ptr Pointer to the object
 */
void eidos_decref_local(void* ptr);

/**
 * Atomic increment for thread-shared objects (SHARED bit set).
 * Safe to call from any thread.
 * @param ptr Pointer to the object
 */
void eidos_incref_shared(void* ptr);

/**
 * Atomic decrement for thread-shared objects (SHARED bit set).
 * Frees the object when the count portion reaches zero.
 * Safe to call from any thread.
 * @param ptr Pointer to the object
 */
void eidos_decref_shared(void* ptr);

/* ============================================================
 * Drop-in-place Reuse (Koka kk_reuse_t pattern)
 * ============================================================ */

/**
 * Reuse slot for drop-then-alloc optimization.
 * Declare on the stack, pass to eidos_drop_reuse/eidos_alloc_reuse.
 * When a drop is followed by an alloc of the same type, the freed
 * memory block is recycled in-place, avoiding malloc/free overhead.
 */
typedef struct EidosReuse {
    void*    header_ptr;   /* Pointer to the EidosHeader (not the object) */
    size_t   total_size;   /* Total allocation size including header */
    uint32_t type_id;      /* Type ID of the freed object */
} EidosReuse;

/**
 * Allocate with potential reuse of a previously dropped block.
 * If reuse->type_id matches type_id and the block is large enough,
 * recycles the block instead of calling malloc.
 * @param reuse Reuse slot (may be NULL for normal allocation)
 * @param obj_size Size of the object (not including header)
 * @param type_id Type identifier
 * @return Pointer to the object (after header)
 */
void* eidos_alloc_reuse(EidosReuse* reuse, size_t obj_size, uint32_t type_id);

/**
 * Drop with reuse: decref and, if the object is freed, store its
 * memory block in the reuse slot for potential recycling.
 * @param ptr Pointer to the object
 * @param reuse Reuse slot (may be NULL for normal drop)
 */
void eidos_drop_reuse(void* ptr, EidosReuse* reuse);

/* ============================================================
 * String Operations
 * ============================================================ */

/**
 * Eidos string header
 */
typedef struct EidosString {
    EidosHeader header;
    size_t length;
    char data[];
} EidosString;

/**
 * Create a new Eidos string from C string
 * @param str Null-terminated C string
 * @return Eidos string object
 */
EidosString* eidos_string_from_cstr(const char* str);

/**
 * Intern a C string slice and return a caller-owned reference
 * @param data String data
 * @param len Length of string in bytes
 * @return Interned Eidos string object
 */
EidosString* eidos_string_intern(const char* data, size_t len);

/**
 * Create a new Eidos string from data
 * @param data String data
 * @param len Length of string
 * @return Eidos string object
 */
EidosString* eidos_string_new(const char* data, size_t len);

/**
 * Concatenate two strings
 * @param a First string
 * @param b Second string
 * @return New concatenated string
 */
EidosString* eidos_string_concat(EidosString* a, EidosString* b);

/**
 * Get string length
 * @param str Eidos string
 * @return Length in bytes (0 if null)
 */
size_t eidos_string_length(EidosString* str);

/**
 * Get byte at index from string
 * @param str Eidos string
 * @param index Byte index
 * @return Unsigned byte value, or -1 when out of bounds
 */
int64_t eidos_string_char_at(EidosString* str, size_t index);

/**
 * Slice string by byte range
 * @param str Eidos string
 * @param start Start byte index
 * @param len Slice length in bytes
 * @return New Eidos string (empty when out of range)
 */
EidosString* eidos_string_slice(EidosString* str, size_t start, size_t len);

/**
 * Compare two strings by content
 * @param a First string
 * @param b Second string
 * @return true if content is equal, false otherwise
 */
bool eidos_string_equals(EidosString* a, EidosString* b);

/**
 * Create a one-byte string from a character code.
 * @param value Character code (lower 8 bits are used)
 * @return New Eidos string containing a single byte
 */
EidosString* eidos_string_from_char(int64_t value);

/**
 * Convert an integer to a decimal string.
 * @param value Integer value
 * @return New Eidos string containing the decimal representation
 */
EidosString* eidos_int_to_string(int64_t value);

/**
 * Convert an integer to a float.
 */
double eidos_int_to_float(int64_t value);

/**
 * Convert a string to a float.
 */
double eidos_string_to_float(EidosString* str);

/* ============================================================
 * I/O Operations
 * ============================================================ */

/**
 * Print an integer to stdout
 * @param value Integer value to print
 */
void eidos_print_int(int64_t value);

/**
 * Print a float to stdout
 * @param value Float value to print
 */
void eidos_print_float(double value);

/**
 * Print a string to stdout
 * @param str Eidos string to print
 */
void eidos_print_string(EidosString* str);

/**
 * Print a newline to stdout
 */
void eidos_print_newline(void);

/**
 * Print a single character to stdout
 * @param value Character code (lower 8 bits are used)
 */
void eidos_print_char(int64_t value);

/**
 * Read a single line from stdin without the trailing newline.
 * @return New Eidos string, or empty string on EOF/error
 */
EidosString* eidos_read_line(void);

/**
 * Read a single character from stdin (non-blocking in raw mode).
 * @return Character code, or -1 if no input available / on EOF
 */
int64_t eidos_read_char(void);

/**
 * Set terminal to raw mode: no echo, no line buffering, immediate key response.
 */
void eidos_terminal_set_raw(void);

/**
 * Restore terminal to normal mode (undo eidos_terminal_set_raw).
 */
void eidos_terminal_restore(void);

/**
 * Sleep for the specified number of milliseconds.
 * @param ms Milliseconds to sleep
 */
void eidos_sleep_ms(int64_t ms);

/**
 * Extract the underlying C string pointer from an EidosString.
 * The returned pointer is valid as long as the EidosString is alive.
 * @param str Eidos string object
 * @return Raw C string pointer (const char*)
 */
void* eidos_string_to_cstr(EidosString* str);

/**
 * Create an EidosString from a raw C string pointer (copies the data).
 * @param cstr Raw C string pointer
 * @return New Eidos string object
 */
EidosString* eidos_string_from_cstr_raw(const char* cstr);

/**
 * Return a null pointer.
 * @return NULL
 */
void* eidos_ptr_null(void);

/**
 * Check if a raw pointer is null.
 * @param ptr Pointer to check
 * @return true if ptr is NULL
 */
bool eidos_ptr_is_null(void* ptr);

/**
 * Check whether two raw pointers are equal.
 */
bool eidos_ptr_equals(void* left, void* right);

/**
 * Query whether the last runtime IO helper succeeded.
 * @return true if the last IO helper finished successfully
 */
bool eidos_io_last_success(void);

/**
 * Get the last runtime IO helper error message.
 * @return Error message for the last failed IO helper, or empty string on success
 */
EidosString* eidos_io_last_error(void);

/**
 * Check whether a file exists.
 * @param path UTF-8 file path
 * @return true when the file can be opened for reading
 */
bool eidos_file_exists(EidosString* path);

/**
 * Read an entire text file into memory.
 * @param path UTF-8 file path
 * @return File contents, or empty string on error
 */
EidosString* eidos_file_read_all_text(EidosString* path);

/**
 * Write text to a file, replacing any existing contents.
 * @param path UTF-8 file path
 * @param content UTF-8 file contents
 * @return true on success
 */
bool eidos_file_write_all_text(EidosString* path, EidosString* content);

/**
 * Capture native process command-line arguments for Std/CommandLine.
 * @param argc Number of native arguments, including executable path
 * @param argv Native argv vector
 */
void eidos_command_line_init(int64_t argc, char** argv);

/**
 * Get the captured native argc value.
 * @return Number of native arguments, including executable path
 */
int64_t eidos_command_line_argc(void);

/**
 * Get a captured command-line argument or a fallback.
 * @param index Native argv index
 * @param fallback Value returned when index is out of range
 * @return Argument text or fallback
 */
EidosString* eidos_command_line_arg_or(int64_t index, EidosString* fallback);

/**
 * Fetch text content using a best-effort HTTP GET implementation.
 * Current implementation invokes curl directly (without shell wrapping),
 * captures body/metadata/error output in memory, records HTTP metadata for the
 * last request, and returns an empty string on failure.
 * @param url HTTP/HTTPS URL
 * @return Response body, or empty string on error
 */
EidosString* eidos_http_get_text(EidosString* url);
EidosString* eidos_http_request_text(
    EidosString* method,
    EidosString* url,
    EidosString* content_type,
    EidosString* body);
EidosString* eidos_http_request_text_with_headers(
    EidosString* method,
    EidosString* url,
    EidosString* content_type,
    EidosString* body,
    EidosString* headers);
EidosString* eidos_http_request_text_with_options(
    EidosString* method,
    EidosString* url,
    EidosString* content_type,
    EidosString* body,
    EidosString* headers,
    int64_t connect_timeout_seconds,
    int64_t total_timeout_seconds);
EidosString* eidos_http_request_body_hex_with_options(
    EidosString* method,
    EidosString* url,
    EidosString* content_type,
    EidosString* body,
    EidosString* headers,
    int64_t connect_timeout_seconds,
    int64_t total_timeout_seconds);
EidosString* eidos_http_request_text_with_binary_body_options(
    EidosString* method,
    EidosString* url,
    EidosString* content_type,
    EidosString* body_hex,
    EidosString* headers,
    int64_t connect_timeout_seconds,
    int64_t total_timeout_seconds);
EidosString* eidos_http_request_body_hex_with_binary_body_options(
    EidosString* method,
    EidosString* url,
    EidosString* content_type,
    EidosString* body_hex,
    EidosString* headers,
    int64_t connect_timeout_seconds,
    int64_t total_timeout_seconds);

/**
 * Get the status code captured from the last HTTP request.
 * Returns 0 when the request failed before a status code was observed.
 * @return Last HTTP status code, or 0
 */
int64_t eidos_http_last_status_code(void);

/**
 * Get the effective URL captured from the last HTTP request.
 * Returns an empty string when unavailable.
 * @return Last effective URL
 */
EidosString* eidos_http_last_effective_url(void);

/**
 * Get the response content type captured from the last HTTP request.
 * Returns an empty string when unavailable.
 * @return Last response content type
 */
EidosString* eidos_http_last_content_type(void);

/**
 * Get the response headers captured from the last HTTP request.
 * Returns newline-separated `Name: value` lines for the final response.
 * @return Last response headers, or empty string
 */
EidosString* eidos_http_last_headers(void);
int64_t eidos_http_backend_selected_kind(void);

/**
 * Get runtime type-id from an allocated object.
 * @param ptr Object pointer returned by eidos_alloc
 * @return Type identifier in object header, or 0 for null
 */
int64_t eidos_type_id(void* ptr);

/* ============================================================
 * Time Operations
 * ============================================================ */

/** 获取当前 Unix 时间戳（秒） */
int64_t eidos_time_now(void);

/** 获取当前毫秒时间戳 */
int64_t eidos_time_now_ms(void);

/** 格式化时间戳，返回写入的字节数 */
int64_t eidos_time_format(int64_t timestamp, char* buf, int64_t buf_len, const char* format_str);

/** 提取时间字段 */
int64_t eidos_time_year(int64_t timestamp);
int64_t eidos_time_month(int64_t timestamp);
int64_t eidos_time_day(int64_t timestamp);
int64_t eidos_time_hour(int64_t timestamp);
int64_t eidos_time_minute(int64_t timestamp);
int64_t eidos_time_second(int64_t timestamp);

/* ============================================================
 * Array/List Operations
 * ============================================================ */

/**
 * Eidos array header
 */
typedef struct EidosArray {
    EidosHeader header;
    size_t length;
    size_t capacity;
    size_t element_size;
    void (*retain_element)(void* element);
    void (*release_element)(void* element);
    unsigned char data[];
} EidosArray;

typedef struct EidosClosure {
    EidosHeader header;
    void* invoke_fn;
    void* release_fn;
    size_t payload_words;
    uintptr_t payload[];
} EidosClosure;

/**
 * Create a new runtime closure object.
 * The returned closure owns its payload slots and will call release_fn when
 * its ref-count reaches zero.
 * @param invoke_fn Typed invoke thunk pointer
 * @param release_fn Optional release thunk pointer
 * @param payload_words Number of machine-word payload slots
 * @return New closure object
 */
EidosClosure* eidos_closure_new(void* invoke_fn, void* release_fn, size_t payload_words);

/**
 * Create a new array with given capacity
 * @param capacity Initial capacity
 * @param element_size Size of each element
 * @return New array
 */
EidosArray* eidos_array_new(size_t capacity, size_t element_size);

/**
 * Create a new array with per-element ownership policy.
 * @param capacity Initial capacity
 * @param element_size Size of each element
 * @param retain_element Optional element retain thunk
 * @param release_element Optional element release thunk
 * @return New array
 */
EidosArray* eidos_array_new_with_policy(
    size_t capacity,
    size_t element_size,
    void (*retain_element)(void* element),
    void (*release_element)(void* element));

/**
 * Get array length
 * @param arr Array
 * @return Array length (0 if null)
 */
size_t eidos_array_length(EidosArray* arr);

/**
 * Get array element
 * @param arr Array
 * @param index Element index
 * @return Pointer to element
 */
void* eidos_array_get(EidosArray* arr, size_t index);

/**
 * Set array element
 * @param arr Array
 * @param index Element index
 * @param value Pointer to value
 * @param element_size Size of element (reserved for ABI compatibility; runtime uses array element size)
 */
void eidos_array_set(EidosArray* arr, size_t index, void* value, size_t element_size);

/**
 * Push element to array end
 * @param arr Array
 * @param value Pointer to value
 * @param element_size Size of element (reserved for ABI compatibility; runtime uses array element size)
 * @return Array (possibly reallocated)
 */
EidosArray* eidos_array_push(EidosArray* arr, void* value, size_t element_size);

/**
 * Append all elements from src array to dst array.
 * Elements are shallow-copied (pointer-sized values).
 * @param dst Destination array (possibly reallocated)
 * @param src Source array to copy elements from
 * @param element_size Element size for compatibility
 * @return Destination array (possibly reallocated)
 */
EidosArray* eidos_array_extend(EidosArray* dst, EidosArray* src, size_t element_size);

/**
 * Remove the last array element by shortening the logical length.
 * Calling this on a null or empty array is a no-op.
 * @param arr Array
 */
void eidos_array_pop(EidosArray* arr);

/**
 * Swap two array elements in place.
 * @param arr Array
 * @param left First element index
 * @param right Second element index
 */
void eidos_array_swap(EidosArray* arr, size_t left, size_t right);

/* ============================================================
 * Panic and Error Handling
 * ============================================================ */

/**
 * Abort with error message
 * @param message Error message
 */
_Noreturn void eidos_panic(const char* message);

/**
 * Assert condition
 * @param condition Condition to check
 * @param message Error message if failed
 */
void eidos_assert(int condition, const char* message);

/* ============================================================
 * Destructor System
 * ============================================================ */

/**
 * Destructor function pointer type
 * Called when an object's reference count reaches zero
 * @param ptr Pointer to the object (after header)
 */
typedef void (*EidosDestructor)(void* ptr);

/**
 * Register a destructor for a type
 * @param type_id Type identifier
 * @param destructor Destructor function
 */
void eidos_register_destructor(uint32_t type_id, EidosDestructor destructor);

/* ============================================================
 * Type Information
 * ============================================================ */

/* Type IDs for built-in types */
#define EIDOS_TYPE_UNIT     0
#define EIDOS_TYPE_INT      1
#define EIDOS_TYPE_FLOAT    2
#define EIDOS_TYPE_BOOL     3
#define EIDOS_TYPE_STRING   4
#define EIDOS_TYPE_ARRAY    5
#define EIDOS_TYPE_CLOSURE  6
#define EIDOS_TYPE_ADT      100  /* Start of ADT types */

/* ============================================================
 * Scheduler Types
 * ============================================================ */

/** Task lifecycle states. */
typedef enum EidosTaskState {
    EIDOS_TASK_CREATED   = 0,
    EIDOS_TASK_SCHEDULED = 1,
    EIDOS_TASK_RUNNING   = 2,
    EIDOS_TASK_SUSPENDED = 3,
    EIDOS_TASK_COMPLETED = 4,
    EIDOS_TASK_CANCELLED = 5
} EidosTaskState;

/** Work item: a closure + invoke function + argument. */
typedef struct EidosWorkItem {
    void* (*invoke_fn)(void* closure, void* arg);
    void*    closure_ptr;
    void*    arg;
} EidosWorkItem;

/** Type IDs for sync primitives. */
#define EIDOS_TYPE_TASK       50
#define EIDOS_TYPE_TASKGROUP  51
#define EIDOS_TYPE_CHANNEL    52
#define EIDOS_TYPE_PROMISE    53
#define EIDOS_TYPE_FUTURE     54
#define EIDOS_TYPE_MUTEX      55
#define EIDOS_TYPE_RWLOCK     56
#define EIDOS_TYPE_MUTEXGUARD 57
#define EIDOS_TYPE_BARRIER    58

/* Forward declarations for scheduler internals. */
struct EidosTask;
struct EidosTaskGroup;
struct EidosScheduler;
struct EidosChannel;
struct EidosPromise;
struct EidosFuture;
struct EidosMutex;
struct EidosRwLock;
struct EidosBarrier;

/* ============================================================
 * Scheduler API
 * ============================================================ */

/**
 * Initialise the work-stealing scheduler.
 * @param worker_count Number of worker threads. 0 = hardware concurrency.
 */
void eidos_scheduler_init(uint32_t worker_count);

/**
 * Shut down the scheduler. Joins all worker threads and frees resources.
 */
void eidos_scheduler_shutdown(void);

/**
 * Post a work item for execution. Prefers the current worker's local
 * queue, then falls back to the global queue.
 * @param item Work item to schedule
 */
void eidos_schedule(EidosWorkItem item);

/**
 * Return the current thread's worker index.
 * @return Worker index, or UINT32_MAX if not a worker thread.
 */
uint32_t eidos_worker_index(void);

/* ============================================================
 * Task / TaskGroup API
 *
 * Tasks are units of concurrent work that run on the scheduler.
 * TaskGroups implement structured concurrency: a group of tasks
 * whose completion can be awaited collectively.
 * Implementation lives in eidos_task.c.
 * ============================================================ */

/**
 * Spawn a task that runs invoke_fn(closure, arg) on the scheduler.
 *
 * Auto-initializes the scheduler if not already running.
 * The returned task is shared (ref_count=1); use eidos_task_await()
 * to register a continuation or eidos_decref_shared() to release.
 *
 * @param closure   User's closure pointer (passed to invoke_fn)
 * @param invoke_fn User's function to execute on the scheduler
 * @param arg       Argument passed to invoke_fn
 * @return Pointer to the spawned EidosTask (shared, ref_count=1)
 */
struct EidosTask* eidos_task_spawn(void* closure,
                                    void* (*invoke_fn)(void*, void*),
                                    void* arg);

/**
 * Spawn a raw-payload task from an Eidos closure of type Unit -> RawPtr.
 * The runtime retains the closure until the scheduler invokes it.
 */
struct EidosTask* eidos_task_spawn_closure_raw(EidosClosure* thunk);

/**
 * Spawn a boxed-value task from an Eidos closure of type Unit -> RawPtr.
 * The returned RawPtr must be a managed boxed value; the task retains it and
 * releases the closure return reference after completion.
 */
struct EidosTask* eidos_task_spawn_closure_value(EidosClosure* thunk);

/**
 * Await completion of a task, registering a continuation.
 *
 * If the task is already COMPLETED, the continuation is scheduled
 * immediately with the task's result as its argument.
 * Otherwise, the continuation is stored and scheduled when the
 * task completes.
 *
 * @param task         The task to await
 * @param continuation Work item to schedule when the task completes
 */
void eidos_task_await(struct EidosTask* task, EidosWorkItem continuation);

/**
 * Await a raw-payload task with an Eidos closure of type RawPtr -> Unit.
 * The runtime retains the closure until the continuation runs.
 */
void eidos_task_await_closure_raw(struct EidosTask* task,
                                  EidosClosure* continuation);

/**
 * Await a boxed-value task with an Eidos closure of type RawPtr -> Unit.
 * The runtime retains the closure until the continuation runs.
 */
void eidos_task_await_closure_value(struct EidosTask* task,
                                    EidosClosure* continuation);

/**
 * Complete a task with a result value.
 *
 * Called internally by the task trampoline. May also be called
 * manually for externally-driven tasks.
 *
 * @param task   The task to complete
 * @param result Shared pointer to the result (may be NULL)
 */
void eidos_task_complete(struct EidosTask* task, void* result);

/**
 * Create an already-completed raw-payload task.
 *
 * Raw-payload tasks do not retain or release result values. They are for
 * low-level FFI/runtime handles, not managed Eidos heap values.
 */
struct EidosTask* eidos_task_new_completed_raw(void* result);

/**
 * Create an already-completed boxed-value task.
 *
 * The task retains the boxed value and releases it when the task is destroyed.
 */
struct EidosTask* eidos_task_new_completed_value(void* result);

/**
 * Return whether a task has reached the completed state.
 */
bool eidos_task_is_completed(struct EidosTask* task);

/**
 * Return the completed raw payload immediately, or NULL when not completed.
 */
void* eidos_task_try_get_raw(struct EidosTask* task);

/**
 * Return the completed boxed payload immediately, or NULL when not completed.
 */
void* eidos_task_try_get_value(struct EidosTask* task);

/**
 * Create a new TaskGroup.
 *
 * @return Pointer to the new EidosTaskGroup (shared, ref_count=1)
 */
struct EidosTaskGroup* eidos_taskgroup_new(void);

/**
 * Spawn a task within a TaskGroup.
 *
 * Atomically increments the group's pending_count, spawns the task,
 * and links it to the group.
 *
 * @param group      The TaskGroup to spawn within
 * @param closure    User's closure pointer
 * @param invoke_fn  User's function to execute
 * @param arg        Argument passed to invoke_fn
 * @return Pointer to the spawned EidosTask, or NULL on failure
 */
struct EidosTask* eidos_taskgroup_spawn(struct EidosTaskGroup* group,
                                         void* closure,
                                         void* (*invoke_fn)(void*, void*),
                                         void* arg);

/**
 * Spawn a raw-payload task within a TaskGroup from an Eidos closure of
 * type Unit -> RawPtr.
 */
struct EidosTask* eidos_taskgroup_spawn_closure_raw(struct EidosTaskGroup* group,
                                                    EidosClosure* thunk);

/**
 * Spawn a boxed-value task within a TaskGroup from an Eidos closure of
 * type Unit -> RawPtr. The returned RawPtr must be a managed boxed value.
 */
struct EidosTask* eidos_taskgroup_spawn_closure_value(struct EidosTaskGroup* group,
                                                      EidosClosure* thunk);

/**
 * Join a TaskGroup: register a continuation for when all tasks complete.
 *
 * If pending_count is already 0, the continuation is scheduled
 * immediately. Otherwise it is stored and scheduled when the last
 * task finishes.
 *
 * @param group        The TaskGroup to join
 * @param continuation Work item to schedule when all tasks complete
 */
void eidos_taskgroup_join(struct EidosTaskGroup* group,
                           EidosWorkItem continuation);

/**
 * Join a TaskGroup with an Eidos closure of type Unit -> Unit.
 */
void eidos_taskgroup_join_closure_raw(struct EidosTaskGroup* group,
                                      EidosClosure* continuation);

/**
 * Cancel a TaskGroup.
 *
 * Sets the error flag. Tasks may check group->error_flag to
 * implement voluntary cancellation (future: cooperative cancel).
 *
 * @param group The TaskGroup to cancel
 */
void eidos_taskgroup_cancel(struct EidosTaskGroup* group);

/**
 * Return whether a TaskGroup has been cancelled.
 */
bool eidos_taskgroup_is_cancelled(struct EidosTaskGroup* group);

/**
 * Return the current number of pending tasks in a TaskGroup.
 */
int64_t eidos_taskgroup_pending_count(struct EidosTaskGroup* group);

/**
 * Shut down the task runtime and underlying scheduler.
 *
 * Idempotent. Resets auto-initialization so a subsequent spawn
 * will re-initialize if needed.
 */
void eidos_task_runtime_shutdown(void);

/* ============================================================
 * Channel API
 *
 * CSP-style async channels with buffered and rendezvous modes.
 * Implementation lives in eidos_channel.c.
 * ============================================================ */

/**
 * Create a new async channel.
 *
 * @param capacity  Buffer capacity. 0 = rendezvous (unbuffered).
 * @return Pointer to the new EidosChannel (shared, ref_count=1)
 */
struct EidosChannel* eidos_channel_new(uint32_t capacity);

/**
 * Create a new async channel using the Eidos Int ABI.
 *
 * Negative capacities create a rendezvous channel. Values larger than UINT32_MAX
 * are clamped to UINT32_MAX.
 */
struct EidosChannel* eidos_channel_new_capacity(int64_t capacity);

/**
 * Create a raw-pointer-payload channel using the Eidos Int ABI.
 *
 * Raw-payload channels do not retain or release payload values. They are for
 * low-level FFI/runtime handles, not managed Eidos heap values.
 */
struct EidosChannel* eidos_channel_new_raw_capacity(int64_t capacity);

/**
 * Async send: enqueue a value onto the channel.
 *
 * @param ch           The channel
 * @param value        Shared pointer to the value (may be NULL)
 * @param continuation Scheduled when the send completes or fails
 */
void eidos_channel_send(struct EidosChannel* ch, void* value,
                         EidosWorkItem continuation);

/**
 * Non-blocking pointer-payload send.
 *
 * Returns true when the value was accepted or delivered immediately, false when
 * the channel is closed, null, or currently unable to accept a value without
 * suspending the sender.
 */
bool eidos_channel_try_send(struct EidosChannel* ch, void* value);

/**
 * Async receive: dequeue a value from the channel.
 *
 * @param ch           The channel
 * @param continuation Scheduled with the received value as arg
 */
void eidos_channel_recv(struct EidosChannel* ch, EidosWorkItem continuation);

/**
 * Non-blocking pointer-payload receive.
 *
 * Returns a value when one is immediately available, NULL when the open channel
 * has no value ready, or eidos_channel_closed_sentinel() when the channel is
 * closed and empty.
 */
void* eidos_channel_try_recv(struct EidosChannel* ch);

/**
 * Close the channel. No more sends allowed.
 *
 * @param ch  The channel to close
 */
void eidos_channel_close(struct EidosChannel* ch);

/**
 * Return the sentinel value indicating a channel is closed.
 */
void* eidos_channel_closed_sentinel(void);

/**
 * Test whether a value is the closed-channel sentinel.
 */
bool eidos_channel_is_closed_value(void* value);

/* ============================================================
 * Promise / Future API
 *
 * One-shot Promise/Future pair for async value delivery.
 * Implementation lives in eidos_promise.c.
 * ============================================================ */

/**
 * Create a linked Promise/Future pair.
 *
 * Both outputs are allocated with ref_count=1.
 * The caller owns one reference to each.
 *
 * @param promise_out  Receives the Promise pointer
 * @param future_out   Receives the Future pointer
 */
void eidos_promise_new(struct EidosPromise** promise_out,
                        struct EidosFuture** future_out);

/**
 * Create a linked Promise/Future pair for unmanaged RawPtr payloads.
 *
 * Raw payload promises do not retain or release fulfilled values.
 */
void eidos_promise_new_raw(struct EidosPromise** promise_out,
                            struct EidosFuture** future_out);

/**
 * Create a standalone raw-payload Promise handle for non-blocking polling APIs.
 */
struct EidosPromise* eidos_promise_new_raw_single(void);

/**
 * Create a standalone managed-payload Promise handle for non-blocking polling APIs.
 *
 * Managed payload promises retain fulfilled values and release them when the
 * promise is destroyed.
 */
struct EidosPromise* eidos_promise_new_single(void);

/**
 * Fulfill a Promise with a value. One-shot: returns 1 on success, 0 if already fulfilled.
 *
 * @param promise  The Promise to fulfill
 * @param value    The result value (shared ptr)
 * @return 1 on success, 0 if already fulfilled
 */
int eidos_promise_fulfill(struct EidosPromise* promise, void* value);

/**
 * Bool-returning wrapper for Eidos FFI callers.
 */
bool eidos_promise_fulfill_raw(struct EidosPromise* promise, void* value);

/**
 * Return the fulfilled future value immediately, or NULL when still pending.
 */
void* eidos_future_try_get(struct EidosFuture* future);

/**
 * Return the fulfilled promise value immediately, or NULL when still pending.
 */
void* eidos_promise_try_get_raw(struct EidosPromise* promise);

/**
 * Await a Future's result, registering a continuation.
 *
 * If already fulfilled, schedules continuation immediately.
 *
 * @param future       The Future to await
 * @param continuation Scheduled with the result as arg
 */
void eidos_future_await(struct EidosFuture* future, EidosWorkItem continuation);

/* ============================================================
 * Mutex / RwLock / Barrier API
 *
 * Task-aware synchronization primitives that never block OS threads.
 * Tasks register continuations and yield; the continuation is scheduled
 * when the synchronization condition is met.
 * Implementation lives in eidos_sync.c.
 * ============================================================ */

/**
 * Create a new Mutex protecting the given value.
 *
 * @param value  The protected data (shared ptr, may be NULL)
 * @return Pointer to the new EidosMutex (shared, ref_count=1)
 */
struct EidosMutex* eidos_mutex_new(void* value);

/**
 * Acquire the Mutex, registering a continuation for when the lock is held.
 *
 * The continuation receives the EidosMutex pointer as its argument.
 * Call eidos_mutex_guard_release() when done.
 *
 * @param mutex        The mutex to acquire
 * @param continuation Work item to schedule when the lock is acquired
 */
void eidos_mutex_lock(struct EidosMutex* mutex, EidosWorkItem continuation);

/**
 * Try to acquire the Mutex without registering a continuation.
 *
 * @param mutex The mutex to acquire
 * @return true when acquired, false when already locked or null
 */
bool eidos_mutex_try_lock(struct EidosMutex* mutex);

/**
 * Read the protected boxed payload pointer.
 *
 * Callers must hold the mutex when a stable scoped value is required.
 *
 * @param mutex The mutex to inspect
 * @return protected payload pointer, or null
 */
void* eidos_mutex_get_inner(struct EidosMutex* mutex);

/**
 * Replace the protected boxed payload pointer.
 *
 * Callers must hold the mutex. The mutex retains the new pointer and releases
 * the old pointer.
 *
 * @param mutex The mutex to update
 * @param value New protected payload pointer, or null
 */
void eidos_mutex_replace_inner(struct EidosMutex* mutex, void* value);

/**
 * Release the Mutex (called by the affine drop of the "guard").
 *
 * @param mutex The mutex to release
 */
void eidos_mutex_guard_release(struct EidosMutex* mutex);

/**
 * Release a Mutex acquired by eidos_mutex_try_lock.
 *
 * @param mutex The mutex to release
 */
void eidos_mutex_unlock(struct EidosMutex* mutex);

/**
 * Create a new RwLock protecting the given value.
 *
 * @param value  The protected data (shared ptr, may be NULL)
 * @return Pointer to the new EidosRwLock (shared, ref_count=1)
 */
struct EidosRwLock* eidos_rwlock_new(void* value);

/**
 * Acquire a read lock on the RwLock.
 *
 * Multiple readers can hold the lock simultaneously.
 * The continuation receives the EidosRwLock pointer as its argument.
 * Call eidos_rwlock_read_release() when done.
 *
 * @param rwlock       The RwLock to read-lock
 * @param continuation Work item to schedule when the read lock is acquired
 */
void eidos_rwlock_read(struct EidosRwLock* rwlock, EidosWorkItem continuation);

/**
 * Try to acquire a read lock without registering a continuation.
 *
 * @param rwlock The RwLock to read-lock
 * @return true when acquired, false when blocked by a writer or null
 */
bool eidos_rwlock_try_read(struct EidosRwLock* rwlock);

/**
 * Acquire an exclusive write lock on the RwLock.
 *
 * The continuation receives the EidosRwLock pointer as its argument.
 * Call eidos_rwlock_write_release() when done.
 *
 * @param rwlock       The RwLock to write-lock
 * @param continuation Work item to schedule when the write lock is acquired
 */
void eidos_rwlock_write(struct EidosRwLock* rwlock, EidosWorkItem continuation);

/**
 * Try to acquire an exclusive write lock without registering a continuation.
 *
 * @param rwlock The RwLock to write-lock
 * @return true when acquired, false when blocked by readers/writer or null
 */
bool eidos_rwlock_try_write(struct EidosRwLock* rwlock);

/**
 * Read the protected boxed payload pointer.
 *
 * Callers must hold a read or write lock when a stable scoped value is required.
 *
 * @param rwlock The RwLock to inspect
 * @return protected payload pointer, or null
 */
void* eidos_rwlock_get_inner(struct EidosRwLock* rwlock);

/**
 * Replace the protected boxed payload pointer.
 *
 * Callers must hold a write lock. The RwLock retains the new pointer and
 * releases the old pointer.
 *
 * @param rwlock The RwLock to update
 * @param value New protected payload pointer, or null
 */
void eidos_rwlock_replace_inner(struct EidosRwLock* rwlock, void* value);

/**
 * Release a read lock on the RwLock.
 *
 * @param rwlock The RwLock to release the read lock on
 */
void eidos_rwlock_read_release(struct EidosRwLock* rwlock);

/**
 * Release a write lock on the RwLock.
 *
 * Wakes all waiting readers, then one waiting writer.
 *
 * @param rwlock The RwLock to release the write lock on
 */
void eidos_rwlock_write_release(struct EidosRwLock* rwlock);

/**
 * Create a new Barrier with the given number of participants.
 *
 * @param capacity Number of participants that must arrive before the barrier trips
 * @return Pointer to the new EidosBarrier (shared, ref_count=1)
 */
struct EidosBarrier* eidos_barrier_new(uint32_t capacity);

/**
 * Arrive at the Barrier without registering a continuation.
 *
 * @param barrier The barrier to arrive at
 * @return true when this arrival trips the barrier, false otherwise
 */
bool eidos_barrier_arrive(struct EidosBarrier* barrier);

/**
 * Wait at the Barrier, registering a continuation for when all participants arrive.
 *
 * The continuation receives the EidosBarrier pointer as its argument.
 * The barrier can be reused after it trips.
 *
 * @param barrier      The barrier to wait at
 * @param continuation Work item to schedule when the barrier trips
 */
void eidos_barrier_wait(struct EidosBarrier* barrier, EidosWorkItem continuation);

/**
 * Wait at a Barrier with an Eidos closure of type Unit -> Unit.
 */
void eidos_barrier_wait_closure_raw(struct EidosBarrier* barrier,
                                    EidosClosure* continuation);

/* ============================================================
 * Regex Operations
 * ============================================================ */

/**
 * Compile a regular expression, returning an opaque pointer.
 * @param pattern Null-terminated C string pattern
 * @param flags Compilation flags (platform-specific)
 * @return Compiled regex handle, or NULL on failure
 */
void* eidos_regex_compile(const char* pattern, int64_t flags);

/**
 * Free a compiled regex handle.
 * @param regex Compiled regex handle (may be NULL)
 */
void eidos_regex_free(void* regex);

/**
 * Test whether text fully matches the compiled regex.
 * @param regex Compiled regex handle
 * @param text Null-terminated C string to test
 * @return 1=match, 0=no match, -1=error
 */
int64_t eidos_regex_is_match(void* regex, const char* text);

/**
 * Find the first match in text, writing it into match_buf.
 * @param regex Compiled regex handle
 * @param text Null-terminated C string to search
 * @param match_buf Output buffer for the matched substring
 * @param buf_len Size of match_buf in bytes
 * @return Length of the match (0=not found)
 */
int64_t eidos_regex_find(void* regex, const char* text, char* match_buf, int64_t buf_len);

/**
 * Find the first match and return it as an EidosString.
 * Returns NULL if no match found.
 * @param regex Compiled regex handle
 * @param text Null-terminated C string to search
 * @return New EidosString with the matched substring, or NULL
 */
EidosString* eidos_regex_find_string(void* regex, const char* text);

#ifdef __cplusplus
}
#endif

#endif /* EIDOS_RUNTIME_H */
