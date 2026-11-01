# lib-wii-u-kit

Convert Wii U games to Cemu's **.wua** from .NET, on Windows, macOS, Linux and
Android. One .wua per game, holding the base game, its newest update and its
DLC, written to a temporary file, verified by reading it back, and only then
moved into place.

Pure managed code (net8.0 and net10.0). No native libraries; the only
dependency is [ZstdSharp.Port](https://github.com/oleg-st/ZstdSharp).

| Input | Notes |
| --- | --- |
| Extracted title folder (`code/`, `content/`, `meta/`) | ID and version from `code/app.xml`, name from `meta/meta.xml` |
| NUS title (`title.tmd`, `title.tik`, `*.app`) | Decrypted with your keys; raw contents checked against the TMD, hashed ones block by block. Personalised (console-bound) tickets are refused. |
| WUD disc image, also split (`game_part1.wud`, `game_part2.wud`, ...) | Game partition decrypted with your keys; a `game.key` disc key file next to the image is used too |
| WUX (deduplicated WUD) | As WUD |
| .wua | Its titles are copied over, so a newer update or DLC can be merged into an existing .wua |

| Output | |
| --- | --- |
| .wua / .zar | Cemu's ZArchive format, version 1: 64 KiB zstd blocks, SHA-256 footer. `ZArchiveWriter` and `ZArchiveReader` read and write any ZArchive, not just Wii U ones. |

| Platform | Runtime | Native libraries |
| --- | --- | --- |
| Windows (x64, ARM64) | .NET 8+ | none |
| macOS (x64, ARM64) | .NET 8+ | none |
| Linux (x64, ARM64) | .NET 8+ | none |
| Android | .NET 8+ for Android (`net8.0-android` and later) | none; trim- and AOT-safe |

## Install

```bash
dotnet add package lib-wii-u-kit
```

Or reference `src/lib-wii-u-kit/lib-wii-u-kit.csproj` from a checkout or git
submodule; there is nothing else to import.

## Using it

```csharp
using LibWiiUKit;

var keys = WiiUKeys.Load(null);          // Cemu's keys.txt, if there is one
string[] sources = ["Game", "Game Update", "Game DLC"];   // folders, NUS titles, .wud/.wux, .wua

foreach (var baseId in WuaBuilder.Games(sources, keys))
{
    var result = WuaBuilder.Build(sources, baseId, "out", new WuaBuildOptions
    {
        Keys = keys,
        Stage = stage => Console.WriteLine(stage),             // "Packing", "Verifying", ...
        Progress = p => Console.Write($"\r{p:P0}"),             // 0 to 1, whole build
        Log = (level, text) => Console.WriteLine($"{level}: {text}"),
    });
    Console.WriteLine($"{result.Output}: {result.FileCount} files, {result.Ratio:P0} of {result.InputBytes:N0} bytes");
}
```

The file name comes from `WiiUTitles.WuaFileName` (`Game Name [0005000010101C00].wua`,
made safe for every platform), and an existing file of that name is replaced
once the new one verifies. To choose the titles or the output path yourself,
read them with `WiiUTitles.Read`, pick with `WuaBuilder.ChooseTitles` (newest
version of each title ID; older ones are logged as warnings) and call
`WuaBuilder.Build(outputPath, titles, options)`.

Reading a .wua (or any ZArchive):

```csharp
using var reader = ZArchiveReader.Open("Game [0005000010101C00].wua");
if (!reader.CheckIntegrity()) throw new InvalidDataException("damaged");
foreach (var entry in reader.Walk().Where(e => e.IsFile))
    Console.WriteLine($"{entry.Path}  {entry.Size}");
var meta = reader.Find("0005000010101c00_v0/meta/meta.xml")!;
using var output = File.Create("meta.xml");
reader.CopyFile(meta, output);
```

Errors: `WiiUKeysMissingException` when an encrypted source is opened with no
keys at all (a `KeyNotFoundException`, which is also thrown when none of the
keys fits), `InvalidDataException` for damaged or unrecognised input,
`ZArchiveException` (an `IOException`) for damaged archives, and
`OperationCanceledException` after `WuaBuildOptions.CancellationToken` is
cancelled. A failed or cancelled build leaves nothing behind.
`WiiUTitles.UnsupportedReason(path)` names inputs that can't be converted (an
NUS title without its ticket, a WUHB homebrew bundle) instead of skipping them.

## Keys

The library ships no keys. NUS titles and disc images need your own
`keys.txt`, in the same format as Cemu's: one 32-digit hex key per line,
anything after `#` or `;` is a comment, and spaces, tabs, `-` and `_` are
ignored. Which key is which doesn't matter; each is tried as a disc key, the
common key or a title key.

`WiiUKeys.Load(path, searchPaths)` loads `path` when given; otherwise every
keys.txt that exists in `searchPaths`, which defaults to Cemu's user-data folder
(`WiiUKeys.DefaultLocations()`: `%APPDATA%\Cemu\keys.txt` on Windows,
`~/Library/Application Support/Cemu/keys.txt` on macOS,
`$XDG_DATA_HOME/Cemu/keys.txt` or `~/.local/share/Cemu/keys.txt` elsewhere).
Lines that aren't keys are listed in `Problems`. Extracted title folders and
.wua files need no keys.

## Tests

```bash
dotnet test lib-wii-u-kit.sln
```

The tests build their own made-up titles, NUS titles, WUD, split WUD and WUX
images under made-up keys; no real game data is needed.

`samples/QuickStart` is a console program
(`dotnet run --project samples/QuickStart -- [--keys keys.txt] [--out folder] <inputs>...`);
`samples/AndroidSmoke` is a .NET for Android app that does a ZArchive round trip
and a small .wua build on the device (`dotnet workload install android`).

## Continuous integration

`.github/workflows/ci.yml` runs the tests on Windows, macOS, Linux x64 and Linux
ARM64 under .NET 8 and 10, builds the Android sample (Release, trimmed) and packs
the NuGet package. It runs only when started by hand (`workflow_dispatch`);
`release.yml` does the same for a `v*` tag and attaches the package to a GitHub
release.

## License

MIT for this project's code. ZstdSharp.Port is MIT; the ZArchive format comes
from Cemu's ZArchive library (MIT No Attribution). See
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
