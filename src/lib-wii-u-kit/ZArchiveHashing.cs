using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using ZstdSharp;

namespace LibWiiUKit;

/// <summary>What <see cref="ZArchiveReader.HashFiles(HashAlgorithmName, Action{long}?, CancellationToken)"/> found.</summary>
/// <param name="IntegrityOk">Whether the footer's SHA-256 matches the whole file.</param>
/// <param name="Files">Each file's path and the hash of its contents.</param>
public sealed record ZArchiveHashes(bool IntegrityOk, IReadOnlyDictionary<string, byte[]> Files);

public sealed partial class ZArchiveReader
{
    private const int HashBatchBlocks = 64; // 4 MiB of plain data per batch

    private sealed class HashBatch
    {
        public readonly byte[] Compressed = new byte[HashBatchBlocks * ZArchiveFormat.BlockSize];
        public readonly byte[] Plain = new byte[HashBatchBlocks * ZArchiveFormat.BlockSize];
        public readonly int[] Starts = new int[HashBatchBlocks];
        public readonly int[] Sizes = new int[HashBatchBlocks];
        public ulong FirstBlock;
        public int Count;
        public Task? Task;
    }

    /// <summary>
    /// Reads the archive once, front to back: checks the footer's SHA-256 (as
    /// <see cref="CheckIntegrity"/> does) and hashes every file's contents
    /// with <paramref name="algorithm"/>, decompressing blocks on several
    /// threads while the next ones are read. Each block is decompressed once,
    /// however many files share it. <paramref name="bytesRead"/> is called
    /// with the bytes of the archive read, which add up to <see cref="Length"/>.
    /// </summary>
    public ZArchiveHashes HashFiles(HashAlgorithmName algorithm, Action<long>? bytesRead = null, CancellationToken token = default) =>
        HashFiles(algorithm, 0, bytesRead, token);

    /// <summary>
    /// <see cref="HashFiles(HashAlgorithmName, Action{long}?, CancellationToken)"/>
    /// with at most <paramref name="threads"/> blocks decompressed at once
    /// (0: one per processor), however many batches overlap. One more thread
    /// reads and one hashes in order, as with any count; the hashes are the
    /// same whatever it is.
    /// </summary>
    public ZArchiveHashes HashFiles(HashAlgorithmName algorithm, int threads, Action<long>? bytesRead = null, CancellationToken token = default)
    {
        if (threads <= 0) threads = Environment.ProcessorCount;
        var files = Walk().Where(e => e.IsFile).OrderBy(e => e.Offset).ThenBy(e => e.Size).ToList();
        var dataEnd = files.Count == 0 ? 0 : files.Max(f => f.Offset + f.Size);
        var blockCount = (dataEnd + ZArchiveFormat.BlockSize - 1) / ZArchiveFormat.BlockSize;
        var blocks = BlockLayout(blockCount);
        if (blocks is null) return HashFilesBySeeking(files, algorithm, bytesRead, token);

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var feeder = new FileFeeder(files, algorithm);
        var decompressors = new ConcurrentBag<Decompressor>();
        using var slotsFree = new SemaphoreSlim(threads, threads); // shared by the two batches in flight
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = threads };
        HashBatch[] slots = [new(), new()];
        Task? previous = null;
        try
        {
            _input.Position = 0;
            ulong position = 0;
            for (ulong first = 0, k = 0; first < blockCount; first += HashBatchBlocks, k++)
            {
                token.ThrowIfCancellationRequested();
                var batch = slots[k % 2];
                batch.Task?.GetAwaiter().GetResult(); // the batch before last: its buffers are free again
                batch.FirstBlock = first;
                batch.Count = (int)Math.Min(HashBatchBlocks, blockCount - first);
                var start = blocks[first].Offset;
                var end = blocks[first + (ulong)batch.Count - 1].Offset + (ulong)blocks[first + (ulong)batch.Count - 1].Size;
                for (var i = 0; i < batch.Count; i++)
                {
                    batch.Starts[i] = (int)(blocks[first + (ulong)i].Offset - start);
                    batch.Sizes[i] = blocks[first + (ulong)i].Size;
                }
                var span = batch.Compressed.AsSpan(0, (int)(end - start));
                _input.ReadExactly(span);
                sha.AppendData(span);
                position = end;
                bytesRead?.Invoke(span.Length);

                var before = previous;
                batch.Task = previous = Task.Run(() =>
                {
                    Decompress(batch, decompressors, parallel, slotsFree);
                    before?.GetAwaiter().GetResult(); // files are fed in order
                    for (var i = 0; i < batch.Count; i++)
                        feeder.Feed((batch.FirstBlock + (ulong)i) * ZArchiveFormat.BlockSize, batch.Plain.AsSpan(i * ZArchiveFormat.BlockSize, ZArchiveFormat.BlockSize));
                });
            }
            previous?.GetAwaiter().GetResult();

            // The rest: padding, the tables and the footer (with its hash zeroed), hashed but not parsed.
            var rest = new byte[1 << 20];
            var remaining = (ulong)Length - ZArchiveFormat.FooterSize - position;
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                var n = (int)Math.Min((ulong)rest.Length, remaining);
                _input.ReadExactly(rest, 0, n);
                sha.AppendData(rest, 0, n);
                remaining -= (ulong)n;
                bytesRead?.Invoke(n);
            }
            var footer = new byte[ZArchiveFormat.FooterSize];
            _input.ReadExactly(footer);
            bytesRead?.Invoke(footer.Length);
            footer.AsSpan(96, 32).Clear();
            sha.AppendData(footer);
            var ok = sha.GetHashAndReset().AsSpan().SequenceEqual(_storedHash);
            return new ZArchiveHashes(ok, feeder.Finish());
        }
        finally
        {
            // On an error or a cancel, let the batches still running end before their buffers and hashes go.
            foreach (var slot in slots)
            {
                try { slot.Task?.Wait(); } catch (AggregateException) { }
            }
            feeder.Dispose();
            while (decompressors.TryTake(out var d)) d.Dispose();
        }
    }

    /// <summary>
    /// Where each of the first <paramref name="count"/> blocks lies, or null
    /// when they don't follow each other from the start of the file (which
    /// one front-to-back pass needs; writers lay them out that way).
    /// </summary>
    private (ulong Offset, int Size)[]? BlockLayout(ulong count)
    {
        if (count > (ulong)(_records.Length / ZArchiveFormat.OffsetRecordSize) * ZArchiveFormat.EntriesPerOffsetRecord)
            throw new ZArchiveException("A file points past the archive's data.");
        var blocks = new (ulong Offset, int Size)[count];
        ulong expected = 0;
        for (ulong i = 0; i < count; i++)
        {
            var r = _records.AsSpan((int)(i / ZArchiveFormat.EntriesPerOffsetRecord) * ZArchiveFormat.OffsetRecordSize, ZArchiveFormat.OffsetRecordSize);
            var slot = (int)(i % ZArchiveFormat.EntriesPerOffsetRecord);
            if (slot == 0 && BinaryPrimitives.ReadUInt64BigEndian(r) != expected) return null;
            var size = BinaryPrimitives.ReadUInt16BigEndian(r[(8 + 2 * slot)..]) + 1;
            // The footer bound matters too: the pass below hashes what follows the blocks up to the footer.
            if (expected + (ulong)size > _dataSize || expected + (ulong)size > (ulong)Length - ZArchiveFormat.FooterSize)
                throw new ZArchiveException("A compressed block lies outside the archive's data.");
            blocks[i] = (expected, size);
            expected += (ulong)size;
        }
        return blocks;
    }

    private static void Decompress(HashBatch batch, ConcurrentBag<Decompressor> decompressors, ParallelOptions parallel, SemaphoreSlim slotsFree)
    {
        Parallel.For(0, batch.Count, parallel, () => decompressors.TryTake(out var d) ? d : new Decompressor(), (i, _, zstd) =>
        {
            slotsFree.Wait();
            try { DecompressBlock(batch, i, zstd); }
            finally { slotsFree.Release(); }
            return zstd;
        }, decompressors.Add);
    }

    private static void DecompressBlock(HashBatch batch, int i, Decompressor zstd)
    {
        var input = batch.Compressed.AsSpan(batch.Starts[i], batch.Sizes[i]);
        var output = batch.Plain.AsSpan(i * ZArchiveFormat.BlockSize, ZArchiveFormat.BlockSize);
        if (batch.Sizes[i] == ZArchiveFormat.BlockSize)
        {
            input.CopyTo(output);
            return;
        }
        int got;
        try { got = zstd.Unwrap(input, output); }
        catch (ZstdException ex) { throw new ZArchiveException($"A compressed block is damaged ({ex.Message})."); }
        if (got != ZArchiveFormat.BlockSize) throw new ZArchiveException("A compressed block decompressed to the wrong size.");
    }

    /// <summary>For archives whose blocks aren't in file order: the integrity check, then each file read on its own.</summary>
    private ZArchiveHashes HashFilesBySeeking(List<Entry> files, HashAlgorithmName algorithm, Action<long>? bytesRead, CancellationToken token)
    {
        var ok = CheckIntegrity(bytesRead, token);
        var hashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var hash = IncrementalHash.CreateHash(algorithm);
        foreach (var file in files)
        {
            var sink = new HashSink(hash);
            CopyFile(file, sink, null, token);
            hashes[file.Path] = hash.GetHashAndReset();
        }
        return new ZArchiveHashes(ok, hashes);
    }

    /// <summary>Hands the archive's plain data, in order, to the hash of every file it belongs to.</summary>
    private sealed class FileFeeder(List<Entry> files, HashAlgorithmName algorithm) : IDisposable
    {
        private readonly Dictionary<string, byte[]> _done = new(StringComparer.Ordinal);
        private readonly List<(Entry File, IncrementalHash Hash)> _open = [];
        private int _next;

        public void Feed(ulong at, ReadOnlySpan<byte> data)
        {
            var end = at + (ulong)data.Length;
            while (_next < files.Count && files[_next].Offset < end)
            {
                var file = files[_next++];
                if (file.Size == 0) Close(file, null);
                else _open.Add((file, IncrementalHash.CreateHash(algorithm)));
            }
            for (var i = 0; i < _open.Count; i++)
            {
                var (file, hash) = _open[i];
                var from = Math.Max(at, file.Offset);
                var to = Math.Min(end, file.Offset + file.Size);
                if (to > from) hash.AppendData(data[(int)(from - at)..(int)(to - at)]);
                if (file.Offset + file.Size <= end)
                {
                    Close(file, hash);
                    _open.RemoveAt(i--);
                }
            }
        }

        private void Close(Entry file, IncrementalHash? hash)
        {
            if (hash is null)
            {
                using var empty = IncrementalHash.CreateHash(algorithm);
                _done[file.Path] = empty.GetHashAndReset();
                return;
            }
            _done[file.Path] = hash.GetHashAndReset();
            hash.Dispose();
        }

        public Dictionary<string, byte[]> Finish()
        {
            // Empty files past the last block: nothing was fed for them.
            while (_next < files.Count) Close(files[_next++], null);
            if (_open.Count > 0) throw new ZArchiveException("A file points past the archive's data.");
            return _done;
        }

        public void Dispose()
        {
            foreach (var (_, hash) in _open) hash.Dispose();
            _open.Clear();
        }
    }

    /// <summary>A write-only stream that only hashes.</summary>
    private sealed class HashSink(IncrementalHash hash) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count) => hash.AppendData(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => hash.AppendData(buffer);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
