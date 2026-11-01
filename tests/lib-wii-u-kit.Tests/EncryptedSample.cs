using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace LibWiiUKit.Tests;

/// <summary>
/// Builds encrypted Wii U test images from a synthetic title folder, with
/// made-up keys: NUS titles (title.tmd, title.tik, .app) and disc images
/// (WUD, split WUD, WUX). They follow the layouts the library's reader
/// documents, so they check its logic end to end; they can't stand in for a
/// real dump.
/// </summary>
internal static class EncryptedSample
{
    public const int Sector = 0x8000;
    private const int OffsetFactor = 0x20;

    public sealed record Keys(byte[] Common, byte[] Title, byte[] Disc);

    public static Keys MakeKeys(int seed)
    {
        var rng = new Random(seed);
        byte[] K() { var k = new byte[16]; rng.NextBytes(k); return k; }
        return new Keys(K(), K(), K());
    }

    public static void WriteKeysTxt(string path, params byte[][] keys) =>
        File.WriteAllLines(path, ["# test keys (made up)", .. keys.Select((k, i) => $"{Convert.ToHexString(k)} # key {i}")]);

    // ---- FST and clusters ---------------------------------------------------

    private sealed record Built(byte[] Fst, Dictionary<int, byte[]> ClusterPlain);

    /// <summary>
    /// Lays out <paramref name="tree"/>'s files: code/ and meta/ in raw
    /// cluster 1, everything else in hashed cluster 2 (cluster 0 is the FST).
    /// </summary>
    private static Built BuildFst(string tree, Func<int, uint> clusterSectorOffset, int fstPadTo = 16)
    {
        var dirs = Directory.EnumerateDirectories(tree, "*", SearchOption.AllDirectories)
            .Select(d => Rel(tree, d)).ToList();
        var files = Directory.EnumerateFiles(tree, "*", SearchOption.AllDirectories).Select(f => Rel(tree, f)).ToList();

        // Depth-first entry order with directories before their contents.
        var order = new List<(string Path, bool Dir)>();
        void Walk(string prefix)
        {
            var childDirs = dirs.Where(d => Parent(d) == prefix).OrderBy(d => d, StringComparer.Ordinal);
            var childFiles = files.Where(f => Parent(f) == prefix).OrderBy(f => f, StringComparer.Ordinal);
            foreach (var f in childFiles) order.Add((f, false));
            foreach (var d in childDirs) { order.Add((d, true)); Walk(d); }
        }
        Walk("");

        var cluster = new Dictionary<int, MemoryStream> { [1] = new(), [2] = new() };
        var names = new MemoryStream();
        names.WriteByte(0); // root's empty name
        var entries = new List<byte[]>();
        var root = new byte[16];
        root[0] = 1;
        entries.Add(root);

        var indexOf = new Dictionary<string, int> { [""] = 0 };
        for (var i = 0; i < order.Count; i++) indexOf[order[i].Path] = i + 1;
        int EndOf(string dir) => order.FindLastIndex(o => o.Path.StartsWith(dir + "/", StringComparison.Ordinal)) is var last and >= 0 ? last + 2 : indexOf[dir] + 1;

        foreach (var (path, isDir) in order)
        {
            var e = new byte[16];
            var nameOffset = (uint)names.Length;
            var name = Encoding.UTF8.GetBytes(path[(path.LastIndexOf('/') + 1)..]);
            names.Write(name);
            names.WriteByte(0);
            if (isDir)
            {
                BinaryPrimitives.WriteUInt32BigEndian(e, 0x01000000 | nameOffset);
                BinaryPrimitives.WriteUInt32BigEndian(e.AsSpan(4), (uint)indexOf[Parent(path)]);
                BinaryPrimitives.WriteUInt32BigEndian(e.AsSpan(8), (uint)EndOf(path));
            }
            else
            {
                var c = path.StartsWith("code/", StringComparison.Ordinal) || path.StartsWith("meta/", StringComparison.Ordinal) ? 1 : 2;
                var data = File.ReadAllBytes(Path.Combine(tree, path));
                var stream = cluster[c];
                while (stream.Length % OffsetFactor != 0) stream.WriteByte(0);
                var offset = stream.Length;
                stream.Write(data);
                BinaryPrimitives.WriteUInt32BigEndian(e, nameOffset);
                BinaryPrimitives.WriteUInt32BigEndian(e.AsSpan(4), (uint)(offset / OffsetFactor));
                BinaryPrimitives.WriteUInt32BigEndian(e.AsSpan(8), (uint)data.Length);
                BinaryPrimitives.WriteUInt16BigEndian(e.AsSpan(14), (ushort)c);
            }
            entries.Add(e);
        }
        BinaryPrimitives.WriteUInt32BigEndian(root.AsSpan(8), (uint)entries.Count);

        var fst = new MemoryStream();
        var header = new byte[0x20];
        BinaryPrimitives.WriteUInt32BigEndian(header, 0x46535400);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), OffsetFactor);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 3);
        fst.Write(header);
        for (var c = 0; c < 3; c++)
        {
            var ce = new byte[0x20];
            BinaryPrimitives.WriteUInt32BigEndian(ce, clusterSectorOffset(c));
            ce[0x14] = (byte)(c == 2 ? 2 : 0);
            fst.Write(ce);
        }
        foreach (var e in entries) fst.Write(e);
        fst.Write(names.ToArray());
        while (fst.Length % fstPadTo != 0) fst.WriteByte(0);
        return new Built(fst.ToArray(), cluster.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
    }

    private static string Rel(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static string Parent(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    public static byte[] EncryptRaw(byte[] plain, byte[] key, int clusterIndex)
    {
        var padded = new byte[(plain.Length + 15) & ~15];
        plain.CopyTo(padded, 0);
        var iv = new byte[16];
        iv[0] = (byte)(clusterIndex >> 8);
        iv[1] = (byte)clusterIndex;
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptCbc(padded, iv, PaddingMode.None);
    }

    /// <summary>Hashed layout: 0x10000-byte blocks, 0x400 bytes of hashes (H0 table at 0) then 0xFC00 of data.</summary>
    public static byte[] EncryptHashed(byte[] plain, byte[] key)
    {
        const int data = 0xFC00;
        var blocks = Math.Max(1, (plain.Length + data - 1) / data);
        var chunks = Enumerable.Range(0, blocks).Select(b =>
        {
            var chunk = new byte[data];
            plain.AsSpan(b * data, Math.Min(data, plain.Length - b * data)).CopyTo(chunk);
            return chunk;
        }).ToList();
        var h0 = chunks.Select(c => SHA1.HashData(c)).ToList();
        using var aes = Aes.Create();
        aes.Key = key;
        var output = new byte[blocks * 0x10000];
        for (var b = 0; b < blocks; b++)
        {
            var hashArea = new byte[0x400];
            var group = b / 16 * 16;
            for (var i = 0; i < 16 && group + i < blocks; i++) h0[group + i].CopyTo(hashArea, i * 20);
            aes.EncryptCbc(hashArea, new byte[16], PaddingMode.None).CopyTo(output, b * 0x10000);
            aes.EncryptCbc(chunks[b], hashArea.AsSpan((b % 16) * 20, 16), PaddingMode.None).CopyTo(output, b * 0x10000 + 0x400);
        }
        return output;
    }

    public static byte[] Ticket(ulong titleId, byte[] titleKey, byte[] commonKey, uint deviceId = 0)
    {
        var t = new byte[0x350];
        BinaryPrimitives.WriteUInt32BigEndian(t, 0x00010004);
        var iv = new byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(iv, titleId);
        using var aes = Aes.Create();
        aes.Key = commonKey;
        aes.EncryptCbc(titleKey, iv, PaddingMode.None).CopyTo(t, 0x1BF);
        BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(0x1D8), deviceId);
        BinaryPrimitives.WriteUInt64BigEndian(t.AsSpan(0x1DC), titleId);
        return t;
    }

    // ---- NUS ----------------------------------------------------------------

    /// <summary>Writes an NUS title for <paramref name="tree"/> into <paramref name="folder"/>.</summary>
    public static string MakeNus(string tree, string folder, ulong titleId, ushort version, Keys keys, uint deviceId = 0)
    {
        Directory.CreateDirectory(folder);
        var built = BuildFst(tree, _ => 0);
        var plain = new Dictionary<int, byte[]> { [0] = built.Fst, [1] = built.ClusterPlain[1], [2] = built.ClusterPlain[2] };
        var tmd = new byte[0xB04 + 3 * 0x30];
        BinaryPrimitives.WriteUInt32BigEndian(tmd, 0x00010004);
        BinaryPrimitives.WriteUInt64BigEndian(tmd.AsSpan(0x18C), titleId);
        BinaryPrimitives.WriteUInt16BigEndian(tmd.AsSpan(0x1DC), version);
        BinaryPrimitives.WriteUInt16BigEndian(tmd.AsSpan(0x1DE), 3);
        for (var c = 0; c < 3; c++)
        {
            var hashed = c == 2;
            var encrypted = hashed ? EncryptHashed(plain[c], keys.Title) : EncryptRaw(plain[c], keys.Title, c);
            File.WriteAllBytes(Path.Combine(folder, $"{c:x8}.app"), encrypted);
            var e = tmd.AsSpan(0xB04 + c * 0x30, 0x30);
            BinaryPrimitives.WriteUInt32BigEndian(e, (uint)c);
            BinaryPrimitives.WriteUInt16BigEndian(e[4..], (ushort)c);
            BinaryPrimitives.WriteUInt16BigEndian(e[6..], (ushort)(0x2001 | (hashed ? 0x0002 : 0)));
            var size = hashed ? encrypted.Length : plain[c].Length;
            BinaryPrimitives.WriteUInt64BigEndian(e[8..], (ulong)size);
            if (!hashed)
            {
                var padded = new byte[(plain[c].Length + 15) & ~15];
                plain[c].CopyTo(padded, 0);
                SHA1.HashData(padded.AsSpan(0, size)).CopyTo(e[0x10..]);
            }
        }
        File.WriteAllBytes(Path.Combine(folder, "title.tmd"), tmd);
        File.WriteAllBytes(Path.Combine(folder, "title.tik"), Ticket(titleId, keys.Title, keys.Common, deviceId));
        return folder;
    }

    // ---- Disc images --------------------------------------------------------

    /// <summary>Builds a WUD image (bytes) whose game partition holds <paramref name="tree"/>.</summary>
    public static byte[] MakeWud(string tree, ulong titleId, Keys keys)
    {
        using var aes = Aes.Create();

        // GM partition (index 1): header sector, FST cluster, then data clusters.
        const uint gmStart = 12;     // sectors from the start of the disc (SI uses 4..8)
        var gmClusterSector = new uint[3];
        var gmBuilt = BuildFst(tree, c => gmClusterSector[c], Sector);
        gmClusterSector[0] = 1;
        var raw1 = EncryptRaw(gmBuilt.ClusterPlain[1], keys.Title, 1);
        var hashed2 = EncryptHashed(gmBuilt.ClusterPlain[2], keys.Title);
        gmClusterSector[1] = 1 + (uint)(gmBuilt.Fst.Length / Sector) + 1;
        gmClusterSector[2] = gmClusterSector[1] + (uint)((raw1.Length + Sector - 1) / Sector) + 1;
        gmBuilt = BuildFst(tree, c => gmClusterSector[c], Sector); // again, now with the final offsets
        var gmFst = EncryptRaw(gmBuilt.Fst, keys.Title, 0);

        // SI partition (index 0): its FST and one file, 01/title.tik, all under the disc key.
        var siTree = Directory.CreateTempSubdirectory("wiiu-si-").FullName;
        Directory.CreateDirectory(Path.Combine(siTree, "01"));
        File.WriteAllBytes(Path.Combine(siTree, "01", "title.tik"), Ticket(titleId, keys.Title, keys.Common));
        const uint siStart = 4;
        var siClusterSector = new uint[] { 1, 2, 3 };
        var siBuilt = BuildFst(siTree, c => siClusterSector[c], Sector);
        Directory.Delete(siTree, true);
        var siFst = EncryptRaw(siBuilt.Fst, keys.Disc, 0);
        var siData = EncryptHashed(siBuilt.ClusterPlain[2], keys.Disc); // "01/..." isn't code/ or meta/, so cluster 2

        var gmEnd = gmStart + gmClusterSector[2] + (uint)(hashed2.Length / Sector) + 1;
        var image = new byte[(long)gmEnd * Sector];
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x10000), 0xCC549EB9);

        // Partition table at 0x18000, encrypted with the disc key.
        var table = new byte[Sector];
        BinaryPrimitives.WriteUInt32BigEndian(table, 0xCCA6E67B);
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(4), Sector);
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(0x1C), 2);
        void Partition(int i, string name, uint start)
        {
            var e = table.AsSpan(0x800 + i * 0x80, 0x80);
            Encoding.ASCII.GetBytes(name).CopyTo(e);
            e[0x1F] = 1;
            BinaryPrimitives.WriteUInt32BigEndian(e[0x20..], start);
        }
        Partition(0, "SI", siStart);
        Partition(1, $"GM{titleId:X16}", gmStart);
        aes.Key = keys.Disc;
        aes.EncryptCbc(table, new byte[16], PaddingMode.None).CopyTo(image, 0x18000);

        void Header(uint start, int fstSize)
        {
            var h = image.AsSpan((int)(start * Sector), 0x40);
            BinaryPrimitives.WriteUInt32BigEndian(h, 0xCC93A4F5);
            BinaryPrimitives.WriteUInt32BigEndian(h[4..], Sector);
            BinaryPrimitives.WriteUInt32BigEndian(h[0x14..], (uint)fstSize);
            BinaryPrimitives.WriteUInt32BigEndian(h[0x18..], 1);
        }
        Header(siStart, siBuilt.Fst.Length);
        siFst.CopyTo(image, (int)((siStart + siClusterSector[0]) * Sector));
        siData.CopyTo(image, (int)((siStart + siClusterSector[2]) * Sector));

        Header(gmStart, gmBuilt.Fst.Length);
        gmFst.CopyTo(image, (int)((gmStart + gmClusterSector[0]) * Sector));
        raw1.CopyTo(image, (int)((gmStart + gmClusterSector[1]) * Sector));
        hashed2.CopyTo(image, (int)((gmStart + gmClusterSector[2]) * Sector));
        return image;
    }

    /// <summary>WUX: the image's distinct sectors once each, with a table mapping every sector to its copy.</summary>
    public static byte[] ToWux(byte[] wud)
    {
        var count = (wud.Length + Sector - 1) / Sector;
        var index = new uint[count];
        var stored = new List<byte[]>();
        var seen = new Dictionary<string, uint>();
        for (var i = 0; i < count; i++)
        {
            var sector = new byte[Sector];
            wud.AsSpan(i * Sector, Math.Min(Sector, wud.Length - i * Sector)).CopyTo(sector);
            var key = Convert.ToHexString(SHA256.HashData(sector));
            if (!seen.TryGetValue(key, out var at))
            {
                at = (uint)stored.Count;
                stored.Add(sector);
                seen[key] = at;
            }
            index[i] = at;
        }
        var ms = new MemoryStream();
        var header = new byte[0x20];
        Encoding.ASCII.GetBytes("WUX0").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 0x1099d02e);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), Sector);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0x10), wud.Length);
        ms.Write(header);
        foreach (var v in index) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); ms.Write(b); }
        while (ms.Length % Sector != 0) ms.WriteByte(0);
        foreach (var s in stored) ms.Write(s);
        return ms.ToArray();
    }
}
