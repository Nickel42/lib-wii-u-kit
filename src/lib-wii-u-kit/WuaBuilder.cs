using System.Diagnostics;
using System.Security.Cryptography;

namespace LibWiiUKit;

public enum WuaLogLevel { Debug, Info, Warning }

/// <summary>What <see cref="WuaBuilder.Build(string, IReadOnlyList{WiiUTitle}, WuaBuildOptions?)"/> needs besides the titles.</summary>
public sealed class WuaBuildOptions
{
    /// <summary>The keys for NUS titles and disc images; folders and .wua files need none.</summary>
    public WiiUKeys Keys { get; init; } = WiiUKeys.Empty;

    /// <summary>Called when the build moves on to another stage ("Packing", "Verifying", ...).</summary>
    public Action<string>? Stage { get; init; }

    /// <summary>Called with how far the whole build is, from 0 to 1.</summary>
    public Action<double>? Progress { get; init; }

    /// <summary>Called with what the build notices on the way: which title comes from where, skipped entries.</summary>
    public Action<WuaLogLevel, string>? Log { get; init; }

    public CancellationToken CancellationToken { get; init; }
}

/// <param name="Output">The .wua written.</param>
/// <param name="Titles">The titles it holds, base game first.</param>
/// <param name="FileCount">How many files it holds.</param>
/// <param name="InputBytes">The titles' files, uncompressed.</param>
/// <param name="OutputBytes">The size of the .wua.</param>
/// <param name="Elapsed">How long packing took (verifying not included).</param>
public sealed record WuaBuildResult(string Output, IReadOnlyList<WiiUTitle> Titles, int FileCount, long InputBytes, long OutputBytes, TimeSpan Elapsed)
{
    public double Ratio => InputBytes > 0 ? (double)OutputBytes / InputBytes : 0;
}

/// <summary>
/// Builds .wua archives (Cemu's ZArchive), one per game, from extracted title
/// folders, NUS titles, WUD/WUX disc images and other .wua files, holding the
/// base game, its newest update and its DLC.
/// </summary>
public static class WuaBuilder
{
    /// <summary>The base title IDs of every game found in <paramref name="sources"/>.</summary>
    public static IReadOnlyList<ulong> Games(IEnumerable<string> sources, WiiUKeys? keys = null) =>
        sources.SelectMany(p => WiiUTitles.Read(p, keys)).Select(t => t.BaseId).Distinct().ToList();

    /// <summary>
    /// The titles of one game to put in its .wua: the newest version of each
    /// title ID belonging to <paramref name="baseId"/>, base game first, then
    /// updates, DLC and the rest. Dropped older versions are logged as warnings.
    /// </summary>
    public static IReadOnlyList<WiiUTitle> ChooseTitles(IEnumerable<WiiUTitle> titles, ulong baseId, Action<WuaLogLevel, string>? log = null)
    {
        var chosen = titles.Where(t => t.BaseId == baseId).GroupBy(t => t.TitleId)
            .Select(g =>
            {
                var newest = g.OrderByDescending(t => t.Version).First();
                foreach (var older in g.Where(t => t != newest && t.Version != newest.Version))
                    log?.Invoke(WuaLogLevel.Warning, $"  {older.TitleId:X16}: using v{newest.Version}, not v{older.Version} from {Path.GetFileName(older.SourcePath)}");
                return newest;
            })
            .OrderBy(t => t.Kind).ThenBy(t => t.TitleId).ToList();
        if (chosen.Count == 1 && chosen[0].Kind == WiiUTitleKind.Update)
            log?.Invoke(WuaLogLevel.Warning, $"  {baseId:X16}: only an update was found, no base game; the .wua will hold just the update.");
        return chosen;
    }

    /// <summary>The game's name, from its base game (or demo), else from any title that has one; "" when none does.</summary>
    public static string GameName(IReadOnlyList<WiiUTitle> titles) =>
        titles.FirstOrDefault(t => t.Kind is WiiUTitleKind.Game or WiiUTitleKind.Demo && t.Name.Length > 0)?.Name
        ?? titles.FirstOrDefault(t => t.Name.Length > 0)?.Name ?? "";

    /// <summary>
    /// Reads every title in <paramref name="sources"/>, picks the ones for
    /// <paramref name="baseId"/> (see <see cref="ChooseTitles"/>) and writes
    /// them to <paramref name="outputFolder"/> under the game's
    /// <see cref="WiiUTitles.WuaFileName"/>, replacing any file of that name.
    /// </summary>
    public static WuaBuildResult Build(IEnumerable<string> sources, ulong baseId, string outputFolder, WuaBuildOptions? options = null)
    {
        options ??= new WuaBuildOptions();
        var chosen = ChooseTitles(sources.SelectMany(p => WiiUTitles.Read(p, options.Keys)), baseId, options.Log);
        if (chosen.Count == 0) throw new InvalidDataException($"No titles for {baseId:X16} were found.");
        Directory.CreateDirectory(outputFolder);
        return Build(Path.Combine(outputFolder, WiiUTitles.WuaFileName(baseId, GameName(chosen))), chosen, options);
    }

    /// <summary>
    /// Writes <paramref name="titles"/> to <paramref name="output"/>, each in
    /// its own <see cref="WiiUTitle.ArchiveFolder"/>. The archive goes to a
    /// temporary file next to <paramref name="output"/>, is verified (see
    /// <see cref="Verify"/>), and only then moved into place, replacing any
    /// file there (which may be one of the titles' own .wua). A failed or
    /// cancelled build leaves nothing behind.
    /// </summary>
    public static WuaBuildResult Build(string output, IReadOnlyList<WiiUTitle> titles, WuaBuildOptions? options = null)
    {
        options ??= new WuaBuildOptions();
        var keys = options.Keys;
        var token = options.CancellationToken;
        foreach (var t in titles)
            options.Log?.Invoke(WuaLogLevel.Debug, $"  {t.ArchiveFolder} ← {t.SourcePath}{(t.FromWua ? $" ({t.WuaFolder})" : t.IsEncrypted ? $" ({t.Source}, decrypted)" : "")}");

        var temp = output + $".{Guid.NewGuid().ToString("N")[..8]}.tmp";
        var sw = Stopwatch.StartNew();
        var hashes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var inputBytes = titles.Sum(t => TitleSize(t, keys));
        try
        {
            var total = Math.Max(1, inputBytes);
            long done = 0;
            options.Stage?.Invoke("Packing");
            using (var writer = new ZArchiveWriter(temp))
            {
                foreach (var title in titles)
                {
                    token.ThrowIfCancellationRequested();
                    writer.AddDirectory(title.ArchiveFolder);
                    void Progress(long n) { done += n; options.Progress?.Invoke(0.85 * done / total); }
                    if (title.FromWua) CopyFromWua(writer, title, hashes, Progress, token);
                    else if (title.IsEncrypted) CopyFromEncrypted(writer, title, keys, hashes, Progress, options);
                    else CopyFromFolder(writer, title, hashes, Progress, token);
                }
                options.Stage?.Invoke("Finishing");
                writer.Finish();
            }
            sw.Stop();

            options.Stage?.Invoke("Verifying");
            Verify(temp, hashes, f => options.Progress?.Invoke(0.85 + 0.15 * f), token);
            File.Move(temp, output, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        return new WuaBuildResult(output, titles, hashes.Count, inputBytes, new FileInfo(output).Length, sw.Elapsed);
    }

    /// <summary>
    /// Reopens a written archive and checks it the way a reader would: the
    /// footer's SHA-256 over the whole file, then every file decompressed and
    /// compared, by SHA-256, with <paramref name="expected"/> (archive path to
    /// SHA-256 of what went in).
    /// </summary>
    public static void Verify(string archive, IReadOnlyDictionary<string, byte[]> expected, Action<double>? progress = null, CancellationToken token = default)
    {
        using var reader = ZArchiveReader.Open(archive);
        long done = 0;
        var total = Math.Max(1, reader.Length * 2);
        if (!reader.CheckIntegrity(n => { done += n; progress?.Invoke((double)done / total); }, token))
            throw new ZArchiveException("The new archive's SHA-256 doesn't match its contents.");

        var seen = 0;
        foreach (var entry in reader.Walk().Where(e => e.IsFile))
        {
            if (!expected.TryGetValue(entry.Path, out var want))
                throw new ZArchiveException($"The new archive has an unexpected file: {entry.Path}");
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var sink = new HashOnlyStream(sha);
            reader.CopyFile(entry, sink, n => { done += n; progress?.Invoke(Math.Min(1, (double)done / total)); }, token);
            if (!sha.GetHashAndReset().AsSpan().SequenceEqual(want))
                throw new ZArchiveException($"{entry.Path} reads back differently from its source.");
            seen++;
        }
        if (seen != expected.Count)
            throw new ZArchiveException($"The new archive holds {seen} files; {expected.Count} went in.");
        progress?.Invoke(1);
    }

    /// <summary>The size of a title's files, uncompressed (what packing it reads).</summary>
    public static long TitleSize(WiiUTitle title, WiiUKeys? keys = null)
    {
        if (title.IsEncrypted)
        {
            using var opened = WiiUTitles.OpenEncrypted(title.SourcePath, title.Source, keys ?? WiiUKeys.Empty);
            return opened.Volume.Entries.Where(e => !e.IsDirectory && !e.IsLink).Sum(e => e.Size);
        }
        if (!title.FromWua)
            return Directory.EnumerateFiles(title.SourcePath, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        using var reader = ZArchiveReader.Open(title.SourcePath);
        var folder = reader.Find(title.WuaFolder!);
        return folder is null ? 0 : reader.Walk(folder).Where(e => e.IsFile).Sum(e => (long)e.Size);
    }

    private static void CopyFromFolder(ZArchiveWriter writer, WiiUTitle title, Dictionary<string, byte[]> hashes,
        Action<long> progress, CancellationToken token)
    {
        var root = title.SourcePath;
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderBy(d => d, StringComparer.Ordinal))
            writer.AddDirectory(title.ArchiveFolder + "/" + Relative(root, dir));
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var path = title.ArchiveFolder + "/" + Relative(root, file);
            using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            using var hashing = new HashingStream(src);
            writer.AddFile(path, hashing, progress, token);
            hashes[path] = hashing.Hash();
        }
    }

    private static void CopyFromWua(ZArchiveWriter writer, WiiUTitle title, Dictionary<string, byte[]> hashes,
        Action<long> progress, CancellationToken token)
    {
        using var reader = ZArchiveReader.Open(title.SourcePath);
        var folder = reader.Find(title.WuaFolder!) ?? throw new InvalidDataException($"{title.WuaFolder} vanished from {title.SourcePath}.");
        var prefix = folder.Path.Length + 1;
        foreach (var entry in reader.Walk(folder))
        {
            var path = title.ArchiveFolder + "/" + entry.Path[prefix..];
            if (!entry.IsFile)
            {
                writer.AddDirectory(path);
                continue;
            }
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            writer.AddFile(path, sink =>
            {
                using var tee = new TeeStream(sink, sha);
                reader.CopyFile(entry, tee, progress, token);
            });
            hashes[path] = sha.GetHashAndReset();
        }
    }

    /// <summary>
    /// Decrypts an NUS title or a disc's game partition straight into the
    /// archive. Hashed clusters are checked block by block as they're read;
    /// an NUS title's raw contents are first checked against its TMD.
    /// </summary>
    private static void CopyFromEncrypted(ZArchiveWriter writer, WiiUTitle title, WiiUKeys keys, Dictionary<string, byte[]> hashes,
        Action<long> progress, WuaBuildOptions options)
    {
        var token = options.CancellationToken;
        using var opened = WiiUTitles.OpenEncrypted(title.SourcePath, title.Source, keys);
        if (opened.Contents is { } contents)
        {
            options.Stage?.Invoke($"Checking {title.KindText} contents");
            var n = opened.Volume.VerifyRawClusters(contents, null, token);
            options.Log?.Invoke(WuaLogLevel.Debug, $"  {title.ArchiveFolder}: {n} raw content(s) match the TMD");
            options.Stage?.Invoke("Packing");
        }
        var links = 0;
        foreach (var entry in opened.Volume.Entries)
        {
            token.ThrowIfCancellationRequested();
            var path = title.ArchiveFolder + "/" + entry.Path;
            if (entry.IsDirectory)
            {
                writer.AddDirectory(path);
                continue;
            }
            if (entry.IsLink)
            {
                links++;
                continue;
            }
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            writer.AddFile(path, sink =>
            {
                using var tee = new TeeStream(sink, sha);
                opened.Volume.Copy(entry, tee, progress, token);
            });
            hashes[path] = sha.GetHashAndReset();
        }
        if (links > 0)
            options.Log?.Invoke(WuaLogLevel.Warning, $"  {title.ArchiveFolder}: skipped {links} linked file entr{(links == 1 ? "y" : "ies")} (they point at data elsewhere on the disc)");
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>Reads through to the source while hashing what passes.</summary>
    private sealed class HashingStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public byte[] Hash() => _sha.GetHashAndReset();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            _sha.AppendData(buffer, offset, n);
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _sha.Dispose(); base.Dispose(disposing); }
    }

    /// <summary>Writes through to a stream while hashing.</summary>
    private sealed class TeeStream(Stream inner, IncrementalHash sha) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            sha.AppendData(buffer, offset, count);
            inner.Write(buffer, offset, count);
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>A write-only stream that only hashes.</summary>
    private sealed class HashOnlyStream(IncrementalHash sha) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count) => sha.AppendData(buffer, offset, count);
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
