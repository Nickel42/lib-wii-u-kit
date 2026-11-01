using Xunit;

namespace LibWiiUKit.Tests;

[Collection("wiiu")]
public sealed class WiiUTitleTests(Samples s) : IDisposable
{
    private readonly Scratch _scratch = new();

    public void Dispose() => _scratch.Dispose();

    [Fact]
    public void Title_folders_are_recognised()
    {
        var t = WiiUTitles.FromFolder(s.Update);
        Assert.Equal(Samples.UpdateId, t.TitleId);
        Assert.Equal(32u, t.Version);
        Assert.Equal(WiiUTitleKind.Update, t.Kind);
        Assert.Equal(Samples.BaseId, t.BaseId);
        Assert.Equal("0005000e10101c00_v32", t.ArchiveFolder);
        Assert.Equal("Test Game", t.Name);
        Assert.Equal(WiiUSource.Folder, t.Source);
        Assert.False(t.FromWua);
        Assert.False(t.IsEncrypted);
        Assert.Equal("update", t.KindText);

        Assert.True(WiiUTitles.IsTitleFolder(s.Game));
        Assert.True(WiiUTitles.IsSource(s.Game));
        Assert.Equal(WiiUTitleKind.Dlc, WiiUTitles.Read(s.Dlc).Single().Kind);
        Assert.Equal(WiiUTitleKind.Game, WiiUTitles.Read(s.Game).Single().Kind);
    }

    [Fact]
    public void Title_kinds_follow_the_title_id()
    {
        WiiUTitle T(ulong id) => new(id, 0, "", "", null);
        Assert.Equal(WiiUTitleKind.Demo, T(0x0005000210101C00).Kind);
        Assert.Equal(WiiUTitleKind.Other, T(0x0005001010101C00).Kind);
        Assert.Equal(0x0005000010101C00UL, T(0x0005000C10101C00).BaseId);
    }

    [Fact]
    public void Things_that_are_not_titles_are_refused()
    {
        var empty = _scratch.Dir("empty");
        Assert.False(WiiUTitles.IsTitleFolder(empty));
        Assert.False(WiiUTitles.IsSource(empty));
        Assert.Throws<InvalidDataException>(() => WiiUTitles.FromFolder(empty));

        var text = Path.Combine(_scratch.Root, "readme.txt");
        File.WriteAllText(text, "hi");
        Assert.False(WiiUTitles.IsSource(text));
        Assert.Throws<InvalidDataException>(() => WiiUTitles.Read(text));

        // A ZArchive with no <titleid>_v<version> folders isn't a .wua.
        var zar = Path.Combine(_scratch.Root, "other.wua");
        using (var w = new ZArchiveWriter(zar))
        {
            w.AddDirectory("not-a-title");
            w.AddFile("not-a-title/x.bin", new MemoryStream([1, 2, 3]));
            w.Finish();
        }
        Assert.Throws<InvalidDataException>(() => WiiUTitles.FromWua(zar));
    }

    [Fact]
    public void Inputs_it_cannot_use_are_named()
    {
        var nus = _scratch.Dir("nus-without-ticket");
        File.WriteAllBytes(Path.Combine(nus, "title.tmd"), [0]);
        var wuhb = Path.Combine(_scratch.Root, "app.wuhb");
        File.WriteAllBytes(wuhb, [0]);
        Assert.Contains("title.tik", WiiUTitles.UnsupportedReason(nus));
        Assert.Contains("WUHB", WiiUTitles.UnsupportedReason(wuhb));
        Assert.Null(WiiUTitles.UnsupportedReason(s.Game));
        Assert.Null(WiiUTitles.UnsupportedReason(s.NusGame));
        Assert.False(WiiUEncrypted.IsNusFolder(nus));
        Assert.False(WiiUTitles.IsSource(nus));
    }

    [Theory]
    [InlineData("Test Game", "Test Game [0005000010101C00].wua")]
    [InlineData("Zelda: Breath/of  the\tWild...", "Zelda Breath of the Wild [0005000010101C00].wua")]
    [InlineData("  <A|B>?*\"  ", "A B [0005000010101C00].wua")]
    [InlineData("", "0005000010101C00.wua")]
    [InlineData(":/?", "0005000010101C00.wua")]
    public void WuaFileName_is_safe_everywhere(string name, string expected) =>
        Assert.Equal(expected, WiiUTitles.WuaFileName(Samples.BaseId, name));

    [Fact]
    public void Newest_version_of_each_title_is_chosen()
    {
        var newer = WiiUSample.MakeTitle(Path.Combine(_scratch.Root, "Update v48"), Samples.UpdateId, 48, "Test Game", 4);
        var other = WiiUSample.MakeTitle(Path.Combine(_scratch.Root, "Other"), 0x0005000010202D00, 0, "Other Game", 5);
        var titles = new[] { s.Update, newer, s.Dlc, s.Game, other }.SelectMany(p => WiiUTitles.Read(p)).ToList();
        var log = new List<(WuaLogLevel Level, string Text)>();
        var chosen = WuaBuilder.ChooseTitles(titles, Samples.BaseId, (l, m) => log.Add((l, m)));
        Assert.Equal(["0005000010101c00_v0", "0005000e10101c00_v48", "0005000c10101c00_v16"], chosen.Select(t => t.ArchiveFolder));
        var warning = Assert.Single(log, e => e.Level == WuaLogLevel.Warning);
        Assert.Contains("v48", warning.Text);
        Assert.Contains("v32", warning.Text);

        Assert.Equal([Samples.BaseId, 0x0005000010202D00UL],
            WuaBuilder.Games([s.Update, newer, s.Dlc, s.Game, other]).Order());
        Assert.Equal("Test Game", WuaBuilder.GameName(chosen));
        Assert.Equal("", WuaBuilder.GameName([]));
    }

    [Fact]
    public void An_update_alone_is_warned_about()
    {
        var log = new List<(WuaLogLevel Level, string Text)>();
        var chosen = WuaBuilder.ChooseTitles(WiiUTitles.Read(s.Update), Samples.BaseId, (l, m) => log.Add((l, m)));
        Assert.Single(chosen);
        Assert.Contains(log, e => e.Level == WuaLogLevel.Warning && e.Text.Contains("only an update"));
        Assert.Empty(WuaBuilder.ChooseTitles(WiiUTitles.Read(s.Update), 0x0005000010202D00));
    }

    [Fact]
    public void Game_name_falls_back_to_any_title()
    {
        var update = new WiiUTitle(Samples.UpdateId, 32, "From Update", "", null);
        var nameless = new WiiUTitle(Samples.BaseId, 0, "", "", null);
        Assert.Equal("From Update", WuaBuilder.GameName([nameless, update]));
        Assert.Equal("Base", WuaBuilder.GameName([update, nameless with { Name = "Base" }]));
    }
}

[Collection("wiiu")]
public sealed class WuaBuilderTests(Samples s) : IDisposable
{
    private readonly Scratch _scratch = new();

    public void Dispose() => _scratch.Dispose();

    private static void NoTempFiles(string dir) =>
        Assert.Empty(Directory.EnumerateFiles(dir, "*.tmp", SearchOption.AllDirectories));

    [Fact]
    public void Base_update_and_DLC_folders_become_one_wua()
    {
        var outDir = _scratch.Dir("out");
        var stages = new List<string>();
        var progress = new List<double>();
        var result = WuaBuilder.Build([s.Game, s.Update, s.Dlc], Samples.BaseId, outDir,
            new WuaBuildOptions { Stage = stages.Add, Progress = progress.Add });

        var wua = Path.Combine(outDir, "Test Game [0005000010101C00].wua");
        Assert.Equal(wua, result.Output);
        Assert.True(File.Exists(wua));
        Assert.Equal(new FileInfo(wua).Length, result.OutputBytes);
        Assert.Equal(3, result.Titles.Count);
        Assert.Equal(WiiUTitleKind.Game, result.Titles[0].Kind);
        var sources = new[] { s.Game, s.Update, s.Dlc };
        Assert.Equal(sources.Sum(d => Directory.GetFiles(d, "*", SearchOption.AllDirectories).Length), result.FileCount);
        Assert.Equal(sources.Sum(d => Directory.GetFiles(d, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)), result.InputBytes);
        Assert.Equal(result.InputBytes, result.Titles.Sum(t => WuaBuilder.TitleSize(t)));
        Assert.InRange(result.Ratio, 0.01, 1.0);
        Assert.Equal(["Packing", "Finishing", "Verifying"], stages);
        Assert.All(progress, p => Assert.InRange(p, 0, 1));
        Assert.Equal(1, progress[^1]);
        NoTempFiles(outDir);

        var unpacked = Path.Combine(_scratch.Root, "unpacked");
        WiiUSample.Unpack(wua, unpacked);
        var expected = Path.Combine(_scratch.Root, "expected");
        WiiUSample.CopyTree(s.Game, Path.Combine(expected, "0005000010101c00_v0"));
        WiiUSample.CopyTree(s.Update, Path.Combine(expected, "0005000e10101c00_v32"));
        WiiUSample.CopyTree(s.Dlc, Path.Combine(expected, "0005000c10101c00_v16"));
        Assert.Null(WiiUSample.SameTree(expected, unpacked));

        // The .wua reads back as the same three titles.
        var read = WiiUTitles.Read(wua);
        Assert.Equal(["0005000010101c00_v0", "0005000c10101c00_v16", "0005000e10101c00_v32"], read.Select(t => t.ArchiveFolder).Order());
        Assert.All(read, t => Assert.Equal(WiiUSource.Wua, t.Source));
        Assert.All(read, t => Assert.Equal("Test Game", t.Name));
        Assert.True(WiiUTitles.IsSource(wua));
        Assert.Equal(result.InputBytes, read.Sum(t => WuaBuilder.TitleSize(t)));
    }

    [Fact]
    public void A_newer_update_is_merged_into_an_existing_wua()
    {
        var dir = _scratch.Dir("merge");
        var first = WuaBuilder.Build([s.Game, s.Update, s.Dlc], Samples.BaseId, dir);
        var newer = WiiUSample.MakeTitle(Path.Combine(_scratch.Root, "Update v48"), Samples.UpdateId, 48, "Test Game", 4);

        var log = new List<(WuaLogLevel Level, string Text)>();
        var merged = WuaBuilder.Build([first.Output, newer], Samples.BaseId, dir, new WuaBuildOptions { Log = (l, m) => log.Add((l, m)) });
        Assert.Equal(first.Output, merged.Output);
        Assert.Equal(["0005000010101c00_v0", "0005000c10101c00_v16", "0005000e10101c00_v48"],
            WiiUTitles.FromWua(merged.Output).Select(t => t.ArchiveFolder).Order());
        Assert.Contains(log, e => e.Level == WuaLogLevel.Warning && e.Text.Contains("v48"));
        Assert.Single(Directory.GetFiles(dir));
        NoTempFiles(dir);

        var unpacked = Path.Combine(_scratch.Root, "unpacked");
        WiiUSample.Unpack(merged.Output, unpacked);
        Assert.Null(WiiUSample.SameTree(newer, Path.Combine(unpacked, "0005000e10101c00_v48")));
        Assert.Null(WiiUSample.SameTree(s.Game, Path.Combine(unpacked, "0005000010101c00_v0")));
        Assert.Null(WiiUSample.SameTree(s.Dlc, Path.Combine(unpacked, "0005000c10101c00_v16")));
    }

    [Fact]
    public void An_existing_file_of_that_name_is_replaced()
    {
        var dir = _scratch.Dir("replace");
        var target = Path.Combine(dir, "Test Game [0005000010101C00].wua");
        File.WriteAllText(target, "old");
        var result = WuaBuilder.Build([s.Game], Samples.BaseId, dir);
        Assert.Equal(target, result.Output);
        Assert.Single(WiiUTitles.FromWua(target));
    }

    [Fact]
    public void No_titles_for_the_game_is_an_error()
    {
        var dir = Path.Combine(_scratch.Root, "none");
        Assert.Throws<InvalidDataException>(() => WuaBuilder.Build([s.Game], 0x0005000010202D00, dir));
        Assert.False(Directory.Exists(dir));
    }

    [Theory]
    [InlineData("Packing")]
    [InlineData("Verifying")]
    public void Cancelling_leaves_nothing_behind(string stage)
    {
        var dir = _scratch.Dir("cancel");
        using var cts = new CancellationTokenSource();
        var options = new WuaBuildOptions
        {
            CancellationToken = cts.Token,
            Stage = st => { if (st == stage) cts.Cancel(); },
        };
        Assert.ThrowsAny<OperationCanceledException>(() => WuaBuilder.Build([s.Game, s.Update], Samples.BaseId, dir, options));
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public void A_cancelled_merge_keeps_the_existing_wua()
    {
        var dir = _scratch.Dir("cancel-merge");
        var first = WuaBuilder.Build([s.Game], Samples.BaseId, dir);
        var before = File.ReadAllBytes(first.Output);
        using var cts = new CancellationTokenSource();
        var options = new WuaBuildOptions { CancellationToken = cts.Token, Stage = st => { if (st == "Verifying") cts.Cancel(); } };
        Assert.ThrowsAny<OperationCanceledException>(() => WuaBuilder.Build([first.Output, s.Update], Samples.BaseId, dir, options));
        Assert.Equal([first.Output], Directory.GetFiles(dir));
        Assert.Equal(before, File.ReadAllBytes(first.Output));
    }

    [Fact]
    public void Build_to_an_explicit_path()
    {
        var titles = WuaBuilder.ChooseTitles(new[] { s.Game, s.Dlc }.SelectMany(p => WiiUTitles.Read(p)), Samples.BaseId);
        var output = Path.Combine(_scratch.Root, "custom name.wua");
        var debug = new List<string>();
        var result = WuaBuilder.Build(output, titles, new WuaBuildOptions { Log = (l, m) => { if (l == WuaLogLevel.Debug) debug.Add(m); } });
        Assert.Equal(output, result.Output);
        Assert.Equal(2, WiiUTitles.FromWua(output).Count);
        Assert.Equal(2, debug.Count);
    }
}
