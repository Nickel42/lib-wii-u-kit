# Third-party components

lib-wii-u-kit's own code is under the MIT license (`LICENSE`). It is pure
managed code and ships no native libraries and no keys.

## ZstdSharp.Port 0.8.6 (NuGet dependency)

Copyright Oleg Stepanischev, MIT license. https://github.com/oleg-st/ZstdSharp

A C# port of Zstandard (zstd), https://github.com/facebook/zstd, Copyright Meta
Platforms, Inc. and affiliates, under the BSD license (dual-licensed with
GPLv2). Used for the zstd blocks of ZArchive files.

## ZArchive format (Cemu)

The .wua / .zar file format is defined by Exzap's ZArchive library
(https://github.com/Exzap/ZArchive, version 0.1.2), Copyright 2022 Exzap, MIT No
Attribution license. lib-wii-u-kit contains no code from it: `ZArchiveWriter`
and `ZArchiveReader` are an independent C# implementation written to be
compatible with the format, so archives it writes open in Cemu and archives
Cemu's tools write open here.

## Wii U formats

The layouts of title folders, NUS titles (TMD, ticket, .app contents), the
Wii U file system table and WUD/WUX disc images follow public documentation of
those formats and the behaviour of the dumps themselves. No code or keys from
other projects are included.
