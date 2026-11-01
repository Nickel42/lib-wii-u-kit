using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ZstdSharp;

namespace LibWiiUKit;

/// <summary>
/// The ZArchive format (.zar; Cemu's .wua is a ZArchive holding Wii U titles),
/// as defined by Exzap's reference library v0.1.2. All integers are
/// big-endian. Layout, front to back:
///
///   compressed data   files concatenated into one stream, cut into 64 KiB
///                     blocks, each zstd-compressed (stored raw when that
///                     doesn't shrink it); the last block is zero-padded;
///                     the section is then padded to 8 bytes
///   offset records    per 16 blocks: u64 offset of the first block, then
///                     16 × u16 (compressed size − 1)
///   name table        each distinct name once: length (1 byte, or 2 bytes
///                     with the top bit set from 128 up) + Windows-1252 bytes
///   file tree         16-byte entries, breadth first from the root; each
///                     folder's children sorted case-insensitively.
///                     u32 name offset (top bit set = file; root uses 0x7FFFFFFF),
///                     then file: offset lo, size lo, (size hi 16 | offset hi 16)
///                     or folder: first child index, child count, 0
///   meta sections     empty in version 1
///   footer (144 B)    six (u64 offset, u64 size) section pairs, SHA-256 of
///                     the whole file with this hash zeroed, u64 total size,
///                     u32 version 0x61bf3a01, u32 magic 0x169f52d6
/// </summary>
internal static class ZArchiveFormat
{
    public const int BlockSize = 64 * 1024;
    public const int EntriesPerOffsetRecord = 16;
    public const int OffsetRecordSize = 8 + 2 * EntriesPerOffsetRecord;
    public const int EntrySize = 16;
    public const int FooterSize = 16 * 6 + 32 + 8 + 4 + 4;
    public const uint Magic = 0x169f52d6;
    public const uint Version1 = 0x61bf3a01;
    public const int ZstdLevel = 6; // what the reference writer uses
    public const ulong MaxFileSize = (1UL << 48) - 1;

    private static Encoding? _names;

    /// <summary>Windows-1252, the archive's name encoding; unmappable characters throw.</summary>
    public static Encoding NameEncoding => _names ??= CreateNameEncoding();

    private static Encoding CreateNameEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
    }

    /// <summary>The archive's name order: byte-wise, with ASCII A–Z folded to a–z; a prefix sorts first.</summary>
    public static int CompareNames(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            var ca = Fold(a[i]);
            var cb = Fold(b[i]);
            if (ca != cb) return ca.CompareTo(cb);
        }
        return a.Length.CompareTo(b.Length);
    }

    public static bool NamesEqual(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) =>
        a.Length == b.Length && CompareNames(a, b) == 0;

    private static byte Fold(byte c) => c is >= (byte)'A' and <= (byte)'Z' ? (byte)(c + 32) : c;

    public static IEnumerable<string> SplitPath(string path) =>
        path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
}

public sealed class ZArchiveException(string message) : IOException(message);

/// <summary>
/// Writes a ZArchive in one pass, front to back (no seeking), as the format
/// allows. Add folders and files, then call <see cref="Finish"/>; a writer
/// that is disposed without finishing deletes nothing itself, so callers
/// write to a temporary name and rename on success.
/// </summary>
public sealed class ZArchiveWriter : IDisposable
{
    private sealed class Node(bool isFile, byte[] name)
    {
        public bool IsFile { get; } = isFile;
        public byte[] Name { get; } = name;
        public List<Node> Children { get; } = [];
        public ulong FileOffset { get; set; }
        public ulong FileSize { get; set; }
        public uint FirstChildIndex { get; set; }
    }

    private readonly Stream _output;
    private readonly bool _ownsOutput;
    private readonly IncrementalHash _sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly Compressor _zstd = new(ZArchiveFormat.ZstdLevel);
    private readonly Node _root = new(false, []);
    private readonly byte[] _block = new byte[ZArchiveFormat.BlockSize];
    private readonly byte[] _compressed;
    private readonly List<(ulong BaseOffset, ushort[] Sizes)> _records = [];
    private int _blockFill;
    private long _blocksWritten;
    private ulong _inputOffset;   // position in the uncompressed stream
    private ulong _outputOffset;  // bytes written to the file
    private bool _finished;

    public ZArchiveWriter(string path) : this(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20), true) { }

    public ZArchiveWriter(Stream output, bool ownsOutput = false)
    {
        _output = output;
        _ownsOutput = ownsOutput;
        _compressed = new byte[Compressor.GetCompressBound(ZArchiveFormat.BlockSize)];
    }

    /// <summary>Bytes of file data added so far (uncompressed).</summary>
    public ulong InputBytes => _inputOffset;

    /// <summary>Creates a folder and any missing parents. Existing folders are fine.</summary>
    public void AddDirectory(string path)
    {
        var node = _root;
        foreach (var part in ZArchiveFormat.SplitPath(path))
        {
            var name = EncodeName(part, path);
            var next = Find(node, name);
            if (next is null)
            {
                next = new Node(false, name);
                node.Children.Add(next);
            }
            else if (next.IsFile)
            {
                throw new ZArchiveException($"'{path}' can't be a folder: '{part}' is already a file.");
            }
            node = next;
        }
    }

    /// <summary>Adds a file whose folder must already exist, copying <paramref name="data"/> to its end.</summary>
    public void AddFile(string path, Stream data, Action<long>? bytesCopied = null, CancellationToken token = default)
    {
        var parts = ZArchiveFormat.SplitPath(path).ToList();
        if (parts.Count == 0) throw new ZArchiveException("A file needs a name.");
        var dir = _root;
        foreach (var part in parts.Take(parts.Count - 1))
        {
            dir = Find(dir, EncodeName(part, path));
            if (dir is null || dir.IsFile)
                throw new ZArchiveException($"The folder for '{path}' hasn't been added.");
        }
        var name = EncodeName(parts[^1], path);
        if (Find(dir, name) is not null)
            throw new ZArchiveException($"'{path}' is already in the archive (names are case-insensitive).");

        var file = new Node(true, name) { FileOffset = _inputOffset };
        dir.Children.Add(file);

        var buffer = new byte[1 << 20];
        int n;
        while ((n = data.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            Append(buffer.AsSpan(0, n));
            file.FileSize += (ulong)n;
            bytesCopied?.Invoke(n);
        }
        if (file.FileSize > ZArchiveFormat.MaxFileSize)
            throw new ZArchiveException($"'{path}' is larger than ZArchive's 256 TB limit.");
    }

    /// <summary>
    /// Adds a file whose contents <paramref name="fill"/> writes into the stream
    /// it is given (for copying out of another archive).
    /// </summary>
    public void AddFile(string path, Action<Stream> fill)
    {
        using var sink = new SinkStream();
        AddFile(path, Stream.Null); // creates the entry at the current offset, empty
        var file = LastAdded(path);
        sink.Target = data =>
        {
            Append(data);
            file.FileSize += (ulong)data.Length;
        };
        fill(sink);
    }

    private Node LastAdded(string path)
    {
        var parts = ZArchiveFormat.SplitPath(path).ToList();
        var node = _root;
        foreach (var part in parts)
            node = Find(node, EncodeName(part, path))!;
        return node;
    }

    /// <summary>A write-only stream that hands each write to a callback.</summary>
    private sealed class SinkStream : Stream
    {
        // A delegate type of its own: Action<ReadOnlySpan<byte>> needs C# 13's "allows ref struct", which net8.0 lacks.
        public delegate void SpanAction(ReadOnlySpan<byte> data);
        public SpanAction? Target { get; set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Target?.Invoke(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) => Target?.Invoke(buffer);
    }

    /// <summary>Writes the tables and footer. The archive is complete after this.</summary>
    public void Finish()
    {
        if (_finished) return;
        _finished = true;

        // Pad the last block; the padding belongs to no file.
        if (_blockFill > 0)
        {
            Array.Clear(_block, _blockFill, _block.Length - _blockFill);
            _inputOffset += (ulong)(_block.Length - _blockFill);
            _blockFill = _block.Length;
            StoreBlock(_block);
            _blockFill = 0;
        }
        var compressedSize = _outputOffset;
        while (_outputOffset % 8 != 0) Write([0]);

        // Offset records.
        var recordsOffset = _outputOffset;
        var record = new byte[ZArchiveFormat.OffsetRecordSize];
        foreach (var (baseOffset, sizes) in _records)
        {
            BinaryPrimitives.WriteUInt64BigEndian(record, baseOffset);
            for (var i = 0; i < ZArchiveFormat.EntriesPerOffsetRecord; i++)
                BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(8 + 2 * i), sizes[i]);
            Write(record);
        }
        var recordsSize = _outputOffset - recordsOffset;

        // Name table: each distinct name once, in first-use order (breadth first).
        var namesOffset = _outputOffset;
        var nameOffsets = new Dictionary<string, uint>(StringComparer.Ordinal);
        uint nameTablePos = 0;
        foreach (var node in BreadthFirst().Skip(1))
        {
            var key = Convert.ToHexString(node.Name);
            if (nameOffsets.ContainsKey(key)) continue;
            var name = node.Name.Length > 0x7FFF ? node.Name[..0x7FFF] : node.Name;
            nameOffsets[key] = nameTablePos;
            byte[] header = name.Length >= 0x80
                ? [(byte)((name.Length & 0x7F) | 0x80), (byte)(name.Length >> 7)]
                : [(byte)name.Length];
            Write(header);
            Write(name);
            nameTablePos += (uint)(header.Length + name.Length);
        }
        var namesSize = _outputOffset - namesOffset;

        // File tree: number the nodes breadth first, children sorted, then write them in that order.
        uint index = 1;
        foreach (var node in BreadthFirst())
        {
            if (node.IsFile) continue;
            node.Children.Sort((a, b) => ZArchiveFormat.CompareNames(a.Name, b.Name));
            node.FirstChildIndex = index;
            index += (uint)node.Children.Count;
        }
        var treeOffset = _outputOffset;
        var entry = new byte[ZArchiveFormat.EntrySize];
        foreach (var node in BreadthFirst())
        {
            var nameField = node == _root ? 0x7FFFFFFFu : nameOffsets[Convert.ToHexString(node.Name)] & 0x7FFFFFFF;
            if (node.IsFile) nameField |= 0x80000000;
            BinaryPrimitives.WriteUInt32BigEndian(entry, nameField);
            if (node.IsFile)
            {
                BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(4), (uint)node.FileOffset);
                BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(8), (uint)node.FileSize);
                var high = (uint)((node.FileSize >> 16) & 0xFFFF0000) | (uint)((node.FileOffset >> 32) & 0xFFFF);
                BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(12), high);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(4), node.FirstChildIndex);
                BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(8), (uint)node.Children.Count);
                BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(12), 0);
            }
            Write(entry);
        }
        var treeSize = _outputOffset - treeOffset;

        // Footer: hash covers everything before it plus the footer with a zeroed hash.
        var metaOffset = _outputOffset;
        var footer = new byte[ZArchiveFormat.FooterSize];
        ulong[] sections = [0, compressedSize, recordsOffset, recordsSize, namesOffset, namesSize, treeOffset, treeSize, metaOffset, 0, metaOffset, 0];
        for (var i = 0; i < sections.Length; i++)
            BinaryPrimitives.WriteUInt64BigEndian(footer.AsSpan(8 * i), sections[i]);
        BinaryPrimitives.WriteUInt64BigEndian(footer.AsSpan(96 + 32), _outputOffset + ZArchiveFormat.FooterSize);
        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(96 + 32 + 8), ZArchiveFormat.Version1);
        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(96 + 32 + 12), ZArchiveFormat.Magic);
        _sha.AppendData(footer);
        _sha.GetHashAndReset().CopyTo(footer.AsSpan(96));
        _output.Write(footer);
        _outputOffset += ZArchiveFormat.FooterSize;
        _output.Flush();
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            var take = Math.Min(data.Length, _block.Length - _blockFill);
            data[..take].CopyTo(_block.AsSpan(_blockFill));
            _blockFill += take;
            _inputOffset += (ulong)take;
            data = data[take..];
            if (_blockFill == _block.Length)
            {
                StoreBlock(_block);
                _blockFill = 0;
            }
        }
    }

    private void StoreBlock(byte[] block)
    {
        var at = _outputOffset;
        var size = _zstd.Wrap(block, _compressed);
        if (size >= ZArchiveFormat.BlockSize)
        {
            size = ZArchiveFormat.BlockSize;
            Write(block);
        }
        else
        {
            Write(_compressed.AsSpan(0, size));
        }
        if (_blocksWritten % ZArchiveFormat.EntriesPerOffsetRecord == 0)
            _records.Add((at, new ushort[ZArchiveFormat.EntriesPerOffsetRecord]));
        _records[^1].Sizes[_blocksWritten % ZArchiveFormat.EntriesPerOffsetRecord] = (ushort)(size - 1);
        _blocksWritten++;
    }

    private void Write(ReadOnlySpan<byte> data)
    {
        _output.Write(data);
        _sha.AppendData(data);
        _outputOffset += (ulong)data.Length;
    }

    private IEnumerable<Node> BreadthFirst()
    {
        var queue = new Queue<Node>();
        queue.Enqueue(_root);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            yield return node;
            foreach (var child in node.Children) queue.Enqueue(child);
        }
    }

    private static Node? Find(Node dir, byte[] name) =>
        dir.Children.FirstOrDefault(c => ZArchiveFormat.NamesEqual(c.Name, name));

    private static byte[] EncodeName(string part, string fullPath)
    {
        try { return ZArchiveFormat.NameEncoding.GetBytes(part); }
        catch (EncoderFallbackException)
        {
            throw new ZArchiveException($"'{fullPath}' has characters ZArchive can't store (names must fit Windows-1252).");
        }
    }

    public void Dispose()
    {
        _zstd.Dispose();
        _sha.Dispose();
        if (_ownsOutput) _output.Dispose();
    }
}

/// <summary>Reads a ZArchive: the file tree, file contents, and the integrity hash.</summary>
public sealed class ZArchiveReader : IDisposable
{
    public sealed record Entry(string Path, string Name, bool IsFile, ulong Offset, ulong Size, uint FirstChild, uint ChildCount, int Index);

    private readonly Stream _input;
    private readonly ulong _dataSize;
    private readonly byte[] _records;
    private readonly byte[] _names;
    private readonly byte[] _tree;
    private readonly byte[] _storedHash;
    private readonly Decompressor _zstd = new();

    public long Length { get; }

    private ZArchiveReader(Stream input)
    {
        _input = input;
        Length = input.Length;
        if (Length < ZArchiveFormat.FooterSize) throw new ZArchiveException("The file is too small to be a ZArchive.");
        var footer = new byte[ZArchiveFormat.FooterSize];
        input.Position = Length - ZArchiveFormat.FooterSize;
        input.ReadExactly(footer);
        if (BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(140)) != ZArchiveFormat.Magic ||
            BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(136)) != ZArchiveFormat.Version1)
            throw new ZArchiveException("Not a ZArchive (.wua/.zar) file, or an unsupported version.");
        if (BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(128)) != (ulong)Length)
            throw new ZArchiveException("The archive's recorded size doesn't match the file; it is truncated or damaged.");

        ulong Section(int i, out ulong size)
        {
            var offset = BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(16 * i));
            size = BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(16 * i + 8));
            if (offset + size > (ulong)Length) throw new ZArchiveException("A section of the archive lies outside the file.");
            return offset;
        }
        Section(0, out _dataSize);
        _records = ReadSection(Section(1, out var rs), rs);
        _names = ReadSection(Section(2, out var ns), ns);
        _tree = ReadSection(Section(3, out var ts), ts);
        _storedHash = footer.AsSpan(96, 32).ToArray();
        if (_tree.Length < ZArchiveFormat.EntrySize || _tree.Length % ZArchiveFormat.EntrySize != 0)
            throw new ZArchiveException("The archive's file tree is damaged.");
    }

    public static ZArchiveReader Open(string path) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20));

    private byte[] ReadSection(ulong offset, ulong size)
    {
        if (size > int.MaxValue) throw new ZArchiveException("An archive table is too large.");
        var data = new byte[size];
        _input.Position = (long)offset;
        _input.ReadExactly(data);
        return data;
    }

    private int EntryCount => _tree.Length / ZArchiveFormat.EntrySize;

    private Entry ReadEntry(int index, string parentPath)
    {
        if (index < 0 || index >= EntryCount) throw new ZArchiveException("The archive's file tree points outside itself.");
        var e = _tree.AsSpan(index * ZArchiveFormat.EntrySize, ZArchiveFormat.EntrySize);
        var nameField = BinaryPrimitives.ReadUInt32BigEndian(e);
        var isFile = (nameField & 0x80000000) != 0;
        var nameOffset = nameField & 0x7FFFFFFF;
        var name = nameOffset == 0x7FFFFFFF ? "" : ReadName(nameOffset);
        var path = parentPath.Length == 0 ? name : parentPath + "/" + name;
        var a = BinaryPrimitives.ReadUInt32BigEndian(e[4..]);
        var b = BinaryPrimitives.ReadUInt32BigEndian(e[8..]);
        var c = BinaryPrimitives.ReadUInt32BigEndian(e[12..]);
        return isFile
            ? new Entry(path, name, true, a | ((ulong)(c & 0xFFFF) << 32), b | ((ulong)(c & 0xFFFF0000) << 16), 0, 0, index)
            : new Entry(path, name, false, 0, 0, a, b, index);
    }

    private string ReadName(uint offset)
    {
        if (offset >= _names.Length) throw new ZArchiveException("A name lies outside the archive's name table.");
        int length = _names[offset];
        var start = (int)offset + 1;
        if ((length & 0x80) != 0)
        {
            length = (length & 0x7F) | (_names[offset + 1] << 7);
            start++;
        }
        if (start + length > _names.Length) throw new ZArchiveException("A name runs past the archive's name table.");
        return ZArchiveFormat.NameEncoding.GetString(_names, start, length);
    }

    /// <summary>The direct children of a folder entry (the root is index 0).</summary>
    public IEnumerable<Entry> Children(Entry folder)
    {
        if (folder.IsFile) yield break;
        for (var i = 0u; i < folder.ChildCount; i++)
            yield return ReadEntry((int)(folder.FirstChild + i), folder.Path);
    }

    public Entry Root => ReadEntry(0, "");

    /// <summary>Every entry below <paramref name="folder"/>, depth first, folders before their contents.</summary>
    public IEnumerable<Entry> Walk(Entry? folder = null)
    {
        var stack = new Stack<Entry>();
        stack.Push(folder ?? Root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var child in Children(dir).Reverse())
            {
                if (!child.IsFile) stack.Push(child);
            }
            foreach (var child in Children(dir))
                yield return child;
        }
    }

    /// <summary>Finds an entry by path (case-insensitive, like the format), or null.</summary>
    public Entry? Find(string path)
    {
        var entry = Root;
        foreach (var part in ZArchiveFormat.SplitPath(path))
        {
            var name = ZArchiveFormat.NameEncoding.GetBytes(part);
            entry = Children(entry).FirstOrDefault(c => ZArchiveFormat.NamesEqual(ZArchiveFormat.NameEncoding.GetBytes(c.Name), name));
            if (entry is null) return null;
        }
        return entry;
    }

    /// <summary>Copies a file's contents to <paramref name="destination"/>.</summary>
    public void CopyFile(Entry file, Stream destination, Action<long>? bytesCopied = null, CancellationToken token = default)
    {
        if (!file.IsFile) throw new ArgumentException("Not a file.", nameof(file));
        var block = new byte[ZArchiveFormat.BlockSize];
        var remaining = file.Size;
        var position = file.Offset;
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            var blockIndex = position / ZArchiveFormat.BlockSize;
            var within = (int)(position % ZArchiveFormat.BlockSize);
            ReadBlock(blockIndex, block);
            var take = (int)Math.Min((ulong)(ZArchiveFormat.BlockSize - within), remaining);
            destination.Write(block, within, take);
            bytesCopied?.Invoke(take);
            remaining -= (ulong)take;
            position += (ulong)take;
        }
    }

    private byte[] _compressedBlock = new byte[ZArchiveFormat.BlockSize];

    private void ReadBlock(ulong index, byte[] output)
    {
        var record = (int)(index / ZArchiveFormat.EntriesPerOffsetRecord);
        var slot = (int)(index % ZArchiveFormat.EntriesPerOffsetRecord);
        if ((record + 1) * ZArchiveFormat.OffsetRecordSize > _records.Length)
            throw new ZArchiveException("A file points past the archive's data.");
        var r = _records.AsSpan(record * ZArchiveFormat.OffsetRecordSize, ZArchiveFormat.OffsetRecordSize);
        var offset = BinaryPrimitives.ReadUInt64BigEndian(r);
        for (var i = 0; i < slot; i++)
            offset += (ulong)BinaryPrimitives.ReadUInt16BigEndian(r[(8 + 2 * i)..]) + 1;
        var size = BinaryPrimitives.ReadUInt16BigEndian(r[(8 + 2 * slot)..]) + 1;
        if (offset + (ulong)size > _dataSize) throw new ZArchiveException("A compressed block lies outside the archive's data.");

        _input.Position = (long)offset;
        if (size == ZArchiveFormat.BlockSize)
        {
            _input.ReadExactly(output);
            return;
        }
        _input.ReadExactly(_compressedBlock, 0, size);
        int got;
        try { got = _zstd.Unwrap(_compressedBlock.AsSpan(0, size), output); }
        catch (ZstdException ex) { throw new ZArchiveException($"A compressed block is damaged ({ex.Message})."); }
        if (got != ZArchiveFormat.BlockSize) throw new ZArchiveException("A compressed block decompressed to the wrong size.");
    }

    /// <summary>Recomputes the SHA-256 over the whole file and compares it with the footer's.</summary>
    public bool CheckIntegrity(Action<long>? bytesRead = null, CancellationToken token = default)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        _input.Position = 0;
        var buffer = new byte[1 << 20];
        var remaining = Length - ZArchiveFormat.FooterSize;
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            var n = _input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (n <= 0) throw new EndOfStreamException();
            sha.AppendData(buffer, 0, n);
            remaining -= n;
            bytesRead?.Invoke(n);
        }
        var footer = new byte[ZArchiveFormat.FooterSize];
        _input.ReadExactly(footer);
        footer.AsSpan(96, 32).Clear();
        sha.AppendData(footer);
        return sha.GetHashAndReset().AsSpan().SequenceEqual(_storedHash);
    }

    public void Dispose()
    {
        _zstd.Dispose();
        _input.Dispose();
    }
}
