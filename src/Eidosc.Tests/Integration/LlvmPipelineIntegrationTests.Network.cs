using Eidosc.Symbols;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Eidosc;
using Eidosc.Diagnostic;
using Eidosc.CodeGen;
using Eidosc.CodeGen.Llvm;
using Eidosc.Mir;
using Eidosc.Pipeline;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Network)]
    public void StdNetworkImportFixture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_network_import.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);

        var llvmModule = result.LlvmModule!;
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__http_get_response", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__http_get_bytes_response", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__http_get_text_result", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__http_get_bytes_result", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__http_get_text_opt", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__http_get_bytes_opt", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__send", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__send_bytes", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__send_with_bytes_body", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__send_bytes_with_bytes_body", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__with_query_param", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__with_header", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__with_accept", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__with_user_agent", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__with_json_content_type", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__with_method", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__with_url", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__with_body", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__request_method", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__with_connect_timeout", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__with_total_timeout", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__http_post_text_result", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__http_post_bytes_text_result", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__http_post_bytes_result", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__http_post_json_opt", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__http_put_text_result", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__http_put_bytes_text_result", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__http_put_bytes_result", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__http_delete_text_opt", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__url_encode_component", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__headers", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__header_value_opt", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__effective_url", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__content_type", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__connect_timeout_seconds", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__total_timeout_seconds", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__is_redirect_code", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__is_error_status", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Network__bytes_is_error_status", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Network)]
    public void StdNetworkLocalSuccessAndRedirect_NativeRuntimeSmoke_WhenToolchainAvailable()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        using var server = new LoopbackHttpServer();
        var url = $"{server.BaseUrl}redirect";
        var escapedUrl = EscapeEidosStringLiteral(url);

        var source = $$"""
import std.Network
import std.Option
import std.Result
import std.Text

main :: Unit -> Int need io
{
    _ => {
        response := Network.http_get_response("{{escapedUrl}}");
        result := Network.http_get_text_result("{{escapedUrl}}");
        opt := Network.http_get_text_opt("{{escapedUrl}}");
        queryResult := Network.http_get_query_text_result("{{server.BaseUrl}}query")("q")("hello world");
        postResult := Network.http_post_text_result("{{server.BaseUrl}}echo")("ping");
        jsonResponse := Network.http_post_json_response("{{server.BaseUrl}}echo")("{\"ok\":true}");
        headerResponse := Network.send(Network.with_header(Network.get_request("{{server.BaseUrl}}header"))("X-Test")("header-value"));
        acceptResponse := Network.send(Network.with_accept_json(Network.get_request("{{server.BaseUrl}}accept")));
        replyHeaderResponse := Network.http_get_response("{{server.BaseUrl}}reply-header");
        replyHeaderLowerResponse := Network.http_get_response("{{server.BaseUrl}}reply-header");
        rawHeadersResponse := Network.http_get_response("{{server.BaseUrl}}reply-header");
        timeoutRequest := Network.with_total_timeout(Network.with_connect_timeout(Network.get_request("{{server.BaseUrl}}slow"))(2))(1);
        timeoutConfigBit := if Network.connect_timeout_seconds(ref timeoutRequest) == 2 &&
            Network.total_timeout_seconds(ref timeoutRequest) == 1
            then { 1 } else { 0 };
        timeoutResponse := Network.send(timeoutRequest);
        putResult := Network.http_put_text_result("{{server.BaseUrl}}echo")("pong");
        deleteResponse := Network.send(Network.with_bearer_auth(Network.delete_request("{{server.BaseUrl}}auth"))("token-123"));
        binaryBodyResponse := Network.http_get_bytes_response("{{server.BaseUrl}}binary");
        binaryHeaderResponse := Network.http_get_bytes_response("{{server.BaseUrl}}binary");
        binaryStatusResponse := Network.http_get_bytes_response("{{server.BaseUrl}}binary");
        binaryContentTypeResponse := Network.http_get_bytes_response("{{server.BaseUrl}}binary");
        binaryUrlResponse := Network.http_get_bytes_response("{{server.BaseUrl}}binary");
        binaryErrorResponse := Network.http_get_bytes_response("{{server.BaseUrl}}binary");
        binaryResult := Network.http_get_bytes_result("{{server.BaseUrl}}binary");
        binaryOpt := Network.http_get_bytes_opt("{{server.BaseUrl}}binary");
        postBinaryBodyResponse := Network.http_post_bytes_response("{{server.BaseUrl}}echo-binary")([0, 1, 255, 65]);
        postBinaryTypeResponse := Network.http_post_bytes_response("{{server.BaseUrl}}echo-binary")([0, 1, 255, 65]);
        putBinaryResult := Network.http_put_bytes_result("{{server.BaseUrl}}echo-binary")([5, 6, 7]);
        postBinaryTextResult := Network.http_post_bytes_text_result("{{server.BaseUrl}}echo")([112, 105, 110, 103]);
        customBinaryBodyResponse := Network.send_bytes_with_bytes_body(
            Network.request("POST")("{{server.BaseUrl}}echo-binary")("application.custom")(""))([9, 8]);
        customBinaryTypeResponse := Network.send_bytes_with_bytes_body(
            Network.request("POST")("{{server.BaseUrl}}echo-binary")("application.custom")(""))([9, 8]);
        customBinaryTextResponse := Network.send_with_bytes_body(
            Network.request("POST")("{{server.BaseUrl}}echo")("application.octet-stream")(""))([111, 107]);
        responseBits := match response
        {
            HttpResponse{
                ok: response_ok,
                status: response_status,
                body: response_body,
                headers: _,
                effective_url: response_effective_url,
                content_type: response_content_type,
                error: response_error
            } => {
                ok_bit := if response_ok then { 1 } else { 0 };
                status_bit := if response_status == 200 then { 1 } else { 0 };
                body_bit := if response_body == "hello-from-eidos" then { 1 } else { 0 };
                url_bit := if Text.ends_with(response_effective_url)("/ok") then { 1 } else { 0 };
                content_type_bit := if Text.starts_with(response_content_type)("text.plain") then { 1 } else { 0 };
                error_bit := if Text.len(response_error) == 0 then { 1 } else { 0 };
                success_bit := if response_status >= 200 && response_status < 300 then { 1 } else { 0 };
                ok_bit + status_bit + body_bit + url_bit + content_type_bit + error_bit + success_bit
            }
        };
        resultBit := match result
        {
            Ok(body) => if body == "hello-from-eidos" then { 1 } else { 0 },
            Err(message) => 0
        };
        optBit := match opt
        {
            Some(body) => if body == "hello-from-eidos" then { 1 } else { 0 },
            None() => 0
        };
        queryBit := match queryResult
        {
            Ok(body) => if body == "q=hello%20world" then { 1 } else { 0 },
            Err(message) => 0
        };
        postBit := match postResult
        {
            Ok(body) => if body == "ping" then { 1 } else { 0 },
            Err(message) => 0
        };
        jsonBit := match jsonResponse
        {
            HttpResponse{
                ok: _,
                status: _,
                body: json_body,
                headers: _,
                effective_url: _,
                content_type: json_content_type,
                error: _
            } => if json_body == "{\"ok\":true}" &&
                Text.starts_with(json_content_type)("application.json")
                then { 1 } else { 0 }
        };
        headerBit := if Network.body(ref headerResponse) == "header-value" then { 1 } else { 0 };
        acceptBit := if Network.body(ref acceptResponse) == "application.json" then { 1 } else { 0 };
        replyHeaderBit := match Network.header_value_opt(ref replyHeaderResponse)("X-Reply")
        {
            Some(value) => if value == "server-value" then { 1 } else { 0 },
            None() => 0
        };
        replyHeaderLowerBit := match Network.header_value_opt(ref replyHeaderLowerResponse)("x-reply")
        {
            Some(value) => if value == "server-value" then { 1 } else { 0 },
            None() => 0
        };
        rawHeadersBit := if Text.contains(Network.headers(ref rawHeadersResponse))("X-Reply: server-value") then { 1 } else { 0 };
        timeoutBit := match timeoutResponse
        {
            HttpResponse{
                ok: timeout_ok,
                status: _,
                body: _,
                headers: _,
                effective_url: _,
                content_type: _,
                error: timeout_error
            } => if !timeout_ok && Text.contains(timeout_error)("timed out")
                then { 1 } else { 0 }
        };
        putBit := match putResult
        {
            Ok(body) => if body == "pong" then { 1 } else { 0 },
            Err(message) => 0
        };
        deleteBit := if Network.body(ref deleteResponse) == "Bearer token-123" then { 1 } else { 0 };
        binaryBodyBit := match Network.body_bytes(ref binaryBodyResponse)
        {
            [0, 1, 255, 65] => 1,
            _ => 0
        };
        binaryStatusBit := if Network.bytes_status(ref binaryStatusResponse) == 200 then { 1 } else { 0 };
        binaryContentTypeBit := if Text.starts_with(Network.bytes_content_type(ref binaryContentTypeResponse))("application.octet-stream") then { 1 } else { 0 };
        binaryUrlBit := if Text.ends_with(Network.bytes_effective_url(ref binaryUrlResponse))("/binary") then { 1 } else { 0 };
        binaryErrorBit := if Text.len(Network.bytes_error(ref binaryErrorResponse)) == 0 then { 1 } else { 0 };
        binaryHeaderBit := match Network.bytes_header_value_opt(ref binaryHeaderResponse)("x-binary-reply")
        {
            Some(value) => if value == "bytes-value" then { 1 } else { 0 },
            None() => 0
        };
        binaryResultBit := match binaryResult
        {
            Ok(bytes) => {
                match bytes
                {
                    [0, 1, 255, 65] => 1,
                    _ => 0
                }
            },
            Err(message) => 0
        };
        binaryOptBit := match binaryOpt
        {
            Some(bytes) => {
                match bytes
                {
                    [0, 1, 255, 65] => 1,
                    _ => 0
                }
            },
            None() => 0
        };
        postBinaryBodyBit := match Network.body_bytes(ref postBinaryBodyResponse)
        {
            [0, 1, 255, 65] => 1,
            _ => 0
        };
        postBinaryTypeBit := if Text.starts_with(Network.bytes_content_type(ref postBinaryTypeResponse))("application.octet-stream") then { 1 } else { 0 };
        putBinaryResultBit := match putBinaryResult
        {
            Ok(bytes) => {
                match bytes
                {
                    [5, 6, 7] => 1,
                    _ => 0
                }
            },
            Err(message) => 0
        };
        postBinaryTextBit := match postBinaryTextResult
        {
            Ok(body) => if body == "ping" then { 1 } else { 0 },
            Err(message) => 0
        };
        customBinaryBodyBit := match Network.body_bytes(ref customBinaryBodyResponse)
        {
            [9, 8] => 1,
            _ => 0
        };
        customBinaryTypeBit := if Text.starts_with(Network.bytes_content_type(ref customBinaryTypeResponse))("application.custom") then { 1 } else { 0 };
        customBinaryTextBit := if Network.body(ref customBinaryTextResponse) == "ok" then { 1 } else { 0 };

        if responseBits + resultBit + optBit + queryBit + postBit + jsonBit + headerBit + acceptBit + replyHeaderBit + replyHeaderLowerBit + rawHeadersBit + timeoutConfigBit + timeoutBit + putBit + deleteBit + binaryBodyBit + binaryStatusBit + binaryContentTypeBit + binaryUrlBit + binaryErrorBit + binaryHeaderBit + binaryResultBit + binaryOptBit + postBinaryBodyBit + postBinaryTypeBit + putBinaryResultBit + postBinaryTextBit + customBinaryBodyBit + customBinaryTypeBit + customBinaryTextBit == 31
            then { 0 }
            else { 1 }
    }
}
""";

        var execution = CompileAndRunSourceAtNative(
            source,
            "network_local_success_redirect_smoke.eidos",
            "network_local_success_redirect_smoke");

        Assert.True(
            execution.ExitCode == 0,
            $"Exit={execution.ExitCode}\nstdout:\n{execution.StandardOutput}\nstderr:\n{execution.StandardError}");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Network)]
    public void StdNetworkLocalSuccessAndRedirect_LibcurlBackendSmoke_WhenExplicitlyEnabled()
    {
        if (!ToolExists("clang") || !LibcurlSmokeEnabled())
        {
            return;
        }

        using var server = new LoopbackHttpServer();
        var baseUrl = EscapeEidosStringLiteral(server.BaseUrl);

        var source = $$"""
import std.Network
import std.Option
import std.Result
import std.Text

main :: Unit -> Int need io
{
    _ => {
        redirect := Network.http_get_response("{{baseUrl}}redirect");
        replyHeader := Network.http_get_response("{{baseUrl}}reply-header");
        bytesBodyResponse := Network.http_get_bytes_response("{{baseUrl}}binary");
        bytesContentTypeResponse := Network.http_get_bytes_response("{{baseUrl}}binary");
        uploadResult := Network.http_post_bytes_text_result("{{baseUrl}}echo")([112, 105, 110, 103]);
        redirectBit := if Network.status(ref redirect) == 200 &&
            Network.body(ref redirect) == "hello-from-eidos" &&
            Text.ends_with(Network.effective_url(ref redirect))("/ok")
            then { 1 } else { 0 };
        headerBit := match Network.header_value_opt(ref replyHeader)("X-Reply")
        {
            Some(value) => if value == "server-value" then { 1 } else { 0 },
            None() => 0
        };
        bytesBit := match Network.body_bytes(ref bytesBodyResponse)
        {
            [0, 1, 255, 65] => 1,
            _ => 0
        };
        contentTypeBit := if Text.starts_with(Network.bytes_content_type(ref bytesContentTypeResponse))("application.octet-stream")
            then { 1 } else { 0 };
        uploadBit := match uploadResult
        {
            Ok(body) => if body == "ping" then { 1 } else { 0 },
            Err(message) => 0
        };

        if redirectBit + headerBit + bytesBit + contentTypeBit + uploadBit == 5
            then { 0 }
            else { 1 }
    }
}
""";

        var execution = CompileAndRunSourceAtNativeWithHttpBackend(
            source,
            "network_local_success_redirect_libcurl_smoke.eidos",
            "network_local_success_redirect_libcurl_smoke",
            "libcurl");

        Assert.True(
            execution.ExitCode == 0,
            $"Exit={execution.ExitCode}\nstdout:\n{execution.StandardOutput}\nstderr:\n{execution.StandardError}");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Network)]
    public void StdNetworkLocalHttpError_LibcurlBackendSmoke_WhenExplicitlyEnabled()
    {
        if (!ToolExists("clang") || !LibcurlSmokeEnabled())
        {
            return;
        }

        using var server = new LoopbackHttpServer();
        var missingUrl = EscapeEidosStringLiteral($"{server.BaseUrl}missing");

        var source = $$"""
import std.Network
import std.Option
import std.Result
import std.Text

main :: Unit -> Int need io
{
    _ => {
        response := Network.http_get_response("{{missingUrl}}");
        result := Network.http_get_text_result("{{missingUrl}}");
        bodyBit := if Network.body(ref response) == "missing-body" then { 1 } else { 0 };
        statusBit := if Network.status(ref response) == 404 then { 1 } else { 0 };
        errorBit := if Text.contains(Network.error(ref response))("404") then { 1 } else { 0 };
        headerBit := if Text.contains(Network.headers(ref response))("Content-Type: text.plain") then { 1 } else { 0 };
        resultBit := match result
        {
            Ok(body) => 0,
            Err(message) => if Text.contains(message)("404") then { 1 } else { 0 }
        };

        if bodyBit + statusBit + errorBit + headerBit + resultBit == 5
            then { 0 }
            else { 1 }
    }
}
""";

        var execution = CompileAndRunSourceAtNativeWithHttpBackend(
            source,
            "network_local_http_error_libcurl_smoke.eidos",
            "network_local_http_error_libcurl_smoke",
            "libcurl");

        Assert.Equal(0, execution.ExitCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Network)]
    public void StdNetworkLocalBytesRoundTrip_LibcurlBackendSmoke_WhenExplicitlyEnabled()
    {
        if (!ToolExists("clang") || !LibcurlSmokeEnabled())
        {
            return;
        }

        using var server = new LoopbackHttpServer();
        var baseUrl = EscapeEidosStringLiteral(server.BaseUrl);

        var source = $$"""
import std.Network
import std.Result
import std.Text

main :: Unit -> Int need io
{
    _ => {
        uploadBodyResponse := Network.http_post_bytes_response("{{baseUrl}}echo-binary")([9, 0, 255, 7]);
        uploadTypeResponse := Network.http_post_bytes_response("{{baseUrl}}echo-binary")([9, 0, 255, 7]);
        putResult := Network.http_put_bytes_result("{{baseUrl}}echo-binary")([1, 2, 3, 4]);
        uploadBodyBit := match Network.body_bytes(ref uploadBodyResponse)
        {
            [9, 0, 255, 7] => 1,
            _ => 0
        };
        uploadTypeBit := if Text.starts_with(Network.bytes_content_type(ref uploadTypeResponse))("application.octet-stream")
            then { 1 } else { 0 };
        putBodyBit := match putResult
        {
            Ok(bytes) => {
                match bytes
                {
                    [1, 2, 3, 4] => 1,
                    _ => 0
                }
            },
            Err(message) => 0
        };

        if uploadBodyBit + uploadTypeBit + putBodyBit == 3
            then { 0 }
            else { 1 }
    }
}
""";

        var execution = CompileAndRunSourceAtNativeWithHttpBackend(
            source,
            "network_local_bytes_roundtrip_libcurl_smoke.eidos",
            "network_local_bytes_roundtrip_libcurl_smoke",
            "libcurl");

        Assert.Equal(0, execution.ExitCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Network)]
    public void StdNetworkLocalQueryAndHeader_LibcurlBackendSmoke_WhenExplicitlyEnabled()
    {
        if (!ToolExists("clang") || !LibcurlSmokeEnabled())
        {
            return;
        }

        using var server = new LoopbackHttpServer();
        var baseUrl = EscapeEidosStringLiteral(server.BaseUrl);

        var source = $$"""
import std.Network
import std.Text

main :: Unit -> Int need io
{
    _ => {
        request := Network.with_header(
            Network.with_query_param(Network.get_request("{{baseUrl}}query-header"))("q")("hello world"))("X-Test")("alpha");
        response := Network.send(request);
        bodyBit := if Network.body(ref response) == "q=hello%20world|alpha" then { 1 } else { 0 };
        statusBit := if Network.status(ref response) == 200 then { 1 } else { 0 };
        contentTypeBit := if Text.starts_with(Network.content_type(ref response))("text.plain") then { 1 } else { 0 };

        if bodyBit + statusBit + contentTypeBit == 3
            then { 0 }
            else { 1 }
    }
}
""";

        var execution = CompileAndRunSourceAtNativeWithHttpBackend(
            source,
            "network_local_query_header_libcurl_smoke.eidos",
            "network_local_query_header_libcurl_smoke",
            "libcurl");

        Assert.Equal(0, execution.ExitCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Network)]
    public void StdNetworkImportFixture_NativeRuntimeSmoke_WithLibcurlBackend_WhenExplicitlyEnabled()
    {
        if (!ToolExists("clang") || !LibcurlSmokeEnabled())
        {
            return;
        }

        var execution = CompileAndRunFixtureAtNativeWithHttpBackend(
            Fx("stdlib/std_network_import.eidos"),
            "std_network_libcurl_native_smoke",
            "libcurl");

        Assert.Equal(22, execution.ExitCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Network)]
    public void StdNetworkLocalBinaryHttpError_LibcurlBackendSmoke_WhenExplicitlyEnabled()
    {
        if (!ToolExists("clang") || !LibcurlSmokeEnabled())
        {
            return;
        }

        using var server = new LoopbackHttpServer();
        var missingBinaryUrl = EscapeEidosStringLiteral($"{server.BaseUrl}missing-binary");

        var source = $$"""
import std.Network
import std.Option
import std.Result
import std.Text

main :: Unit -> Int need io
{
    _ => {
        bodyResponse := Network.http_get_bytes_response("{{missingBinaryUrl}}");
        statusResponse := Network.http_get_bytes_response("{{missingBinaryUrl}}");
        errorResponse := Network.http_get_bytes_response("{{missingBinaryUrl}}");
        typeResponse := Network.http_get_bytes_response("{{missingBinaryUrl}}");
        result := Network.http_get_bytes_result("{{missingBinaryUrl}}");
        opt := Network.http_get_bytes_opt("{{missingBinaryUrl}}");
        bodyBit := match Network.body_bytes(ref bodyResponse)
        {
            [222, 173, 190, 239] => 1,
            _ => 0
        };
        statusBit := if Network.bytes_status(ref statusResponse) == 404 then { 1 } else { 0 };
        errorBit := if Text.contains(Network.bytes_error(ref errorResponse))("404") then { 1 } else { 0 };
        typeBit := if Text.starts_with(Network.bytes_content_type(ref typeResponse))("application.octet-stream") then { 1 } else { 0 };
        resultBit := match result
        {
            Ok(bytes) => 0,
            Err(message) => if Text.contains(message)("404") then { 1 } else { 0 }
        };
        optBit := match opt
        {
            Some(bytes) => 0,
            None() => 1
        };

        if bodyBit + statusBit + errorBit + typeBit + resultBit + optBit == 6
            then { 0 }
            else { 1 }
    }
}
""";

        var execution = CompileAndRunSourceAtNativeWithHttpBackend(
            source,
            "network_local_binary_http_error_libcurl_smoke.eidos",
            "network_local_binary_http_error_libcurl_smoke",
            "libcurl");

        Assert.Equal(0, execution.ExitCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Network)]
    public void StdNetworkLocalTimeout_LibcurlBackendSmoke_WhenExplicitlyEnabled()
    {
        if (!ToolExists("clang") || !LibcurlSmokeEnabled())
        {
            return;
        }

        using var server = new LoopbackHttpServer();
        var slowUrl = EscapeEidosStringLiteral($"{server.BaseUrl}slow");

        var source = $$"""
import std.Network
import std.Text

main :: Unit -> Int need io
{
    _ => {
        request := Network.with_total_timeout(Network.with_connect_timeout(Network.get_request("{{slowUrl}}"))(2))(1);
        response := Network.send(request);
        okBit := if Network.ok(ref response) then { 0 } else { 1 };
        statusBit := if Network.status(ref response) == 0 then { 1 } else { 0 };
        errorBit := if Text.contains(Network.error(ref response))("timed out") then { 1 } else { 0 };

        if okBit + statusBit + errorBit == 3
            then { 0 }
            else { 1 }
    }
}
""";

        var execution = CompileAndRunSourceAtNativeWithHttpBackend(
            source,
            "network_local_timeout_libcurl_smoke.eidos",
            "network_local_timeout_libcurl_smoke",
            "libcurl");

        Assert.Equal(0, execution.ExitCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Network)]
    public void StdNetworkLocalParity_DefaultAndCurlBackendsAgree_WhenExplicitlyEnabled()
    {
        if (!ToolExists("clang") || !LibcurlSmokeEnabled())
        {
            return;
        }

        using var server = new LoopbackHttpServer();
        var baseUrl = EscapeEidosStringLiteral(server.BaseUrl);

        var source = $$"""
import std.Network
import std.Option
import std.Result
import std.Text

main :: Unit -> Int need io
{
    _ => {
        request := Network.with_header(
            Network.with_query_param(Network.get_request("{{baseUrl}}query-header"))("q")("hello world"))("X-Test")("alpha");
        queryHeaderResponse := Network.send(request);
        replyHeaderResponse := Network.http_get_response("{{baseUrl}}reply-header");
        binaryHeaderResponse := Network.http_get_bytes_response("{{baseUrl}}binary");
        binaryErrorStatusResponse := Network.http_get_bytes_response("{{baseUrl}}missing-binary");
        binaryErrorMessageResponse := Network.http_get_bytes_response("{{baseUrl}}missing-binary");
        timeoutRequest := Network.with_total_timeout(Network.with_connect_timeout(Network.get_request("{{baseUrl}}slow"))(2))(1);
        timeoutResponse := Network.send(timeoutRequest);
        queryHeaderBit := if Network.body(ref queryHeaderResponse) == "q=hello%20world|alpha" then { 1 } else { 0 };
        replyHeaderBit := match Network.header_value_opt(ref replyHeaderResponse)("x-reply")
        {
            Some(value) => if value == "server-value" then { 1 } else { 0 },
            None() => 0
        };
        binaryHeaderBit := match Network.bytes_header_value_opt(ref binaryHeaderResponse)("x-binary-reply")
        {
            Some(value) => if value == "bytes-value" then { 1 } else { 0 },
            None() => 0
        };
        binaryErrorBit := if Network.bytes_status(ref binaryErrorStatusResponse) == 404 &&
            Text.contains(Network.bytes_error(ref binaryErrorMessageResponse))("404")
            then { 1 } else { 0 };
        timeoutBit := if !Network.ok(ref timeoutResponse) &&
            Text.contains(Network.error(ref timeoutResponse))("timed out")
            then { 1 } else { 0 };

        if queryHeaderBit + replyHeaderBit + binaryHeaderBit + binaryErrorBit + timeoutBit == 5
            then { 0 }
            else { 1 }
    }
}
""";

        var defaultExecution = CompileAndRunSourceAtNativeWithHttpBackend(
            source,
            "network_local_backend_parity_default.eidos",
            "network_local_backend_parity_default",
            null);
        var curlExecution = CompileAndRunSourceAtNativeWithHttpBackend(
            source,
            "network_local_backend_parity_curl.eidos",
            "network_local_backend_parity_curl",
            "curl");

        Assert.Equal(0, defaultExecution.ExitCode);
        Assert.Equal(defaultExecution.ExitCode, curlExecution.ExitCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Network)]
    public void StdNetworkImportFixture_DefaultAndCurlBackendsAgree_WhenExplicitlyEnabled()
    {
        if (!ToolExists("clang") || !LibcurlSmokeEnabled())
        {
            return;
        }

        var defaultExecution = CompileAndRunFixtureAtNativeWithHttpBackend(
            Fx("stdlib/std_network_import.eidos"),
            "std_network_default_backend_parity",
            null);
        var curlExecution = CompileAndRunFixtureAtNativeWithHttpBackend(
            Fx("stdlib/std_network_import.eidos"),
            "std_network_curl_backend_parity",
            "curl");

        Assert.Equal(22, defaultExecution.ExitCode);
        Assert.Equal(defaultExecution.ExitCode, curlExecution.ExitCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Network)]
    public void StdNetworkLocalHead_DefaultAndCurlBackendsAgree_WhenExplicitlyEnabled()
    {
        if (!ToolExists("clang") || !LibcurlSmokeEnabled())
        {
            return;
        }

        using var server = new LoopbackHttpServer();
        var baseUrl = EscapeEidosStringLiteral(server.BaseUrl);

        var source = $$"""
import std.Network
import std.Option
import std.Text

main :: Unit -> Int need io
{
    _ => {
        response := Network.send(Network.request("HEAD")("{{baseUrl}}reply-header")("text.plain")(""));
        statusBit := if Network.status(ref response) == 200 then { 1 } else { 0 };
        bodyBit := if Network.body(ref response) == "" then { 1 } else { 0 };
        headerBit := match Network.header_value_opt(ref response)("X-Reply")
        {
            Some(value) => if value == "server-value" then { 1 } else { 0 },
            None() => 0
        };

        if statusBit + bodyBit + headerBit == 3
            then { 0 }
            else { 1 }
    }
}
""";

        var defaultExecution = CompileAndRunSourceAtNativeWithHttpBackend(
            source,
            "network_local_head_default.eidos",
            "network_local_head_default",
            null);
        var curlExecution = CompileAndRunSourceAtNativeWithHttpBackend(
            source,
            "network_local_head_curl.eidos",
            "network_local_head_curl",
            "curl");

        Assert.Equal(0, defaultExecution.ExitCode);
        Assert.Equal(defaultExecution.ExitCode, curlExecution.ExitCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Network)]
    public void StdNetworkLocalUnknownBackend_FallsBackToDefaultBehavior_WhenExplicitlyEnabled()
    {
        if (!ToolExists("clang") || !LibcurlSmokeEnabled())
        {
            return;
        }

        using var server = new LoopbackHttpServer();
        var okUrl = EscapeEidosStringLiteral($"{server.BaseUrl}ok");

        var source = $$"""
import std.Network

main :: Unit -> Int need io
{
    _ => {
        response := Network.http_get_response("{{okUrl}}");
        if Network.status(ref response) == 200 && Network.body(ref response) == "hello-from-eidos"
            then { 0 }
            else { 1 }
    }
}
""";

        var defaultExecution = CompileAndRunSourceAtNativeWithHttpBackend(
            source,
            "network_local_unknown_backend_default.eidos",
            "network_local_unknown_backend_default",
            null);
        var unknownExecution = CompileAndRunSourceAtNativeWithHttpBackend(
            source,
            "network_local_unknown_backend_unknown.eidos",
            "network_local_unknown_backend_unknown",
            "definitely-not-a-backend");

        Assert.Equal(0, defaultExecution.ExitCode);
        Assert.Equal(defaultExecution.ExitCode, unknownExecution.ExitCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Network)]
    public void StdNetworkLocalHttpError_PreservesBodyAndMetadata_WhenToolchainAvailable()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        using var server = new LoopbackHttpServer();
        var url = $"{server.BaseUrl}missing";
        var escapedUrl = EscapeEidosStringLiteral(url);

        var source = $$"""
import std.Network
import std.Option
import std.Result
import std.Text

main :: Unit -> Int need io
{
    _ => {
        response := Network.http_get_response("{{escapedUrl}}");
        result := Network.http_get_text_result("{{escapedUrl}}");
        opt := Network.http_get_text_opt("{{escapedUrl}}");
        okBit := if Network.ok(ref response) then { 0 } else { 1 };
        statusBit := if Network.status(ref response) == 404 then { 1 } else { 0 };
        bodyBit := if Network.body(ref response) == "missing-body" then { 1 } else { 0 };
        urlBit := if Text.ends_with(Network.effective_url(ref response))("/missing") then { 1 } else { 0 };
        contentTypeBit := if Text.starts_with(Network.content_type(ref response))("text.plain") then { 1 } else { 0 };
        errorBit := if Text.len(Network.error(ref response)) > 0 then { 1 } else { 0 };
        successBit := if Network.is_success_status(ref response) then { 0 } else { 1 };
        resultBit := match result
        {
            Ok(body) => 0,
            Err(message) => if Text.len(message) > 0 then { 1 } else { 0 }
        };
        optBit := match opt
        {
            Some(body) => 0,
            None() => 1
        };

        if okBit + statusBit + bodyBit + urlBit + contentTypeBit + errorBit + successBit + resultBit + optBit == 8
            then { 0 }
            else { 1 }
    }
}
""";

        var execution = CompileAndRunSourceAtNative(
            source,
            "network_local_http_error_smoke.eidos",
            "network_local_http_error_smoke");

        Assert.Equal(0, execution.ExitCode);
    }
}
