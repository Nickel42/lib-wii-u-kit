# Changelog

## 1.0.0-rc.2 (2026-11-16)

- **Faster packing:** `ZArchiveWriter` compresses blocks on several threads
  (`CompressionThreads`, default one per processor) and writes them in order,
  so the archive is byte for byte the same as before. This also speeds up
  any other ZArchive (.zar) written with it.
- **Faster verifying:** new `ZArchiveReader.HashFiles(algorithm)` checks the
  footer's SHA-256 and hashes every file in one front-to-back read,
  decompressing on several threads. `WuaBuilder.Verify` uses it, so a new
  .wua is read once instead of twice. `ZArchiveReader` also keeps the last
  block it decompressed, so files sharing a block don't decompress it again.
- **Faster decrypting:** NUS titles and disc images are decrypted in runs of
  up to 1 MiB, hashed blocks checked on several threads, without setting the
  AES key again for every sector; consecutive WUX sectors are read in one go.
- **Less garbage:** no 1 MiB buffer per file added to an archive; folder
  entries are looked up by name instead of one by one.
- **Dependency:** ZstdSharp.Port 0.8.6 → 0.8.8 (bug fixes).
- **Title key lists in keys.txt:** a line holding a 16-digit title ID and that
  title's 32-digit key is now read, not reported. Those keys are kept by title
  (`WiiUKeys.TitleKeys`) and tried first for their own title, so a NUS title opens
  with just its title key. `WiiUKeys.IsEmpty` covers both kinds of key, and
  `Describe()` counts the title keys.
- **One problem per keys.txt:** lines that aren't keys are reported in a single
  message per file (how many, and the first five line numbers), not one per line.

## 1.0.0-rc.1 (2026-10-31)

First release, extracted from NoIntrovert's Wii U converter.

- `WuaBuilder`: builds one verified .wua per game from extracted title folders,
  NUS titles, WUD (also split) and WUX disc images and other .wua files: the base
  game, the newest update and the DLC. Writes to a temporary file, verifies every
  file by SHA-256 and moves it into place; a failed or cancelled build leaves
  nothing behind. Progress, stage and log callbacks, cancellation.
- `ZArchiveWriter` / `ZArchiveReader`: Cemu's ZArchive format (version 1) in
  managed code, with the footer's SHA-256 integrity check.
- `WiiUTitles`: reads titles from folders, .wua, NUS titles and disc images;
  `WuaFileName`, `UnsupportedReason`.
- `WiiUKeys`: loads the user's keys.txt (Cemu's format), by default from Cemu's
  user-data folder; reports lines that aren't keys. No keys are included.
- `WiiUEncrypted`: NUS and disc image detection, `ImageFiles` for split WUDs.
- Paths given to `ZArchiveWriter` and `ZArchiveReader.Find` may use `\` as well
  as `/` between folders (in NoIntrovert, `\` was taken as part of a name).
