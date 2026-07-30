using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void NativeRuntimeArray_CowSelfExtendAndScalarPolicy_BalanceOwnership()
    {
        var clang = ResolveToolPath("clang");
        if (clang == null)
        {
            return;
        }

        const string harness = """
#include "eidos_runtime.h"
#include <stdint.h>

static int retain_calls = 0;
static int release_calls = 0;

typedef struct RecordFixture {
    void* managed;
    int64_t value;
} RecordFixture;

static void retain_owned_pointer(void* element) {
    retain_calls++;
    eidos_incref(*(void**)element);
}

static void release_owned_pointer(void* element) {
    release_calls++;
    eidos_decref(*(void**)element);
}

static int balanced(void) {
    return eidos_memory_alloc_count() == eidos_memory_free_count();
}

static int verify_cow_push(void) {
    eidos_memory_counters_reset();
    retain_calls = 0;
    release_calls = 0;

    EidosArray* original = eidos_array_new_with_policy(
        1, sizeof(void*), retain_owned_pointer, release_owned_pointer);
    void* first = eidos_alloc(sizeof(int64_t), 9001);
    original = eidos_array_push(original, &first, sizeof(void*));
    eidos_incref(original);

    void* second = eidos_alloc(sizeof(int64_t), 9002);
    EidosArray* clone = eidos_array_push(original, &second, sizeof(void*));
    if (clone == original || original->length != 1 || clone->length != 2) return 11;
    if (*(void**)eidos_array_get(original, 0) != first) return 12;
    if (*(void**)eidos_array_get(clone, 0) != first) return 13;
    if (*(void**)eidos_array_get(clone, 1) != second) return 14;
    if (retain_calls != 1) return 15;

    eidos_decref(original);
    eidos_decref(clone);
    if (release_calls != 3) return 16;
    return balanced() ? 0 : 17;
}

static int verify_self_extend(void) {
    eidos_memory_counters_reset();
    retain_calls = 0;
    release_calls = 0;

    EidosArray* array = eidos_array_new_with_policy(
        1, sizeof(void*), retain_owned_pointer, release_owned_pointer);
    void* value = eidos_alloc(sizeof(int64_t), 9003);
    array = eidos_array_push(array, &value, sizeof(void*));
    eidos_incref(array);

    EidosArray* combined = eidos_array_extend(array, array, sizeof(void*));
    if (combined == array || combined->length != 2) return 21;
    if (*(void**)eidos_array_get(combined, 0) != value ||
        *(void**)eidos_array_get(combined, 1) != value) return 22;
    if (retain_calls != 2 || release_calls != 1) return 23;

    eidos_decref(combined);
    if (release_calls != 3) return 24;
    return balanced() ? 0 : 25;
}

static int verify_scalar_policy(void) {
    eidos_memory_counters_reset();
    EidosArray* original = eidos_array_new(1, sizeof(int64_t));
    if (original->retain_element != NULL || original->release_element != NULL) return 31;
    int64_t first = 41;
    original = eidos_array_push(original, &first, sizeof(first));
    eidos_incref(original);
    int64_t second = 43;
    EidosArray* clone = eidos_array_push(original, &second, sizeof(second));
    if (clone->retain_element != NULL || clone->release_element != NULL) return 32;
    if (*(int64_t*)eidos_array_get(original, 0) != first) return 33;
    if (*(int64_t*)eidos_array_get(clone, 1) != second) return 34;
    eidos_decref(original);
    eidos_decref(clone);
    return balanced() ? 0 : 35;
}

static int verify_consuming_transforms(void) {
    eidos_memory_counters_reset();
    retain_calls = 0;
    release_calls = 0;

    EidosArray* array = eidos_array_new_with_policy(
        4, sizeof(void*), retain_owned_pointer, release_owned_pointer);
    void* first = eidos_alloc(sizeof(int64_t), 9010);
    void* second = eidos_alloc(sizeof(int64_t), 9011);
    void* third = eidos_alloc(sizeof(int64_t), 9012);
    void* front = eidos_alloc(sizeof(int64_t), 9013);
    array = eidos_array_push(array, &first, sizeof(void*));
    array = eidos_array_push(array, &second, sizeof(void*));
    array = eidos_array_push(array, &third, sizeof(void*));

    EidosArray* storage = array;
    array = eidos_array_take(array, 2);
    if (array != storage || array->length != 2 || release_calls != 1) return 41;
    array = eidos_array_prepend(array, &front, sizeof(void*));
    if (array != storage || array->length != 3) return 42;
    array = eidos_array_slice(array, 1, 1);
    if (array != storage || array->length != 1 ||
        *(void**)eidos_array_get(array, 0) != first || release_calls != 3) return 43;

    eidos_decref(array);
    if (release_calls != 4) return 44;
    return balanced() ? 0 : 45;
}

static int verify_right_extend_reuse(void) {
    eidos_memory_counters_reset();
    EidosArray* left = eidos_array_new(1, sizeof(int64_t));
    EidosArray* right = eidos_array_new(4, sizeof(int64_t));
    int64_t one = 1;
    int64_t two = 2;
    int64_t three = 3;
    left = eidos_array_push(left, &one, sizeof(one));
    right = eidos_array_push(right, &two, sizeof(two));
    right = eidos_array_push(right, &three, sizeof(three));

    EidosArray* storage = right;
    EidosArray* combined = eidos_array_extend(left, right, sizeof(int64_t));
    if (combined != storage || combined->length != 3) return 51;
    if (*(int64_t*)eidos_array_get(combined, 0) != 1 ||
        *(int64_t*)eidos_array_get(combined, 1) != 2 ||
        *(int64_t*)eidos_array_get(combined, 2) != 3) return 52;
    eidos_decref(combined);
    return balanced() ? 0 : 53;
}

static void retain_record_fields(void* pointer) {
    RecordFixture* record = (RecordFixture*)pointer;
    retain_calls++;
    eidos_incref(record->managed);
}

static void release_record_fields(void* pointer) {
    RecordFixture* record = (RecordFixture*)pointer;
    release_calls++;
    eidos_decref(record->managed);
}

static int verify_record_update_cow(void) {
    const uint32_t record_type = 9020;
    eidos_register_destructor(record_type, release_record_fields);
    eidos_register_retainer(record_type, retain_record_fields);

    eidos_memory_counters_reset();
    retain_calls = 0;
    release_calls = 0;
    void* unique_child = eidos_alloc(sizeof(int64_t), 9021);
    RecordFixture* unique = (RecordFixture*)eidos_alloc(sizeof(RecordFixture), record_type);
    unique->managed = unique_child;
    unique->value = 1;
    RecordFixture* updated_unique = (RecordFixture*)eidos_record_update_cow(
        unique, sizeof(RecordFixture), record_type);
    if (updated_unique != unique || retain_calls != 0) return 61;
    updated_unique->value = 2;
    eidos_decref(updated_unique);
    if (release_calls != 1 || !balanced()) return 62;

    eidos_memory_counters_reset();
    retain_calls = 0;
    release_calls = 0;
    void* shared_child = eidos_alloc(sizeof(int64_t), 9022);
    void* replacement = eidos_alloc(sizeof(int64_t), 9023);
    RecordFixture* original = (RecordFixture*)eidos_alloc(sizeof(RecordFixture), record_type);
    original->managed = shared_child;
    original->value = 3;
    eidos_incref(original);

    RecordFixture* clone = (RecordFixture*)eidos_record_update_cow(
        original, sizeof(RecordFixture), record_type);
    if (clone == original || clone->managed != shared_child || clone->value != 3) return 63;
    if (retain_calls != 1) return 64;
    eidos_decref(clone->managed);
    clone->managed = replacement;
    clone->value = 4;
    if (original->managed != shared_child || original->value != 3) return 65;

    eidos_decref(original);
    eidos_decref(clone);
    if (release_calls != 2) return 66;
    return balanced() ? 0 : 67;
}

int main(void) {
    int result = verify_cow_push();
    if (result != 0) return result;
    result = verify_self_extend();
    if (result != 0) return result;
    result = verify_scalar_policy();
    if (result != 0) return result;
    result = verify_consuming_transforms();
    if (result != 0) return result;
    result = verify_right_extend_reuse();
    if (result != 0) return result;
    return verify_record_update_cow();
}
""";

        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            $"eidosc_runtime_array_ownership_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var harnessPath = Path.Combine(tempDirectory, "runtime_array_ownership.c");
            var executablePath = Path.Combine(
                tempDirectory,
                OperatingSystem.IsWindows() ? "runtime_array_ownership.exe" : "runtime_array_ownership");
            File.WriteAllText(harnessPath, harness);
            var runtimeSource = ResolveRuntimeSourcePath();
            var runtimeDirectory = Path.GetDirectoryName(runtimeSource)!;
            var compile = ExecuteProcess(
                clang,
                $"-std=c11 -DEIDOS_ENABLE_MEMORY_COUNTERS -I\"{runtimeDirectory}\" \"{harnessPath}\" \"{runtimeSource}\" -o \"{executablePath}\"");
            Assert.Equal(0, compile.ExitCode);

            var execution = ExecuteProcess(executablePath, workingDirectory: tempDirectory);
            Assert.Equal(0, execution.ExitCode);
        }
        finally
        {
            DeleteDirectoryQuietly(tempDirectory);
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void NativeRuntimeArray_ManagedPushSetPopAndExtend_BalanceAllocations()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string source = """
import std.RuntimeArray
import std.Seq

@[extern(c, name: "eidos_memory_counters_reset")]
reset_memory_counters :: Unit -> Unit need ffi

@[extern(c, name: "eidos_memory_alloc_count")]
memory_alloc_count :: Unit -> Int need ffi

@[extern(c, name: "eidos_memory_free_count")]
memory_free_count :: Unit -> Int need ffi

Box :: type {
    value :: Int
}

balance :: Unit -> Int need ffi {
    _ => memory_alloc_count() - memory_free_count()
}

push_boxes :: Int -> Int {
    seed => {
        values := RuntimeArray.push(
            RuntimeArray.push(RuntimeArray.empty[Box](()))(Box { value: seed })
        )(Box { value: seed + 1 })
        values[0].value + values[1].value
    }
}

overwrite_boxes :: Int -> Int {
    seed => {
        mut values := RuntimeArray.with_capacity[Box](2)
        RuntimeArray.set(mref values, 0, Box { value: seed })
        RuntimeArray.set(mref values, 0, Box { value: seed + 1 })
        RuntimeArray.set(mref values, 1, Box { value: seed + 2 })
        values[0].value + values[1].value
    }
}

pop_box :: Int -> Int {
    seed => {
        mut values := RuntimeArray.singleton(Box { value: seed })
        RuntimeArray.pop_last(mref values)
        Seq.len(ref values)
    }
}

extend_boxes :: Int -> Int {
    seed => {
        left := RuntimeArray.singleton((Box { value: seed }, Box { value: seed + 1 }))
        right := RuntimeArray.singleton((Box { value: seed + 2 }, Box { value: seed + 3 }))
        empty := RuntimeArray.empty[(Box, Box)](())
        values := RuntimeArray.extend(RuntimeArray.extend(left)(empty))(right)
        Seq.len(ref values)
    }
}

main :: Unit -> Int need ffi {
    _ => {
        reset_memory_counters()
        push_boxes(10)
        push_delta := balance()

        reset_memory_counters()
        overwrite_boxes(20)
        overwrite_delta := balance()

        reset_memory_counters()
        pop_box(30)
        pop_delta := balance()

        reset_memory_counters()
        extend_boxes(40)
        extend_delta := balance()

        if push_delta != 0 then { push_delta + 10 }
        else if overwrite_delta != 0 then { overwrite_delta + 20 }
        else if pop_delta != 0 then { pop_delta + 30 }
        else if extend_delta != 0 then { extend_delta + 40 }
        else { 0 }
    }
}
""";

        var execution = CompileAndRunSourceAtNative(
            source,
            "native_runtime_array_memory_balance.eidos",
            "native_runtime_array_memory_balance",
            runtimeExtraCFlags: "-DEIDOS_ENABLE_MEMORY_COUNTERS");

        Assert.Equal(0, execution.ExitCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void NativeStackPromotion_SmallCopyRecordInBranchAndLoop_PerformsNoHeapAllocations()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string source = """
@[extern(c, name: "eidos_memory_counters_reset")]
reset_memory_counters :: Unit -> Unit need ffi

@[extern(c, name: "eidos_memory_alloc_count")]
memory_alloc_count :: Unit -> Int need ffi

Pair :: type {
    left :: Int,
    right :: Int
}

pair_sum :: Bool -> Int -> Int {
    flag => seed => {
        pair := if flag then {
            Pair { left: seed, right: seed + 1 }
        } else {
            Pair { left: seed + 2, right: seed + 3 }
        }
        pair.left + pair.right
    }
}

run :: Int -> Int -> Int {
    0 => total => total,
    count => total => run(count - 1)(total + pair_sum(count % 2 == 0)(count))
}

main :: Unit -> Int need ffi {
    _ => {
        reset_memory_counters()
        run(64)(0)
        memory_alloc_count()
    }
}
""";

        var execution = CompileAndRunSourceAtNative(
            source,
            "native_stack_promotion_small_copy.eidos",
            "native_stack_promotion_small_copy",
            runtimeExtraCFlags: "-DEIDOS_ENABLE_MEMORY_COUNTERS");

        Assert.Equal(0, execution.ExitCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void NativeDropInsertion_OverwriteLoopBranchAndBodylessFfi_BalanceAllocations()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string source = """
import std.Text

@[extern(c, name: "eidos_memory_counters_reset")]
reset_memory_counters :: Unit -> Unit need ffi

@[extern(c, name: "eidos_memory_alloc_count")]
memory_alloc_count :: Unit -> Int need ffi

@[extern(c, name: "eidos_memory_free_count")]
memory_free_count :: Unit -> Int need ffi

Box :: type {
    value:: Int
}

Inner :: type {
    value :: Int
}

Outer :: type {
    inner :: Inner
}

overwrite :: Int -> Int
{
    seed => {
        mut box := Box { value: seed };
        box := Box { value: box.value + 1 };
        box.value
    }
}

loop_build :: Int -> Int -> Int
{
    0, total => total,
    count, total => {
        box := Box { value: count };
        loop_build(count - 1, total + box.value)
    }
}

branch_build :: Bool -> Int
{
    flag => {
        box := if flag then { Box { value: 7 } } else { Box { value: 11 } };
        box.value
    }
}

nested_build :: Int -> Int
{
    seed => {
        outer := Outer { inner: Inner { value: seed } };
        outer.inner.value
    }
}

ffi_string_loop :: Int -> Int {
    0 => 0,
    count => {
        Text.from_int(count)
        ffi_string_loop(count - 1)
    }
}

balance :: Unit -> Int need ffi
{
    _ => memory_alloc_count() - memory_free_count()
}

main :: Unit -> Int need ffi
{
    _ => {
        reset_memory_counters();
        overwrite(3);
        overwrite_delta := balance();

        reset_memory_counters();
        loop_build(64, 0);
        loop_delta := balance();

        reset_memory_counters();
        branch_build(true);
        branch_build(false);
        branch_delta := balance();

        reset_memory_counters();
        nested_build(13);
        nested_delta := balance();

        reset_memory_counters();
        ffi_string_loop(64);
        ffi_delta := balance();

        if overwrite_delta == 0 then {
            if loop_delta == 0 then {
                if branch_delta == 0 then {
                    if nested_delta == 0 then {
                        if ffi_delta == 0 then { 0 } else { ffi_delta + 50 }
                    } else { nested_delta + 40 }
                } else { branch_delta + 30 }
            } else { loop_delta + 20 }
        } else { overwrite_delta + 10 }
    }
}
""";

        var execution = CompileAndRunSourceAtNative(
            source,
            "native_memory_balance.eidos",
            "native_memory_balance",
            runtimeExtraCFlags: "-DEIDOS_ENABLE_MEMORY_COUNTERS");

        Assert.Equal(0, execution.ExitCode);
    }
}
