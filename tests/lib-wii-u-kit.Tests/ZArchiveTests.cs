using Xunit;

namespace LibWiiUKit.Tests;

public sealed class ZArchiveTests : IDisposable
{
    private readonly Scratch _scratch = new();

    public void Dispose() => _scratch.Dispose();

    /// <summary>A small archive: raw and compressed blocks, an empty file, an empty folder, more than 16 blocks.</summary>
    private (string Archive, Dictionary<string, byte[]> Files) Write()
    {
        var archive = Path.Combine(_scratch.Root, "t.zar");
        var rng = new Random(7);
        var files = new Dictionary<string, byte[]>
        {
            ["b/Zeta.bin"] = WiiUSample.Random(rng, 300_000),           // incompressible: raw blocks
            ["b/alpha.txt"] = WiiUSample.Text(400_000),                 // compressible
            ["B/sub/empty.dat"] = [],
            ["a.txt"] = WiiUSample.Text(10),
            ["big/many.bin"] = WiiUSample.Text(17 * 65536 + 123),       // more than 16 blocks: two offset records
        };
        for (var i = 0; i < 40; i++) files[$"many/f{i:00}.bin"] = WiiUSample.Random(rng, rng.Next(0, 5000));
        using var w = new ZArchiveWriter(archive);
        foreach (var (path, data) in files)
        {
            var slash = path.LastIndexOf('/');
            if (slash > 0) w.AddDirectory(path[..slash]);
            w.AddFile(path, new MemoryStream(data));
        }
        w.AddDirectory("emptyfolder");
        w.Finish();
        return (archive, files);
    }

    [Fact]
    public void Round_trip()
    {
        var (archive, files) = Write();
        using var r = ZArchiveReader.Open(archive);
        Assert.True(r.CheckIntegrity());
        Assert.Equal(["a.txt", "b", "big", "emptyfolder", "many"], r.Children(r.Root).Select(e => e.Name));
        foreach (var (path, data) in files)
        {
            var e = r.Find(path.Replace("B/", "b/"));
            Assert.NotNull(e);
            Assert.True(e.IsFile);
            Assert.Equal((ulong)data.Length, e.Size);
            var ms = new MemoryStream();
            r.CopyFile(e, ms);
            Assert.True(ms.ToArray().AsSpan().SequenceEqual(data), $"{path} reads back differently");
        }
        var empty = r.Find("emptyfolder");
        Assert.NotNull(empty);
        Assert.False(empty.IsFile);
        Assert.Empty(r.Children(empty));
        Assert.Equal(files.Count, r.Walk().Count(e => e.IsFile));
    }

    [Fact]
    public void Find_is_case_insensitive()
    {
        var (archive, _) = Write();
        using var r = ZArchiveReader.Open(archive);
        Assert.NotNull(r.Find("B/ALPHA.TXT"));
        Assert.NotNull(r.Find("/b\\sub/Empty.dat"));
        Assert.Null(r.Find("b/missing.txt"));
    }

    [Fact]
    public void Writes_to_a_stream()
    {
        var ms = new MemoryStream();
        using (var w = new ZArchiveWriter(ms))
        {
            w.AddDirectory("d");
            w.AddFile("d/x.bin", new MemoryStream(WiiUSample.Text(1000)));
            w.AddFile("d/y.bin", s => s.Write(WiiUSample.Text(70_000)));
            w.Finish();
            Assert.Equal(2UL * 65536, w.InputBytes); // 71,000 bytes, padded to whole 64 KiB blocks
        }
        var path = Path.Combine(_scratch.Root, "s.zar");
        File.WriteAllBytes(path, ms.ToArray());
        using var r = ZArchiveReader.Open(path);
        Assert.True(r.CheckIntegrity());
        var y = r.Find("d/y.bin")!;
        var outMs = new MemoryStream();
        r.CopyFile(y, outMs);
        Assert.Equal(WiiUSample.Text(70_000), outMs.ToArray());
    }

    [Fact]
    public void Damage_is_caught_by_the_integrity_check()
    {
        var (archive, _) = Write();
        var bytes = File.ReadAllBytes(archive);
        bytes[1000] ^= 0x40;
        var bad = Path.Combine(_scratch.Root, "bad.zar");
        File.WriteAllBytes(bad, bytes);
        using var r = ZArchiveReader.Open(bad);
        Assert.False(r.CheckIntegrity());
    }

    [Fact]
    public void Verify_reports_damage()
    {
        var (archive, files) = Write();
        var expected = files.ToDictionary(kv => kv.Key.Replace("B/", "b/"), kv => System.Security.Cryptography.SHA256.HashData(kv.Value));
        WuaBuilder.Verify(archive, expected);

        var wrong = new Dictionary<string, byte[]>(expected) { ["a.txt"] = new byte[32] };
        Assert.Throws<ZArchiveException>(() => WuaBuilder.Verify(archive, wrong));
        var fewer = new Dictionary<string, byte[]>(expected);
        fewer.Remove("a.txt");
        Assert.Throws<ZArchiveException>(() => WuaBuilder.Verify(archive, fewer));
        var more = new Dictionary<string, byte[]>(expected) { ["extra.bin"] = new byte[32] };
        Assert.Throws<ZArchiveException>(() => WuaBuilder.Verify(archive, more));

        var bytes = File.ReadAllBytes(archive);
        bytes[1000] ^= 0x40;
        File.WriteAllBytes(archive, bytes);
        Assert.Throws<ZArchiveException>(() => WuaBuilder.Verify(archive, expected));
    }

    [Fact]
    public void Truncated_or_foreign_files_are_refused()
    {
        var (archive, _) = Write();
        var bytes = File.ReadAllBytes(archive);
        var truncated = Path.Combine(_scratch.Root, "short.zar");
        File.WriteAllBytes(truncated, bytes[..^1000]);
        Assert.Throws<ZArchiveException>(() => ZArchiveReader.Open(truncated).Dispose());

        var padded = Path.Combine(_scratch.Root, "long.zar");
        File.WriteAllBytes(padded, [.. bytes, 0]);
        Assert.Throws<ZArchiveException>(() => ZArchiveReader.Open(padded).Dispose());

        var tiny = Path.Combine(_scratch.Root, "tiny.zar");
        File.WriteAllBytes(tiny, new byte[10]);
        Assert.Throws<ZArchiveException>(() => ZArchiveReader.Open(tiny).Dispose());

        var text = Path.Combine(_scratch.Root, "text.wua");
        File.WriteAllText(text, new string('x', 4000));
        Assert.Throws<ZArchiveException>(() => ZArchiveReader.Open(text).Dispose());
    }

    [Fact]
    public void Backslashes_separate_folders_too()
    {
        var path = Path.Combine(_scratch.Root, "bs.zar");
        using (var w = new ZArchiveWriter(path))
        {
            w.AddDirectory(@"x\y");
            w.AddFile(@"x\y\z.bin", new MemoryStream([1, 2, 3]));
            w.Finish();
        }
        using var r = ZArchiveReader.Open(path);
        Assert.Equal(["x"], r.Children(r.Root).Select(e => e.Name));
        var z = r.Find("x/y/z.bin");
        Assert.NotNull(z);
        Assert.Equal("x/y/z.bin", z.Path);
    }

    [Fact]
    public void Writer_refuses_bad_paths()
    {
        using var w = new ZArchiveWriter(new MemoryStream());
        w.AddDirectory("d");
        w.AddFile("d/a.bin", new MemoryStream([1]));
        Assert.Throws<ZArchiveException>(() => w.AddFile("D/A.BIN", new MemoryStream([2])));   // names are case-insensitive
        Assert.Throws<ZArchiveException>(() => w.AddFile("missing/a.bin", new MemoryStream([1])));
        Assert.Throws<ZArchiveException>(() => w.AddDirectory("d/a.bin/sub"));
        Assert.Throws<ZArchiveException>(() => w.AddFile("", new MemoryStream([1])));
        Assert.Throws<ZArchiveException>(() => w.AddFile("d/日本.bin", new MemoryStream([1]))); // not Windows-1252
        w.AddFile("d/café.bin", new MemoryStream([1]));                                     // Windows-1252 is fine
    }

    [Fact]
    public void Writing_does_not_overwrite_an_existing_file()
    {
        var path = Path.Combine(_scratch.Root, "exists.zar");
        File.WriteAllText(path, "mine");
        Assert.Throws<IOException>(() => new ZArchiveWriter(path).Dispose());
        Assert.Equal("mine", File.ReadAllText(path));
    }
}
