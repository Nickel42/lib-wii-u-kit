using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace LibWiiUKit;

/// <summary>Where a volume's encrypted cluster data comes from (NUS .app files, or a disc partition).</summary>
internal interface IClusterSource : IDisposable
{
    /// <summary>Fills <paramref name="buffer"/> from cluster <paramref name="cluster"/>, starting <paramref name="offset"/> bytes in. Bytes past the end read as zero.</summary>
    void Read(int cluster, long offset, Span<byte> buffer);

    /// <summary>Reads the encrypted FST itself.</summary>
    void ReadFst(Span<byte> buffer);

    /// <summary>
    /// Told where each cluster starts once the FST is parsed (bytes, relative
    /// to the partition). Disc partitions need this; NUS .app files, which
    /// each hold one cluster from byte 0, ignore it.
    /// </summary>
    void SetClusterOffsets(IReadOnlyList<long> offsets);
}

/// <summary>One entry of a Wii U file system table.</summary>
public sealed record FstEntry(string Path, bool IsDirectory, long Offset, long Size, int Cluster, bool IsLink);

/// <summary>
/// A Wii U file system (FST): the file table of a NUS title or a disc
/// partition, plus decryption of its file data. Layout (big-endian):
/// a 0x20 header ("FST\0", offset factor, cluster count), one 0x20 entry per
/// cluster (offset in 0x8000 sectors, size, owner title, group, hash mode),
/// then 0x10-byte entries (type and 24-bit name offset, offset or parent
/// index, size or end index, flags, cluster) and a NUL-terminated name table.
/// A file's data starts at its offset times the offset factor. Hash mode 2
/// clusters are 0x10000-byte blocks of 0x400 bytes of hashes and 0xFC00 bytes
/// of data, the data decrypted with its own H0 hash as IV and checked
/// against it; other clusters are one AES-CBC stream starting with the
/// cluster index as IV.
/// </summary>
public sealed class FstVolume : IDisposable
{
    private const uint FstMagic = 0x46535400; // "FST\0"
    private const int RawSector = 0x8000;
    private const int HashedBlock = 0x10000;
    private const int HashArea = 0x400;
    private const int HashedData = HashedBlock - HashArea; // 0xFC00

    private sealed record Cluster(long Offset, long Size, ulong OwnerTitleId, int HashMode);

    private readonly IClusterSource _source;
    private readonly byte[] _key;
    private readonly uint _offsetFactor;
    private readonly List<Cluster> _clusters;

    public IReadOnlyList<FstEntry> Entries { get; }

    private FstVolume(IClusterSource source, byte[] key, uint offsetFactor, List<Cluster> clusters, List<FstEntry> entries)
    {
        _source = source;
        _key = key;
        _offsetFactor = offsetFactor;
        _clusters = clusters;
        Entries = entries;
    }

    /// <summary>Whether <paramref name="key"/> decrypts the start of this FST to its magic.</summary>
    internal static bool KeyFits(IClusterSource source, ReadOnlySpan<byte> key)
    {
        Span<byte> head = stackalloc byte[16];
        source.ReadFst(head);
        Span<byte> plain = stackalloc byte[16];
        WiiUCrypto.DecryptCbc(key, new byte[16], head, plain);
        return BinaryPrimitives.ReadUInt32BigEndian(plain) == FstMagic;
    }

    /// <summary>Decrypts and parses an FST of <paramref name="fstSize"/> bytes.</summary>
    internal static FstVolume Open(IClusterSource source, byte[] key, long fstSize)
    {
        if (fstSize < 0x20 || fstSize > 256L << 20) throw new InvalidDataException("The file table has an impossible size.");
        var padded = (int)((fstSize + 15) & ~15L);
        var encrypted = new byte[padded];
        source.ReadFst(encrypted);
        var fst = WiiUCrypto.DecryptCbc(key, new byte[16], encrypted).AsSpan(0, (int)fstSize).ToArray();

        if (BinaryPrimitives.ReadUInt32BigEndian(fst) != FstMagic)
            throw new InvalidDataException("The file table didn't decrypt (wrong key, or a damaged dump).");
        var offsetFactor = BinaryPrimitives.ReadUInt32BigEndian(fst.AsSpan(4));
        var clusterCount = BinaryPrimitives.ReadUInt32BigEndian(fst.AsSpan(8));
        if (clusterCount == 0 || clusterCount >= 0x1000) throw new InvalidDataException("The file table lists an impossible number of clusters.");

        var clusters = new List<Cluster>();
        var pos = 0x20;
        for (var i = 0; i < clusterCount; i++, pos += 0x20)
        {
            if (pos + 0x20 > fst.Length) throw new InvalidDataException("The file table's cluster list is cut short.");
            var e = fst.AsSpan(pos, 0x20);
            clusters.Add(new Cluster(
                BinaryPrimitives.ReadUInt32BigEndian(e) * (long)RawSector,
                BinaryPrimitives.ReadUInt32BigEndian(e[4..]) * (long)RawSector,
                BinaryPrimitives.ReadUInt64BigEndian(e[8..]),
                e[0x14]));
        }

        source.SetClusterOffsets(clusters.Select(c => c.Offset).ToList());

        var entriesStart = pos;
        if (entriesStart + 16 > fst.Length) throw new InvalidDataException("The file table has no entries.");
        var count = BinaryPrimitives.ReadUInt32BigEndian(fst.AsSpan(entriesStart + 8));
        var namesStart = entriesStart + (long)count * 16;
        if (count == 0 || namesStart > fst.Length) throw new InvalidDataException("The file table's entry count doesn't fit.");

        string Name(uint offset)
        {
            var start = (int)(namesStart + offset);
            if (start >= fst.Length) throw new InvalidDataException("A file name lies outside the file table.");
            var end = Array.IndexOf(fst, (byte)0, start);
            if (end < 0) end = fst.Length;
            return Encoding.UTF8.GetString(fst, start, end - start);
        }

        // Walk the entries in order, tracking where each directory ends.
        var entries = new List<FstEntry>();
        var stack = new Stack<(int End, string Path)>();
        stack.Push(((int)count, ""));
        for (var i = 1; i < count; i++)
        {
            while (i >= stack.Peek().End) stack.Pop();
            var e = fst.AsSpan(entriesStart + i * 16, 16);
            var typeAndName = BinaryPrimitives.ReadUInt32BigEndian(e);
            var type = (byte)(typeAndName >> 24);
            var name = Name(typeAndName & 0xFFFFFF);
            if (name.Length == 0 || name is "." or ".." || name.Contains('/') || name.Contains('\\'))
                throw new InvalidDataException($"The file table has an unusable name at entry {i}.");
            var path = stack.Peek().Path.Length == 0 ? name : stack.Peek().Path + "/" + name;
            var a = BinaryPrimitives.ReadUInt32BigEndian(e[4..]);
            var b = BinaryPrimitives.ReadUInt32BigEndian(e[8..]);
            var cluster = BinaryPrimitives.ReadUInt16BigEndian(e[14..]);
            if ((type & 0x01) != 0)
            {
                if (b <= i || b > stack.Peek().End) throw new InvalidDataException($"Directory {path} has an impossible end index.");
                entries.Add(new FstEntry(path, true, 0, 0, 0, false));
                stack.Push(((int)b, path));
            }
            else
            {
                if (cluster >= clusters.Count) throw new InvalidDataException($"{path} points at a cluster that doesn't exist.");
                entries.Add(new FstEntry(path, false, a * (long)offsetFactor, b, cluster, (type & 0x80) != 0));
            }
        }
        return new FstVolume(source, key, offsetFactor, clusters, entries);
    }

    public FstEntry? Find(string path) =>
        Entries.FirstOrDefault(e => e.Path.Equals(path.Trim('/'), StringComparison.OrdinalIgnoreCase));

    public byte[] ReadAll(FstEntry file)
    {
        using var ms = new MemoryStream();
        Copy(file, ms, null, CancellationToken.None);
        return ms.ToArray();
    }

    /// <summary>Decrypts a file to <paramref name="destination"/>; hashed clusters are checked block by block.</summary>
    public void Copy(FstEntry file, Stream destination, Action<long>? progress, CancellationToken token)
    {
        if (file.IsDirectory) throw new ArgumentException("Not a file.", nameof(file));
        var cluster = _clusters[file.Cluster];
        var hashed = cluster.HashMode == 2;
        var unit = hashed ? HashedData : RawSector;
        var block = new byte[hashed ? HashedBlock : RawSector];
        var plain = new byte[unit];
        long cachedIndex = -1;
        var position = file.Offset;
        var remaining = file.Size;
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            var index = position / unit;
            if (index != cachedIndex)
            {
                if (hashed) DecryptHashed(file.Cluster, index, block, plain);
                else DecryptRaw(file.Cluster, index, block, plain);
                cachedIndex = index;
            }
            var within = (int)(position % unit);
            var take = (int)Math.Min(unit - within, remaining);
            destination.Write(plain, within, take);
            progress?.Invoke(take);
            position += take;
            remaining -= take;
        }
    }

    private void DecryptRaw(int cluster, long index, byte[] buffer, byte[] plain)
    {
        // One CBC stream per cluster: the first sector's IV is the cluster
        // index, every later sector's is the last 16 encrypted bytes before it.
        Span<byte> iv = stackalloc byte[16];
        iv.Clear();
        if (index == 0)
        {
            iv[0] = (byte)(cluster >> 8);
            iv[1] = (byte)cluster;
        }
        else
        {
            _source.Read(cluster, index * RawSector - 16, iv);
        }
        _source.Read(cluster, index * RawSector, buffer);
        WiiUCrypto.DecryptCbc(_key, iv, buffer, plain);
    }

    private void DecryptHashed(int cluster, long index, byte[] buffer, byte[] plain)
    {
        _source.Read(cluster, index * HashedBlock, buffer);
        var hashes = WiiUCrypto.DecryptCbc(_key, new byte[16], buffer.AsSpan(0, HashArea));
        var h0 = hashes.AsSpan((int)(index % 16) * 20, 20);
        WiiUCrypto.DecryptCbc(_key, h0[..16], buffer.AsSpan(HashArea), plain);
        if (!SHA1.HashData(plain).AsSpan().SequenceEqual(h0))
            throw new InvalidDataException($"Block {index} of cluster {cluster} fails its hash check: the dump is damaged or the wrong key was used.");
    }

    /// <summary>
    /// Checks raw clusters against the content hashes in a TMD (hashed
    /// clusters are checked as they're read). Returns the number checked.
    /// </summary>
    internal int VerifyRawClusters(IReadOnlyList<TmdContent> contents, Action<long>? progress, CancellationToken token)
    {
        var checkedCount = 0;
        for (var c = 0; c < _clusters.Count && c < contents.Count; c++)
        {
            if (_clusters[c].HashMode == 2) continue;
            var content = contents[c];
            using IncrementalHash hash = IncrementalHash.CreateHash(content.IsSha1 ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256);
            var buffer = new byte[RawSector];
            var plain = new byte[RawSector];
            for (long done = 0, index = 0; done < content.Size; index++)
            {
                token.ThrowIfCancellationRequested();
                DecryptRaw(c, index, buffer, plain);
                var take = (int)Math.Min(RawSector, content.Size - done);
                hash.AppendData(plain, 0, take);
                done += take;
                progress?.Invoke(take);
            }
            var expected = content.Hash.AsSpan(0, content.IsSha1 ? 20 : 32);
            if (!hash.GetHashAndReset().AsSpan().SequenceEqual(expected))
                throw new InvalidDataException($"Content {content.Id:x8} doesn't match its hash in the TMD: the dump is damaged or the wrong key was used.");
            checkedCount++;
        }
        return checkedCount;
    }

    public void Dispose() => _source.Dispose();
}

/// <summary>One content (.app) listed in a TMD.</summary>
public sealed record TmdContent(uint Id, ushort Index, ushort Type, long Size, byte[] Hash)
{
    public bool IsHashed => (Type & 0x0002) != 0;
    public bool IsSha1 => (Type & 0x2000) != 0;
}

/// <summary>The parts of a Wii U title.tmd and title.tik that decryption needs.</summary>
public static class WiiUTicket
{
    public sealed record Tmd(ulong TitleId, ushort Version, List<TmdContent> Contents);
    public sealed record Ticket(ulong TitleId, byte[] EncryptedTitleKey, uint DeviceId);

    public static Tmd ParseTmd(byte[] data)
    {
        if (data.Length < 0xB04) throw new InvalidDataException("title.tmd is too short.");
        var titleId = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(0x18C));
        var version = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0x1DC));
        var count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0x1DE));
        if (data.Length < 0xB04 + count * 0x30) throw new InvalidDataException("title.tmd's content list is cut short.");
        var contents = new List<TmdContent>();
        for (var i = 0; i < count; i++)
        {
            var e = data.AsSpan(0xB04 + i * 0x30, 0x30);
            contents.Add(new TmdContent(
                BinaryPrimitives.ReadUInt32BigEndian(e),
                BinaryPrimitives.ReadUInt16BigEndian(e[4..]),
                BinaryPrimitives.ReadUInt16BigEndian(e[6..]),
                (long)BinaryPrimitives.ReadUInt64BigEndian(e[8..]),
                e.Slice(0x10, 32).ToArray()));
        }
        if ((titleId >> 48) != 0x0005) throw new InvalidDataException("title.tmd isn't for a Wii U title.");
        return new Tmd(titleId, version, contents);
    }

    public static Ticket ParseTicket(byte[] data)
    {
        if (data.Length < 0x1E8) throw new InvalidDataException("The ticket is too short.");
        var titleId = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(0x1DC));
        if ((titleId >> 48) != 0x0005) throw new InvalidDataException("The ticket isn't for a Wii U title.");
        return new Ticket(titleId, data.AsSpan(0x1BF, 16).ToArray(), BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0x1D8)));
    }

    /// <summary>
    /// Finds the title key for a ticket by trying every key from keys.txt
    /// as the common key (decrypting the ticket's title key) and as the title
    /// key itself; the one that decrypts the FST to its magic wins.
    /// </summary>
    internal static byte[]? FindTitleKey(Ticket ticket, WiiUKeys keys, IClusterSource source)
    {
        var iv = WiiUCrypto.TitleIdIv(ticket.TitleId);
        foreach (var key in keys.Keys)
        {
            var titleKey = WiiUCrypto.DecryptCbc(key, iv, ticket.EncryptedTitleKey);
            if (FstVolume.KeyFits(source, titleKey)) return titleKey;
            if (FstVolume.KeyFits(source, key)) return key;
        }
        return null;
    }
}
