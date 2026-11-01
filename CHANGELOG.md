# Changelog

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
