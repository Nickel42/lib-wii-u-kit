using Xunit;

namespace LibWiiUKit.Tests;

/// <summary>NUS titles and WUD/WUX disc images, under made-up keys.</summary>
[Collection("wiiu")]
public sealed class EncryptedTests(Samples s) : IDisposable
{
    private const string GameFolder = "0005000010303e00_v0", UpdateFolder = "0005000e10303e00_v16";

    private readonly Scratch _scratch = new();

    public void Dispose() => _scratch.Dispose();

    /// <summary>Builds the encrypted game from <paramref name="sources"/> and checks the .wua holds exactly <paramref name="expect"/>.</summary>
    private WuaBuildResult BuildAndCheck(IReadOnlyList<string> sources, WiiUKeys? keys, params (string Folder, string Tree)[] expect)
    {
        var outDir = _scratch.Dir("out-" + Guid.NewGuid().ToString("N")[..6]);
        var result = WuaBuilder.Build(sources, Samples.EncBase, outDir, new WuaBuildOptions { Keys = keys ?? s.Keys });
        Assert.Equal(Path.Combine(outDir, "Encrypted Game [0005000010303E00].wua"), result.Output);
        Assert.Single(Directory.GetFiles(outDir));
        var unpacked = Path.Combine(_scratch.Root, "unpacked-" + Path.GetFileName(outDir));
        WiiUSample.Unpack(result.Output, unpacked);
        foreach (var (folder, tree) in expect)
            Assert.Null(WiiUSample.SameTree(tree, Path.Combine(unpacked, folder)));
        Assert.Equal(expect.Select(e => e.Folder).Order(), Directory.GetDirectories(unpacked).Select(Path.GetFileName).Order());
        return result;
    }

    [Fact]
    public void Keys_txt_comments_separators_and_bad_lines()
    {
        var path = Path.Combine(_scratch.Root, "keys-format.txt");
        File.WriteAllText(path, $"# comment\n{Convert.ToHexString(s.EncKeys.Common).ToLowerInvariant()} # common\n\n" +
            $"{string.Join("-", Convert.ToHexString(s.EncKeys.Disc).Chunk(8).Select(c => new string(c)))} ; disc\n" +
            $"  {Convert.ToHexString(s.EncKeys.Common)}\t# again: a duplicate\n" +
            "not-a-key\n" +
            "0123456789abcdef\n");
        var k = WiiUKeys.Load(path);
        Assert.Equal(2, k.Keys.Count);
        Assert.Equal(s.EncKeys.Common, k.Keys[0]);
        Assert.Equal(s.EncKeys.Disc, k.Keys[1]);
        Assert.Equal(2, k.Problems.Count);
        Assert.Contains("line 6", k.Problems[0]);
        Assert.Contains("line 7", k.Problems[1]);
        Assert.Equal([path], k.Files);
        Assert.Contains("2 key(s)", k.Describe());
    }

    [Fact]
    public void Keys_txt_search_paths()
    {
        var a = Path.Combine(_scratch.Dir("a"), "keys.txt");
        var b = Path.Combine(_scratch.Dir("b"), "keys.txt");
        var missing = Path.Combine(_scratch.Root, "c", "keys.txt");
        EncryptedSample.WriteKeysTxt(a, s.EncKeys.Common);
        EncryptedSample.WriteKeysTxt(b, s.EncKeys.Disc, s.EncKeys.Common);

        var found = WiiUKeys.Load(null, [missing, a, b, a]);
        Assert.Equal([a, b], found.Files);
        Assert.Equal(2, found.Keys.Count);
        Assert.Empty(found.Problems);

        var none = WiiUKeys.Load("", [missing]);
        Assert.Empty(none.Keys);
        Assert.Empty(none.Problems);
        Assert.Equal("no keys.txt found", none.Describe());

        var explicitMissing = WiiUKeys.Load(missing, [a]);   // an explicit path wins over the search
        Assert.Empty(explicitMissing.Keys);
        Assert.Contains("doesn't exist", Assert.Single(explicitMissing.Problems));

        Assert.All(WiiUKeys.DefaultLocations(), p => Assert.EndsWith(Path.Combine("Cemu", "keys.txt"), p));
        Assert.NotEmpty(WiiUKeys.DefaultLocations());

        var more = WiiUKeys.FromKeys([s.EncKeys.Common]).With([s.EncKeys.Common, s.EncKeys.Disc]);
        Assert.Equal(2, more.Keys.Count);
    }

    [Fact]
    public void Nus_title_is_read()
    {
        Assert.True(WiiUEncrypted.IsNusFolder(s.NusGame));
        Assert.True(WiiUTitles.IsSource(s.NusGame));
        var t = Assert.Single(WiiUTitles.Read(s.NusGame, s.Keys));
        Assert.Equal(Samples.EncBase, t.TitleId);
        Assert.Equal(0u, t.Version);
        Assert.Equal("Encrypted Game", t.Name);
        Assert.Equal(WiiUSource.Nus, t.Source);
        Assert.True(t.IsEncrypted);
        Assert.Equal(16u, WiiUTitles.Read(s.NusUpdate, s.Keys).Single().Version);
        var files = Directory.GetFiles(s.EncTree, "*", SearchOption.AllDirectories);
        Assert.Equal(files.Sum(f => new FileInfo(f).Length), WuaBuilder.TitleSize(t, s.Keys));

        var tmd = WiiUTicket.ParseTmd(File.ReadAllBytes(Path.Combine(s.NusGame, "title.tmd")));
        Assert.Equal(Samples.EncBase, tmd.TitleId);
        Assert.Equal(3, tmd.Contents.Count);
        Assert.True(tmd.Contents[2].IsHashed);
        Assert.False(tmd.Contents[1].IsHashed);
        Assert.Throws<InvalidDataException>(() => WiiUTicket.ParseTmd(new byte[100]));
    }

    [Fact]
    public void Nus_decrypts_to_the_original_files() =>
        BuildAndCheck([s.NusGame], null, (GameFolder, s.EncTree));

    [Fact]
    public void Nus_game_and_update_become_one_wua()
    {
        var r = BuildAndCheck([s.NusUpdate, s.NusGame], null, (GameFolder, s.EncTree), (UpdateFolder, s.EncUpdateTree));
        Assert.Equal(WiiUTitleKind.Game, r.Titles[0].Kind);
    }

    [Fact]
    public void Encrypted_and_plain_sources_mix()
    {
        // The update as a plain folder, the game as an NUS title.
        BuildAndCheck([s.NusGame, s.EncUpdateTree], null, (GameFolder, s.EncTree), (UpdateFolder, s.EncUpdateTree));
    }

    [Fact]
    public void Wud_decrypts_the_game_partition()
    {
        Assert.True(WiiUEncrypted.IsDiscImage(s.Wud));
        var t = Assert.Single(WiiUTitles.Read(s.Wud, s.Keys));
        Assert.Equal(Samples.EncBase, t.TitleId);
        Assert.Equal(WiiUSource.Disc, t.Source);
        Assert.Equal("Encrypted Game", t.Name);
        BuildAndCheck([s.Wud], null, (GameFolder, s.EncTree));
    }

    [Fact]
    public void Wux_decompresses_and_decrypts()
    {
        var wux = Path.Combine(_scratch.Root, "game.wux");
        File.WriteAllBytes(wux, EncryptedSample.ToWux(s.WudBytes));
        Assert.True(new FileInfo(wux).Length < s.WudBytes.Length);
        Assert.True(WiiUEncrypted.IsDiscImage(wux));
        Assert.Equal([wux], WiiUEncrypted.ImageFiles(wux));
        BuildAndCheck([wux], null, (GameFolder, s.EncTree));
    }

    [Fact]
    public void Wud_split_into_parts()
    {
        var dir = _scratch.Dir("split");
        var half = s.WudBytes.Length / 2 / EncryptedSample.Sector * EncryptedSample.Sector;
        var part1 = Path.Combine(dir, "game_part1.wud");
        var part2 = Path.Combine(dir, "game_part2.wud");
        File.WriteAllBytes(part1, s.WudBytes[..half]);
        File.WriteAllBytes(part2, s.WudBytes[half..]);

        Assert.True(WiiUEncrypted.IsDiscImage(part1));
        Assert.False(WiiUEncrypted.IsDiscImage(part2));
        Assert.False(WiiUTitles.IsSource(part2));
        Assert.Equal([part1, part2], WiiUEncrypted.ImageFiles(part1));
        Assert.Equal([s.Wud], WiiUEncrypted.ImageFiles(s.Wud));
        BuildAndCheck([part1], null, (GameFolder, s.EncTree));
    }

    [Fact]
    public void Disc_key_can_come_from_game_key()
    {
        var dir = _scratch.Dir("keyfile");
        var wud = Path.Combine(dir, "game.wud");
        File.Copy(s.Wud, wud);
        File.WriteAllBytes(Path.Combine(dir, "game.key"), s.EncKeys.Disc);
        BuildAndCheck([wud], WiiUKeys.FromKeys([s.EncKeys.Common]), (GameFolder, s.EncTree));

        // Without game.key the common key alone can't open the disc.
        var bare = Path.Combine(_scratch.Dir("nokeyfile"), "game.wud");
        File.Copy(s.Wud, bare);
        var ex = Assert.Throws<KeyNotFoundException>(() => WiiUTitles.Read(bare, WiiUKeys.FromKeys([s.EncKeys.Common])));
        Assert.IsNotType<WiiUKeysMissingException>(ex);
    }

    [Fact]
    public void Wrong_keys_are_reported()
    {
        var wrong = WiiUKeys.FromKeys([new byte[16], Enumerable.Repeat((byte)0x5A, 16).ToArray()]);
        var dir = Path.Combine(_scratch.Root, "wrong");
        var nus = Assert.Throws<KeyNotFoundException>(() => WuaBuilder.Build([s.NusGame], Samples.EncBase, dir, new WuaBuildOptions { Keys = wrong }));
        Assert.IsNotType<WiiUKeysMissingException>(nus);
        Assert.Contains("keys.txt", nus.Message);
        var disc = Assert.Throws<KeyNotFoundException>(() => WuaBuilder.Build([s.Wud], Samples.EncBase, dir, new WuaBuildOptions { Keys = wrong }));
        Assert.IsNotType<WiiUKeysMissingException>(disc);
        Assert.False(Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any());
    }

    [Fact]
    public void Missing_keys_throw_WiiUKeysMissingException()
    {
        var dir = Path.Combine(_scratch.Root, "nokeys");
        Assert.Throws<WiiUKeysMissingException>(() => WuaBuilder.Build([s.NusGame], Samples.EncBase, dir));
        Assert.Throws<WiiUKeysMissingException>(() => WuaBuilder.Build([s.Wud], Samples.EncBase, dir, new WuaBuildOptions { Keys = WiiUKeys.Empty }));
        Assert.Throws<WiiUKeysMissingException>(() => WiiUTitles.Read(s.NusGame));
        Assert.Throws<WiiUKeysMissingException>(() => WuaBuilder.Games([s.Wud]));
        // It is still a KeyNotFoundException, for callers that catch that.
        Assert.IsAssignableFrom<KeyNotFoundException>(new WiiUKeysMissingException("x"));
        // Plain sources need no keys.
        Assert.Single(WuaBuilder.Games([s.EncTree]));
    }

    [Fact]
    public void Damaged_hashed_block_fails_and_leaves_no_temp_file()
    {
        var bad = Path.Combine(_scratch.Root, "nus-damaged");
        WiiUSample.CopyTree(s.NusGame, bad);
        var app = Path.Combine(bad, "00000002.app");
        var bytes = File.ReadAllBytes(app);
        bytes[0x400 + 100] ^= 0x01;
        File.WriteAllBytes(app, bytes);

        var dir = _scratch.Dir("damaged-out");
        var ex = Assert.Throws<InvalidDataException>(() => WuaBuilder.Build([bad], Samples.EncBase, dir, new WuaBuildOptions { Keys = s.Keys }));
        Assert.Contains("hash", ex.Message);
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public void Damaged_raw_content_fails_the_tmd_check()
    {
        var bad = Path.Combine(_scratch.Root, "nus-raw-damaged");
        WiiUSample.CopyTree(s.NusGame, bad);
        var app = Path.Combine(bad, "00000001.app");
        var bytes = File.ReadAllBytes(app);
        bytes[4000] ^= 0x01;   // inside code/game.rpx, after code/app.xml
        File.WriteAllBytes(app, bytes);

        var dir = _scratch.Dir("raw-damaged-out");
        var ex = Assert.Throws<InvalidDataException>(() => WuaBuilder.Build([bad], Samples.EncBase, dir, new WuaBuildOptions { Keys = s.Keys }));
        Assert.Contains("TMD", ex.Message);
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public void Missing_app_file_fails()
    {
        var bad = Path.Combine(_scratch.Root, "nus-missing-app");
        WiiUSample.CopyTree(s.NusGame, bad);
        File.Delete(Path.Combine(bad, "00000002.app"));
        var dir = _scratch.Dir("missing-app-out");
        Assert.Throws<FileNotFoundException>(() => WuaBuilder.Build([bad], Samples.EncBase, dir, new WuaBuildOptions { Keys = s.Keys }));
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public void Personalised_ticket_is_refused()
    {
        var personal = EncryptedSample.MakeNus(s.EncTree, Path.Combine(_scratch.Root, "nus-personal"), Samples.EncBase, 0, s.EncKeys, deviceId: 0x12345678);
        var ex = Assert.Throws<NotSupportedException>(() => WiiUTitles.Read(personal, s.Keys));
        Assert.Contains("personalised", ex.Message);
    }

    [Fact]
    public void Cancelling_an_encrypted_build_leaves_nothing_behind()
    {
        var dir = _scratch.Dir("enc-cancel");
        using var cts = new CancellationTokenSource();
        var options = new WuaBuildOptions
        {
            Keys = s.Keys,
            CancellationToken = cts.Token,
            Stage = st => { if (st.StartsWith("Checking", StringComparison.Ordinal)) cts.Cancel(); },
        };
        Assert.ThrowsAny<OperationCanceledException>(() => WuaBuilder.Build([s.NusGame], Samples.EncBase, dir, options));
        Assert.Empty(Directory.GetFiles(dir));
    }
}
