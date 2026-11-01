using System.Globalization;
using System.Security.Cryptography;

namespace LibWiiUKit;

/// <summary>
/// The AES-128 keys from the user's own keys.txt (Cemu's format: one key of
/// 32 hex digits per line; anything after '#' or ';' is a comment; spaces,
/// tabs, '-' and '_' are ignored). The library ships no keys. Which line is
/// which doesn't matter: every key is tried, as a disc key, a common key or
/// a title key, and the one that decrypts to a valid header is used.
/// </summary>
public sealed class WiiUKeys
{
    private readonly List<byte[]> _keys;

    public IReadOnlyList<byte[]> Keys => _keys;
    public IReadOnlyList<string> Files { get; }
    public IReadOnlyList<string> Problems { get; }

    private WiiUKeys(List<byte[]> keys, List<string> files, List<string> problems)
    {
        _keys = keys;
        Files = files;
        Problems = problems;
    }

    public static WiiUKeys Empty { get; } = new([], [], []);

    /// <summary>
    /// Where a keys.txt is looked for when no path is set: Cemu's usual
    /// user-data folder on each platform.
    /// </summary>
    public static IEnumerable<string> DefaultLocations()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
            yield return Path.Combine(appData, "Cemu", "keys.txt");
        else if (OperatingSystem.IsMacOS())
            yield return Path.Combine(home, "Library", "Application Support", "Cemu", "keys.txt");
        else
        {
            var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            yield return Path.Combine(string.IsNullOrEmpty(dataHome) ? Path.Combine(home, ".local", "share") : dataHome, "Cemu", "keys.txt");
        }
    }

    /// <summary>
    /// Loads <paramref name="path"/> if given, otherwise every keys.txt found in
    /// <paramref name="searchPaths"/> (by default <see cref="DefaultLocations"/>).
    /// </summary>
    public static WiiUKeys Load(string? path, IEnumerable<string>? searchPaths = null)
    {
        var files = string.IsNullOrWhiteSpace(path)
            ? (searchPaths ?? DefaultLocations()).Where(File.Exists).Distinct(StringComparer.Ordinal).ToList()
            : File.Exists(path) ? [path] : [];
        var keys = new List<byte[]>();
        var problems = new List<string>();
        if (!string.IsNullOrWhiteSpace(path) && files.Count == 0)
            problems.Add($"{path} doesn't exist.");
        foreach (var file in files)
        {
            var lineNumber = 0;
            foreach (var raw in File.ReadLines(file))
            {
                lineNumber++;
                var line = raw;
                var cut = line.IndexOfAny(['#', ';']);
                if (cut >= 0) line = line[..cut];
                line = new string(line.Where(c => c is not (' ' or '\t' or '-' or '_')).ToArray());
                if (line.Length == 0) continue;
                if (line.Length != 32 || !line.All(Uri.IsHexDigit))
                {
                    problems.Add($"{Path.GetFileName(file)} line {lineNumber} isn't a 32-digit hex key.");
                    continue;
                }
                var key = Convert.FromHexString(line);
                if (!keys.Any(k => k.AsSpan().SequenceEqual(key))) keys.Add(key);
            }
        }
        return new WiiUKeys(keys, files, problems);
    }

    /// <summary>Wraps keys given directly (tests, or a .key file next to a disc image).</summary>
    public static WiiUKeys FromKeys(IEnumerable<byte[]> keys) => new(keys.ToList(), [], []);

    public WiiUKeys With(IEnumerable<byte[]> extra) =>
        new(_keys.Concat(extra.Where(e => !_keys.Any(k => k.AsSpan().SequenceEqual(e)))).ToList(), Files.ToList(), Problems.ToList());

    public string Describe() => Files.Count == 0
        ? "no keys.txt found"
        : $"{_keys.Count} key(s) from {string.Join(", ", Files)}";
}

/// <summary>
/// Thrown when an encrypted title or disc image is opened with no keys at all
/// (no keys.txt found, and no .key file next to a disc image).
/// </summary>
public sealed class WiiUKeysMissingException(string message) : KeyNotFoundException(message);

/// <summary>AES-128-CBC without padding, the only mode Wii U data uses.</summary>
internal static class WiiUCrypto
{
    [ThreadStatic] private static Aes? _aes;

    public static void DecryptCbc(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> input, Span<byte> output)
    {
        var aes = _aes ??= Aes.Create();
        aes.Key = key.ToArray();
        aes.DecryptCbc(input, iv, output, PaddingMode.None);
    }

    public static byte[] DecryptCbc(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> input)
    {
        var output = new byte[input.Length];
        DecryptCbc(key, iv, input, output);
        return output;
    }

    /// <summary>The IV a ticket's title key is encrypted with: the title ID, big-endian, then 8 zero bytes.</summary>
    public static byte[] TitleIdIv(ulong titleId)
    {
        var iv = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(iv, titleId);
        return iv;
    }

    public static string Hex(ulong value) => value.ToString("X16", CultureInfo.InvariantCulture);
}
