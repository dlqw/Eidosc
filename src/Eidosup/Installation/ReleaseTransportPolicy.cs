namespace Eidosup.Installation;

internal static class ReleaseTransportPolicy
{
    internal const string TestServerEnvironmentVariable = "EIDOSUP_TEST_RELEASE_SERVER";

    public static Uri? GetTestServerBaseUri()
    {
        var value = Environment.GetEnvironmentVariable(TestServerEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp ||
            !uri.IsLoopback ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                $"{TestServerEnvironmentVariable} must be an absolute loopback HTTP URL without credentials, query, or fragment.");
        }

        var builder = new UriBuilder(uri);
        if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
        {
            builder.Path += "/";
        }

        return builder.Uri;
    }

    public static bool IsAllowedAssetUri(Uri uri)
    {
        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }

        var testServer = GetTestServerBaseUri();
        return testServer != null &&
               uri.Scheme == Uri.UriSchemeHttp &&
               uri.IsLoopback &&
               uri.Port == testServer.Port &&
               string.Equals(uri.Host, testServer.Host, StringComparison.OrdinalIgnoreCase);
    }
}
