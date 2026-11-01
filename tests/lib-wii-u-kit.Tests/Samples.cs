using Xunit;

namespace LibWiiUKit.Tests;

/// <summary>A scratch folder removed afterwards.</summary>
public sealed class Scratch : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("wiiu-kit-").FullName;

    public string Dir(string name) => Directory.CreateDirectory(Path.Combine(Root, name)).FullName;

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// The synthetic titles every Wii U test shares, made once: plain title
/// folders (a game, an update, a DLC), and an encrypted game and update as NUS
/// titles and a WUD image, all under made-up keys.
/// </summary>
public sealed class Samples : IDisposable
{
    public const ulong BaseId = 0x0005000010101C00, UpdateId = 0x0005000E10101C00, DlcId = 0x0005000C10101C00;
    public const ulong EncBase = 0x0005000010303E00, EncUpdate = 0x0005000E10303E00;

    private readonly Scratch _scratch = new();

    public string Root => _scratch.Root;
    public string Game { get; }
    public string Update { get; }
    public string Dlc { get; }

    internal EncryptedSample.Keys EncKeys { get; } = EncryptedSample.MakeKeys(42);
    public string KeysTxt { get; }
    public string EncTree { get; }
    public string EncUpdateTree { get; }
    public string NusGame { get; }
    public string NusUpdate { get; }
    public byte[] WudBytes { get; }
    public string Wud { get; }

    public Samples()
    {
        var plain = _scratch.Dir("titles");
        Game = WiiUSample.MakeTitle(Path.Combine(plain, "Game"), BaseId, 0, "Test Game", 1);
        Update = WiiUSample.MakeTitle(Path.Combine(plain, "Update v32"), UpdateId, 32, "Test Game", 2);
        Dlc = WiiUSample.MakeTitle(Path.Combine(plain, "DLC"), DlcId, 16, "Test Game", 3);

        var enc = _scratch.Dir("enc");
        KeysTxt = Path.Combine(enc, "keys.txt");
        EncryptedSample.WriteKeysTxt(KeysTxt, EncKeys.Disc, EncKeys.Common);
        EncTree = WiiUSample.MakeTitle(Path.Combine(enc, "plain-game"), EncBase, 0, "Encrypted Game", 21);
        EncUpdateTree = WiiUSample.MakeTitle(Path.Combine(enc, "plain-update"), EncUpdate, 16, "Encrypted Game", 22);
        NusGame = EncryptedSample.MakeNus(EncTree, Path.Combine(enc, "nus-game"), EncBase, 0, EncKeys);
        NusUpdate = EncryptedSample.MakeNus(EncUpdateTree, Path.Combine(enc, "nus-update"), EncUpdate, 16, EncKeys);
        WudBytes = EncryptedSample.MakeWud(EncTree, EncBase, EncKeys);
        Wud = Path.Combine(_scratch.Dir("wud"), "game.wud");
        File.WriteAllBytes(Wud, WudBytes);
    }

    public WiiUKeys Keys => WiiUKeys.Load(KeysTxt);

    public void Dispose() => _scratch.Dispose();
}

[CollectionDefinition("wiiu")]
public sealed class WiiUCollection : ICollectionFixture<Samples>;
