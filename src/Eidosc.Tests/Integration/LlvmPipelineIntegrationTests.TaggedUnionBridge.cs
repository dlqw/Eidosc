using Eidosc;
using Eidosc.CodeGen;
using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    /// <summary>
    /// M4c 桥接形态的原生冒烟：内联 bindgen 生成的 tagged-union 绑定
    /// （标签读写 + payload 取址 + 成员访问 + ADT decode/encode），C 侧提供
    /// 同形状的 union/struct 与 shim 函数，验证 encode → decode 往返与模式匹配。
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void TaggedUnionBridge_EncodeDecode_NativeSmoke_RoundTrips()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string source = """
            import std.IntNarrow

            @[extern(c, name: "eidos_shim_struct_Event_kind_get")]
            event_kind_get :: RawPtr -> Int need ffi;

            @[extern(c, name: "eidos_shim_struct_Event_kind_set")]
            event_kind_set :: RawPtr -> Int -> Unit need ffi;

            @[extern(c, name: "eidos_shim_struct_Event_payload_ptr")]
            event_payload_ptr :: RawPtr -> RawPtr need ffi;

            @[extern(c, name: "eidos_shim_union_Value_i_get")]
            value_i_get :: RawPtr -> Int32 need ffi;

            @[extern(c, name: "eidos_shim_union_Value_i_set")]
            value_i_set :: RawPtr -> Int32 -> Unit need ffi;

            @[extern(c, name: "alloc_event")]
            alloc_event :: Unit -> RawPtr need ffi;

            EventValue :: type { KindClick :: type(Int32), KindMove :: type(Float32) }

            event_value_decode :: RawPtr -> EventValue need ffi
            {
                p => {
                    tag: Int := event_kind_get(p);
                    tag == 0 then KindClick(value_i_get(event_payload_ptr(p)))
                    else KindClick(value_i_get(event_payload_ptr(p)))
                }
            }

            event_value_encode :: EventValue -> RawPtr -> Unit need ffi
            {
                KindClick(value) => p => {
                    event_kind_set(p, 0);
                    value_i_set(event_payload_ptr(p), value)
                },
                KindMove(_) => p => event_kind_set(p, 1)
            }

            main :: Unit -> Int need ffi
            {
                _ => {
                    event: RawPtr := alloc_event();
                    event_value_encode(KindClick(IntNarrow.from_int32(41)))(event);
                    match event_value_decode(event)
                    {
                        KindClick(value) => IntNarrow.to_int32(value) + 1,
                        KindMove(_) => 0
                    }
                }
            }
            """;

        const string cSource = """
            #include <stdint.h>

            union Value { int i; float f; };
            enum Kind { KIND_CLICK, KIND_MOVE };
            struct Event {
                enum Kind kind;
                union Value payload;
            };

            int64_t eidos_shim_struct_Event_kind_get(void* p) { return (int64_t)((struct Event*)p)->kind; }
            void eidos_shim_struct_Event_kind_set(void* p, int64_t v) { ((struct Event*)p)->kind = (enum Kind)v; }
            void* eidos_shim_struct_Event_payload_ptr(void* p) { return &((struct Event*)p)->payload; }
            int eidos_shim_union_Value_i_get(void* p) { return ((union Value*)p)->i; }
            void eidos_shim_union_Value_i_set(void* p, int v) { ((union Value*)p)->i = v; }

            static struct Event static_event;
            void* alloc_event(void) { return &static_event; }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "tagged_union_bridge_native.eidos",
            "tagged_union_bridge_native",
            nativeCSource: cSource);

        Assert.Equal(42, execution.ExitCode);
    }
}
