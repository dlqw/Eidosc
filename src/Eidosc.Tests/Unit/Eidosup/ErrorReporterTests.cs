using System.Text.Json;
using Eidosup.Diagnostics;

namespace Eidosc.Tests.Unit.Eidosup;

public sealed class ErrorReporterTests
{
    [Fact]
    public void Write_DefaultOutputHidesStackAndInnerException()
    {
        var exception = new EidosupException(
            EidosupErrorCode.ReleaseNotFound,
            EidosupExitCodes.ReleaseNotFound,
            "Release was not found.",
            "Choose another version.",
            new InvalidOperationException("internal detail"));
        using var writer = new StringWriter();

        var exitCode = ErrorReporter.Write(exception, verbose: false, json: false, writer);

        var output = writer.ToString();
        Assert.Equal(EidosupExitCodes.ReleaseNotFound, exitCode);
        Assert.Contains("error[release-not-found]", output, StringComparison.Ordinal);
        Assert.Contains("hint: Choose another version.", output, StringComparison.Ordinal);
        Assert.DoesNotContain("internal detail", output, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_JsonOutputHasStableMachineReadableFields()
    {
        var exception = new EidosupException(
            EidosupErrorCode.AuthenticationRequired,
            EidosupExitCodes.AuthenticationRequired,
            "Authentication is required.",
            "Set a token.");
        using var writer = new StringWriter();

        ErrorReporter.Write(exception, verbose: false, json: true, writer);

        using var document = JsonDocument.Parse(writer.ToString());
        var root = document.RootElement;
        Assert.Equal("authenticationRequired", root.GetProperty("code").GetString());
        Assert.Equal(EidosupExitCodes.AuthenticationRequired, root.GetProperty("exitCode").GetInt32());
        Assert.Equal("Set a token.", root.GetProperty("hint").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("details").ValueKind);
    }

    [Fact]
    public void Write_VerboseOutputRedactsEnvironmentTokens()
    {
        const string token = "unit-test-secret-token";
        var previous = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", token);
        try
        {
            using var writer = new StringWriter();
            ErrorReporter.Write(new InvalidOperationException($"failure containing {token}"), verbose: true, json: false, writer);

            Assert.DoesNotContain(token, writer.ToString(), StringComparison.Ordinal);
            Assert.Contains("[redacted]", writer.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", previous);
        }
    }
}
