using System.Collections.Concurrent;
using ZstdSharp;

namespace LibWiiUKit;

/// <summary>
/// Compresses ZArchive blocks on the thread pool while the writer fills the
/// next ones, and hands them back in the order they were submitted. Each
/// block is its own one-shot zstd frame at the archive's level, so the bytes
/// are the same as compressing them one after another on one thread. Not
/// thread-safe: one writer submits and drains.
/// </summary>
internal sealed class BlockCompressor : IDisposable
{
    public delegate void StoreAction(byte[] block, byte[] compressed, int size);

    private sealed class Job(byte[] input, byte[] output)
    {
        public byte[] Input { get; set; } = input;
        public byte[] Output { get; } = output;
        public int Size { get; set; }
        public Task? Task { get; set; }
    }

    private static readonly int CompressBound = Compressor.GetCompressBound(ZArchiveFormat.BlockSize);

    private readonly int _maxInFlight;
    private readonly Queue<Job> _inFlight = new();
    private readonly Stack<Job> _free = new();
    private readonly ConcurrentBag<Compressor> _compressors = [];

    public BlockCompressor(int threads)
    {
        // A few blocks per thread keeps every worker busy while the oldest is written.
        _maxInFlight = threads * 4;
    }

    /// <summary>
    /// Starts compressing <paramref name="block"/> (which the caller must no
    /// longer touch) and returns an empty block to fill next. When too many
    /// blocks are in flight, waits for the oldest and stores it first.
    /// </summary>
    public byte[] Submit(byte[] block, StoreAction store)
    {
        while (_inFlight.Count >= _maxInFlight) StoreOldest(store);
        Job job;
        byte[] next;
        if (_free.Count > 0)
        {
            job = _free.Pop();
            next = job.Input; // already written: reuse it
            job.Input = block;
        }
        else
        {
            job = new Job(block, new byte[CompressBound]);
            next = new byte[ZArchiveFormat.BlockSize];
        }
        job.Task = Task.Run(() => job.Size = Compress(job.Input, job.Output));
        _inFlight.Enqueue(job);
        return next;
    }

    /// <summary>Stores every block still in flight, in order.</summary>
    public void Drain(StoreAction store)
    {
        while (_inFlight.Count > 0) StoreOldest(store);
    }

    private void StoreOldest(StoreAction store)
    {
        var job = _inFlight.Peek();
        job.Task!.GetAwaiter().GetResult();
        _inFlight.Dequeue();
        store(job.Input, job.Output, job.Size);
        job.Task = null;
        _free.Push(job);
    }

    private int Compress(byte[] input, byte[] output)
    {
        if (!_compressors.TryTake(out var zstd)) zstd = new Compressor(ZArchiveFormat.ZstdLevel);
        try { return zstd.Wrap(input, output); }
        finally { _compressors.Add(zstd); }
    }

    public void Dispose()
    {
        // A writer abandoned mid-way (an error, a cancel) still has work running: let it end before freeing the compressors.
        foreach (var job in _inFlight)
        {
            try { job.Task?.Wait(); } catch (AggregateException) { }
        }
        _inFlight.Clear();
        while (_compressors.TryTake(out var zstd)) zstd.Dispose();
    }
}
