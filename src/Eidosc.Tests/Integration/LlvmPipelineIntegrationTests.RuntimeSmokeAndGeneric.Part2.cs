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
    public void StdNetworkLocalWideMatrix_DefaultLibcurlBackend_StaysAlignedWithExplicitCurlOverride()
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

main :: Unit -> Int
{
    _ => {
        queryHeaderRequest := Network.with_header(
            Network.with_query_param(Network.get_request("{{baseUrl}}query-header"))("q")("hello world"))("X-Test")("alpha");
        queryHeaderResponse := Network.send(queryHeaderRequest);
        binaryQueryHeaderRequest := Network.with_header(
            Network.with_query_param(Network.get_request("{{baseUrl}}binary-query-header"))("q")("hello world"))("X-Test")("beta");
        binaryQueryHeaderResponse := Network.send_bytes(binaryQueryHeaderRequest);
        binaryQueryHeaderHeaderRequest := Network.with_header(
            Network.with_query_param(Network.get_request("{{baseUrl}}binary-query-header"))("q")("hello world"))("X-Test")("beta");
        binaryQueryHeaderHeaderResponse := Network.send_bytes(binaryQueryHeaderHeaderRequest);
        binaryRedirectBodyResponse := Network.http_get_bytes_response("{{baseUrl}}binary-redirect");
        binaryRedirectUrlResponse := Network.http_get_bytes_response("{{baseUrl}}binary-redirect");
        binaryRedirectHeaderResponse := Network.http_get_bytes_response("{{baseUrl}}binary-redirect");
        repeatHeaderValueResponse := Network.http_get_response("{{baseUrl}}repeat-header");
        repeatHeaderRawFirstResponse := Network.http_get_response("{{baseUrl}}repeat-header");
        repeatHeaderRawSecondResponse := Network.http_get_response("{{baseUrl}}repeat-header");
        emptyJsonBodyResponse := Network.http_get_response("{{baseUrl}}empty-json");
        emptyJsonTypeResponse := Network.http_get_response("{{baseUrl}}empty-json");
        binaryRepeatHeaderValueResponse := Network.http_get_bytes_response("{{baseUrl}}binary-repeat-header");
        binaryRepeatHeaderRawFirstResponse := Network.http_get_bytes_response("{{baseUrl}}binary-repeat-header");
        binaryRepeatHeaderRawSecondResponse := Network.http_get_bytes_response("{{baseUrl}}binary-repeat-header");
        binaryEmptyBodyResponse := Network.http_get_bytes_response("{{baseUrl}}binary-empty");
        binaryEmptyTypeResponse := Network.http_get_bytes_response("{{baseUrl}}binary-empty");
        binaryEmptyHeaderResponse := Network.http_get_bytes_response("{{baseUrl}}binary-empty");
        headStatusResponse := Network.send(Network.request("HEAD")("{{baseUrl}}reply-header")("text.plain")(""));
        headBodyResponse := Network.send(Network.request("HEAD")("{{baseUrl}}reply-header")("text.plain")(""));
        headHeaderResponse := Network.send(Network.request("HEAD")("{{baseUrl}}reply-header")("text.plain")(""));
        binaryErrorStatusResponse := Network.http_get_bytes_response("{{baseUrl}}missing-binary");
        binaryErrorMessageResponse := Network.http_get_bytes_response("{{baseUrl}}missing-binary");
        binaryErrorHeaderResponse := Network.http_get_bytes_response("{{baseUrl}}missing-binary");
        binaryErrorResult := Network.http_get_bytes_result("{{baseUrl}}missing-binary");
        uploadBodyResponse := Network.send_bytes_with_bytes_body(
            Network.request("POST")("{{baseUrl}}echo-binary")("application.custom")(""))([9, 8]);
        uploadTypeResponse := Network.send_bytes_with_bytes_body(
            Network.request("POST")("{{baseUrl}}echo-binary")("application.custom")(""))([9, 8]);
        timeoutStatusRequest := Network.with_total_timeout(Network.with_connect_timeout(Network.get_request("{{baseUrl}}slow"))(2))(1);
        timeoutErrorRequest := Network.with_total_timeout(Network.with_connect_timeout(Network.get_request("{{baseUrl}}slow"))(2))(1);
        timeoutStatusResponse := Network.send(timeoutStatusRequest);
        timeoutErrorResponse := Network.send(timeoutErrorRequest);

        queryHeaderBit := if Network.body(ref queryHeaderResponse) == "q=hello%20world|alpha" then { 1 } else { 0 };
        binaryQueryHeaderBit := match Network.body_bytes(ref binaryQueryHeaderResponse)
        {
            [113, 61, 104, 101, 108, 108, 111, 37, 50, 48, 119, 111, 114, 108, 100, 124, 98, 101, 116, 97] => 1,
            _ => 0
        };
        binaryQueryHeaderResponseBit := match Network.bytes_header_value_opt(ref binaryQueryHeaderHeaderResponse)("x-binary-reply")
        {
            Some(value) => if value == "bytes-query" then { 1 } else { 0 },
            None() => 0
        };
        binaryRedirectBodyBit := match Network.body_bytes(ref binaryRedirectBodyResponse)
        {
            [0, 1, 255, 65] => 1,
            _ => 0
        };
        binaryRedirectUrlBit := if Text.ends_with(Network.bytes_effective_url(ref binaryRedirectUrlResponse))("/binary") then { 1 } else { 0 };
        binaryRedirectHeaderBit := match Network.bytes_header_value_opt(ref binaryRedirectHeaderResponse)("x-binary-reply")
        {
            Some(value) => if value == "bytes-value" then { 1 } else { 0 },
            None() => 0
        };
        repeatHeaderBit := match Network.header_value_opt(ref repeatHeaderValueResponse)("x-repeat")
        {
            Some(value) => if value == "first" then { 1 } else { 0 },
            None() => 0
        };
        repeatHeaderRawBit := if Text.contains(Network.headers(ref repeatHeaderRawFirstResponse))("X-Repeat: first") &&
            Text.contains(Network.headers(ref repeatHeaderRawSecondResponse))("X-Repeat: second")
            then { 1 } else { 0 };
        emptyJsonBodyBit := if Network.body(ref emptyJsonBodyResponse) == "" then { 1 } else { 0 };
        emptyJsonTypeBit := if Text.starts_with(Network.content_type(ref emptyJsonTypeResponse))("application.json") then { 1 } else { 0 };
        binaryRepeatHeaderBit := match Network.bytes_header_value_opt(ref binaryRepeatHeaderValueResponse)("x-repeat")
        {
            Some(value) => if value == "first" then { 1 } else { 0 },
            None() => 0
        };
        binaryRepeatHeaderRawBit := if Text.contains(Network.bytes_headers(ref binaryRepeatHeaderRawFirstResponse))("X-Repeat: first") &&
            Text.contains(Network.bytes_headers(ref binaryRepeatHeaderRawSecondResponse))("X-Repeat: second")
            then { 1 } else { 0 };
        binaryEmptyBodyBit := match Network.body_bytes(ref binaryEmptyBodyResponse)
        {
            [] => 1,
            _ => 0
        };
        binaryEmptyTypeBit := if Text.starts_with(Network.bytes_content_type(ref binaryEmptyTypeResponse))("application.empty") then { 1 } else { 0 };
        binaryEmptyHeaderBit := match Network.bytes_header_value_opt(ref binaryEmptyHeaderResponse)("x-binary-reply")
        {
            Some(value) => if value == "empty-value" then { 1 } else { 0 },
            None() => 0
        };
        headBit := if Network.status(ref headStatusResponse) == 200 && Network.body(ref headBodyResponse) == "" then { 1 } else { 0 };
        headHeaderBit := match Network.header_value_opt(ref headHeaderResponse)("X-Reply")
        {
            Some(value) => if value == "server-value" then { 1 } else { 0 },
            None() => 0
        };
        binaryErrorBit := if Network.bytes_status(ref binaryErrorStatusResponse) == 404 &&
            Text.contains(Network.bytes_error(ref binaryErrorMessageResponse))("404")
            then { 1 } else { 0 };
        binaryErrorHeaderBit := match Network.bytes_header_value_opt(ref binaryErrorHeaderResponse)("x-binary-reply")
        {
            Some(value) => if value == "bytes-value" then { 1 } else { 0 },
            None() => 0
        };
        binaryResultBit := match binaryErrorResult
        {
            Ok(bytes) => 0,
            Err(message) => if Text.contains(message)("404") then { 1 } else { 0 }
        };
        uploadBodyBit := match Network.body_bytes(ref uploadBodyResponse)
        {
            [9, 8] => 1,
            _ => 0
        };
        uploadTypeBit := if Text.starts_with(Network.bytes_content_type(ref uploadTypeResponse))("application.custom") then { 1 } else { 0 };
        timeoutBit := if !Network.ok(ref timeoutStatusResponse) &&
            Network.status(ref timeoutStatusResponse) == 0 &&
            Text.contains(Network.error(ref timeoutErrorResponse))("timed out")
            then { 1 } else { 0 };

        if queryHeaderBit + binaryQueryHeaderBit + binaryQueryHeaderResponseBit + binaryRedirectBodyBit + binaryRedirectUrlBit + binaryRedirectHeaderBit + repeatHeaderBit + repeatHeaderRawBit + emptyJsonBodyBit + emptyJsonTypeBit + binaryRepeatHeaderBit + binaryRepeatHeaderRawBit + binaryEmptyBodyBit + binaryEmptyTypeBit + binaryEmptyHeaderBit + headBit + headHeaderBit + binaryErrorBit + binaryErrorHeaderBit + binaryResultBit + uploadBodyBit + uploadTypeBit + timeoutBit == 23
            then { 0 }
            else { 1 }
    }
}
""";

        var preferDefaultExecution = CompileAndRunSourceAtNativeWithHttpBackend(
            source,
            "network_local_wide_prefer_libcurl_default.eidos",
            "network_local_wide_prefer_libcurl_default",
            null);
        var explicitCurlExecution = CompileAndRunSourceAtNativeWithHttpBackend(
            source,
            "network_local_wide_prefer_libcurl_explicit_curl.eidos",
            "network_local_wide_prefer_libcurl_explicit_curl",
            "curl");

        Assert.Equal(0, preferDefaultExecution.ExitCode);
        Assert.Equal(preferDefaultExecution.ExitCode, explicitCurlExecution.ExitCode);
    }

    [Fact]
    public void StdNetworkLocalUnknownBackend_DefaultLibcurlBackend_FallsBackToLibcurl()
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

main :: Unit -> Int
{
    _ => {
        redirect := Network.http_get_response("{{baseUrl}}redirect");
        headResponse := Network.send(Network.request("HEAD")("{{baseUrl}}reply-header")("text.plain")(""));
        redirectBit := if Network.status(ref redirect) == 200 &&
            Network.body(ref redirect) == "hello-from-eidos" &&
            Text.ends_with(Network.effective_url(ref redirect))("/ok")
            then { 1 } else { 0 };
        headHeaderBit := match Network.header_value_opt(ref headResponse)("X-Reply")
        {
            Some(value) => if value == "server-value" then { 1 } else { 0 },
            None() => 0
        };

        if redirectBit + headHeaderBit == 2
            then { 0 }
            else { 1 }
    }
}
""";

        var preferDefaultExecution = CompileAndRunSourceAtNativeWithHttpBackend(
            source,
            "network_local_unknown_prefer_libcurl_default.eidos",
            "network_local_unknown_prefer_libcurl_default",
            null);
        var unknownBackendExecution = CompileAndRunSourceAtNativeWithHttpBackend(
            source,
            "network_local_unknown_prefer_libcurl_unknown.eidos",
            "network_local_unknown_prefer_libcurl_unknown",
            "definitely-not-a-backend");

        Assert.Equal(0, preferDefaultExecution.ExitCode);
        Assert.Equal(preferDefaultExecution.ExitCode, unknownBackendExecution.ExitCode);
    }

    [Fact]
    public void StdNetworkLocalRequestShape_DefaultLibcurlBackend_StaysAlignedWithExplicitCurlOverride()
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

main :: Unit -> Int
{
    _ => {
        textRequest := Network.with_header(
            Network.with_query_param(
                Network.request("POST")("{{baseUrl}}request-shape")("text.custom")("ping"))("q")("hello world"))("X-Test")("alpha");
        textResponse := Network.send(textRequest);

        bytesRequest := Network.with_header(
            Network.with_query_param(
                Network.request("PUT")("{{baseUrl}}request-shape")("application.custom")(""))("k")("v"))("X-Test")("beta");
        bytesResponse := Network.send_with_bytes_body(bytesRequest)([9, 8, 7]);
        textDuplicateHeaderRequest := Network.with_header(
            Network.with_header(
                Network.request("POST")("{{baseUrl}}request-shape-headers")("application.json")(""))("X-Repeat")("first"))("X-Repeat")("second");
        textDuplicateHeaderResponse := Network.send(textDuplicateHeaderRequest);
        bytesDuplicateHeaderRequest := Network.with_header(
            Network.with_header(
                Network.request("PUT")("{{baseUrl}}request-shape-headers")("application.empty")(""))("X-Repeat")("first"))("X-Repeat")("second");
        bytesDuplicateHeaderResponse := Network.send_with_bytes_body(bytesDuplicateHeaderRequest)([]);

        textBit := if Network.body(ref textResponse) == "POST|q=hello%20world|text.custom|4|alpha"
            then { 1 } else { 0 };
        bytesBit := if Network.body(ref bytesResponse) == "PUT|k=v|application.custom|3|beta"
            then { 1 } else { 0 };
        textDuplicateHeaderBit := if Network.body(ref textDuplicateHeaderResponse) == "POST||application.json|0||first,second"
            then { 1 } else { 0 };
        bytesDuplicateHeaderBit := if Network.body(ref bytesDuplicateHeaderResponse) == "PUT||application.empty|0||first,second"
            then { 1 } else { 0 };

        if textBit + bytesBit + textDuplicateHeaderBit + bytesDuplicateHeaderBit == 4
            then { 0 }
            else { 1 }
    }
}
""";

        var preferDefaultExecution = CompileAndRunSourceAtNativeWithHttpBackend(
            source,
            "network_local_request_shape_prefer_libcurl_default.eidos",
            "network_local_request_shape_prefer_libcurl_default",
            null);
        var explicitCurlExecution = CompileAndRunSourceAtNativeWithHttpBackend(
            source,
            "network_local_request_shape_prefer_libcurl_explicit_curl.eidos",
            "network_local_request_shape_prefer_libcurl_explicit_curl",
            "curl");

        Assert.Equal(0, preferDefaultExecution.ExitCode);
        Assert.Equal(preferDefaultExecution.ExitCode, explicitCurlExecution.ExitCode);
    }

    [Fact]
    public void StdNetworkLocalRedirectRequestShape_DefaultLibcurlBackend_StaysAlignedWithExplicitCurlOverride()
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

main :: Unit -> Int
{
    _ => {
        textRedirectRequest := Network.with_header(
            Network.with_header(
                Network.request("POST")("{{baseUrl}}redirect-307-request-shape")("application.custom")("ping"))("X-Test")("alpha"))("X-Repeat")("first");
        textRedirectResponse := Network.send(Network.with_header(textRedirectRequest)("x-repeat")("second"));
        textRedirectUrlRequest := Network.with_header(
            Network.with_header(
                Network.request("POST")("{{baseUrl}}redirect-307-request-shape")("application.custom")("ping"))("X-Test")("alpha"))("X-Repeat")("first");
        textRedirectUrlResponse := Network.send(Network.with_header(textRedirectUrlRequest)("x-repeat")("second"));

        bytesRedirectRequest := Network.with_header(
            Network.with_header(
                Network.request("PUT")("{{baseUrl}}redirect-308-request-shape")("application.octet-stream")(""))("X-Test")("beta"))("X-Repeat")("first");
        bytesRedirectResponse := Network.send_with_bytes_body(Network.with_header(bytesRedirectRequest)("x-repeat")("second"))([9, 8, 7]);
        bytesRedirectUrlRequest := Network.with_header(
            Network.with_header(
                Network.request("PUT")("{{baseUrl}}redirect-308-request-shape")("application.octet-stream")(""))("X-Test")("beta"))("X-Repeat")("first");
        bytesRedirectUrlResponse := Network.send_with_bytes_body(Network.with_header(bytesRedirectUrlRequest)("x-repeat")("second"))([9, 8, 7]);

        textRedirectBit := if Network.body(ref textRedirectResponse) == "POST||application.custom|4|alpha|first,second"
            then { 1 } else { 0 };
        textRedirectUrlBit := if Text.ends_with(Network.effective_url(ref textRedirectUrlResponse))("/request-shape-headers")
            then { 1 } else { 0 };
        bytesRedirectBit := if Network.body(ref bytesRedirectResponse) == "PUT||application.octet-stream|3|beta|first,second"
            then { 1 } else { 0 };
        bytesRedirectUrlBit := if Text.ends_with(Network.effective_url(ref bytesRedirectUrlResponse))("/request-shape-headers")
            then { 1 } else { 0 };

        if textRedirectBit + textRedirectUrlBit + bytesRedirectBit + bytesRedirectUrlBit == 4
            then { 0 }
            else { 1 }
    }
}
""";

        var preferDefaultExecution = CompileAndRunSourceAtNativeWithHttpBackend(
            source,
            "network_local_redirect_request_shape_prefer_libcurl_default.eidos",
            "network_local_redirect_request_shape_prefer_libcurl_default",
            null);
        var explicitCurlExecution = CompileAndRunSourceAtNativeWithHttpBackend(
            source,
            "network_local_redirect_request_shape_prefer_libcurl_explicit_curl.eidos",
            "network_local_redirect_request_shape_prefer_libcurl_explicit_curl",
            "curl");

        Assert.Equal(0, preferDefaultExecution.ExitCode);
        Assert.Equal(preferDefaultExecution.ExitCode, explicitCurlExecution.ExitCode);
    }

    [Fact]
    public void StdNetworkLocalEmptyPayloadContentLength_DefaultLibcurlBackend_StaysAlignedWithExplicitCurlOverride()
    {
        if (!ToolExists("clang") || !LibcurlSmokeEnabled())
        {
            return;
        }

        using var server = new LoopbackHttpServer();
        var baseUrl = EscapeEidosStringLiteral(server.BaseUrl);

        var source = $$"""
import std.Network

main :: Unit -> Int
{
    _ => {
        textResponse := Network.send(
            Network.request("POST")("{{baseUrl}}request-shape-length")("application.json")(""));
        bytesResponse := Network.send_with_bytes_body(
            Network.request("PUT")("{{baseUrl}}request-shape-length")("application.octet-stream")(""))([]);

        textBit := if Network.body(ref textResponse) == "POST|application.json|0|0"
            then { 1 } else { 0 };
        bytesBit := if Network.body(ref bytesResponse) == "PUT|application.octet-stream|0|0"
            then { 1 } else { 0 };

        if textBit + bytesBit == 2
            then { 0 }
            else { 1 }
    }
}
""";

        var preferDefaultExecution = CompileAndRunSourceAtNativeWithHttpBackend(
            source,
            "network_local_empty_payload_content_length_prefer_libcurl_default.eidos",
            "network_local_empty_payload_content_length_prefer_libcurl_default",
            null);
        var explicitCurlExecution = CompileAndRunSourceAtNativeWithHttpBackend(
            source,
            "network_local_empty_payload_content_length_prefer_libcurl_explicit_curl.eidos",
            "network_local_empty_payload_content_length_prefer_libcurl_explicit_curl",
            "curl");

        Assert.Equal(0, preferDefaultExecution.ExitCode);
        Assert.Equal(preferDefaultExecution.ExitCode, explicitCurlExecution.ExitCode);
    }

    [Fact]
    public void StdNetworkLocalTextErrorAndAuth_DefaultLibcurlBackend_StaysAlignedWithExplicitCurlOverride()
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

main :: Unit -> Int
{
    _ => {
        acceptResponse := Network.send(Network.with_accept_json(Network.get_request("{{baseUrl}}accept")));
        deleteResponse := Network.send(Network.with_bearer_auth(Network.delete_request("{{baseUrl}}auth"))("token-123"));
        missingResponse := Network.http_get_response("{{baseUrl}}missing");
        missingResult := Network.http_get_text_result("{{baseUrl}}missing");

        acceptBit := if Network.body(ref acceptResponse) == "application.json" then { 1 } else { 0 };
        deleteBit := if Network.body(ref deleteResponse) == "Bearer token-123" then { 1 } else { 0 };
        missingBodyBit := if Network.body(ref missingResponse) == "missing-body" then { 1 } else { 0 };
        missingHeaderBit := if Text.contains(Network.headers(ref missingResponse))("Content-Type: text.plain") then { 1 } else { 0 };
        missingErrorBit := if Text.contains(Network.error(ref missingResponse))("404") then { 1 } else { 0 };
        missingResultBit := match missingResult
        {
            Ok(body) => 0,
            Err(message) => if Text.contains(message)("404") then { 1 } else { 0 }
        };

        if acceptBit + deleteBit + missingBodyBit + missingHeaderBit + missingErrorBit + missingResultBit == 6
            then { 0 }
            else { 1 }
    }
}
""";

        var preferDefaultExecution = CompileAndRunSourceAtNativeWithHttpBackend(
            source,
            "network_local_text_error_auth_prefer_libcurl_default.eidos",
            "network_local_text_error_auth_prefer_libcurl_default",
            null);
        var explicitCurlExecution = CompileAndRunSourceAtNativeWithHttpBackend(
            source,
            "network_local_text_error_auth_prefer_libcurl_explicit_curl.eidos",
            "network_local_text_error_auth_prefer_libcurl_explicit_curl",
            "curl");

        Assert.Equal(0, preferDefaultExecution.ExitCode);
        Assert.Equal(preferDefaultExecution.ExitCode, explicitCurlExecution.ExitCode);
    }
}
