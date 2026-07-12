using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eidosup.Diagnostics;

public static class ErrorReporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static int Write(Exception exception, bool verbose, bool json, TextWriter? writer = null)
    {
        writer ??= Console.Error;
        var error = Describe(exception);
        if (json)
        {
            writer.WriteLine(JsonSerializer.Serialize(new
            {
                error.Code,
                error.Message,
                error.Hint,
                error.ExitCode,
                details = verbose ? Redact(exception.ToString()) : null
            }, JsonOptions));
        }
        else
        {
            writer.WriteLine($"error[{ToKebabCase(error.Code.ToString())}]: {error.Message}");
            if (!string.IsNullOrWhiteSpace(error.Hint))
            {
                writer.WriteLine($"hint: {error.Hint}");
            }

            if (verbose)
            {
                writer.WriteLine(Redact(exception.ToString()));
            }
        }

        return error.ExitCode;
    }

    public static EidosupException Describe(Exception exception) => exception switch
    {
        EidosupException known => known,
        OperationCanceledException => new EidosupException(
            EidosupErrorCode.Cancelled,
            EidosupExitCodes.Cancelled,
            "The operation was cancelled."),
        HttpRequestException httpException => new EidosupException(
            EidosupErrorCode.NetworkFailure,
            EidosupExitCodes.NetworkFailure,
            BuildNetworkMessage(httpException.StatusCode),
            "Check the network connection, proxy configuration, and selected release source.",
            httpException),
        UnauthorizedAccessException unauthorized => new EidosupException(
            EidosupErrorCode.PermissionDenied,
            EidosupExitCodes.PermissionDenied,
            "The operation does not have permission to access a required path.",
            "Choose a user-writable install root or correct the path permissions.",
            unauthorized),
        IOException ioException => new EidosupException(
            EidosupErrorCode.IoFailure,
            EidosupExitCodes.IoFailure,
            "A local file operation failed.",
            "Check available disk space, path permissions, and whether another process holds the file.",
            ioException),
        ArgumentException argumentException => new EidosupException(
            EidosupErrorCode.InvalidArgument,
            EidosupExitCodes.InvalidArgument,
            argumentException.Message,
            innerException: argumentException),
        FormatException formatException => new EidosupException(
            EidosupErrorCode.InvalidArgument,
            EidosupExitCodes.InvalidArgument,
            formatException.Message,
            innerException: formatException),
        _ => new EidosupException(
            EidosupErrorCode.InternalError,
            EidosupExitCodes.InternalError,
            "Eidosup encountered an unexpected internal error.",
            "Re-run with --verbose and report the diagnostic output if the problem persists.",
            exception)
    };

    private static string BuildNetworkMessage(HttpStatusCode? statusCode) => statusCode == null
        ? "The release source could not be reached."
        : $"The release source returned HTTP {(int)statusCode.Value} ({statusCode.Value}).";

    private static string Redact(string value)
    {
        foreach (var variable in new[] { "GITHUB_TOKEN", "GH_TOKEN" })
        {
            var secret = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(secret))
            {
                value = value.Replace(secret, "[redacted]", StringComparison.Ordinal);
            }
        }

        return value;
    }

    private static string ToKebabCase(string value)
    {
        var buffer = new System.Text.StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (index > 0 && char.IsUpper(character))
            {
                buffer.Append('-');
            }

            buffer.Append(char.ToLowerInvariant(character));
        }

        return buffer.ToString();
    }
}
