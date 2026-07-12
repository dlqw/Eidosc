/**
 * Eidos Runtime - Memory Implementation
 *
 * Implements Perceus-style reference counting memory management.
 * Optionally uses mimalloc for improved allocation performance.
 * Compile with -DEIDOS_USE_MIMALLOC to enable mimalloc.
 */

#include "eidos_runtime.h"
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <stdarg.h>
#include <errno.h>

#if defined(EIDOS_USE_MIMALLOC)
#include <mimalloc.h>
#define EIDOS_MALLOC(size)       mi_malloc(size)
#define EIDOS_FREE(ptr)          mi_free(ptr)
#define EIDOS_REALLOC(ptr, size) mi_realloc(ptr, size)
#else
#define EIDOS_MALLOC(size)       malloc(size)
#define EIDOS_FREE(ptr)          free(ptr)
#define EIDOS_REALLOC(ptr, size) realloc(ptr, size)
#endif

#if defined(EIDOS_ENABLE_LIBCURL)
#include <curl/curl.h>
#endif

#include <time.h>

#if defined(_WIN32)
#include <windows.h>
#include <io.h>
#include <conio.h>
#else
#include <fcntl.h>
#include <sys/select.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>
#include <termios.h>
#endif

/* Platform-abstracted atomic operations for reference counting */
#if defined(_WIN32)
#include <intrin.h>
#define EIDOS_ATOMIC_INC32(ptr) InterlockedIncrement((LONG volatile*)(ptr))
#define EIDOS_ATOMIC_DEC32(ptr) InterlockedDecrement((LONG volatile*)(ptr))
#define EIDOS_ATOMIC_OR32(ptr, val) InterlockedOr((LONG volatile*)(ptr), (LONG)(val))
#else
#define EIDOS_ATOMIC_INC32(ptr) __atomic_add_fetch((ptr), 1, __ATOMIC_SEQ_CST)
#define EIDOS_ATOMIC_DEC32(ptr) __atomic_sub_fetch((ptr), 1, __ATOMIC_SEQ_CST)
#define EIDOS_ATOMIC_OR32(ptr, val) __atomic_or_fetch((ptr), (val), __ATOMIC_SEQ_CST)
#endif

/* Shared-bit reference counting model:
 * Bit 31 of ref_count is the SHARED flag. When set, the object has been
 * shared across threads and all RC mutations must use atomic operations.
 * Bits 0-30 hold the actual reference count (up to ~2 billion refs).
 * In single-threaded code bit 31 is always 0, so the branch check in
 * _local variants is perfectly predicted. */
#define EIDOS_SHARED_BIT  0x80000000
#define EIDOS_COUNT_MASK  0x7FFFFFFF

/* Thread-local IO status: each thread tracks its own last-operation state */
static _Thread_local int g_eidos_last_io_success = 1;
static _Thread_local char g_eidos_last_io_error[256] = "";
static _Thread_local int64_t g_eidos_last_http_status_code = 0;
static _Thread_local char g_eidos_last_http_effective_url[2048] = "";
static _Thread_local char g_eidos_last_http_content_type[256] = "";
static _Thread_local char g_eidos_last_http_headers[8192] = "";

#define EIDOS_HTTP_META_MARKER "\n__EIDOS_HTTP_META_7F3A9C5D__\n"

typedef struct EidosHttpRequestSpec {
    EidosString* method;
    EidosString* url;
    EidosString* content_type;
    EidosString* headers;
    EidosString* text_body;
    const unsigned char* binary_body;
    size_t binary_body_length;
    int use_binary_body;
    int64_t connect_timeout_seconds;
    int64_t total_timeout_seconds;
} EidosHttpRequestSpec;

typedef struct EidosHttpTransportResult {
    char* stdout_buffer;
    size_t stdout_length;
    char* stderr_buffer;
    size_t stderr_length;
    int exit_code;
} EidosHttpTransportResult;

typedef void (*EidosClosureReleaseFn)(void* closure_ptr);

static int eidos_parse_http_headers(EidosString* headers, char*** header_lines, size_t* header_count);
static void eidos_free_http_headers(char** header_lines, size_t header_count);

typedef enum EidosHttpBackendKind {
    EIDOS_HTTP_BACKEND_KIND_DEFAULT = 0,
    EIDOS_HTTP_BACKEND_KIND_CURL = 1,
    EIDOS_HTTP_BACKEND_KIND_LIBCURL = 2
} EidosHttpBackendKind;

#if defined(EIDOS_ENABLE_LIBCURL)
typedef struct EidosLibcurlCapture {
    char* final_header_buffer;
    size_t final_header_length;
    size_t final_header_capacity;
    char* body_buffer;
    size_t body_length;
    size_t body_capacity;
} EidosLibcurlCapture;
#endif

static void eidos_set_io_success(void) {
    g_eidos_last_io_success = 1;
    g_eidos_last_io_error[0] = '\0';
}

static void eidos_set_io_error_message(const char* message) {
    const char* fallback = "io error";
    const char* text = (message != NULL && message[0] != '\0') ? message : fallback;
    g_eidos_last_io_success = 0;
    snprintf(g_eidos_last_io_error, sizeof(g_eidos_last_io_error), "%s", text);
}

static EidosString* eidos_string_new_hex_from_bytes(const unsigned char* data, size_t length) {
    static const char hex_digits[] = "0123456789ABCDEF";
    size_t hex_length = length * 2;
    char* hex_text = NULL;
    EidosString* result = NULL;

    if (hex_length == 0) {
        return eidos_string_new("", 0);
    }

    hex_text = (char*)malloc(hex_length);
    if (hex_text == NULL) {
        eidos_set_io_error_message("failed to allocate http hex body buffer");
        return eidos_string_new("", 0);
    }

    for (size_t i = 0; i < length; i++) {
        unsigned char byte = data[i];
        hex_text[i * 2] = hex_digits[(byte >> 4) & 0x0F];
        hex_text[i * 2 + 1] = hex_digits[byte & 0x0F];
    }

    result = eidos_string_new(hex_text, hex_length);
    free(hex_text);
    return result;
}

static int eidos_hex_digit_to_value(char ch, unsigned char* value) {
    if (ch >= '0' && ch <= '9') {
        *value = (unsigned char)(ch - '0');
        return 1;
    }

    if (ch >= 'A' && ch <= 'F') {
        *value = (unsigned char)(10 + (ch - 'A'));
        return 1;
    }

    if (ch >= 'a' && ch <= 'f') {
        *value = (unsigned char)(10 + (ch - 'a'));
        return 1;
    }

    return 0;
}

static int eidos_decode_hex_bytes(
    EidosString* hex_text,
    unsigned char** out_data,
    size_t* out_length) {
    unsigned char* buffer = NULL;

    if (out_data == NULL || out_length == NULL) {
        return 0;
    }

    *out_data = NULL;
    *out_length = 0;

    if (hex_text == NULL || hex_text->length == 0) {
        return 1;
    }

    if ((hex_text->length % 2) != 0) {
        return 0;
    }

    *out_length = hex_text->length / 2;
    buffer = (unsigned char*)malloc(*out_length);
    if (buffer == NULL) {
        *out_length = 0;
        return 0;
    }

    for (size_t i = 0; i < *out_length; i++) {
        unsigned char high = 0;
        unsigned char low = 0;
        if (!eidos_hex_digit_to_value(hex_text->data[i * 2], &high) ||
            !eidos_hex_digit_to_value(hex_text->data[i * 2 + 1], &low)) {
            free(buffer);
            *out_length = 0;
            return 0;
        }

        buffer[i] = (unsigned char)((high << 4) | low);
    }

    *out_data = buffer;
    return 1;
}

#if defined(_WIN32)
static int eidos_write_all_to_handle(HANDLE handle, const unsigned char* data, size_t length) {
    size_t offset = 0;

    while (offset < length) {
        DWORD written = 0;
        DWORD chunk = (DWORD)((length - offset) > 65536 ? 65536 : (length - offset));
        if (!WriteFile(handle, data + offset, chunk, &written, NULL)) {
            return 0;
        }

        if (written == 0) {
            return 0;
        }

        offset += (size_t)written;
    }

    return 1;
}
#else
static int eidos_write_all_to_fd(int fd, const unsigned char* data, size_t length) {
    size_t offset = 0;

    while (offset < length) {
        ssize_t written = write(fd, data + offset, length - offset);
        if (written < 0) {
            return 0;
        }

        if (written == 0) {
            return 0;
        }

        offset += (size_t)written;
    }

    return 1;
}
#endif

static void eidos_set_io_errno_error(const char* prefix) {
    const char* detail = strerror(errno);
    const char* head = (prefix != NULL && prefix[0] != '\0') ? prefix : "io error";
    g_eidos_last_io_success = 0;
    if (detail != NULL && detail[0] != '\0') {
        snprintf(g_eidos_last_io_error, sizeof(g_eidos_last_io_error), "%s: %s", head, detail);
    }
    else {
        snprintf(g_eidos_last_io_error, sizeof(g_eidos_last_io_error), "%s", head);
    }
}

static void eidos_reset_http_metadata(void) {
    g_eidos_last_http_status_code = 0;
    g_eidos_last_http_effective_url[0] = '\0';
    g_eidos_last_http_content_type[0] = '\0';
    g_eidos_last_http_headers[0] = '\0';
}

static void eidos_copy_http_metadata(char* target, size_t target_size, const char* value) {
    if (target == NULL || target_size == 0) {
        return;
    }

    if (value == NULL) {
        target[0] = '\0';
        return;
    }

    snprintf(target, target_size, "%s", value);
}

static void eidos_set_http_effective_url_from_eidos(EidosString* url) {
    if (url == NULL || url->length == 0) {
        g_eidos_last_http_effective_url[0] = '\0';
        return;
    }

    snprintf(
        g_eidos_last_http_effective_url,
        sizeof(g_eidos_last_http_effective_url),
        "%.*s",
        (int)url->length,
        url->data);
}

static volatile int g_eidos_closure_destructor_registered = 0;
static volatile int g_eidos_array_destructor_registered = 0;

static void eidos_array_release_range(EidosArray* arr, size_t start, size_t count) {
    if (arr == NULL || arr->release_element == NULL || count == 0) {
        return;
    }

    for (size_t i = 0; i < count; i++) {
        arr->release_element(arr->data + (start + i) * arr->element_size);
    }
}

static void eidos_array_retain_element(EidosArray* arr, void* element) {
    if (arr != NULL && arr->retain_element != NULL && element != NULL) {
        arr->retain_element(element);
    }
}

static void eidos_array_destructor(void* ptr) {
    EidosArray* arr = (EidosArray*)ptr;
    eidos_array_release_range(arr, 0, arr != NULL ? arr->length : 0);
}

static void eidos_ensure_array_destructor_registered(void) {
#if defined(_WIN32)
    if (InterlockedCompareExchange((LONG volatile*)&g_eidos_array_destructor_registered, 1, 0) == 0)
#else
    int expected = 0;
    if (__atomic_compare_exchange_n(&g_eidos_array_destructor_registered,
                                    &expected,
                                    1,
                                    false,
                                    __ATOMIC_ACQ_REL,
                                    __ATOMIC_ACQUIRE))
#endif
    {
        eidos_register_destructor(EIDOS_TYPE_ARRAY, eidos_array_destructor);
    }
}

static void eidos_closure_destructor(void* ptr) {
    EidosClosure* closure = (EidosClosure*)ptr;
    if (closure == NULL || closure->release_fn == NULL) {
        return;
    }

    ((EidosClosureReleaseFn)closure->release_fn)(ptr);
}

static void eidos_ensure_closure_destructor_registered(void) {
    /* Atomic compare-and-swap to ensure only one thread registers the destructor.
     * EIDOS_ATOMIC_INC32 returns the new value; if it was 0, we are the first. */
#if defined(_WIN32)
    if (InterlockedCompareExchange((LONG volatile*)&g_eidos_closure_destructor_registered, 1, 0) == 0)
#else
    if (__atomic_compare_exchange_n(&g_eidos_closure_destructor_registered,
            &(int){0}, 1, 0, __ATOMIC_SEQ_CST, __ATOMIC_SEQ_CST))
#endif
    {
        eidos_register_destructor(EIDOS_TYPE_CLOSURE, eidos_closure_destructor);
    }
}

static void eidos_trim_trailing_newlines(char* text) {
    size_t length = 0;

    if (text == NULL) {
        return;
    }

    length = strlen(text);
    while (length > 0 && (text[length - 1] == '\r' || text[length - 1] == '\n')) {
        text[length - 1] = '\0';
        length--;
    }
}

static int eidos_append_capture_bytes(
    char** buffer,
    size_t* length,
    size_t* capacity,
    const char* chunk,
    size_t chunk_length) {
    char* grown = NULL;
    size_t required = 0;
    size_t new_capacity = 0;

    if (buffer == NULL || length == NULL || capacity == NULL) {
        return 0;
    }

    if (chunk_length == 0) {
        if (*buffer == NULL) {
            *buffer = (char*)malloc(1);
            if (*buffer == NULL) {
                return 0;
            }

            (*buffer)[0] = '\0';
            *capacity = 1;
        }

        return 1;
    }

    required = *length + chunk_length + 1;
    if (*capacity < required) {
        new_capacity = *capacity == 0 ? 4096 : *capacity;
        while (new_capacity < required) {
            new_capacity *= 2;
        }

        grown = (char*)realloc(*buffer, new_capacity);
        if (grown == NULL) {
            return 0;
        }

        *buffer = grown;
        *capacity = new_capacity;
    }

    memcpy(*buffer + *length, chunk, chunk_length);
    *length += chunk_length;
    (*buffer)[*length] = '\0';
    return 1;
}

static int eidos_append_windows_command_arg(
    char* command_line,
    size_t command_line_size,
    size_t* offset,
    const char* arg) {
    size_t i = 0;

    if (command_line == NULL || offset == NULL || arg == NULL) {
        return 0;
    }

    if (*offset >= command_line_size) {
        return 0;
    }

    if (*offset > 0) {
        if (*offset + 1 >= command_line_size) {
            return 0;
        }

        command_line[(*offset)++] = ' ';
    }

    if (*offset + 1 >= command_line_size) {
        return 0;
    }

    command_line[(*offset)++] = '"';
    while (arg[i] != '\0') {
        if (arg[i] == '"') {
            if (*offset + 2 >= command_line_size) {
                return 0;
            }

            command_line[(*offset)++] = '\\';
        }

        if (*offset + 1 >= command_line_size) {
            return 0;
        }

        command_line[(*offset)++] = arg[i++];
    }

    if (*offset + 2 >= command_line_size) {
        return 0;
    }

    command_line[(*offset)++] = '"';
    command_line[*offset] = '\0';
    return 1;
}

static int eidos_run_process_capture_to_memory(
    const char* const* argv,
    const unsigned char* stdin_bytes,
    size_t stdin_length,
    int use_stdin_bytes,
    char** out_stdout,
    size_t* out_stdout_length,
    char** out_stderr,
    size_t* out_stderr_length,
    int* exit_code) {
#if defined(_WIN32)
    SECURITY_ATTRIBUTES security_attributes;
    STARTUPINFOA startup_info;
    PROCESS_INFORMATION process_info;
    HANDLE stdout_read = INVALID_HANDLE_VALUE;
    HANDLE stdout_write = INVALID_HANDLE_VALUE;
    HANDLE stderr_read = INVALID_HANDLE_VALUE;
    HANDLE stderr_write = INVALID_HANDLE_VALUE;
    HANDLE stdin_read = INVALID_HANDLE_VALUE;
    HANDLE stdin_write = INVALID_HANDLE_VALUE;
    char* stdout_buffer = NULL;
    char* stderr_buffer = NULL;
    size_t stdout_length = 0;
    size_t stderr_length = 0;
    size_t stdout_capacity = 0;
    size_t stderr_capacity = 0;
    int stdout_open = 1;
    int stderr_open = 1;
    char command_line[8192];
    size_t offset = 0;
    int i = 0;

    if (argv == NULL || argv[0] == NULL || exit_code == NULL ||
        out_stdout == NULL || out_stdout_length == NULL ||
        out_stderr == NULL || out_stderr_length == NULL) {
        return 0;
    }

    *out_stdout = NULL;
    *out_stdout_length = 0;
    *out_stderr = NULL;
    *out_stderr_length = 0;

    ZeroMemory(&security_attributes, sizeof(security_attributes));
    security_attributes.nLength = sizeof(security_attributes);
    security_attributes.bInheritHandle = TRUE;

    if (!CreatePipe(&stdout_read, &stdout_write, &security_attributes, 0)) {
        return 0;
    }

    if (!SetHandleInformation(stdout_read, HANDLE_FLAG_INHERIT, 0)) {
        CloseHandle(stdout_read);
        CloseHandle(stdout_write);
        return 0;
    }

    if (!CreatePipe(&stderr_read, &stderr_write, &security_attributes, 0)) {
        CloseHandle(stdout_read);
        CloseHandle(stdout_write);
        return 0;
    }

    if (!SetHandleInformation(stderr_read, HANDLE_FLAG_INHERIT, 0)) {
        CloseHandle(stdout_read);
        CloseHandle(stdout_write);
        CloseHandle(stderr_read);
        CloseHandle(stderr_write);
        return 0;
    }

    if (use_stdin_bytes) {
        if (!CreatePipe(&stdin_read, &stdin_write, &security_attributes, 0)) {
            CloseHandle(stdout_read);
            CloseHandle(stdout_write);
            CloseHandle(stderr_read);
            CloseHandle(stderr_write);
            return 0;
        }

        if (!SetHandleInformation(stdin_write, HANDLE_FLAG_INHERIT, 0)) {
            CloseHandle(stdout_read);
            CloseHandle(stdout_write);
            CloseHandle(stderr_read);
            CloseHandle(stderr_write);
            CloseHandle(stdin_read);
            CloseHandle(stdin_write);
            return 0;
        }
    }

    command_line[0] = '\0';
    for (i = 0; argv[i] != NULL; i++) {
        if (!eidos_append_windows_command_arg(command_line, sizeof(command_line), &offset, argv[i])) {
            CloseHandle(stdout_read);
            CloseHandle(stdout_write);
            CloseHandle(stderr_read);
            CloseHandle(stderr_write);
            return 0;
        }
    }

    ZeroMemory(&startup_info, sizeof(startup_info));
    startup_info.cb = sizeof(startup_info);
    startup_info.dwFlags = STARTF_USESTDHANDLES;
    startup_info.hStdInput = use_stdin_bytes ? stdin_read : GetStdHandle(STD_INPUT_HANDLE);
    startup_info.hStdOutput = stdout_write;
    startup_info.hStdError = stderr_write;

    ZeroMemory(&process_info, sizeof(process_info));
    if (!CreateProcessA(
            NULL,
            command_line,
            NULL,
            NULL,
            TRUE,
            CREATE_NO_WINDOW,
            NULL,
            NULL,
            &startup_info,
            &process_info)) {
        CloseHandle(stdout_read);
        CloseHandle(stdout_write);
        CloseHandle(stderr_read);
        CloseHandle(stderr_write);
        if (stdin_read != INVALID_HANDLE_VALUE) {
            CloseHandle(stdin_read);
        }
        if (stdin_write != INVALID_HANDLE_VALUE) {
            CloseHandle(stdin_write);
        }
        return 0;
    }

    CloseHandle(stdout_write);
    CloseHandle(stderr_write);
    stdout_write = INVALID_HANDLE_VALUE;
    stderr_write = INVALID_HANDLE_VALUE;

    if (stdin_read != INVALID_HANDLE_VALUE) {
        CloseHandle(stdin_read);
        stdin_read = INVALID_HANDLE_VALUE;
    }

    if (use_stdin_bytes && stdin_write != INVALID_HANDLE_VALUE) {
        if (stdin_length > 0 && !eidos_write_all_to_handle(stdin_write, stdin_bytes, stdin_length)) {
            goto windows_fail;
        }

        CloseHandle(stdin_write);
        stdin_write = INVALID_HANDLE_VALUE;
    }

    while (stdout_open || stderr_open) {
        DWORD bytes_available = 0;
        DWORD bytes_read = 0;
        char chunk[4096];
        DWORD wait_result = WaitForSingleObject(process_info.hProcess, 10);

        if (stdout_open) {
            if (PeekNamedPipe(stdout_read, NULL, 0, NULL, &bytes_available, NULL)) {
                while (bytes_available > 0) {
                    DWORD request = bytes_available > (DWORD)sizeof(chunk) ? (DWORD)sizeof(chunk) : bytes_available;
                    if (!ReadFile(stdout_read, chunk, request, &bytes_read, NULL)) {
                        stdout_open = 0;
                        break;
                    }

                    if (bytes_read == 0) {
                        stdout_open = 0;
                        break;
                    }

                    if (!eidos_append_capture_bytes(
                            &stdout_buffer,
                            &stdout_length,
                            &stdout_capacity,
                            chunk,
                            (size_t)bytes_read)) {
                        goto windows_fail;
                    }

                    if (!PeekNamedPipe(stdout_read, NULL, 0, NULL, &bytes_available, NULL)) {
                        break;
                    }
                }
            }
            else if (GetLastError() == ERROR_BROKEN_PIPE) {
                stdout_open = 0;
            }
        }

        if (stderr_open) {
            if (PeekNamedPipe(stderr_read, NULL, 0, NULL, &bytes_available, NULL)) {
                while (bytes_available > 0) {
                    DWORD request = bytes_available > (DWORD)sizeof(chunk) ? (DWORD)sizeof(chunk) : bytes_available;
                    if (!ReadFile(stderr_read, chunk, request, &bytes_read, NULL)) {
                        stderr_open = 0;
                        break;
                    }

                    if (bytes_read == 0) {
                        stderr_open = 0;
                        break;
                    }

                    if (!eidos_append_capture_bytes(
                            &stderr_buffer,
                            &stderr_length,
                            &stderr_capacity,
                            chunk,
                            (size_t)bytes_read)) {
                        goto windows_fail;
                    }

                    if (!PeekNamedPipe(stderr_read, NULL, 0, NULL, &bytes_available, NULL)) {
                        break;
                    }
                }
            }
            else if (GetLastError() == ERROR_BROKEN_PIPE) {
                stderr_open = 0;
            }
        }

        if (wait_result == WAIT_OBJECT_0 && !stdout_open && !stderr_open) {
            break;
        }
    }

    if (!eidos_append_capture_bytes(&stdout_buffer, &stdout_length, &stdout_capacity, "", 0) ||
        !eidos_append_capture_bytes(&stderr_buffer, &stderr_length, &stderr_capacity, "", 0)) {
        goto windows_fail;
    }

    if (!GetExitCodeProcess(process_info.hProcess, (DWORD*)exit_code)) {
        *exit_code = -1;
    }

    CloseHandle(process_info.hThread);
    CloseHandle(process_info.hProcess);
    CloseHandle(stdout_read);
    CloseHandle(stderr_read);

    *out_stdout = stdout_buffer;
    *out_stdout_length = stdout_length;
    *out_stderr = stderr_buffer;
    *out_stderr_length = stderr_length;
    return 1;

windows_fail:
    free(stdout_buffer);
    free(stderr_buffer);
    if (stdout_read != INVALID_HANDLE_VALUE) {
        CloseHandle(stdout_read);
    }
    if (stdout_write != INVALID_HANDLE_VALUE) {
        CloseHandle(stdout_write);
    }
    if (stderr_read != INVALID_HANDLE_VALUE) {
        CloseHandle(stderr_read);
    }
    if (stderr_write != INVALID_HANDLE_VALUE) {
        CloseHandle(stderr_write);
    }
    if (stdin_read != INVALID_HANDLE_VALUE) {
        CloseHandle(stdin_read);
    }
    if (stdin_write != INVALID_HANDLE_VALUE) {
        CloseHandle(stdin_write);
    }
    CloseHandle(process_info.hThread);
    CloseHandle(process_info.hProcess);
    return 0;
#else
    pid_t pid = 0;
    int stdout_pipe[2] = { -1, -1 };
    int stderr_pipe[2] = { -1, -1 };
    int stdin_pipe[2] = { -1, -1 };
    int status = 0;
    char* stdout_buffer = NULL;
    char* stderr_buffer = NULL;
    size_t stdout_length = 0;
    size_t stderr_length = 0;
    size_t stdout_capacity = 0;
    size_t stderr_capacity = 0;
    int stdout_open = 1;
    int stderr_open = 1;

    if (argv == NULL || argv[0] == NULL || exit_code == NULL ||
        out_stdout == NULL || out_stdout_length == NULL ||
        out_stderr == NULL || out_stderr_length == NULL) {
        return 0;
    }

    *out_stdout = NULL;
    *out_stdout_length = 0;
    *out_stderr = NULL;
    *out_stderr_length = 0;

    if (pipe(stdout_pipe) != 0) {
        return 0;
    }

    if (pipe(stderr_pipe) != 0) {
        close(stdout_pipe[0]);
        close(stdout_pipe[1]);
        return 0;
    }

    if (use_stdin_bytes && pipe(stdin_pipe) != 0) {
        close(stdout_pipe[0]);
        close(stdout_pipe[1]);
        close(stderr_pipe[0]);
        close(stderr_pipe[1]);
        return 0;
    }

    pid = fork();
    if (pid < 0) {
        close(stdout_pipe[0]);
        close(stdout_pipe[1]);
        close(stderr_pipe[0]);
        close(stderr_pipe[1]);
        if (use_stdin_bytes) {
            close(stdin_pipe[0]);
            close(stdin_pipe[1]);
        }
        return 0;
    }

    if (pid == 0) {
        if (use_stdin_bytes) {
            close(stdin_pipe[1]);
            dup2(stdin_pipe[0], STDIN_FILENO);
            close(stdin_pipe[0]);
        }
        close(stdout_pipe[0]);
        close(stderr_pipe[0]);
        dup2(stdout_pipe[1], STDOUT_FILENO);
        dup2(stderr_pipe[1], STDERR_FILENO);
        close(stdout_pipe[1]);
        close(stderr_pipe[1]);
        execvp(argv[0], (char* const*)argv);
        perror("execvp");
        _exit(127);
    }

    close(stdout_pipe[1]);
    close(stderr_pipe[1]);

    if (use_stdin_bytes) {
        close(stdin_pipe[0]);
        if (stdin_length > 0 && !eidos_write_all_to_fd(stdin_pipe[1], stdin_bytes, stdin_length)) {
            free(stdout_buffer);
            free(stderr_buffer);
            close(stdout_pipe[0]);
            close(stderr_pipe[0]);
            close(stdin_pipe[1]);
            waitpid(pid, &status, 0);
            return 0;
        }

        close(stdin_pipe[1]);
        stdin_pipe[1] = -1;
    }

    while (stdout_open || stderr_open) {
        fd_set read_set;
        int max_fd = -1;
        int select_result = 0;
        char chunk[4096];

        FD_ZERO(&read_set);
        if (stdout_open) {
            FD_SET(stdout_pipe[0], &read_set);
            if (stdout_pipe[0] > max_fd) {
                max_fd = stdout_pipe[0];
            }
        }

        if (stderr_open) {
            FD_SET(stderr_pipe[0], &read_set);
            if (stderr_pipe[0] > max_fd) {
                max_fd = stderr_pipe[0];
            }
        }

        select_result = select(max_fd + 1, &read_set, NULL, NULL, NULL);
        if (select_result < 0) {
            free(stdout_buffer);
            free(stderr_buffer);
            close(stdout_pipe[0]);
            close(stderr_pipe[0]);
            if (stdin_pipe[1] >= 0) {
                close(stdin_pipe[1]);
            }
            return 0;
        }

        if (stdout_open && FD_ISSET(stdout_pipe[0], &read_set)) {
            ssize_t read_size = read(stdout_pipe[0], chunk, sizeof(chunk));
            if (read_size < 0) {
                free(stdout_buffer);
                free(stderr_buffer);
                close(stdout_pipe[0]);
                close(stderr_pipe[0]);
                if (stdin_pipe[1] >= 0) {
                    close(stdin_pipe[1]);
                }
                return 0;
            }

            if (read_size == 0) {
                close(stdout_pipe[0]);
                stdout_open = 0;
            }
            else if (!eidos_append_capture_bytes(
                         &stdout_buffer,
                         &stdout_length,
                         &stdout_capacity,
                         chunk,
                         (size_t)read_size)) {
                free(stdout_buffer);
                free(stderr_buffer);
                close(stdout_pipe[0]);
                close(stderr_pipe[0]);
                if (stdin_pipe[1] >= 0) {
                    close(stdin_pipe[1]);
                }
                return 0;
            }
        }

        if (stderr_open && FD_ISSET(stderr_pipe[0], &read_set)) {
            ssize_t read_size = read(stderr_pipe[0], chunk, sizeof(chunk));
            if (read_size < 0) {
                free(stdout_buffer);
                free(stderr_buffer);
                close(stdout_pipe[0]);
                close(stderr_pipe[0]);
                if (stdin_pipe[1] >= 0) {
                    close(stdin_pipe[1]);
                }
                return 0;
            }

            if (read_size == 0) {
                close(stderr_pipe[0]);
                stderr_open = 0;
            }
            else if (!eidos_append_capture_bytes(
                         &stderr_buffer,
                         &stderr_length,
                         &stderr_capacity,
                         chunk,
                         (size_t)read_size)) {
                free(stdout_buffer);
                free(stderr_buffer);
                close(stdout_pipe[0]);
                close(stderr_pipe[0]);
                if (stdin_pipe[1] >= 0) {
                    close(stdin_pipe[1]);
                }
                return 0;
            }
        }
    }

    if (!eidos_append_capture_bytes(&stdout_buffer, &stdout_length, &stdout_capacity, "", 0) ||
        !eidos_append_capture_bytes(&stderr_buffer, &stderr_length, &stderr_capacity, "", 0)) {
        free(stdout_buffer);
        free(stderr_buffer);
        if (stdin_pipe[1] >= 0) {
            close(stdin_pipe[1]);
        }
        return 0;
    }

    if (waitpid(pid, &status, 0) < 0) {
        free(stdout_buffer);
        free(stderr_buffer);
        if (stdin_pipe[1] >= 0) {
            close(stdin_pipe[1]);
        }
        return 0;
    }

    if (WIFEXITED(status)) {
        *exit_code = WEXITSTATUS(status);
    }
    else {
        *exit_code = -1;
    }

    *out_stdout = stdout_buffer;
    *out_stdout_length = stdout_length;
    *out_stderr = stderr_buffer;
    *out_stderr_length = stderr_length;
    return 1;
#endif
}

static void eidos_parse_http_metadata(const char* metadata_text) {
    char metadata_copy[4096];
    char* cursor = NULL;
    char* lines[3] = { NULL, NULL, NULL };
    int index = 0;

    if (metadata_text == NULL || metadata_text[0] == '\0') {
        return;
    }

    snprintf(metadata_copy, sizeof(metadata_copy), "%s", metadata_text);
    cursor = metadata_copy;
    while (index < 3 && cursor != NULL) {
        char* next = strpbrk(cursor, "\r\n");
        lines[index++] = cursor;
        if (next == NULL) {
            cursor = NULL;
        }
        else {
            *next = '\0';
            cursor = next + 1;
            while (*cursor == '\r' || *cursor == '\n') {
                cursor++;
            }
        }
    }

    if (lines[0] != NULL && lines[0][0] != '\0') {
        g_eidos_last_http_status_code = (int64_t)strtoll(lines[0], NULL, 10);
    }

    if (lines[1] != NULL && lines[1][0] != '\0') {
        eidos_copy_http_metadata(
            g_eidos_last_http_effective_url,
            sizeof(g_eidos_last_http_effective_url),
            lines[1]);
    }

    if (lines[2] != NULL && lines[2][0] != '\0') {
        eidos_copy_http_metadata(
            g_eidos_last_http_content_type,
            sizeof(g_eidos_last_http_content_type),
            lines[2]);
    }
}

static int eidos_find_http_header_terminator(
    const char* text,
    size_t length,
    size_t* out_header_length,
    size_t* out_total_length) {
    if (text == NULL || out_header_length == NULL || out_total_length == NULL) {
        return 0;
    }

    for (size_t i = 0; i + 3 < length; i++) {
        if (text[i] == '\r' && text[i + 1] == '\n' &&
            text[i + 2] == '\r' && text[i + 3] == '\n') {
            *out_header_length = i;
            *out_total_length = i + 4;
            return 1;
        }
    }

    for (size_t i = 0; i + 1 < length; i++) {
        if (text[i] == '\n' && text[i + 1] == '\n') {
            *out_header_length = i;
            *out_total_length = i + 2;
            return 1;
        }
    }

    return 0;
}

static void eidos_set_http_response_headers_from_block(const char* header_block, size_t header_length) {
    size_t status_line_end = 0;
    size_t write_index = 0;

    g_eidos_last_http_headers[0] = '\0';

    if (header_block == NULL || header_length == 0) {
        return;
    }

    while (status_line_end < header_length &&
           header_block[status_line_end] != '\n' &&
           header_block[status_line_end] != '\r') {
        status_line_end++;
    }

    while (status_line_end < header_length &&
           (header_block[status_line_end] == '\n' || header_block[status_line_end] == '\r')) {
        status_line_end++;
    }

    for (size_t i = status_line_end; i < header_length && write_index + 1 < sizeof(g_eidos_last_http_headers); i++) {
        char ch = header_block[i];
        if (ch == '\r') {
            continue;
        }

        g_eidos_last_http_headers[write_index++] = ch;
    }

    while (write_index > 0 && g_eidos_last_http_headers[write_index - 1] == '\n') {
        write_index--;
    }

    g_eidos_last_http_headers[write_index] = '\0';
}

static int eidos_extract_http_headers_and_body(
    char* stdout_buffer,
    size_t captured_length,
    size_t* out_body_offset,
    size_t* out_body_length) {
    size_t cursor = 0;
    size_t last_header_start = 0;
    size_t last_header_length = 0;
    int found_headers = 0;

    if (stdout_buffer == NULL || out_body_offset == NULL || out_body_length == NULL) {
        return 0;
    }

    *out_body_offset = 0;
    *out_body_length = captured_length;
    g_eidos_last_http_headers[0] = '\0';

    while (cursor + 5 <= captured_length &&
           memcmp(stdout_buffer + cursor, "HTTP/", 5) == 0) {
        size_t block_header_length = 0;
        size_t block_total_length = 0;

        if (!eidos_find_http_header_terminator(
                stdout_buffer + cursor,
                captured_length - cursor,
                &block_header_length,
                &block_total_length)) {
            break;
        }

        found_headers = 1;
        last_header_start = cursor;
        last_header_length = block_header_length;
        cursor += block_total_length;
    }

    if (!found_headers) {
        return 1;
    }

    eidos_set_http_response_headers_from_block(stdout_buffer + last_header_start, last_header_length);
    *out_body_offset = cursor;
    *out_body_length = captured_length >= cursor ? captured_length - cursor : 0;
    return 1;
}

static int eidos_split_http_capture(
    char* stdout_buffer,
    size_t stdout_length,
    char** out_metadata_text,
    size_t* out_body_length) {
    size_t marker_length = strlen(EIDOS_HTTP_META_MARKER);
    size_t marker_offset = (size_t)-1;

    if (stdout_buffer == NULL || out_metadata_text == NULL || out_body_length == NULL ||
        stdout_length < marker_length) {
        return 0;
    }

    for (size_t i = stdout_length - marker_length + 1; i > 0; i--) {
        size_t offset = i - 1;
        if (memcmp(stdout_buffer + offset, EIDOS_HTTP_META_MARKER, marker_length) == 0) {
            marker_offset = offset;
            break;
        }
    }

    if (marker_offset == (size_t)-1) {
        return 0;
    }

    stdout_buffer[marker_offset] = '\0';
    *out_body_length = marker_offset;
    *out_metadata_text = stdout_buffer + marker_offset + marker_length;
    return 1;
}

static int eidos_string_equals_literal(EidosString* value, const char* literal) {
    size_t literal_length = 0;

    if (value == NULL || literal == NULL) {
        return 0;
    }

    literal_length = strlen(literal);
    return value->length == literal_length &&
           memcmp(value->data, literal, literal_length) == 0;
}

static EidosHttpBackendKind eidos_http_backend_default_kind(void) {
#if defined(EIDOS_ENABLE_LIBCURL)
    return EIDOS_HTTP_BACKEND_KIND_LIBCURL;
#else
    return EIDOS_HTTP_BACKEND_KIND_CURL;
#endif
}

static EidosHttpBackendKind eidos_http_backend_requested_kind(void) {
    const char* backend = getenv("EIDOS_HTTP_BACKEND");

    if (backend == NULL || backend[0] == '\0' ||
        strcmp(backend, "default") == 0 ||
        strcmp(backend, "auto") == 0) {
        return EIDOS_HTTP_BACKEND_KIND_DEFAULT;
    }

    if (strcmp(backend, "curl") == 0) {
        return EIDOS_HTTP_BACKEND_KIND_CURL;
    }

    if (strcmp(backend, "libcurl") == 0) {
        return EIDOS_HTTP_BACKEND_KIND_LIBCURL;
    }

    return EIDOS_HTTP_BACKEND_KIND_DEFAULT;
}

static EidosHttpBackendKind eidos_http_backend_effective_kind(void) {
    EidosHttpBackendKind requested = eidos_http_backend_requested_kind();

    if (requested == EIDOS_HTTP_BACKEND_KIND_DEFAULT) {
        requested = eidos_http_backend_default_kind();
    }

#if defined(EIDOS_ENABLE_LIBCURL)
    if (requested == EIDOS_HTTP_BACKEND_KIND_LIBCURL) {
        return EIDOS_HTTP_BACKEND_KIND_LIBCURL;
    }
#endif

    return EIDOS_HTTP_BACKEND_KIND_CURL;
}

static int eidos_http_method_uses_payload(EidosString* method) {
    return !eidos_string_equals_literal(method, "GET") &&
           !eidos_string_equals_literal(method, "HEAD");
}

#if defined(EIDOS_ENABLE_LIBCURL)
static void eidos_libcurl_capture_init(EidosLibcurlCapture* capture) {
    if (capture == NULL) {
        return;
    }

    capture->final_header_buffer = NULL;
    capture->final_header_length = 0;
    capture->final_header_capacity = 0;
    capture->body_buffer = NULL;
    capture->body_length = 0;
    capture->body_capacity = 0;
}

static void eidos_libcurl_capture_free(EidosLibcurlCapture* capture) {
    if (capture == NULL) {
        return;
    }

    free(capture->final_header_buffer);
    free(capture->body_buffer);
    capture->final_header_buffer = NULL;
    capture->final_header_length = 0;
    capture->final_header_capacity = 0;
    capture->body_buffer = NULL;
    capture->body_length = 0;
    capture->body_capacity = 0;
}

static size_t eidos_libcurl_write_body_callback(void* contents, size_t size, size_t nmemb, void* userdata) {
    EidosLibcurlCapture* capture = (EidosLibcurlCapture*)userdata;
    size_t chunk_length = size * nmemb;

    if (chunk_length == 0) {
        return 0;
    }

    if (capture == NULL ||
        !eidos_append_capture_bytes(
            &capture->body_buffer,
            &capture->body_length,
            &capture->body_capacity,
            (const char*)contents,
            chunk_length)) {
        return 0;
    }

    return chunk_length;
}

static size_t eidos_libcurl_write_header_callback(void* contents, size_t size, size_t nmemb, void* userdata) {
    EidosLibcurlCapture* capture = (EidosLibcurlCapture*)userdata;
    size_t chunk_length = size * nmemb;
    const char* chunk = (const char*)contents;

    if (chunk_length == 0) {
        return 0;
    }

    if (capture == NULL) {
        return 0;
    }

    if (chunk_length >= 5 && memcmp(chunk, "HTTP/", 5) == 0) {
        capture->final_header_length = 0;
        if (capture->final_header_buffer != NULL) {
            capture->final_header_buffer[0] = '\0';
        }
    }

    if (!eidos_append_capture_bytes(
            &capture->final_header_buffer,
            &capture->final_header_length,
            &capture->final_header_capacity,
            chunk,
            chunk_length)) {
        return 0;
    }

    return chunk_length;
}

static int eidos_http_transport_set_stderr(EidosHttpTransportResult* transport, const char* message) {
    size_t stderr_capacity = 0;
    size_t message_length = 0;

    if (transport == NULL) {
        return 0;
    }

    free(transport->stderr_buffer);
    transport->stderr_buffer = NULL;
    transport->stderr_length = 0;

    if (message == NULL) {
        return eidos_append_capture_bytes(
            &transport->stderr_buffer,
            &transport->stderr_length,
            &stderr_capacity,
            "",
            0);
    }

    message_length = strlen(message);
    return eidos_append_capture_bytes(
        &transport->stderr_buffer,
        &transport->stderr_length,
        &stderr_capacity,
        message,
        message_length);
}

static int eidos_build_http_synthetic_capture(
    EidosHttpTransportResult* transport,
    const char* headers,
    size_t headers_length,
    const char* body,
    size_t body_length,
    long long status_code,
    const char* effective_url,
    const char* content_type) {
    size_t stdout_capacity = 0;
    char status_text[32];

    if (transport == NULL) {
        return 0;
    }

    free(transport->stdout_buffer);
    transport->stdout_buffer = NULL;
    transport->stdout_length = 0;

    snprintf(status_text, sizeof(status_text), "%lld", status_code);

    if (!eidos_append_capture_bytes(
            &transport->stdout_buffer,
            &transport->stdout_length,
            &stdout_capacity,
            headers != NULL ? headers : "",
            headers != NULL ? headers_length : 0) ||
        !eidos_append_capture_bytes(
            &transport->stdout_buffer,
            &transport->stdout_length,
            &stdout_capacity,
            body != NULL ? body : "",
            body != NULL ? body_length : 0) ||
        !eidos_append_capture_bytes(
            &transport->stdout_buffer,
            &transport->stdout_length,
            &stdout_capacity,
            EIDOS_HTTP_META_MARKER,
            strlen(EIDOS_HTTP_META_MARKER)) ||
        !eidos_append_capture_bytes(
            &transport->stdout_buffer,
            &transport->stdout_length,
            &stdout_capacity,
            status_text,
            strlen(status_text)) ||
        !eidos_append_capture_bytes(
            &transport->stdout_buffer,
            &transport->stdout_length,
            &stdout_capacity,
            "\n",
            1) ||
        !eidos_append_capture_bytes(
            &transport->stdout_buffer,
            &transport->stdout_length,
            &stdout_capacity,
            effective_url != NULL ? effective_url : "",
            effective_url != NULL ? strlen(effective_url) : 0) ||
        !eidos_append_capture_bytes(
            &transport->stdout_buffer,
            &transport->stdout_length,
            &stdout_capacity,
            "\n",
            1) ||
        !eidos_append_capture_bytes(
            &transport->stdout_buffer,
            &transport->stdout_length,
            &stdout_capacity,
            content_type != NULL ? content_type : "",
            content_type != NULL ? strlen(content_type) : 0) ||
        !eidos_append_capture_bytes(
            &transport->stdout_buffer,
            &transport->stdout_length,
            &stdout_capacity,
            "\n",
            1)) {
        free(transport->stdout_buffer);
        transport->stdout_buffer = NULL;
        transport->stdout_length = 0;
        return 0;
    }

    return 1;
}

static int eidos_http_execute_request_libcurl(
    const EidosHttpRequestSpec* request,
    EidosHttpTransportResult* transport) {
    CURL* curl = NULL;
    struct curl_slist* header_list = NULL;
    char** extra_headers = NULL;
    size_t extra_header_count = 0;
    char error_buffer[CURL_ERROR_SIZE];
    EidosLibcurlCapture capture;
    CURLcode perform_result;
    CURLcode info_result;
    long response_code = 0;
    long connect_timeout = 5;
    long total_timeout = 15;
    char* effective_url = NULL;
    char* content_type = NULL;
    const char* request_body = "";
    size_t request_body_length = 0;
    int has_payload = 0;
    int success = 0;

    if (request == NULL || transport == NULL) {
        return 0;
    }

    if (!eidos_parse_http_headers(request->headers, &extra_headers, &extra_header_count)) {
        eidos_set_io_error_message("failed to allocate request headers");
        return 0;
    }

    eidos_libcurl_capture_init(&capture);
    memset(error_buffer, 0, sizeof(error_buffer));

    curl = curl_easy_init();
    if (curl == NULL) {
        eidos_free_http_headers(extra_headers, extra_header_count);
        eidos_set_io_error_message("failed to initialize libcurl");
        return 0;
    }

    connect_timeout = request->connect_timeout_seconds > 0 ? (long)request->connect_timeout_seconds : 5L;
    total_timeout = request->total_timeout_seconds > 0 ? (long)request->total_timeout_seconds : 15L;

    if (request->content_type != NULL && request->content_type->length > 0) {
        size_t header_length = strlen("Content-Type: ") + request->content_type->length + 1;
        char* content_type_header = (char*)malloc(header_length);
        if (content_type_header == NULL) {
            curl_easy_cleanup(curl);
            eidos_free_http_headers(extra_headers, extra_header_count);
            eidos_set_io_error_message("failed to allocate content-type header");
            return 0;
        }

        snprintf(content_type_header, header_length, "Content-Type: %s", request->content_type->data);
        header_list = curl_slist_append(header_list, content_type_header);
        free(content_type_header);
        if (header_list == NULL) {
            curl_easy_cleanup(curl);
            eidos_free_http_headers(extra_headers, extra_header_count);
            eidos_set_io_error_message("failed to build libcurl request headers");
            return 0;
        }
    }

    for (size_t i = 0; i < extra_header_count; i++) {
        struct curl_slist* next_list = curl_slist_append(header_list, extra_headers[i]);
        if (next_list == NULL) {
            curl_slist_free_all(header_list);
            curl_easy_cleanup(curl);
            eidos_free_http_headers(extra_headers, extra_header_count);
            eidos_set_io_error_message("failed to build libcurl request headers");
            return 0;
        }

        header_list = next_list;
    }

    if (request->use_binary_body) {
        has_payload = request->binary_body_length > 0 || eidos_http_method_uses_payload(request->method);
        request_body = has_payload && request->binary_body_length > 0
            ? (const char*)request->binary_body
            : "";
        request_body_length = request->binary_body_length;
    }
    else if (request->text_body != NULL) {
        has_payload = request->text_body->length > 0 || eidos_http_method_uses_payload(request->method);
        request_body = has_payload && request->text_body->length > 0
            ? request->text_body->data
            : "";
        request_body_length = request->text_body->length;
    }

    if (curl_easy_setopt(curl, CURLOPT_ERRORBUFFER, error_buffer) != CURLE_OK ||
        curl_easy_setopt(curl, CURLOPT_URL, (const char*)request->url->data) != CURLE_OK ||
        curl_easy_setopt(curl, CURLOPT_FOLLOWLOCATION, 1L) != CURLE_OK ||
        curl_easy_setopt(curl, CURLOPT_CONNECTTIMEOUT, connect_timeout) != CURLE_OK ||
        curl_easy_setopt(curl, CURLOPT_TIMEOUT, total_timeout) != CURLE_OK ||
        curl_easy_setopt(curl, CURLOPT_CUSTOMREQUEST, (const char*)request->method->data) != CURLE_OK ||
        curl_easy_setopt(curl, CURLOPT_WRITEFUNCTION, eidos_libcurl_write_body_callback) != CURLE_OK ||
        curl_easy_setopt(curl, CURLOPT_WRITEDATA, &capture) != CURLE_OK ||
        curl_easy_setopt(curl, CURLOPT_HEADERFUNCTION, eidos_libcurl_write_header_callback) != CURLE_OK ||
        curl_easy_setopt(curl, CURLOPT_HEADERDATA, &capture) != CURLE_OK) {
        if (header_list != NULL) {
            curl_slist_free_all(header_list);
        }
        curl_easy_cleanup(curl);
        eidos_libcurl_capture_free(&capture);
        eidos_free_http_headers(extra_headers, extra_header_count);
        eidos_set_io_error_message("failed to configure libcurl request");
        return 0;
    }

    if (header_list != NULL && curl_easy_setopt(curl, CURLOPT_HTTPHEADER, header_list) != CURLE_OK) {
        curl_slist_free_all(header_list);
        curl_easy_cleanup(curl);
        eidos_libcurl_capture_free(&capture);
        eidos_free_http_headers(extra_headers, extra_header_count);
        eidos_set_io_error_message("failed to configure libcurl request headers");
        return 0;
    }

    if (eidos_string_equals_literal(request->method, "HEAD")) {
        if (curl_easy_setopt(curl, CURLOPT_NOBODY, 1L) != CURLE_OK) {
            if (header_list != NULL) {
                curl_slist_free_all(header_list);
            }

            curl_easy_cleanup(curl);
            eidos_libcurl_capture_free(&capture);
            eidos_free_http_headers(extra_headers, extra_header_count);
            eidos_set_io_error_message("failed to configure HEAD request");
            return 0;
        }
    }
    else if (curl_easy_setopt(curl, CURLOPT_NOBODY, 0L) != CURLE_OK) {
        if (header_list != NULL) {
            curl_slist_free_all(header_list);
        }

        curl_easy_cleanup(curl);
        eidos_libcurl_capture_free(&capture);
        eidos_free_http_headers(extra_headers, extra_header_count);
        eidos_set_io_error_message("failed to configure libcurl request body mode");
        return 0;
    }

    if (has_payload &&
        (curl_easy_setopt(curl, CURLOPT_POSTFIELDS, request_body) != CURLE_OK ||
         curl_easy_setopt(curl, CURLOPT_POSTFIELDSIZE_LARGE, (curl_off_t)request_body_length) != CURLE_OK)) {
        if (header_list != NULL) {
            curl_slist_free_all(header_list);
        }

        curl_easy_cleanup(curl);
        eidos_libcurl_capture_free(&capture);
        eidos_free_http_headers(extra_headers, extra_header_count);
        eidos_set_io_error_message("failed to configure libcurl request payload");
        return 0;
    }

    perform_result = curl_easy_perform(curl);
    if (perform_result != CURLE_OK) {
        transport->exit_code = 1;
        if (!eidos_http_transport_set_stderr(
                transport,
                error_buffer[0] != '\0' ? error_buffer : curl_easy_strerror(perform_result))) {
            if (header_list != NULL) {
                curl_slist_free_all(header_list);
            }

            curl_easy_cleanup(curl);
            eidos_libcurl_capture_free(&capture);
            eidos_free_http_headers(extra_headers, extra_header_count);
            eidos_set_io_error_message("failed to capture libcurl error output");
            return 0;
        }
    }
    else {
        transport->exit_code = 0;
        if (!eidos_http_transport_set_stderr(transport, "")) {
            if (header_list != NULL) {
                curl_slist_free_all(header_list);
            }

            curl_easy_cleanup(curl);
            eidos_libcurl_capture_free(&capture);
            eidos_free_http_headers(extra_headers, extra_header_count);
            eidos_set_io_error_message("failed to capture libcurl stderr");
            return 0;
        }
    }

    info_result = curl_easy_getinfo(curl, CURLINFO_RESPONSE_CODE, &response_code);
    if (info_result != CURLE_OK) {
        response_code = 0;
    }

    info_result = curl_easy_getinfo(curl, CURLINFO_EFFECTIVE_URL, &effective_url);
    if (info_result != CURLE_OK || effective_url == NULL) {
        effective_url = (char*)request->url->data;
    }

    info_result = curl_easy_getinfo(curl, CURLINFO_CONTENT_TYPE, &content_type);
    if (info_result != CURLE_OK || content_type == NULL) {
        content_type = "";
    }

    success = eidos_build_http_synthetic_capture(
        transport,
        capture.final_header_buffer,
        capture.final_header_length,
        capture.body_buffer,
        capture.body_length,
        (long long)response_code,
        effective_url,
        content_type);

    if (header_list != NULL) {
        curl_slist_free_all(header_list);
    }

    curl_easy_cleanup(curl);
    eidos_libcurl_capture_free(&capture);
    eidos_free_http_headers(extra_headers, extra_header_count);

    if (!success) {
        eidos_set_io_error_message("failed to build libcurl http capture");
        return 0;
    }

    return 1;
}
#endif

static char* eidos_copy_trimmed_header_line(const char* data, size_t length) {
    size_t start = 0;
    size_t end = length;
    char* line = NULL;

    while (start < end && (data[start] == ' ' || data[start] == '\t' || data[start] == '\r')) {
        start++;
    }

    while (end > start && (data[end - 1] == ' ' || data[end - 1] == '\t' || data[end - 1] == '\r')) {
        end--;
    }

    if (end <= start) {
        return NULL;
    }

    line = (char*)malloc(end - start + 1);
    if (line == NULL) {
        return NULL;
    }

    memcpy(line, data + start, end - start);
    line[end - start] = '\0';
    return line;
}

static int eidos_parse_http_headers(EidosString* headers, char*** header_lines, size_t* header_count) {
    size_t estimated = 1;
    size_t line_start = 0;
    size_t count = 0;
    char** lines = NULL;

    if (header_lines == NULL || header_count == NULL) {
        return 0;
    }

    *header_lines = NULL;
    *header_count = 0;

    if (headers == NULL || headers->length == 0) {
        return 1;
    }

    for (size_t i = 0; i < headers->length; i++) {
        if (headers->data[i] == '\n') {
            estimated++;
        }
    }

    lines = (char**)calloc(estimated, sizeof(char*));
    if (lines == NULL) {
        return 0;
    }

    for (size_t i = 0; i <= headers->length; i++) {
        if (i < headers->length && headers->data[i] != '\n') {
            continue;
        }

        if (i > line_start) {
            char* line = eidos_copy_trimmed_header_line(headers->data + line_start, i - line_start);
            if (line == NULL) {
                if (i - line_start > 0) {
                    for (size_t j = 0; j < count; j++) {
                        free(lines[j]);
                    }

                    free(lines);
                    return 0;
                }
            }
            else {
                lines[count++] = line;
            }
        }

        line_start = i + 1;
    }

    if (count == 0) {
        free(lines);
        return 1;
    }

    *header_lines = lines;
    *header_count = count;
    return 1;
}

static void eidos_free_http_headers(char** header_lines, size_t header_count) {
    if (header_lines == NULL) {
        return;
    }

    for (size_t i = 0; i < header_count; i++) {
        free(header_lines[i]);
    }

    free(header_lines);
}

static void eidos_http_transport_result_init(EidosHttpTransportResult* result) {
    if (result == NULL) {
        return;
    }

    result->stdout_buffer = NULL;
    result->stdout_length = 0;
    result->stderr_buffer = NULL;
    result->stderr_length = 0;
    result->exit_code = -1;
}

static void eidos_http_transport_result_free(EidosHttpTransportResult* result) {
    if (result == NULL) {
        return;
    }

    free(result->stdout_buffer);
    free(result->stderr_buffer);
    result->stdout_buffer = NULL;
    result->stdout_length = 0;
    result->stderr_buffer = NULL;
    result->stderr_length = 0;
    result->exit_code = -1;
}

static int eidos_http_execute_request_curl(
    const EidosHttpRequestSpec* request,
    EidosHttpTransportResult* transport) {
    char* content_type_header = NULL;
    char** extra_headers = NULL;
    size_t extra_header_count = 0;
    const char** curl_argv = NULL;
    int curl_argc = 0;
    int use_request_body_stdin = 0;
    char connect_timeout_text[32];
    char total_timeout_text[32];

    if (request == NULL || transport == NULL) {
        return 0;
    }

    if (!eidos_parse_http_headers(request->headers, &extra_headers, &extra_header_count)) {
        eidos_set_io_error_message("failed to allocate request headers");
        return 0;
    }

    if (request->connect_timeout_seconds <= 0) {
        const char* env_connect = getenv("EIDOS_HTTP_CONNECT_TIMEOUT");
        if (env_connect != NULL && env_connect[0] != '\0') {
            snprintf(connect_timeout_text, sizeof(connect_timeout_text), "%s", env_connect);
        }
        else {
            snprintf(connect_timeout_text, sizeof(connect_timeout_text), "%d", 5);
        }
    }
    else {
        snprintf(connect_timeout_text, sizeof(connect_timeout_text), "%lld", (long long)request->connect_timeout_seconds);
    }

    if (request->total_timeout_seconds <= 0) {
        const char* env_total = getenv("EIDOS_HTTP_TOTAL_TIMEOUT");
        if (env_total != NULL && env_total[0] != '\0') {
            snprintf(total_timeout_text, sizeof(total_timeout_text), "%s", env_total);
        }
        else {
            snprintf(total_timeout_text, sizeof(total_timeout_text), "%d", 15);
        }
    }
    else {
        snprintf(total_timeout_text, sizeof(total_timeout_text), "%lld", (long long)request->total_timeout_seconds);
    }

    use_request_body_stdin = request->use_binary_body &&
        (request->binary_body_length > 0 || eidos_http_method_uses_payload(request->method));

    curl_argv = (const char**)calloc(26 + (extra_header_count * 2), sizeof(const char*));
    if (curl_argv == NULL) {
        eidos_free_http_headers(extra_headers, extra_header_count);
        eidos_set_io_error_message("failed to allocate curl argv");
        return 0;
    }

    curl_argv[curl_argc++] = "curl";
    curl_argv[curl_argc++] = "-sS";
    curl_argv[curl_argc++] = "-L";
    curl_argv[curl_argc++] = "-D";
    curl_argv[curl_argc++] = "-";
    curl_argv[curl_argc++] = "--connect-timeout";
    curl_argv[curl_argc++] = connect_timeout_text;
    curl_argv[curl_argc++] = "--max-time";
    curl_argv[curl_argc++] = total_timeout_text;
    curl_argv[curl_argc++] = "-X";
    curl_argv[curl_argc++] = request->method->data;

    if (request->content_type != NULL && request->content_type->length > 0) {
        size_t header_length = strlen("Content-Type: ") + request->content_type->length + 1;
        content_type_header = (char*)malloc(header_length);
        if (content_type_header == NULL) {
            free(curl_argv);
            eidos_free_http_headers(extra_headers, extra_header_count);
            eidos_set_io_error_message("failed to allocate content-type header");
            return 0;
        }

        snprintf(content_type_header, header_length, "Content-Type: %s", request->content_type->data);
        curl_argv[curl_argc++] = "-H";
        curl_argv[curl_argc++] = content_type_header;
    }

    if (request->use_binary_body) {
        if (use_request_body_stdin) {
            curl_argv[curl_argc++] = "--data-binary";
            curl_argv[curl_argc++] = "@-";
        }
    }
    else if (request->text_body != NULL &&
             (request->text_body->length > 0 || eidos_http_method_uses_payload(request->method))) {
        curl_argv[curl_argc++] = "--data-binary";
        curl_argv[curl_argc++] = request->text_body->data;
    }

    for (size_t i = 0; i < extra_header_count; i++) {
        curl_argv[curl_argc++] = "-H";
        curl_argv[curl_argc++] = extra_headers[i];
    }

    curl_argv[curl_argc++] = "-w";
    curl_argv[curl_argc++] = EIDOS_HTTP_META_MARKER "%{http_code}\n%{url_effective}\n%{content_type}\n";
    curl_argv[curl_argc++] = request->url->data;
    curl_argv[curl_argc] = NULL;

    if (!eidos_run_process_capture_to_memory(
            curl_argv,
            request->binary_body,
            request->binary_body_length,
            use_request_body_stdin,
            &transport->stdout_buffer,
            &transport->stdout_length,
            &transport->stderr_buffer,
            &transport->stderr_length,
            &transport->exit_code)) {
        free(curl_argv);
        free(content_type_header);
        eidos_free_http_headers(extra_headers, extra_header_count);
        eidos_set_io_error_message("failed to start curl process");
        return 0;
    }

    free(curl_argv);
    free(content_type_header);
    eidos_free_http_headers(extra_headers, extra_header_count);
    return 1;
}

static int eidos_http_execute_request(
    const EidosHttpRequestSpec* request,
    EidosHttpTransportResult* transport) {
    /* Default backend is libcurl when available; keep this wrapper thin so backend
       selection stays isolated from stdlib-facing request/response logic. */
#if defined(EIDOS_ENABLE_LIBCURL)
    if (eidos_http_backend_effective_kind() == EIDOS_HTTP_BACKEND_KIND_LIBCURL) {
        return eidos_http_execute_request_libcurl(request, transport);
    }
#endif

    return eidos_http_execute_request_curl(request, transport);
}

/* ============================================================
 * Destructor Registry
 * ============================================================ */

#define EIDOS_MAX_DESTRUCTORS 256

typedef struct DestructorEntry {
    uint32_t type_id;
    EidosDestructor destructor;
} DestructorEntry;

static DestructorEntry g_destructors[EIDOS_MAX_DESTRUCTORS];
static volatile size_t g_destructor_count = 0;

/* Mutex for thread-safe destructor table access */
#if defined(_WIN32)
static CRITICAL_SECTION g_destructor_lock;
static int g_destructor_lock_initialized = 0;

static void ensure_destructor_lock(void) {
    if (!g_destructor_lock_initialized) {
        InitializeCriticalSection(&g_destructor_lock);
        g_destructor_lock_initialized = 1;
    }
}

#define EIDOS_LOCK_DESTRUCTORS()   (ensure_destructor_lock(), EnterCriticalSection(&g_destructor_lock))
#define EIDOS_UNLOCK_DESTRUCTORS() LeaveCriticalSection(&g_destructor_lock)
#else
#include <pthread.h>
static pthread_mutex_t g_destructor_lock = PTHREAD_MUTEX_INITIALIZER;

#define EIDOS_LOCK_DESTRUCTORS()   pthread_mutex_lock(&g_destructor_lock)
#define EIDOS_UNLOCK_DESTRUCTORS() pthread_mutex_unlock(&g_destructor_lock)
#endif

void eidos_register_destructor(uint32_t type_id, EidosDestructor destructor) {
    EIDOS_LOCK_DESTRUCTORS();

    if (g_destructor_count >= EIDOS_MAX_DESTRUCTORS) {
        EIDOS_UNLOCK_DESTRUCTORS();
        eidos_panic("eidos_register_destructor: destructor table full");
    }

    /* Check for existing entry */
    for (size_t i = 0; i < g_destructor_count; i++) {
        if (g_destructors[i].type_id == type_id) {
            g_destructors[i].destructor = destructor;
            EIDOS_UNLOCK_DESTRUCTORS();
            return;
        }
    }

    /* Add new entry */
    g_destructors[g_destructor_count].type_id = type_id;
    g_destructors[g_destructor_count].destructor = destructor;
    g_destructor_count++;

    EIDOS_UNLOCK_DESTRUCTORS();
}

static EidosDestructor find_destructor(uint32_t type_id) {
    /* Acquire fence: ensures we see all destructor entries written before
     * g_destructor_count was incremented in eidos_register_destructor.
     * This pairs with the release semantics of the mutex unlock in register. */
#if defined(_WIN32)
    MemoryBarrier();  /* Full barrier on Windows */
#else
    __atomic_thread_fence(__ATOMIC_ACQUIRE);
#endif
    size_t count = g_destructor_count;
    for (size_t i = 0; i < count; i++) {
        if (g_destructors[i].type_id == type_id) {
            return g_destructors[i].destructor;
        }
    }
    return NULL;
}

/* ============================================================
 * Memory Management Implementation
 * ============================================================ */

/**
 * Get pointer to header from object pointer
 */
static inline EidosHeader* get_header(void* ptr) {
    return (EidosHeader*)((char*)ptr - sizeof(EidosHeader));
}

/**
 * Get object pointer from header pointer
 */
static inline void* get_object(EidosHeader* header) {
    return (void*)((char*)header + sizeof(EidosHeader));
}

void* eidos_alloc(size_t size, uint32_t type_id) {
    /* Allocate memory with space for reference-count header.
     * Layout: [EidosHeader(ref_count, type_id)] [user data of 'size' bytes]
     * The returned pointer points past the header to user data.
     * get_header(ptr) recovers the header via pointer arithmetic.
     *
     * NOTE: Callers pass sizeof(EidosType) where EidosType's first field is
     * EidosHeader — this means 8 bytes are wasted per allocation (the struct's
     * own header field is never initialized). This is a known design trade-off
     * kept for ABI stability with generated code. */
    size_t total_size = sizeof(EidosHeader) + size;
    EidosHeader* header = (EidosHeader*)EIDOS_MALLOC(total_size);

    if (header == NULL) {
        eidos_panic("eidos_alloc: out of memory");
    }

    /* Initialize header */
    header->ref_count = 1;
    header->type_id = type_id;

    /* Return pointer to object (after header) */
    return get_object(header);
}

void eidos_free(void* ptr) {
    if (ptr == NULL) return;

    EidosHeader* header = get_header(ptr);
    EIDOS_FREE(header);
}

void* eidos_incref(void* ptr) {
    if (ptr == NULL) return NULL;

    EidosHeader* header = get_header(ptr);

    /* Atomic increment for thread-safe reference counting */
    EIDOS_ATOMIC_INC32(&header->ref_count);

    return ptr;
}

/* Stack-less freeing: defer queue for iterative destruction.
 * When eidos_decref reaches ref_count 0 inside a destructor chain,
 * it pushes the object to this thread-local queue instead of recursing.
 * The outermost eidos_decref call drains the queue iteratively. */

#define EIDOS_DEFER_QUEUE_INITIAL_CAP 64

typedef struct EidosDeferQueue {
    void** entries;
    size_t count;
    size_t capacity;
    int active;  /* Non-zero when inside eidos_decref draining loop */
} EidosDeferQueue;

static _Thread_local EidosDeferQueue g_decref_queue = { NULL, 0, 0, 0 };

static void eidos_defer_queue_push(void* ptr) {
    if (g_decref_queue.count >= g_decref_queue.capacity) {
        size_t new_cap = g_decref_queue.capacity == 0
            ? EIDOS_DEFER_QUEUE_INITIAL_CAP
            : g_decref_queue.capacity * 2;
        void** new_entries = (void**)EIDOS_REALLOC(g_decref_queue.entries, new_cap * sizeof(void*));
        if (new_entries == NULL) {
            /* Allocation failure: fall back to immediate free */
            eidos_free(ptr);
            return;
        }
        g_decref_queue.entries = new_entries;
        g_decref_queue.capacity = new_cap;
    }
    g_decref_queue.entries[g_decref_queue.count++] = ptr;
}

static void eidos_decref_inner(void* ptr) {
    if (ptr == NULL) return;

    EidosHeader* header = get_header(ptr);
    uint32_t new_count = (uint32_t)EIDOS_ATOMIC_DEC32(&header->ref_count);

    if ((new_count & EIDOS_COUNT_MASK) == 0) {
        if (g_decref_queue.active) {
            /* Inside a drain loop: defer to avoid stack overflow */
            eidos_defer_queue_push(ptr);
        } else {
            /* Outermost call: drain iteratively */
            g_decref_queue.active = 1;
            eidos_defer_queue_push(ptr);

            while (g_decref_queue.count > 0) {
                void* obj = g_decref_queue.entries[--g_decref_queue.count];
                EidosHeader* obj_header = get_header(obj);

                /* Call destructor if registered */
                EidosDestructor destructor = find_destructor(obj_header->type_id);
                if (destructor != NULL) {
                    destructor(obj);
                }
                eidos_free(obj);
            }

            g_decref_queue.active = 0;
        }
    }
}

void eidos_decref(void* ptr) {
    eidos_decref_inner(ptr);
}

void* eidos_dup(void* ptr) {
    return eidos_incref(ptr);
}

void eidos_drop(void* ptr) {
    eidos_decref(ptr);
}

void eidos_share(void* ptr) {
    if (ptr == NULL) return;

    EidosHeader* header = get_header(ptr);

    /* Atomically set the SHARED bit (bit 31). Once set, all subsequent
     * incref/decref on this object must go through atomic shared variants.
     * The count bits (0-30) are preserved. */
    EIDOS_ATOMIC_OR32(&header->ref_count, EIDOS_SHARED_BIT);
}

/* ============================================================
 * Atomic shared-path incref / decref
 * ============================================================ */

void eidos_incref_shared(void* ptr) {
    if (ptr == NULL) return;

    EidosHeader* header = get_header(ptr);
    EIDOS_ATOMIC_INC32(&header->ref_count);
}

void eidos_decref_shared(void* ptr) {
    eidos_decref_inner(ptr);
}

/* ============================================================
 * Non-atomic fast path (single-threaded objects)
 * ============================================================ */

void* eidos_incref_local(void* ptr) {
    if (ptr == NULL) return NULL;

    EidosHeader* header = get_header(ptr);

    /* Fast path: if SHARED bit is set, forward to atomic variant.
     * In single-threaded code this branch is never taken (perfect prediction). */
    if (header->ref_count & EIDOS_SHARED_BIT) {
        eidos_incref_shared(ptr);
        return ptr;
    }

    header->ref_count++;  /* Non-atomic: only safe for single-threaded objects */

    return ptr;
}

void eidos_decref_local(void* ptr) {
    if (ptr == NULL) return;

    EidosHeader* header = get_header(ptr);

    /* Fast path: if SHARED bit is set, forward to atomic variant.
     * In single-threaded code this branch is never taken (perfect prediction). */
    if (header->ref_count & EIDOS_SHARED_BIT) {
        eidos_decref_shared(ptr);
        return;
    }

    header->ref_count--;  /* Non-atomic: only safe for single-threaded objects */

    if (header->ref_count == 0) {
        if (g_decref_queue.active) {
            /* Inside a drain loop: defer to avoid stack overflow */
            eidos_defer_queue_push(ptr);
        } else {
            /* Outermost call: drain iteratively */
            g_decref_queue.active = 1;
            eidos_defer_queue_push(ptr);

            while (g_decref_queue.count > 0) {
                void* obj = g_decref_queue.entries[--g_decref_queue.count];
                EidosHeader* obj_header = get_header(obj);

                EidosDestructor destructor = find_destructor(obj_header->type_id);
                if (destructor != NULL) {
                    destructor(obj);
                }
                eidos_free(obj);
            }

            g_decref_queue.active = 0;
        }
    }
}

/* ============================================================
 * Drop-in-place Reuse (Koka kk_reuse_t pattern)
 * ============================================================ */

void* eidos_alloc_reuse(EidosReuse* reuse, size_t obj_size, uint32_t type_id) {
    if (reuse != NULL && reuse->header_ptr != NULL) {
        /* Check if type matches and block is large enough.
         * total_size == 0 means "unknown" (set by eidos_drop_reuse when
         * the original allocation size is not tracked) — accept any size. */
        bool size_ok = (reuse->total_size == 0) ||
                       (reuse->total_size >= sizeof(EidosHeader) + obj_size);
        if (reuse->type_id == type_id && size_ok) {
            void* header_ptr = reuse->header_ptr;
            reuse->header_ptr = NULL;
            reuse->total_size = 0;
            reuse->type_id = 0;

            /* Reinitialize header for the new object */
            EidosHeader* header = (EidosHeader*)header_ptr;
            header->ref_count = 1;
            header->type_id = type_id;
            return get_object(header);
        }

        /* Type or size mismatch — free the old block */
        EIDOS_FREE(reuse->header_ptr);
        reuse->header_ptr = NULL;
        reuse->total_size = 0;
        reuse->type_id = 0;
    }

    /* Fall back to fresh allocation */
    return eidos_alloc(obj_size, type_id);
}

void eidos_drop_reuse(void* ptr, EidosReuse* reuse) {
    if (ptr == NULL) return;

    EidosHeader* header = get_header(ptr);

    int32_t new_count;
    if (header->ref_count & EIDOS_SHARED_BIT) {
        new_count = (int32_t)EIDOS_ATOMIC_DEC32(&header->ref_count);
    } else {
        header->ref_count--;
        new_count = header->ref_count;
    }

    if ((new_count & EIDOS_COUNT_MASK) == 0) {
        int outermost = !g_decref_queue.active;
        if (outermost) {
            g_decref_queue.active = 1;
        }

        EidosDestructor destructor = find_destructor(header->type_id);
        if (destructor != NULL) {
            destructor(ptr);
        }

        if (reuse != NULL && reuse->header_ptr == NULL) {
            reuse->header_ptr = header;
            reuse->total_size = 0;
            reuse->type_id = header->type_id;
        } else {
            EIDOS_FREE(header);
        }

        if (outermost) {
            while (g_decref_queue.count > 0) {
                void* obj = g_decref_queue.entries[--g_decref_queue.count];
                EidosHeader* obj_header = get_header(obj);

                EidosDestructor queued_destructor = find_destructor(obj_header->type_id);
                if (queued_destructor != NULL) {
                    queued_destructor(obj);
                }
                eidos_free(obj);
            }

            g_decref_queue.active = 0;
        }
    }
}

/* ============================================================
 * String Implementation
 * ============================================================ */

#define EIDOS_STRING_INTERN_CAPACITY 1024

typedef struct EidosStringInternEntry {
    uint64_t hash;
    EidosString* value;
} EidosStringInternEntry;

static EidosStringInternEntry g_string_intern_table[EIDOS_STRING_INTERN_CAPACITY];
static size_t g_string_intern_count = 0;

#if defined(_WIN32)
static CRITICAL_SECTION g_string_intern_lock;
static int g_string_intern_lock_initialized = 0;

static void eidos_string_intern_lock(void) {
    if (!g_string_intern_lock_initialized) {
        InitializeCriticalSection(&g_string_intern_lock);
        g_string_intern_lock_initialized = 1;
    }
    EnterCriticalSection(&g_string_intern_lock);
}

static void eidos_string_intern_unlock(void) {
    LeaveCriticalSection(&g_string_intern_lock);
}
#else
#include <pthread.h>
static pthread_mutex_t g_string_intern_lock = PTHREAD_MUTEX_INITIALIZER;

static void eidos_string_intern_lock(void) {
    pthread_mutex_lock(&g_string_intern_lock);
}

static void eidos_string_intern_unlock(void) {
    pthread_mutex_unlock(&g_string_intern_lock);
}
#endif

static uint64_t eidos_string_hash_fnv1a(const char* data, size_t len) {
    uint64_t hash = 14695981039346656037ull;
    for (size_t i = 0; i < len; i++) {
        hash ^= (unsigned char)data[i];
        hash *= 1099511628211ull;
    }
    return hash;
}

EidosString* eidos_string_from_cstr(const char* str) {
    if (str == NULL) return NULL;

    size_t len = strlen(str);
    return eidos_string_new(str, len);
}

EidosString* eidos_string_intern(const char* data, size_t len) {
    if (data == NULL && len != 0) return NULL;

    uint64_t hash = eidos_string_hash_fnv1a(data == NULL ? "" : data, len);
    size_t index = (size_t)(hash % EIDOS_STRING_INTERN_CAPACITY);

    eidos_string_intern_lock();

    for (size_t probe = 0; probe < EIDOS_STRING_INTERN_CAPACITY; probe++) {
        EidosStringInternEntry* entry = &g_string_intern_table[index];
        EidosString* interned = entry->value;

        if (interned == NULL) {
            if (g_string_intern_count >= EIDOS_STRING_INTERN_CAPACITY) {
                eidos_string_intern_unlock();
                return eidos_string_new(data, len);
            }

            EidosString* created = eidos_string_new(data, len);
            if (created == NULL) {
                eidos_string_intern_unlock();
                return NULL;
            }

            entry->hash = hash;
            entry->value = created;
            g_string_intern_count++;
            EidosString* result = (EidosString*)eidos_incref(created);
            eidos_string_intern_unlock();
            return result;
        }

        if (entry->hash == hash &&
            interned->length == len &&
            (len == 0 || memcmp(interned->data, data, len) == 0)) {
            EidosString* result = (EidosString*)eidos_incref(interned);
            eidos_string_intern_unlock();
            return result;
        }

        index = (index + 1) % EIDOS_STRING_INTERN_CAPACITY;
    }

    eidos_string_intern_unlock();
    return eidos_string_new(data, len);
}

EidosString* eidos_string_new(const char* data, size_t len) {
    /* Allocate string object */
    size_t total_size = sizeof(EidosString) + len + 1;  /* +1 for null terminator */
    EidosString* str = (EidosString*)eidos_alloc(total_size, EIDOS_TYPE_STRING);

    if (str == NULL) return NULL;

    str->length = len;

    /* Copy data */
    if (data != NULL && len > 0) {
        memcpy(str->data, data, len);
    }
    str->data[len] = '\0';  /* Null terminate for C compatibility */

    return str;
}

EidosString* eidos_string_concat(EidosString* a, EidosString* b) {
    if (a == NULL && b == NULL) return NULL;
    if (a == NULL) return eidos_incref(b);
    if (b == NULL) return eidos_incref(a);

    size_t total_len = a->length + b->length;
    size_t total_size = sizeof(EidosString) + total_len + 1;
    EidosString* result = (EidosString*)eidos_alloc(total_size, EIDOS_TYPE_STRING);

    if (result == NULL) return NULL;

    result->length = total_len;

    /* Copy data from both strings */
    memcpy(result->data, a->data, a->length);
    memcpy(result->data + a->length, b->data, b->length);
    result->data[total_len] = '\0';

    return result;
}

size_t eidos_string_length(EidosString* str) {
    if (str == NULL) {
        return 0;
    }

    return str->length;
}

int64_t eidos_string_char_at(EidosString* str, size_t index) {
    if (str == NULL || index >= str->length) {
        return -1;
    }

    return (unsigned char)str->data[index];
}

EidosString* eidos_string_slice(EidosString* str, size_t start, size_t len) {
    if (str == NULL || start >= str->length) {
        return eidos_string_new("", 0);
    }

    size_t max_len = str->length - start;
    size_t clamped_len = len > max_len ? max_len : len;
    return eidos_string_new(str->data + start, clamped_len);
}

bool eidos_string_equals(EidosString* a, EidosString* b) {
    if (a == b) {
        return true;
    }

    if (a == NULL || b == NULL) {
        return false;
    }

    if (a->length != b->length) {
        return false;
    }

    if (a->length == 0) {
        return true;
    }

    return memcmp(a->data, b->data, a->length) == 0;
}

EidosString* eidos_string_from_char(int64_t value) {
    char ch = (char)(value & 0xFF);
    return eidos_string_new(&ch, 1);
}

EidosString* eidos_int_to_string(int64_t value) {
    char buffer[32];
    int written = snprintf(buffer, sizeof(buffer), "%lld", (long long)value);
    if (written < 0) {
        return eidos_string_new("", 0);
    }

    return eidos_string_new(buffer, (size_t)written);
}

double eidos_int_to_float(int64_t value) {
    return (double)value;
}

double eidos_string_to_float(EidosString* str) {
    if (str == NULL || str->data == NULL) return 0.0;
    char* buf = (char*)malloc(str->length + 1);
    if (buf == NULL) return 0.0;
    memcpy(buf, str->data, str->length);
    buf[str->length] = '\0';
    double result = strtod(buf, NULL);
    free(buf);
    return result;
}

/* ============================================================
 * I/O Implementation
 * ============================================================ */

void eidos_print_int(int64_t value) {
    printf("%lld", (long long)value);
}

void eidos_print_float(double value) {
    printf("%g", value);
}

void eidos_print_string(EidosString* str) {
    if (str != NULL) {
        printf("%.*s", (int)str->length, str->data);
    }
}

void eidos_print_newline(void) {
    printf("\n");
}

void eidos_print_char(int64_t value) {
    putchar((unsigned char)(value & 0xFF));
}

/* Pushback byte for read_line when handling \r\n sequences with raw reads.
 * Holds one byte that was peeked but not consumed.  EOF means empty. */
static int eidos_stdin_pushback_byte = EOF;

EidosString* eidos_read_line(void) {
    size_t capacity = 128;
    size_t length = 0;
    char* buffer = (char*)malloc(capacity);
    int ch = 0;
    int reached_eof = 0;

    if (buffer == NULL) {
        eidos_set_io_error_message("stdin read allocation failed");
        return eidos_string_new("", 0);
    }

    for (;;) {
        /* Consume pushback byte first. */
        if (eidos_stdin_pushback_byte != EOF) {
            ch = eidos_stdin_pushback_byte;
            eidos_stdin_pushback_byte = EOF;
        } else {
            unsigned char byte;
#if defined(_WIN32)
            int n = _read(_fileno(stdin), &byte, 1);
#else
            ssize_t n = read(STDIN_FILENO, &byte, 1);
#endif
            if (n == 0) {
                reached_eof = 1;
                break;
            }
            if (n < 0) {
#if !defined(_WIN32)
                if (errno == EINTR) continue;
#endif
                free(buffer);
                eidos_set_io_error_message("stdin read failed");
                return eidos_string_new("", 0);
            }
            ch = (int)byte;
        }

        if (ch == '\r') {
            /* Check for \r\n sequence. */
            unsigned char peek;
#if defined(_WIN32)
            int n2 = _read(_fileno(stdin), &peek, 1);
#else
            ssize_t n2 = read(STDIN_FILENO, &peek, 1);
#endif
            if (n2 == 1) {
                if (peek != '\n') {
                    /* Not \r\n — save the peeked byte for the next read. */
                    eidos_stdin_pushback_byte = (int)peek;
                }
            }
            /* n2 <= 0: EOF or error after \r — treat as line end. */
            break;
        }

        if (ch == '\n') {
            break;
        }

        if (length + 1 >= capacity) {
            size_t new_capacity = capacity * 2;
            char* new_buffer = (char*)realloc(buffer, new_capacity);
            if (new_buffer == NULL) {
                free(buffer);
                eidos_set_io_error_message("stdin read allocation failed");
                return eidos_string_new("", 0);
            }

            buffer = new_buffer;
            capacity = new_capacity;
        }

        buffer[length++] = (char)ch;
    }

    if (reached_eof && length == 0) {
        free(buffer);
        eidos_set_io_error_message("end of input");
        return eidos_string_new("", 0);
    }

    EidosString* result = eidos_string_new(buffer, length);
    free(buffer);
    eidos_set_io_success();
    return result;
}

/* ---- Terminal raw mode state ---- */
static int eidos_raw_mode_active = 0;
#if defined(_WIN32)
static DWORD eidos_original_console_mode = 0;
static int eidos_has_original_console_mode = 0;
#else
static struct termios eidos_original_termios;
#endif

void eidos_terminal_set_raw(void) {
    if (eidos_raw_mode_active) return;
#if defined(_WIN32)
    HANDLE hStdin = GetStdHandle(STD_INPUT_HANDLE);
    DWORD mode = 0;
    if (hStdin == INVALID_HANDLE_VALUE || !GetConsoleMode(hStdin, &mode)) {
        eidos_set_io_success();
        return;
    }

    eidos_original_console_mode = mode;
    eidos_has_original_console_mode = 1;
    if (!SetConsoleMode(hStdin, ENABLE_PROCESSED_INPUT)) {
        eidos_set_io_error_message("failed to set terminal raw mode");
        return;
    }
#else
    if (tcgetattr(STDIN_FILENO, &eidos_original_termios) != 0) return;
    struct termios raw = eidos_original_termios;
    raw.c_lflag &= ~(ECHO | ICANON | IEXTEN);
    raw.c_iflag &= ~(IXON | ICRNL);
    raw.c_cc[VMIN] = 0;
    raw.c_cc[VTIME] = 0;
    tcsetattr(STDIN_FILENO, TCSAFLUSH, &raw);
#endif
    eidos_raw_mode_active = 1;
    eidos_set_io_success();
}

void eidos_terminal_restore(void) {
    if (!eidos_raw_mode_active) return;
#if defined(_WIN32)
    HANDLE hStdin = GetStdHandle(STD_INPUT_HANDLE);
    if (hStdin != INVALID_HANDLE_VALUE && eidos_has_original_console_mode) {
        SetConsoleMode(hStdin, eidos_original_console_mode);
    }
    eidos_has_original_console_mode = 0;
#else
    tcsetattr(STDIN_FILENO, TCSAFLUSH, &eidos_original_termios);
#endif
    eidos_raw_mode_active = 0;
    eidos_set_io_success();
}

int64_t eidos_read_char(void) {
#if defined(_WIN32)
    if (!_isatty(_fileno(stdin))) {
        int ch = fgetc(stdin);
        if (ch == EOF) {
            if (ferror(stdin)) {
                clearerr(stdin);
            }
            return -1;
        }
        return (int64_t)(unsigned char)ch;
    }

    if (_kbhit()) {
        int ch = _getch();
        /* Handle Windows arrow keys (two-byte sequences: 0x00 or 0xE0 prefix) */
        if (ch == 0x00 || ch == 0xE0) {
            if (_kbhit()) { (void)_getch(); }
            return -1;
        }
        return (int64_t)ch;
    }
    return -1;
#else
    fd_set fds;
    struct timeval tv;
    FD_ZERO(&fds);
    FD_SET(STDIN_FILENO, &fds);
    tv.tv_sec = 0;
    tv.tv_usec = 0;
    if (select(STDIN_FILENO + 1, &fds, NULL, NULL, &tv) > 0) {
        unsigned char ch;
        if (read(STDIN_FILENO, &ch, 1) == 1) {
            return (int64_t)ch;
        }
    }
    return -1;
#endif
}

void eidos_sleep_ms(int64_t ms) {
    if (ms <= 0) return;
#if defined(_WIN32)
    Sleep((DWORD)ms);
#else
    struct timespec ts;
    ts.tv_sec = (time_t)(ms / 1000);
    ts.tv_nsec = (long)((ms % 1000) * 1000000L);
    nanosleep(&ts, NULL);
#endif
}

/* ============================================================
 * FFI Helper Functions
 * ============================================================ */

void* eidos_string_to_cstr(EidosString* str) {
    if (str == NULL) return NULL;
    return (void*)str->data;
}

EidosString* eidos_string_from_cstr_raw(const char* cstr) {
    return eidos_string_from_cstr(cstr);
}

void* eidos_ptr_null(void) {
    return NULL;
}

bool eidos_ptr_is_null(void* ptr) {
    return ptr == NULL;
}

bool eidos_ptr_equals(void* left, void* right) {
    return left == right;
}

static int64_t g_eidos_command_line_argc = 0;
static char** g_eidos_command_line_argv = NULL;

void eidos_command_line_init(int64_t argc, char** argv) {
    g_eidos_command_line_argc = argc;
    g_eidos_command_line_argv = argv;
}

int64_t eidos_command_line_argc(void) {
    return g_eidos_command_line_argc;
}

EidosString* eidos_command_line_arg_or(int64_t index, EidosString* fallback) {
    if (index >= 0 &&
        index < g_eidos_command_line_argc &&
        g_eidos_command_line_argv != NULL &&
        g_eidos_command_line_argv[index] != NULL) {
        eidos_set_io_success();
        return eidos_string_new(g_eidos_command_line_argv[index], strlen(g_eidos_command_line_argv[index]));
    }

    eidos_set_io_success();
    if (fallback == NULL) {
        return eidos_string_new("", 0);
    }

    return eidos_string_new(fallback->data, fallback->length);
}

bool eidos_io_last_success(void) {
    return g_eidos_last_io_success != 0;
}

EidosString* eidos_io_last_error(void) {
    return eidos_string_new(g_eidos_last_io_error, strlen(g_eidos_last_io_error));
}

static FILE* eidos_open_string_path(EidosString* path, const char* mode) {
    if (path == NULL || mode == NULL) {
        return NULL;
    }

    return fopen(path->data, mode);
}

bool eidos_file_exists(EidosString* path) {
    FILE* file = eidos_open_string_path(path, "rb");
    if (file == NULL) {
        eidos_set_io_errno_error("file open failed");
        return false;
    }

    fclose(file);
    eidos_set_io_success();
    return true;
}

EidosString* eidos_file_read_all_text(EidosString* path) {
    FILE* file = eidos_open_string_path(path, "rb");
    char* buffer = NULL;
    long size = 0;
    size_t read_count = 0;
    EidosString* result = NULL;

    if (file == NULL) {
        eidos_set_io_errno_error("file open failed");
        return eidos_string_new("", 0);
    }

    if (fseek(file, 0, SEEK_END) != 0) {
        fclose(file);
        eidos_set_io_errno_error("file seek failed");
        return eidos_string_new("", 0);
    }

    size = ftell(file);
    if (size < 0) {
        fclose(file);
        eidos_set_io_errno_error("file size query failed");
        return eidos_string_new("", 0);
    }

    if (fseek(file, 0, SEEK_SET) != 0) {
        fclose(file);
        eidos_set_io_errno_error("file seek failed");
        return eidos_string_new("", 0);
    }

    if (size == 0) {
        fclose(file);
        eidos_set_io_success();
        return eidos_string_new("", 0);
    }

    buffer = (char*)malloc((size_t)size);
    if (buffer == NULL) {
        fclose(file);
        eidos_set_io_error_message("file read allocation failed");
        return eidos_string_new("", 0);
    }

    read_count = fread(buffer, 1, (size_t)size, file);
    fclose(file);

    if (read_count != (size_t)size) {
        free(buffer);
        eidos_set_io_error_message("file read failed");
        return eidos_string_new("", 0);
    }

    result = eidos_string_new(buffer, read_count);
    free(buffer);
    eidos_set_io_success();
    return result;
}

bool eidos_file_write_all_text(EidosString* path, EidosString* content) {
    FILE* file = eidos_open_string_path(path, "wb");
    size_t expected = 0;
    size_t written = 0;

    if (file == NULL) {
        eidos_set_io_errno_error("file open failed");
        return false;
    }

    expected = content != NULL ? content->length : 0;
    if (expected > 0) {
        written = fwrite(content->data, 1, expected, file);
    }

    if (fclose(file) != 0) {
        eidos_set_io_errno_error("file close failed");
        return false;
    }

    if (written != expected) {
        eidos_set_io_error_message("file write failed");
        return false;
    }

    eidos_set_io_success();
    return true;
}

EidosString* eidos_http_request_text_with_headers(
    EidosString* method,
    EidosString* url,
    EidosString* content_type,
    EidosString* body,
    EidosString* headers) {
    return eidos_http_request_text_with_options(method, url, content_type, body, headers, 5, 15);
}

static EidosString* eidos_http_request_body_string_with_options(
    EidosString* method,
    EidosString* url,
    EidosString* content_type,
    EidosString* body,
    EidosString* headers,
    int64_t connect_timeout_seconds,
    int64_t total_timeout_seconds,
    int request_body_is_hex,
    int encode_hex) {
    EidosHttpRequestSpec request;
    EidosHttpTransportResult transport;
    size_t stdout_length = 0;
    size_t capture_body_offset = 0;
    size_t capture_body_length = 0;
    EidosString* result = NULL;
    char* metadata_text = NULL;
    unsigned char* request_body_bytes = NULL;
    size_t request_body_length = 0;

    eidos_http_transport_result_init(&transport);

    eidos_reset_http_metadata();
    eidos_set_http_effective_url_from_eidos(url);

    if (method == NULL || method->length == 0) {
        eidos_set_io_error_message("empty http method");
        return eidos_string_new("", 0);
    }

    if (url == NULL || url->length == 0) {
        eidos_set_io_error_message("empty url");
        return eidos_string_new("", 0);
    }

    if (request_body_is_hex && !eidos_decode_hex_bytes(body, &request_body_bytes, &request_body_length)) {
        eidos_set_io_error_message("invalid http binary request body");
        return eidos_string_new("", 0);
    }

    request.method = method;
    request.url = url;
    request.content_type = content_type;
    request.headers = headers;
    request.text_body = body;
    request.binary_body = request_body_bytes;
    request.binary_body_length = request_body_length;
    request.use_binary_body = request_body_is_hex;
    request.connect_timeout_seconds = connect_timeout_seconds;
    request.total_timeout_seconds = total_timeout_seconds;

    if (!eidos_http_execute_request(&request, &transport)) {
        free(request_body_bytes);
        return eidos_string_new("", 0);
    }

    stdout_length = transport.stdout_length;

    if (!eidos_split_http_capture(transport.stdout_buffer, stdout_length, &metadata_text, &capture_body_length)) {
        free(request_body_bytes);
        eidos_http_transport_result_free(&transport);
        eidos_set_io_error_message("failed to parse http response metadata");
        return eidos_string_new("", 0);
    }

    eidos_parse_http_metadata(metadata_text);

    if (!eidos_extract_http_headers_and_body(
            transport.stdout_buffer,
            capture_body_length,
            &capture_body_offset,
            &capture_body_length)) {
        free(request_body_bytes);
        eidos_http_transport_result_free(&transport);
        eidos_set_io_error_message("failed to parse http response headers");
        return eidos_string_new("", 0);
    }

    if (transport.exit_code != 0) {
        if (transport.stderr_buffer != NULL && transport.stderr_buffer[0] != '\0') {
            eidos_trim_trailing_newlines(transport.stderr_buffer);
            eidos_set_io_error_message(transport.stderr_buffer);
        }
        else {
            char message[128];
            snprintf(message, sizeof(message), "curl exited with status %d", transport.exit_code);
            eidos_set_io_error_message(message);
        }

        free(request_body_bytes);
        eidos_http_transport_result_free(&transport);
        return eidos_string_new("", 0);
    }

    if (g_eidos_last_http_status_code < 200 || g_eidos_last_http_status_code >= 400) {
        char message[128];
        snprintf(
            message,
            sizeof(message),
            "http request failed with status %lld",
            (long long)g_eidos_last_http_status_code);
        result = encode_hex
            ? eidos_string_new_hex_from_bytes(
                (const unsigned char*)(transport.stdout_buffer + capture_body_offset),
                capture_body_length)
            : eidos_string_new(transport.stdout_buffer + capture_body_offset, capture_body_length);
        eidos_set_io_error_message(message);
        free(request_body_bytes);
        eidos_http_transport_result_free(&transport);
        return result;
    }

    result = encode_hex
        ? eidos_string_new_hex_from_bytes(
            (const unsigned char*)(transport.stdout_buffer + capture_body_offset),
            capture_body_length)
        : eidos_string_new(transport.stdout_buffer + capture_body_offset, capture_body_length);
    free(request_body_bytes);
    eidos_http_transport_result_free(&transport);
    eidos_set_io_success();
    return result;
}

EidosString* eidos_http_request_text_with_options(
    EidosString* method,
    EidosString* url,
    EidosString* content_type,
    EidosString* body,
    EidosString* headers,
    int64_t connect_timeout_seconds,
    int64_t total_timeout_seconds) {
    return eidos_http_request_body_string_with_options(
        method,
        url,
        content_type,
        body,
        headers,
        connect_timeout_seconds,
        total_timeout_seconds,
        0,
        0);
}

EidosString* eidos_http_request_body_hex_with_options(
    EidosString* method,
    EidosString* url,
    EidosString* content_type,
    EidosString* body,
    EidosString* headers,
    int64_t connect_timeout_seconds,
    int64_t total_timeout_seconds) {
    return eidos_http_request_body_string_with_options(
        method,
        url,
        content_type,
        body,
        headers,
        connect_timeout_seconds,
        total_timeout_seconds,
        0,
        1);
}

EidosString* eidos_http_request_text_with_binary_body_options(
    EidosString* method,
    EidosString* url,
    EidosString* content_type,
    EidosString* body_hex,
    EidosString* headers,
    int64_t connect_timeout_seconds,
    int64_t total_timeout_seconds) {
    return eidos_http_request_body_string_with_options(
        method,
        url,
        content_type,
        body_hex,
        headers,
        connect_timeout_seconds,
        total_timeout_seconds,
        1,
        0);
}

EidosString* eidos_http_request_body_hex_with_binary_body_options(
    EidosString* method,
    EidosString* url,
    EidosString* content_type,
    EidosString* body_hex,
    EidosString* headers,
    int64_t connect_timeout_seconds,
    int64_t total_timeout_seconds) {
    return eidos_http_request_body_string_with_options(
        method,
        url,
        content_type,
        body_hex,
        headers,
        connect_timeout_seconds,
        total_timeout_seconds,
        1,
        1);
}

EidosString* eidos_http_request_text(
    EidosString* method,
    EidosString* url,
    EidosString* content_type,
    EidosString* body) {
    EidosString* empty_headers = eidos_string_new("", 0);
    EidosString* result = eidos_http_request_text_with_options(method, url, content_type, body, empty_headers, 5, 15);
    eidos_decref(empty_headers);
    return result;
}

EidosString* eidos_http_get_text(EidosString* url) {
    EidosString* method = eidos_string_from_cstr("GET");
    EidosString* empty = eidos_string_new("", 0);
    EidosString* result = eidos_http_request_text(method, url, empty, empty);
    eidos_decref(method);
    eidos_decref(empty);
    return result;
}

int64_t eidos_http_last_status_code(void) {
    return g_eidos_last_http_status_code;
}

EidosString* eidos_http_last_effective_url(void) {
    return eidos_string_new(
        g_eidos_last_http_effective_url,
        strlen(g_eidos_last_http_effective_url));
}

EidosString* eidos_http_last_content_type(void) {
    return eidos_string_new(
        g_eidos_last_http_content_type,
        strlen(g_eidos_last_http_content_type));
}

EidosString* eidos_http_last_headers(void) {
    return eidos_string_new(
        g_eidos_last_http_headers,
        strlen(g_eidos_last_http_headers));
}

int64_t eidos_http_backend_selected_kind(void) {
    return (int64_t)eidos_http_backend_effective_kind();
}

int64_t eidos_type_id(void* ptr) {
    if (ptr == NULL) {
        return 0;
    }

    EidosHeader* header = get_header(ptr);
    return (int64_t)header->type_id;
}

/* ============================================================
 * Array Implementation
 * ============================================================ */

EidosArray* eidos_array_new_with_policy(
    size_t capacity,
    size_t element_size,
    void (*retain_element)(void* element),
    void (*release_element)(void* element)) {
    size_t normalized_element_size = element_size == 0 ? 1 : element_size;
    size_t data_size = capacity * normalized_element_size;
    size_t total_size = sizeof(EidosArray) + data_size;

    EidosArray* arr = (EidosArray*)eidos_alloc(total_size, EIDOS_TYPE_ARRAY);
    if (arr == NULL) return NULL;

    eidos_ensure_array_destructor_registered();

    arr->length = 0;
    arr->capacity = capacity;
    arr->element_size = normalized_element_size;
    arr->retain_element = retain_element;
    arr->release_element = release_element;
    memset(arr->data, 0, data_size);

    return arr;
}

EidosArray* eidos_array_new(size_t capacity, size_t element_size) {
    return eidos_array_new_with_policy(capacity, element_size, NULL, NULL);
}

EidosClosure* eidos_closure_new(void* invoke_fn, void* release_fn, size_t payload_words) {
    size_t total_size = sizeof(EidosClosure) + (payload_words * sizeof(uintptr_t));
    EidosClosure* closure = (EidosClosure*)eidos_alloc(total_size, EIDOS_TYPE_CLOSURE);

    eidos_ensure_closure_destructor_registered();

    closure->invoke_fn = invoke_fn;
    closure->release_fn = release_fn;
    closure->payload_words = payload_words;

    if (payload_words > 0) {
        memset(closure->payload, 0, payload_words * sizeof(uintptr_t));
    }

    return closure;
}

size_t eidos_array_length(EidosArray* arr) {
    if (arr == NULL) {
        return 0;
    }
    return arr->length;
}

void* eidos_array_get(EidosArray* arr, size_t index) {
    if (arr == NULL || index >= arr->length) {
        eidos_panic("eidos_array_get: index out of bounds");
    }

    return arr->data + index * arr->element_size;
}

void eidos_array_set(EidosArray* arr, size_t index, void* value, size_t element_size) {
    (void)element_size;

    if (arr == NULL || index >= arr->capacity) {
        eidos_panic("eidos_array_set: index out of bounds");
    }

    if (value != NULL && arr->element_size > 0) {
        if (index < arr->length) {
            eidos_array_release_range(arr, index, 1);
        }
        memcpy(arr->data + index * arr->element_size, value, arr->element_size);
        eidos_array_retain_element(arr, arr->data + index * arr->element_size);
    } else if (index < arr->length && arr->element_size > 0) {
        eidos_array_release_range(arr, index, 1);
        memset(arr->data + index * arr->element_size, 0, arr->element_size);
    }

    if (index >= arr->length) {
        arr->length = index + 1;
    }
}

EidosArray* eidos_array_push(EidosArray* arr, void* value, size_t element_size) {
    size_t normalized_element_size = element_size == 0 ? 1 : element_size;

    if (arr == NULL) {
        arr = eidos_array_new_with_policy(8, normalized_element_size, NULL, NULL);
        if (arr == NULL) return NULL;
    }

    /* Grow if needed */
    if (arr->length >= arr->capacity) {
        size_t new_capacity = arr->capacity == 0 ? 8 : arr->capacity * 2;
        size_t new_data_size = new_capacity * arr->element_size;

        /* Allocate new array */
        EidosArray* new_arr = (EidosArray*)eidos_alloc(
            sizeof(EidosArray) + new_data_size, EIDOS_TYPE_ARRAY);
        if (new_arr == NULL) return NULL;

        /* Copy old data */
        size_t old_length = arr->length;
        new_arr->length = old_length;
        new_arr->capacity = new_capacity;
        new_arr->element_size = arr->element_size;
        new_arr->retain_element = arr->retain_element;
        new_arr->release_element = arr->release_element;
        if (old_length > 0) {
            memcpy(new_arr->data, arr->data, old_length * arr->element_size);
            memset(arr->data, 0, old_length * arr->element_size);
            arr->length = 0;
        }
        if (new_data_size > old_length * arr->element_size) {
            memset(
                new_arr->data + old_length * arr->element_size,
                0,
                new_data_size - old_length * arr->element_size);
        }

        /* Drop old array */
        eidos_decref(arr);
        arr = new_arr;
    }

    /* Add element */
    if (value != NULL && arr->element_size > 0) {
        memcpy(arr->data + arr->length * arr->element_size, value, arr->element_size);
        eidos_array_retain_element(arr, arr->data + arr->length * arr->element_size);
    }
    arr->length++;

    return arr;
}

EidosArray* eidos_array_extend(EidosArray* dst, EidosArray* src, size_t element_size) {
    if (src == NULL || src->length == 0) {
        return dst;
    }

    size_t normalized_element_size = element_size == 0 ? 1 : element_size;

    for (size_t i = 0; i < src->length; i++) {
        void* src_elem = src->data + i * src->element_size;
        dst = eidos_array_push(dst, src_elem, normalized_element_size);
    }

    return dst;
}

void eidos_array_pop(EidosArray* arr) {
    if (arr == NULL || arr->length == 0) {
        return;
    }

    arr->length--;
    if (arr->element_size > 0) {
        eidos_array_release_range(arr, arr->length, 1);
        memset(arr->data + arr->length * arr->element_size, 0, arr->element_size);
    }
}

void eidos_array_swap(EidosArray* arr, size_t left, size_t right) {
    if (arr == NULL || left >= arr->length || right >= arr->length) {
        eidos_panic("eidos_array_swap: index out of bounds");
    }

    if (left == right || arr->element_size == 0) {
        return;
    }

    unsigned char* left_ptr = arr->data + left * arr->element_size;
    unsigned char* right_ptr = arr->data + right * arr->element_size;
    for (size_t i = 0; i < arr->element_size; i++) {
        unsigned char tmp = left_ptr[i];
        left_ptr[i] = right_ptr[i];
        right_ptr[i] = tmp;
    }
}

/* ============================================================
 * Error Handling Implementation
 * ============================================================ */

void eidos_panic(const char* message) {
    fprintf(stderr, "PANIC: %s\n", message);
    abort();
}

void eidos_assert(int condition, const char* message) {
    if (!condition) {
        fprintf(stderr, "Assertion failed: %s\n", message);
        abort();
    }
}

/* ============================================================
 * Time Operations Implementation
 * ============================================================ */

int64_t eidos_time_now(void) {
    return (int64_t)time(NULL);
}

int64_t eidos_time_now_ms(void) {
#if defined(_WIN32)
    FILETIME ft;
    ULARGE_INTEGER uli;
    GetSystemTimeAsFileTime(&ft);
    uli.LowPart = ft.dwLowDateTime;
    uli.HighPart = ft.dwHighDateTime;
    return (int64_t)((uli.QuadPart - 116444736000000000ULL) / 10000);
#else
    struct timespec ts;
    clock_gettime(CLOCK_REALTIME, &ts);
    return (int64_t)(ts.tv_sec) * 1000 + ts.tv_nsec / 1000000;
#endif
}

int64_t eidos_time_format(int64_t timestamp, char* buf, int64_t buf_len, const char* format_str) {
    time_t t = (time_t)timestamp;
    struct tm* tm_info = localtime(&t);
    if (tm_info == NULL || buf == NULL) return 0;
    return (int64_t)strftime(buf, (size_t)buf_len, format_str, tm_info);
}

int64_t eidos_time_year(int64_t timestamp) {
    time_t t = (time_t)timestamp;
    struct tm* tm_info = localtime(&t);
    return tm_info ? (int64_t)(tm_info->tm_year + 1900) : 0;
}

int64_t eidos_time_month(int64_t timestamp) {
    time_t t = (time_t)timestamp;
    struct tm* tm_info = localtime(&t);
    return tm_info ? (int64_t)(tm_info->tm_mon + 1) : 0;
}

int64_t eidos_time_day(int64_t timestamp) {
    time_t t = (time_t)timestamp;
    struct tm* tm_info = localtime(&t);
    return tm_info ? (int64_t)tm_info->tm_mday : 0;
}

int64_t eidos_time_hour(int64_t timestamp) {
    time_t t = (time_t)timestamp;
    struct tm* tm_info = localtime(&t);
    return tm_info ? (int64_t)tm_info->tm_hour : 0;
}

int64_t eidos_time_minute(int64_t timestamp) {
    time_t t = (time_t)timestamp;
    struct tm* tm_info = localtime(&t);
    return tm_info ? (int64_t)tm_info->tm_min : 0;
}

int64_t eidos_time_second(int64_t timestamp) {
    time_t t = (time_t)timestamp;
    struct tm* tm_info = localtime(&t);
    return tm_info ? (int64_t)tm_info->tm_sec : 0;
}

/* ============================================================
 * Regex Operations Implementation
 * ============================================================ */

#if !defined(_WIN32)
#include <regex.h>

void* eidos_regex_compile(const char* pattern, int64_t flags) {
    if (pattern == NULL) return NULL;
    regex_t* compiled = (regex_t*)malloc(sizeof(regex_t));
    if (compiled == NULL) return NULL;
    int ret = regcomp(compiled, pattern, (int)flags);
    if (ret != 0) {
        free(compiled);
        return NULL;
    }
    return (void*)compiled;
}

void eidos_regex_free(void* regex) {
    if (regex != NULL) {
        regfree((regex_t*)regex);
        free(regex);
    }
}

int64_t eidos_regex_is_match(void* regex, const char* text) {
    if (regex == NULL || text == NULL) return -1;
    return regexec((regex_t*)regex, text, 0, NULL, 0) == 0 ? 1 : 0;
}

int64_t eidos_regex_find(void* regex, const char* text, char* match_buf, int64_t buf_len) {
    if (regex == NULL || text == NULL || match_buf == NULL) return 0;
    regmatch_t match;
    if (regexec((regex_t*)regex, text, 1, &match, 0) != 0) return 0;
    int64_t len = (int64_t)(match.rm_eo - match.rm_so);
    if (len >= buf_len) len = buf_len - 1;
    if (len < 0) return 0;
    memcpy(match_buf, text + match.rm_so, (size_t)len);
    match_buf[len] = '\0';
    return len;
}

EidosString* eidos_regex_find_string(void* regex, const char* text) {
    if (regex == NULL || text == NULL) return NULL;
    regmatch_t match;
    if (regexec((regex_t*)regex, text, 1, &match, 0) != 0) return NULL;
    int64_t len = (int64_t)(match.rm_eo - match.rm_so);
    if (len < 0) return NULL;
    return eidos_string_new(text + match.rm_so, (size_t)len);
}

#else
/* Windows: simple backtracking regex engine (no external dependencies) */

/* ---- internal regex representation ---- */

typedef enum {
    RE_LITERAL,     /* match a specific character */
    RE_DOT,         /* match any char (except \n) */
    RE_CLASS,       /* [abc] or [a-z] */
    RE_CLASS_NEG,   /* [^abc] */
    RE_ANCHOR_START,/* ^ */
    RE_ANCHOR_END,  /* $ */
    RE_GROUP_START, /* ( */
    RE_GROUP_END,   /* ) */
    RE_ALT,         /* | */
    RE_SHORT_D,     /* \d */
    RE_SHORT_W,     /* \w */
    RE_SHORT_S,     /* \s */
    RE_SHORT_D_NEG, /* \D */
    RE_SHORT_W_NEG, /* \W */
    RE_SHORT_S_NEG  /* \S */
} ReNodeType;

typedef enum {
    QUANT_NONE,     /* exactly one */
    QUANT_STAR,     /* * zero or more */
    QUANT_PLUS,     /* + one or more */
    QUANT_QUESTION  /* ? zero or one */
} ReQuantType;

typedef struct ReNode {
    ReNodeType  type;
    ReQuantType quant;
    char        ch;                /* for RE_LITERAL */
    unsigned char class_bitmap[16]; /* 128-bit bitmap for character classes */
} ReNode;

typedef struct {
    ReNode* nodes;
    int     node_count;
    int     node_cap;
    int     flags;
} SimpleRegex;

/* ---- bitmap helpers for character classes ---- */

static void re_bitmap_set(unsigned char* bm, unsigned char c) {
    bm[c >> 3] |= (unsigned char)(1 << (c & 7));
}

static int re_bitmap_test(const unsigned char* bm, unsigned char c) {
    return (bm[c >> 3] >> (c & 7)) & 1;
}

static void re_bitmap_set_range(unsigned char* bm, unsigned char lo, unsigned char hi) {
    unsigned char i;
    for (i = lo; i <= hi; ++i) re_bitmap_set(bm, i);
}

/* ---- compiler: pattern string -> node array ---- */

static int re_add_node(SimpleRegex* re, ReNodeType type, char ch) {
    if (re->node_count >= re->node_cap) {
        int new_cap = re->node_cap == 0 ? 16 : re->node_cap * 2;
        ReNode* new_nodes = (ReNode*)EIDOS_REALLOC(re->nodes, (size_t)new_cap * sizeof(ReNode));
        if (new_nodes == NULL) return -1;
        re->nodes   = new_nodes;
        re->node_cap = new_cap;
    }
    {
        ReNode* n = &re->nodes[re->node_count];
        n->type  = type;
        n->quant = QUANT_NONE;
        n->ch    = ch;
        memset(n->class_bitmap, 0, sizeof(n->class_bitmap));
    }
    return re->node_count++;
}

/* Parse a character class [...] starting after the opening '['.
   Sets the bitmap and negated flag. Returns pointer past the closing ']'. */
static const char* re_parse_class(const char* p, unsigned char* bm, int* negated, int* closed) {
    *negated = 0;
    *closed = 0;
    if (*p == '^') { *negated = 1; p++; }
    while (*p && *p != ']') {
        if (p[1] == '-' && p[2] && p[2] != ']') {
            re_bitmap_set_range(bm, (unsigned char)p[0], (unsigned char)p[2]);
            p += 3;
        } else if (*p == '\\' && p[1]) {
            switch (p[1]) {
                case 'd': re_bitmap_set_range(bm, '0', '9'); break;
                case 'w': re_bitmap_set_range(bm, 'a', 'z');
                          re_bitmap_set_range(bm, 'A', 'Z');
                          re_bitmap_set_range(bm, '0', '9');
                          re_bitmap_set(bm, '_'); break;
                case 's': re_bitmap_set(bm, ' ');  re_bitmap_set(bm, '\t');
                          re_bitmap_set(bm, '\n'); re_bitmap_set(bm, '\r');
                          re_bitmap_set(bm, '\f'); re_bitmap_set(bm, '\v'); break;
                default:  re_bitmap_set(bm, (unsigned char)p[1]); break;
            }
            p += 2;
        } else {
            re_bitmap_set(bm, (unsigned char)*p);
            p++;
        }
    }
    if (*p == ']') {
        *closed = 1;
        p++;
    }
    return p;
}

/* Compile pattern string into a SimpleRegex. Returns NULL on failure. */
static SimpleRegex* re_compile(const char* pattern, int flags) {
    SimpleRegex* re;
    const char* p;

    if (pattern == NULL) return NULL;
    re = (SimpleRegex*)EIDOS_MALLOC(sizeof(SimpleRegex));
    if (re == NULL) return NULL;
    memset(re, 0, sizeof(SimpleRegex));
    re->flags = flags;

    for (p = pattern; *p; ) {
        if (*p == '\\') {
            p++;
            if (*p == '\0') { EIDOS_FREE(re->nodes); EIDOS_FREE(re); return NULL; }
            switch (*p) {
                case 'd': re_add_node(re, RE_SHORT_D, 0); break;
                case 'D': re_add_node(re, RE_SHORT_D_NEG, 0); break;
                case 'w': re_add_node(re, RE_SHORT_W, 0); break;
                case 'W': re_add_node(re, RE_SHORT_W_NEG, 0); break;
                case 's': re_add_node(re, RE_SHORT_S, 0); break;
                case 'S': re_add_node(re, RE_SHORT_S_NEG, 0); break;
                default:  re_add_node(re, RE_LITERAL, *p); break;
            }
            p++;
        } else if (*p == '.') {
            re_add_node(re, RE_DOT, 0); p++;
        } else if (*p == '^') {
            re_add_node(re, RE_ANCHOR_START, 0); p++;
        } else if (*p == '$') {
            re_add_node(re, RE_ANCHOR_END, 0); p++;
        } else if (*p == '(') {
            re_add_node(re, RE_GROUP_START, 0); p++;
        } else if (*p == ')') {
            re_add_node(re, RE_GROUP_END, 0); p++;
        } else if (*p == '|') {
            re_add_node(re, RE_ALT, 0); p++;
        } else if (*p == '[') {
            int idx, negated = 0, closed = 0;
            p++;  /* skip '[' */
            idx = re_add_node(re, RE_CLASS, 0);
            if (idx < 0) { EIDOS_FREE(re->nodes); EIDOS_FREE(re); return NULL; }
            p = re_parse_class(p, re->nodes[idx].class_bitmap, &negated, &closed);
            if (!closed) { EIDOS_FREE(re->nodes); EIDOS_FREE(re); return NULL; }
            if (negated) re->nodes[idx].type = RE_CLASS_NEG;
        } else if (*p == '*' || *p == '+' || *p == '?') {
            /* stray quantifier without preceding element: treat as literal */
            re_add_node(re, RE_LITERAL, *p); p++;
        } else {
            re_add_node(re, RE_LITERAL, *p); p++;
        }

        /* attach quantifier to the last node */
        if (re->node_count > 0 && (*p == '*' || *p == '+' || *p == '?')) {
            ReNode* last = &re->nodes[re->node_count - 1];
            if (last->type != RE_ANCHOR_START && last->type != RE_ANCHOR_END &&
                last->type != RE_ALT) {
                if      (*p == '*') last->quant = QUANT_STAR;
                else if (*p == '+') last->quant = QUANT_PLUS;
                else                last->quant = QUANT_QUESTION;
                p++;
            }
        }
    }
    return re;
}

/* ---- matching engine ---- */

static int re_char_in_class(unsigned char c, const ReNode* node) {
    int in = re_bitmap_test(node->class_bitmap, c);
    return (node->type == RE_CLASS_NEG) ? !in : in;
}

static int re_match_shortcut(unsigned char c, ReNodeType t) {
    switch (t) {
        case RE_SHORT_D:     return (c >= '0' && c <= '9');
        case RE_SHORT_D_NEG: return !(c >= '0' && c <= '9');
        case RE_SHORT_W:     return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                                    (c >= '0' && c <= '9') || (c == '_');
        case RE_SHORT_W_NEG: return !((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                                    (c >= '0' && c <= '9') || (c == '_'));
        case RE_SHORT_S:     return c == ' ' || c == '\t' || c == '\n' ||
                                    c == '\r' || c == '\f' || c == '\v';
        case RE_SHORT_S_NEG: return !(c == ' ' || c == '\t' || c == '\n' ||
                                    c == '\r' || c == '\f' || c == '\v');
        default: return 0;
    }
}

/* Try to match a single (non-quantified) node at text position.
   Returns number of characters consumed, or -1 on mismatch. */
static int re_match_node(const ReNode* node, const char* text) {
    unsigned char c;
    if (*text == '\0' && node->type != RE_ANCHOR_END) return -1;
    c = (unsigned char)*text;

    switch (node->type) {
        case RE_LITERAL:
            return (c == (unsigned char)node->ch) ? 1 : -1;
        case RE_DOT:
            return (*text != '\n') ? 1 : -1;
        case RE_CLASS:
        case RE_CLASS_NEG:
            return re_char_in_class(c, node) ? 1 : -1;
        case RE_SHORT_D: case RE_SHORT_D_NEG:
        case RE_SHORT_W: case RE_SHORT_W_NEG:
        case RE_SHORT_S: case RE_SHORT_S_NEG:
            return re_match_shortcut(c, node->type) ? 1 : -1;
        default:
            return -1;
    }
}

/* Forward declaration for recursive backtracker. */
static const char* re_try(const ReNode* nodes, int count, int ni, const char* text);

/* Handle quantified nodes with greedy matching + backtracking. */
static const char* re_try_quant(const ReNode* node,
                                 const ReNode* all, int all_count, int next_ni,
                                 const char* text) {
    int min_rep, max_rep;
    int matched_count, i;
    const char* rest;
    const char* saved[1024];  /* positions for backtracking */

    switch (node->quant) {
        case QUANT_STAR:     min_rep = 0; max_rep = 0x7FFFFFFF; break;
        case QUANT_PLUS:     min_rep = 1; max_rep = 0x7FFFFFFF; break;
        case QUANT_QUESTION: min_rep = 0; max_rep = 1;          break;
        default:             min_rep = 1; max_rep = 1;          break;
    }

    /* greedy: consume as many as possible */
    matched_count = 0;
    rest = text;
    while (matched_count < 1024 && matched_count < max_rep) {
        int consumed = re_match_node(node, rest);
        if (consumed < 0) break;
        saved[matched_count] = rest;
        rest += consumed;
        matched_count++;
    }

    /* backtrack from greedy max down to min */
    for (i = matched_count; i >= min_rep; i--) {
        const char* probe = re_try(all, all_count, next_ni, rest);
        if (probe != NULL) return probe;
        if (i > 0) rest = saved[i - 1];
    }
    return NULL;
}

/*
 * Recursive backtracking matcher.
 * Returns pointer past the matched portion, or NULL on failure.
 *
 * Handles groups via depth tracking: when we encounter GROUP_START we find
 * the matching GROUP_END (and any ALT at the same depth) and try branches.
 * ALT nodes split into left-branch and right-branch attempts.
 */
static const char* re_try(const ReNode* nodes, int count, int ni, const char* text) {
    while (ni < count) {
        const ReNode* node = &nodes[ni];

        switch (node->type) {

        case RE_ANCHOR_START:
            /* ^ is only meaningful at the start; skip in the middle */
            ni++;
            continue;

        case RE_ANCHOR_END:
            if (*text == '\0') { ni++; continue; }
            return NULL;

        case RE_ALT: {
            /* Try the rest of the current branch first, then the alternate branch.
               The alternate branch runs from the next node up to GROUP_END or end. */
            const char* result = re_try(nodes, count, ni + 1, text);
            if (result != NULL) return result;
            /* skip ahead to find GROUP_END at depth 0 (or fall off the end) */
            {
                int depth = 0, j = ni + 1;
                while (j < count) {
                    if (nodes[j].type == RE_GROUP_START) depth++;
                    if (nodes[j].type == RE_GROUP_END) {
                        if (depth == 0) break;
                        depth--;
                    }
                    j++;
                }
                /* If we stopped at a GROUP_END at depth 0, succeed past it.
                   The parent group's GROUP_END will continue matching. */
                if (j < count && nodes[j].type == RE_GROUP_END) {
                    return re_try(nodes, count, j + 1, text);
                }
            }
            return NULL;
        }

        case RE_GROUP_START: {
            /* Find matching GROUP_END and the first ALT at depth 0 inside. */
            int depth = 0, j;
            int alt_pos = -1, end_pos = -1;
            for (j = ni + 1; j < count; j++) {
                if (nodes[j].type == RE_GROUP_START) depth++;
                if (nodes[j].type == RE_GROUP_END) {
                    if (depth == 0) { end_pos = j; break; }
                    depth--;
                }
                if (nodes[j].type == RE_ALT && depth == 0 && alt_pos < 0) {
                    alt_pos = j;
                }
            }
            if (end_pos < 0) {
                /* no closing paren: treat ( as literal */
                int consumed = re_match_node(node, text);
                if (consumed < 0) return NULL;
                text += consumed; ni++; continue;
            }

            /* Try matching inside the group, then continue after end_pos.
               We jump into the node array just after GROUP_START. */
            {
                const char* inner = re_try(nodes, count, ni + 1, text);
                if (inner != NULL) {
                    /* inner matched; now try to continue past the GROUP_END */
                    const char* after = re_try(nodes, count, end_pos + 1, inner);
                    if (after != NULL) return after;
                }
            }
            return NULL;
        }

        case RE_GROUP_END:
            /* Reached end of group; caller (GROUP_START handler) will continue. */
            return text;

        default: {
            /* Regular node (literal, dot, class, shortcut) possibly quantified. */
            if (node->quant != QUANT_NONE) {
                const char* result = re_try_quant(node, nodes, count, ni + 1, text);
                if (result == NULL) return NULL;
                text = result;
                ni++;
                continue;
            } else {
                int consumed = re_match_node(node, text);
                if (consumed < 0) return NULL;
                text += consumed;
                ni++;
                continue;
            }
        }
        } /* switch */
    }
    return text;
}

/* ---- public API ---- */

void* eidos_regex_compile(const char* pattern, int64_t flags) {
    return (void*)re_compile(pattern, (int)flags);
}

void eidos_regex_free(void* regex) {
    if (regex != NULL) {
        SimpleRegex* re = (SimpleRegex*)regex;
        if (re->nodes != NULL) EIDOS_FREE(re->nodes);
        EIDOS_FREE(re);
    }
}

int64_t eidos_regex_is_match(void* regex, const char* text) {
    SimpleRegex* re;
    if (regex == NULL || text == NULL) return -1;
    re = (SimpleRegex*)regex;

    if (re->node_count > 0 && re->nodes[0].type == RE_ANCHOR_START) {
        /* anchored pattern: must match at position 0 */
        const char* result = re_try(re->nodes, re->node_count, 0, text);
        return (result != NULL) ? 1 : 0;
    }

    /* unanchored: try at every starting position */
    {
        const char* t = text;
        for (;;) {
            const char* result = re_try(re->nodes, re->node_count, 0, t);
            if (result != NULL) return 1;
            if (*t == '\0') break;
            t++;
        }
    }
    return 0;
}

int64_t eidos_regex_find(void* regex, const char* text, char* match_buf, int64_t buf_len) {
    SimpleRegex* re;
    const char* t;
    if (regex == NULL || text == NULL || match_buf == NULL) return 0;
    re = (SimpleRegex*)regex;

    for (t = text; ; t++) {
        const char* result = re_try(re->nodes, re->node_count, 0, t);
        if (result != NULL) {
            int64_t len = (int64_t)(result - t);
            if (len >= buf_len) len = buf_len - 1;
            if (len < 0) len = 0;
            memcpy(match_buf, t, (size_t)len);
            match_buf[len] = '\0';
            return len;
        }
        if (*t == '\0') break;
    }
    return 0;
}

EidosString* eidos_regex_find_string(void* regex, const char* text) {
    SimpleRegex* re;
    const char* t;
    if (regex == NULL || text == NULL) return NULL;
    re = (SimpleRegex*)regex;

    for (t = text; ; t++) {
        const char* result = re_try(re->nodes, re->node_count, 0, t);
        if (result != NULL) {
            int64_t len = (int64_t)(result - t);
            if (len < 0) len = 0;
            return eidos_string_new(t, (size_t)len);
        }
        if (*t == '\0') break;
    }
    return NULL;
}

#endif
