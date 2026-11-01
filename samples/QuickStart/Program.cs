using LibWiiUKit;

// quickstart [--keys keys.txt] [--out folder] <title folder|NUS folder|.wud|.wux|.wua>...
// Builds one .wua per game found in the inputs (base game, newest update, DLC),
// printing progress, then lists what each .wua holds.
string? keysPath = null;
string? outFolder = null;
var inputs = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--keys" && i + 1 < args.Length) keysPath = args[++i];
    else if (args[i] == "--out" && i + 1 < args.Length) outFolder = args[++i];
    else inputs.Add(args[i]);
}
if (inputs.Count == 0)
{
    Console.Error.WriteLine("usage: quickstart [--keys keys.txt] [--out folder] <title folder|NUS folder|.wud|.wux|.wua>...");
    return 2;
}
foreach (var input in inputs)
{
    if (WiiUTitles.UnsupportedReason(input) is { } reason)
    {
        Console.Error.WriteLine($"{input}: {reason}");
        return 1;
    }
}

// Without --keys, Cemu's own keys.txt is used if there is one. Folders and .wua files need no keys.
var keys = WiiUKeys.Load(keysPath);
Console.WriteLine($"Keys: {keys.Describe()}");
foreach (var problem in keys.Problems) Console.WriteLine($"  {problem}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    foreach (var game in WuaBuilder.Games(inputs, keys))
    {
        var output = outFolder ?? Path.GetDirectoryName(Path.GetFullPath(inputs[0].TrimEnd('/', '\\')))!;
        var stage = "";
        var result = WuaBuilder.Build(inputs, game, output, new WuaBuildOptions
        {
            Keys = keys,
            CancellationToken = cts.Token,
            Stage = s => { stage = s; Console.WriteLine(); },
            Progress = p => Console.Write($"\r{stage,-24} {p,6:P0}"),
            Log = (level, text) => { if (level != WuaLogLevel.Debug) Console.WriteLine($"\n{level}: {text.Trim()}"); },
        });
        Console.WriteLine();
        Console.WriteLine($"{result.Output}: {result.FileCount:N0} files, {result.InputBytes:N0} → {result.OutputBytes:N0} bytes ({result.Ratio:P0}) in {result.Elapsed.TotalSeconds:N1} s");
        foreach (var title in WiiUTitles.FromWua(result.Output))
            Console.WriteLine($"  {title.ArchiveFolder}  {title}");
    }
}
catch (WiiUKeysMissingException ex)
{
    Console.Error.WriteLine($"\n{ex.Message}  Pass --keys <path to keys.txt>.");
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("\nCancelled; nothing was written.");
    return 130;
}
catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException or NotSupportedException)
{
    Console.Error.WriteLine($"\nFailed: {ex.Message}");
    return 1;
}
return 0;
