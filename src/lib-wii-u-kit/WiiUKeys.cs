using System.Globalization;
using System.Security.Cryptography;

namespace LibWiiUKit;

/// <summary>
/// The AES-128 keys from the user's own keys.txt (Cemu's format: one key of
/// 32 hex digits per line; anything after '#' or ';' is a comment; spaces,
/// tabs, '-' and '_' are ignored). The library ships no keys. Which line is
/// which doesn't matter: every key is tried, as a disc key, a common key or
/// a title key, and the one that decrypts to a valid header is used.
/// A line can also be a title key list's: a 16-digit title ID, then that
/// title's 32-digit key (<c>0005000010101C00	&lt;key&gt;  # Game  USA</c>).
/// Those keys are kept by title (<see cref="TitleKeys"/>) and tried first for
/// their own title, so a title opens with its key even without the common key.
/// </summary>
public sealed class WiiUKeys
{
    private readonly List<byte[]> _keys;
    private readonly Dictionary<ulong, byte[]> _titleKeys;

    public IReadOnlyList<byte[]> Keys => _keys;

    /// <summary>Title keys from "title ID + key" lines, by title ID.</summary>
    public IReadOnlyDictionary<ulong, byte[]> TitleKeys => _titleKeys;

    public IReadOnlyList<string> Files { get; }
    public IReadOnlyList<string> Problems { get; }

    /// <summary>Whether there's any key at all, plain or by title.</summary>
    public bool IsEmpty => _keys.Count == 0 && _titleKeys.Count == 0;

    private WiiUKeys(List<byte[]> keys, List<string> files, List<string> problems, Dictionary<ulong, byte[]>? titleKeys = null)
    {
        _keys = keys;
        _titleKeys = titleKeys ?? [];
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
        var titleKeys = new Dictionary<ulong, byte[]>();
        var problems = new List<string>();
        if (!string.IsNullOrWhiteSpace(path) && files.Count == 0)
            problems.Add($"{path} doesn't exist.");
        foreach (var file in files)
        {
            var lineNumber = 0;
            var bad = new List<int>();
            foreach (var raw in File.ReadLines(file))
            {
                lineNumber++;
                var line = raw;
                var cut = line.IndexOfAny(['#', ';']);
                if (cut >= 0) line = line[..cut];
                line = new string(line.Where(c => c is not (' ' or '\t' or '-' or '_')).ToArray());
                if (line.Length == 0) continue;
                if (!line.All(Uri.IsHexDigit) || line.Length is not (32 or 48))
                {
                    bad.Add(lineNumber);
                    continue;
                }
                if (line.Length == 48)
                {
                    // A title key list's line: the title ID, then its key. The first line for a title wins.
                    titleKeys.TryAdd(ulong.Parse(line[..16], NumberStyles.HexNumber, CultureInfo.InvariantCulture), Convert.FromHexString(line[16..]));
                    continue;
                }
                var key = Convert.FromHexString(line);
                if (!keys.Any(k => k.AsSpan().SequenceEqual(key))) keys.Add(key);
            }
            // One problem per file, however many lines: a list of thousands of warnings helps nobody.
            if (bad.Count == 1)
                problems.Add($"{Path.GetFileName(file)} line {bad[0]} isn't a key (32 hex digits, or a 16-digit title ID and its 32-digit key).");
            else if (bad.Count > 1)
                problems.Add($"{Path.GetFileName(file)}: {bad.Count} lines aren't keys (32 hex digits, or a 16-digit title ID and its 32-digit key): " +
                             $"lines {string.Join(", ", bad.Take(5))}{(bad.Count > 5 ? $" and {bad.Count - 5} more" : "")}.");
        }
        return new WiiUKeys(keys, files, problems, titleKeys);
    }

    /// <summary>Wraps keys given directly (tests, or a .key file next to a disc image).</summary>
    public static WiiUKeys FromKeys(IEnumerable<byte[]> keys) => new(keys.ToList(), [], []);

    public WiiUKeys With(IEnumerable<byte[]> extra) =>
        new(_keys.Concat(extra.Where(e => !_keys.Any(k => k.AsSpan().SequenceEqual(e)))).ToList(), Files.ToList(), Problems.ToList(),
            new Dictionary<ulong, byte[]>(_titleKeys));

    public string Describe() => Files.Count == 0
        ? "no keys.txt found"
        : $"{_keys.Count} key(s){(_titleKeys.Count > 0 ? $" and {_titleKeys.Count} title key(s)" : "")} from {string.Join(", ", Files)}";
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
