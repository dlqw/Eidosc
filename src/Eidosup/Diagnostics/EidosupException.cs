namespace Eidosup.Diagnostics;

public enum EidosupErrorCode
{
    InvalidArgument,
    NetworkFailure,
    AuthenticationRequired,
    AccessDenied,
    RateLimited,
    ReleaseNotFound,
    NoMatchingRelease,
    InvalidReleaseMetadata,
    MissingReleaseAsset,
    IntegrityFailure,
    UnsafeArchive,
    InstallConflict,
    InstallFailure,
    LockTimeout,
    PermissionDenied,
    IoFailure,
    Cancelled,
    InternalError
}

public sealed class EidosupException : Exception
{
    public EidosupException(
        EidosupErrorCode code,
        int exitCode,
        string message,
        string? hint = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        ExitCode = exitCode;
        Hint = hint;
    }

    public EidosupErrorCode Code { get; }

    public int ExitCode { get; }

    public string? Hint { get; }
}

public static class EidosupExitCodes
{
    public const int Success = 0;
    public const int InvalidArgument = 2;
    public const int NetworkFailure = 10;
    public const int AuthenticationRequired = 11;
    public const int AccessDenied = 12;
    public const int RateLimited = 13;
    public const int ReleaseNotFound = 14;
    public const int InvalidRelease = 15;
    public const int MissingAsset = 16;
    public const int IntegrityFailure = 20;
    public const int UnsafeArchive = 21;
    public const int InstallConflict = 22;
    public const int InstallFailure = 23;
    public const int LockTimeout = 24;
    public const int PermissionDenied = 30;
    public const int IoFailure = 31;
    public const int DoctorUnhealthy = 50;
    public const int InternalError = 70;
    public const int Cancelled = 130;
}
