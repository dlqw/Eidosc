using Eidosup.Diagnostics;

namespace Eidosup.Installation;

public sealed class ChecksumManifest
{
    private readonly IReadOnlyDictionary<string, string> _checksums;

    private ChecksumManifest(IReadOnlyDictionary<string, string> checksums)
    {
        _checksums = checksums;
    }

    public static ChecksumManifest Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var checksums = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.Length < 67 || !IsSha256(line.AsSpan(0, 64)) || line[64] is not (' ' or '\t'))
            {
                throw InvalidManifest("contains a malformed line");
            }

            var separatorEnd = 64;
            while (separatorEnd < line.Length && line[separatorEnd] is ' ' or '\t')
            {
                separatorEnd++;
            }

            if (separatorEnd < line.Length && line[separatorEnd] == '*')
            {
                separatorEnd++;
            }

            var fileName = line[separatorEnd..];
            if (!IsSafeFileName(fileName))
            {
                throw InvalidManifest($"contains unsafe asset name '{fileName}'");
            }

            var checksum = line[..64].ToLowerInvariant();
            if (!checksums.TryAdd(fileName, checksum))
            {
                throw InvalidManifest($"contains duplicate entry '{fileName}'");
            }
        }

        if (checksums.Count == 0)
        {
            throw InvalidManifest("does not contain any checksum entries");
        }

        return new ChecksumManifest(checksums);
    }

    public string GetRequiredChecksum(string assetName)
    {
        if (!_checksums.TryGetValue(assetName, out var checksum))
        {
            throw new EidosupException(
                EidosupErrorCode.IntegrityFailure,
                EidosupExitCodes.IntegrityFailure,
                $"The checksum manifest does not contain asset '{assetName}'.",
                "Use a release whose SHA256SUMS file covers every published installable asset.");
        }

        return checksum;
    }

    public static bool IsSha256(ReadOnlySpan<char> value) =>
        value.Length == 64 && value.IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    private static bool IsSafeFileName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value == Path.GetFileName(value) &&
        value is not "." and not ".." &&
        !value.Contains('/') &&
        !value.Contains('\\') &&
        !value.Contains(':');

    private static EidosupException InvalidManifest(string reason) => new(
        EidosupErrorCode.IntegrityFailure,
        EidosupExitCodes.IntegrityFailure,
        $"SHA256SUMS {reason}.",
        "Do not install this release; publish a valid checksum manifest and retry.");
}
