using System.Text;

namespace LibWiiUKit.Tests;

/// <summary>
/// Synthetic Wii U titles for tests: the code/content/meta layout with the
/// app.xml and meta.xml fields the library reads, and deterministic file
/// contents. No real game data.
/// </summary>
internal static class WiiUSample
{
    public static string MakeTitle(string folder, ulong titleId, uint version, string name, int seed)
    {
        var rng = new Random(seed);
        Directory.CreateDirectory(Path.Combine(folder, "code"));
        Directory.CreateDirectory(Path.Combine(folder, "meta"));
        Directory.CreateDirectory(Path.Combine(folder, "content", "Common", "Sound"));
        Directory.CreateDirectory(Path.Combine(folder, "content", "Empty Folder"));

        File.WriteAllText(Path.Combine(folder, "code", "app.xml"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <app type="complex" access="777">
              <version type="unsignedInt" length="4">15</version>
              <os_version type="hexBinary" length="8">000500101000400A</os_version>
              <title_id type="hexBinary" length="8">{titleId:X16}</title_id>
              <title_version type="unsignedInt" length="4">{version}</title_version>
            </app>
            """);
        File.WriteAllText(Path.Combine(folder, "meta", "meta.xml"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <menu type="complex" access="777">
              <title_id type="hexBinary" length="8">{titleId:X16}</title_id>
              <title_version type="unsignedInt" length="4">{version}</title_version>
              <longname_en type="string" length="512">{name}</longname_en>
              <shortname_en type="string" length="256">{name}</shortname_en>
            </menu>
            """);

        File.WriteAllBytes(Path.Combine(folder, "code", "game.rpx"), Random(rng, 150_000 + seed * 1000));
        File.WriteAllBytes(Path.Combine(folder, "meta", "iconTex.tga"), Random(rng, 4096));
        File.WriteAllBytes(Path.Combine(folder, "content", "Common", "Level01.pack"), Text(200_000 + seed));
        File.WriteAllBytes(Path.Combine(folder, "content", "Common", "Sound", "Music.bfstm"), Random(rng, 90_000));
        File.WriteAllBytes(Path.Combine(folder, "content", "Common", "Sound", "empty.bin"), []);
        File.WriteAllBytes(Path.Combine(folder, "content", $"Title{seed}.txt"), Encoding.ASCII.GetBytes($"{name} {version}"));
        return folder;
    }

    /// <summary>Unpacks a .wua with the library's reader (empty folders included).</summary>
    public static void Unpack(string archive, string destination)
    {
        using var reader = ZArchiveReader.Open(archive);
        foreach (var entry in reader.Walk())
        {
            var target = Path.Combine(destination, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!entry.IsFile)
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var fs = File.Create(target);
            reader.CopyFile(entry, fs);
        }
    }

    public static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    /// <summary>
    /// Null when the two folders hold the same folders and files with the same
    /// bytes; otherwise what differs.
    /// </summary>
    public static string? SameTree(string expected, string actual)
    {
        if (!Directory.Exists(actual)) return $"{actual} doesn't exist";
        static List<string> Dirs(string root) => Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Select(d => Path.GetRelativePath(root, d).Replace('\\', '/')).OrderBy(d => d, StringComparer.Ordinal).ToList();
        static List<string> Files(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal).ToList();
        var (ed, ad) = (Dirs(expected), Dirs(actual));
        if (!ed.SequenceEqual(ad)) return $"folders differ: [{string.Join(", ", ed)}] vs [{string.Join(", ", ad)}]";
        var (ef, af) = (Files(expected), Files(actual));
        if (!ef.SequenceEqual(af)) return $"files differ: [{string.Join(", ", ef)}] vs [{string.Join(", ", af)}]";
        foreach (var f in ef)
            if (!File.ReadAllBytes(Path.Combine(expected, f)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(actual, f))))
                return $"{f} differs";
        return null;
    }

    public static byte[] Random(Random rng, int length)
    {
        var data = new byte[length];
        rng.NextBytes(data);
        return data;
    }

    public static byte[] Text(int length)
    {
        var line = Encoding.ASCII.GetBytes("lib-wii-u-kit sample data, compressible on purpose.\n");
        var data = new byte[length];
        for (var i = 0; i < length; i++) data[i] = line[i % line.Length];
        return data;
    }
}
