using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace LibWiiUKit;

/// <summary>An opened encrypted title: its file system and identity.</summary>
internal sealed class EncryptedTitle(FstVolume volume, ulong titleId, uint version, string kind, IReadOnlyList<TmdContent>? contents) : IDisposable
{
    public FstVolume Volume { get; } = volume;
    public ulong TitleId { get; } = titleId;
    public uint Version { get; } = version;
    public string Kind { get; } = kind;
    internal IReadOnlyList<TmdContent>? Contents { get; } = contents;
    public void Dispose() => Volume.Dispose();
}

/// <summary>Opens NUS titles and WUD/WUX disc images with the user's keys.</summary>
public static partial class WiiUEncrypted
{
    public static bool IsNusFolder(string path) =>
        Directory.Exists(path) && File.Exists(Path.Combine(path, "title.tmd")) && File.Exists(Path.Combine(path, "title.tik"));

    public static bool IsDiscImage(string path)
    {
        if (!File.Exists(path)) return false;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".wux") return true;
        if (ext != ".wud") return false;
        // Of a split dump (game_part1.wud, game_part2.wud, ...) only the first part is the image.
        var m = SplitPart().Match(Path.GetFileName(path));
        return !m.Success || m.Groups[2].Value == "1";
    }

    // ---- NUS / installable titles ------------------------------------------

    internal static EncryptedTitle OpenNus(string folder, WiiUKeys keys)
    {
        var tmd = WiiUTicket.ParseTmd(File.ReadAllBytes(Path.Combine(folder, "title.tmd")));
        var ticket = WiiUTicket.ParseTicket(File.ReadAllBytes(Path.Combine(folder, "title.tik")));
        if (ticket.DeviceId != 0)
            throw new NotSupportedException("This ticket is personalised to one console (it needs that console's keys); use a dump made with a common ticket.");
        if (ticket.TitleId != tmd.TitleId)
            throw new InvalidDataException($"title.tik is for {ticket.TitleId:X16} but title.tmd is for {tmd.TitleId:X16}.");
        if (tmd.Contents.Count == 0) throw new InvalidDataException("title.tmd lists no contents.");

        var source = new NusSource(folder, tmd.Contents);
        try
        {
            if (keys.IsEmpty) throw new WiiUKeysMissingException(NoKeys);
            var titleKey = WiiUTicket.FindTitleKey(ticket, keys, source)
                ?? throw new KeyNotFoundException($"None of the {keys.Keys.Count} key(s) in keys.txt decrypts title {tmd.TitleId:X16}" +
                    $"{(keys.TitleKeys.Count > 0 ? $", and it has no title key for it among {keys.TitleKeys.Count}" : "")}. Add the Wii U common key or this title's key.");
            var volume = FstVolume.Open(source, titleKey, tmd.Contents[0].Size);
            return new EncryptedTitle(volume, tmd.TitleId, tmd.Version, "NUS", tmd.Contents);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private sealed class NusSource(string folder, IReadOnlyList<TmdContent> contents) : IClusterSource
    {
        private readonly Dictionary<int, FileStream> _files = [];

        private FileStream File(int cluster)
        {
            if (_files.TryGetValue(cluster, out var fs)) return fs;
            if (cluster >= contents.Count) throw new InvalidDataException($"The file table uses content {cluster}, which title.tmd doesn't list.");
            var id = contents[cluster].Id;
            var path = new[] { $"{id:x8}.app", $"{id:X8}.app", $"{id:x8}", $"{id:X8}" }
                .Select(n => Path.Combine(folder, n)).FirstOrDefault(System.IO.File.Exists)
                ?? throw new FileNotFoundException($"{id:x8}.app is missing from {Path.GetFileName(folder)}.");
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            _files[cluster] = fs;
            return fs;
        }

        public void Read(int cluster, long offset, Span<byte> buffer) => ReadPadded(File(cluster), offset, buffer);
        public void ReadFst(Span<byte> buffer) => ReadPadded(File(0), 0, buffer);
        public void SetClusterOffsets(IReadOnlyList<long> offsets) { } // each .app starts at its cluster's byte 0

        public void Dispose()
        {
            foreach (var fs in _files.Values) fs.Dispose();
            _files.Clear();
        }
    }

    // ---- WUD / WUX disc images ---------------------------------------------

    private const int Sector = 0x8000;
    private const uint DiscMagic = 0xCC549EB9;        // at 0x10000
    private const uint PartitionTableMagic = 0xCCA6E67B; // at 0x18000, encrypted with the disc key
    private const uint PartitionMagic = 0xCC93A4F5;   // at the start of each partition

    /// <summary>
    /// Opens the game partition of a disc image. The disc key is found in
    /// keys.txt (or a .key file next to the image) by decrypting a known-zero
    /// area; the game's ticket comes from the SI partition, whose title key
    /// is found the same way as a NUS title's.
    /// </summary>
    internal static EncryptedTitle OpenDisc(string path, WiiUKeys keys)
    {
        var image = DiscImage.Open(path);
        try
        {
            Span<byte> magic = stackalloc byte[4];
            image.Read(0x10000, magic);
            if (BinaryPrimitives.ReadUInt32BigEndian(magic) != DiscMagic)
                throw new InvalidDataException("Not a Wii U disc image (the disc header is missing).");

            var discKey = FindDiscKey(image, keys.With(KeyFiles(path)))
                ?? (keys.Keys.Count == 0 && KeyFiles(path).Count == 0
                    ? throw new WiiUKeysMissingException(NoKeys)
                    : throw new KeyNotFoundException("No key in keys.txt (or a .key file next to the image) is this disc's key. Add the disc key for this game."));

            var table = new byte[Sector];
            image.Read(0x18000, table);
            table = WiiUCrypto.DecryptCbc(discKey, new byte[16], table);
            if (BinaryPrimitives.ReadUInt32BigEndian(table) != PartitionTableMagic || BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(4)) != Sector)
                throw new InvalidDataException("The disc's partition table didn't decrypt.");
            var count = BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(0x1C));
            if (count is 0 or > 30) throw new InvalidDataException("The disc lists an impossible number of partitions.");

            int si = -1, gm = -1;
            var names = new string[count];
            var addresses = new long[count];
            for (var i = 0; i < count; i++)
            {
                var e = table.AsSpan(0x800 + i * 0x80, 0x80);
                names[i] = Encoding.ASCII.GetString(e[..31]).TrimEnd('\0');
                addresses[i] = BinaryPrimitives.ReadUInt32BigEndian(e[0x20..]) * (long)Sector;
                if (names[i].StartsWith("SI", StringComparison.Ordinal) && si < 0) si = i;
                if (names[i].StartsWith("GM", StringComparison.Ordinal) && gm < 0) gm = i;
            }
            if (si < 0 || gm < 0) throw new InvalidDataException("The disc has no SI or GM partition.");

            // SI partition: its FST uses the disc key; it holds the GM partition's ticket.
            var (siFstSector, siFstSize, siHash) = PartitionHeader(image, addresses[si]);
            byte[] ticketData;
            using (var siVolume = FstVolume.Open(new DiscSource(image, addresses[si], siFstSector, owns: false), discKey, siFstSize))
            {
                var tik = siVolume.Find($"{gm:x2}/title.tik") ?? throw new InvalidDataException($"The disc's SI partition has no ticket for partition {gm:x2}.");
                ticketData = siVolume.ReadAll(tik);
            }
            var ticket = WiiUTicket.ParseTicket(ticketData);

            var (gmFstSector, gmFstSize, _) = PartitionHeader(image, addresses[gm]);
            var gmSource = new DiscSource(image, addresses[gm], gmFstSector, owns: true);
            image = null!; // now owned by gmSource
            try
            {
                var titleKey = WiiUTicket.FindTitleKey(ticket, keys, gmSource)
                    ?? throw new KeyNotFoundException($"None of the keys in keys.txt decrypts the game partition of {Path.GetFileName(path)}. Add the Wii U common key.");
                var volume = FstVolume.Open(gmSource, titleKey, gmFstSize);
                var titleId = ParseGmTitleId(names[gm]) ?? ticket.TitleId;
                var version = VersionFromVolume(volume);
                return new EncryptedTitle(volume, titleId, version, Path.GetExtension(path).TrimStart('.').ToUpperInvariant(), null);
            }
            catch
            {
                gmSource.Dispose();
                throw;
            }
        }
        catch
        {
            image?.Dispose();
            throw;
        }
    }

    private static (long FstSector, long FstSize, int HashType) PartitionHeader(DiscImage image, long address)
    {
        var header = new byte[0x40];
        image.Read(address, header);
        if (BinaryPrimitives.ReadUInt32BigEndian(header) != PartitionMagic)
            throw new InvalidDataException("A disc partition header is missing.");
        return (BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x18)), BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x14)), header[0x24]);
    }

    private static byte[]? FindDiscKey(DiscImage image, WiiUKeys keys)
    {
        // 48 bytes at 0x18100: decrypting the last 32 (IV = the first 16) with the disc key gives zeros.
        var probe = new byte[48];
        image.Read(0x18100, probe);
        var plain = new byte[32];
        foreach (var key in keys.Keys)
        {
            WiiUCrypto.DecryptCbc(key, probe.AsSpan(0, 16), probe.AsSpan(16), plain);
            if (plain.All(b => b == 0)) return key;
        }
        return null;
    }

    /// <summary>A raw 16-byte disc key file next to the image (game.key, or game_part1.key's base name).</summary>
    private static List<byte[]> KeyFiles(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var m = SplitPart().Match(Path.GetFileName(path));
        var names = new List<string> { stem + ".key", "game.key", "disc.key" };
        if (m.Success) names.Insert(0, m.Groups[1].Value + ".key");
        return names.Select(n => Path.Combine(dir, n)).Where(File.Exists)
            .Select(File.ReadAllBytes).Where(b => b.Length == 16).ToList();
    }

    private static ulong? ParseGmTitleId(string name)
    {
        var hex = name.Length >= 18 ? name.Substring(2, 16) : "";
        return ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var id) && (id >> 48) == 0x0005 ? id : null;
    }

    private static uint VersionFromVolume(FstVolume volume)
    {
        foreach (var (file, tag) in new[] { ("code/app.xml", "title_version"), ("meta/meta.xml", "title_version") })
        {
            if (volume.Find(file) is not { IsDirectory: false } entry || entry.Size > 1 << 20) continue;
            try
            {
                var doc = System.Xml.Linq.XDocument.Parse(Encoding.UTF8.GetString(volume.ReadAll(entry)));
                if (uint.TryParse(doc.Root?.Element(tag)?.Value.Trim(), out var v)) return v;
            }
            catch (System.Xml.XmlException) { }
        }
        return 0;
    }

    private sealed class DiscSource(DiscImage image, long partition, long fstSector, bool owns) : IClusterSource
    {
        private long[]? _clusterOffsets;

        public void SetClusterOffsets(IReadOnlyList<long> offsets) => _clusterOffsets = offsets.ToArray();

        public void Read(int cluster, long offset, Span<byte> buffer)
        {
            var clusterOffset = _clusterOffsets is { } c && cluster < c.Length ? c[cluster] : 0;
            image.Read(partition + clusterOffset + offset, buffer);
        }

        public void ReadFst(Span<byte> buffer) => image.Read(partition + fstSector * Sector, buffer);

        public void Dispose()
        {
            if (owns) image.Dispose();
        }
    }

    private const string NoKeys =
        "No keys.txt with keys was found. Load your own keys.txt (the same file Cemu uses).";

    [GeneratedRegex(@"^(.*)_part(\d+)\.wud$", RegexOptions.IgnoreCase)]
    internal static partial Regex SplitPart();

    /// <summary>
    /// The files a disc image is made of: every part of a split WUD
    /// (game_part1.wud, game_part2.wud, ...), otherwise just <paramref name="path"/>.
    /// </summary>
    public static IReadOnlyList<string> ImageFiles(string path)
    {
        var m = SplitPart().Match(Path.GetFileName(path));
        if (!m.Success || !File.Exists(path)) return [path];
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var parts = new List<string>();
        for (var i = 1; File.Exists(Path.Combine(dir, $"{m.Groups[1].Value}_part{i}.wud")); i++)
            parts.Add(Path.Combine(dir, $"{m.Groups[1].Value}_part{i}.wud"));
        return parts;
    }

    internal static void ReadPadded(Stream stream, long offset, Span<byte> buffer)
    {
        buffer.Clear();
        if (offset >= stream.Length) return;
        stream.Position = offset;
        var got = 0;
        int n;
        while (got < buffer.Length && (n = stream.Read(buffer[got..])) > 0) got += n;
    }
}

/// <summary>
/// A Wii U disc image as one byte range: a .wud, a split dump
/// (game_part1.wud, game_part2.wud, ...), or a .wux (WUD with duplicate
/// sectors removed: "WUX0" header, sector size, uncompressed size, then a
/// table mapping each sector to its stored copy).
/// </summary>
internal sealed class DiscImage : IDisposable
{
    private const uint WuxMagic0 = 0x30585557; // "WUX0" read little-endian
    private const uint WuxMagic1 = 0x1099d02e;

    private readonly List<(FileStream File, long Start, long Length)> _parts = [];
    private uint[]? _wuxIndex;
    private int _wuxSectorSize;
    private long _wuxDataStart;

    public long Length { get; private set; }

    public static DiscImage Open(string path)
    {
        var image = new DiscImage();
        try
        {
            var m = WiiUEncrypted.SplitPart().Match(Path.GetFileName(path));
            var files = new List<string>();
            if (m.Success)
            {
                var dir = Path.GetDirectoryName(path)!;
                for (var i = 1; ; i++)
                {
                    var part = Path.Combine(dir, $"{m.Groups[1].Value}_part{i}.wud");
                    if (!File.Exists(part)) break;
                    files.Add(part);
                }
            }
            else
            {
                files.Add(path);
            }
            long start = 0;
            foreach (var f in files)
            {
                var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                image._parts.Add((fs, start, fs.Length));
                start += fs.Length;
            }
            image.Length = start;

            Span<byte> header = stackalloc byte[0x20];
            WiiUEncrypted.ReadPadded(image._parts[0].File, 0, header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) == WuxMagic0 && BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) == WuxMagic1)
                image.OpenWux(header);
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private void OpenWux(ReadOnlySpan<byte> header)
    {
        var sectorSize = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        var uncompressed = BinaryPrimitives.ReadInt64LittleEndian(header[0x10..]);
        if (sectorSize is < 0x100 or >= 0x10000000 || uncompressed <= 0)
            throw new InvalidDataException("The WUX header is invalid.");
        var count = (uncompressed + sectorSize - 1) / sectorSize;
        if (count > int.MaxValue / 4) throw new InvalidDataException("The WUX index table is impossibly large.");
        var table = new byte[count * 4];
        var file = _parts[0].File;
        WiiUEncrypted.ReadPadded(file, 0x20, table);
        _wuxIndex = new uint[count];
        for (var i = 0; i < count; i++) _wuxIndex[i] = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i * 4));
        var dataStart = 0x20 + count * 4;
        _wuxDataStart = (dataStart + sectorSize - 1) / sectorSize * sectorSize;
        _wuxSectorSize = (int)sectorSize;
        Length = uncompressed;
    }

    /// <summary>Reads image bytes; anything past the end reads as zero.</summary>
    public void Read(long offset, Span<byte> buffer)
    {
        buffer.Clear();
        if (_wuxIndex is null)
        {
            ReadRaw(offset, buffer);
            return;
        }
        while (!buffer.IsEmpty && offset < Length)
        {
            var sector = offset / _wuxSectorSize;
            var within = (int)(offset % _wuxSectorSize);
            var take = Math.Min(buffer.Length, _wuxSectorSize - within);
            var stored = _wuxIndex[sector];
            ReadRaw(_wuxDataStart + (long)stored * _wuxSectorSize + within, buffer[..take]);
            buffer = buffer[take..];
            offset += take;
        }
    }

    private void ReadRaw(long offset, Span<byte> buffer)
    {
        foreach (var (file, start, length) in _parts)
        {
            if (buffer.IsEmpty) return;
            if (offset >= start + length) continue;
            var within = offset - start;
            var take = (int)Math.Min(buffer.Length, length - within);
            WiiUEncrypted.ReadPadded(file, within, buffer[..take]);
            buffer = buffer[take..];
            offset += take;
        }
    }

    public void Dispose()
    {
        foreach (var (file, _, _) in _parts) file.Dispose();
        _parts.Clear();
    }
}
