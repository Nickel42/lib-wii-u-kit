using System.Security.Cryptography;
using Android.App;
using Android.OS;
using Android.Widget;
using LibWiiUKit;

namespace AndroidSmoke;

/// <summary>
/// Writes a ZArchive (zstd blocks, Windows-1252 names), reads it back and
/// checks its SHA-256 and contents; then builds a .wua from a tiny made-up
/// title folder and reads its title back. Shows what happened.
/// </summary>
[Activity(Label = "Wii U library smoke test", MainLauncher = true)]
public class MainActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        string text;
        try
        {
            var dir = Path.Combine(CacheDir!.AbsolutePath, "smoke");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            var lines = new List<string>();

            // ZArchive round trip.
            var files = new Dictionary<string, byte[]>
            {
                ["data/hello.txt"] = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("hello from Android ", 10_000))),
                ["data/random.bin"] = RandomNumberGenerator.GetBytes(100_000),
                ["data/café.txt"] = [],
            };
            var archive = Path.Combine(dir, "smoke.zar");
            using (var writer = new ZArchiveWriter(archive))
            {
                writer.AddDirectory("data");
                foreach (var (path, data) in files) writer.AddFile(path, new MemoryStream(data));
                writer.Finish();
            }
            using (var reader = ZArchiveReader.Open(archive))
            {
                if (!reader.CheckIntegrity()) throw new InvalidDataException("SHA-256 mismatch");
                foreach (var (path, data) in files)
                {
                    var entry = reader.Find(path) ?? throw new InvalidDataException($"{path} is missing");
                    var ms = new MemoryStream();
                    reader.CopyFile(entry, ms);
                    if (!ms.ToArray().AsSpan().SequenceEqual(data)) throw new InvalidDataException($"{path} reads back differently");
                }
                lines.Add($"ZArchive: {files.Count} files, {reader.Length:N0} bytes, SHA-256 OK");
            }

            // A .wua from a title folder.
            var title = Path.Combine(dir, "title");
            Directory.CreateDirectory(Path.Combine(title, "code"));
            Directory.CreateDirectory(Path.Combine(title, "meta"));
            Directory.CreateDirectory(Path.Combine(title, "content"));
            File.WriteAllText(Path.Combine(title, "code", "app.xml"),
                "<app><title_id>0005000010101C00</title_id><title_version>0</title_version></app>");
            File.WriteAllText(Path.Combine(title, "meta", "meta.xml"),
                "<menu><title_id>0005000010101C00</title_id><longname_en>Smoke Test</longname_en></menu>");
            File.WriteAllBytes(Path.Combine(title, "content", "data.bin"), files["data/hello.txt"]);
            var result = WuaBuilder.Build([title], 0x0005000010101C00, Path.Combine(dir, "out"));
            var read = WiiUTitles.Read(result.Output).Single();
            lines.Add($"{Path.GetFileName(result.Output)}: {result.FileCount} files, {result.OutputBytes:N0} bytes, {read}");

            text = string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            text = "Failed: " + ex;
        }
        SetContentView(new TextView(this) { Text = text });
    }
}
