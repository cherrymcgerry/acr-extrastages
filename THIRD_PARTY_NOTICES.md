# Third-party notices

ACR Extra Stages is licensed under MIT OR Apache-2.0 (see [LICENSE-MIT](LICENSE-MIT) and
[LICENSE-APACHE](LICENSE-APACHE)). This file lists the third-party software it is built with or
uses at run time, and how each is licensed. Licenses were checked against the upstream
repository or the package's own metadata (`.nuspec`) for the exact versions in use, not from
memory; the version and source used are given in each row.

**This repository and the release archive contain no game content** (nothing from *DiRT Rally
2.0* or *Assetto Corsa Rally*), no `.usmap` mappings, and **no Oodle library**. See
[Oodle](#oodle-never-bundled).

## Summary

| Component | Version / pin | License | How it is used |
|---|---|---|---|
| [ego.nefsedit](https://github.com/EgoEngineModding/ego.nefsedit) (`VictorBush.Ego.NefsLib`) | commit `163c463e6e0c`, plus our patch | MIT | Reads DiRT Rally 2.0 `.nefs` / `.dat` archives (`tools/dr2-extract`). Cloned by `scripts/bootstrap.ps1`; compiled into our tool. |
| [Ego-Engine-Modding](https://github.com/EgoEngineModding/Ego-Engine-Modding) (`EgoEngineLibrary`) | commit `f3fe9ee0f6e8` | MIT | Reads Ego-engine formats (PSSG meshes, textures) (`tools/dr2-extract`). Cloned by bootstrap; compiled into our tool. Its repository also carries a prebuilt `libzstd.dll` (see [Zstandard](#zstandard)). |
| [CUE4Parse](https://github.com/FabianFG/CUE4Parse) | NuGet 1.2.2.202610 | Apache-2.0 | Reads Unreal Engine packages and IoStore containers of ACR (`tools/acr-ref`, `tools/acr-additive`). |
| [retoc](https://github.com/trumank/retoc) (`retoc_cli`) | git `885a8dae740c` (v0.1.5) | MIT only (not dual) | Converts between ACR's IoStore containers and legacy packages. **Run as a separate program**, installed by the user/launcher (not part of this repository). |
| [SixLabors.ImageSharp](https://github.com/SixLabors/ImageSharp) | 2.1.13 (transitive via Ego-Engine-Modding) | Apache-2.0 (see note) | Image handling inside `EgoEngineLibrary`. |
| [BCnEncoder.Net](https://github.com/Nominom/BCnEncoder.NET) | 2.2.1 | MIT OR Unlicense (public domain) | Block-compressed texture encoding/decoding. |
| [Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json) | 13.0.4 | MIT | JSON in `acr-ref` and `acr-additive`. |
| [Iced](https://github.com/icedland/iced) | 1.21.0 | MIT | x86 decoding in `acr-ref`. |
| Oodle (RAD Game Tools / Epic) | not bundled | Proprietary | Runtime-fetched or user-supplied only. See below. |

Other NuGet packages pulled in transitively (via CUE4Parse, Ego-Engine-Modding and
ego.nefsedit) are listed in [Transitive packages](#transitive-packages).

## Notes

### Oodle (never bundled)

Unreal Engine 5 packages are Oodle-compressed, so reading ACR needs the Oodle Data library
(`oo2core_9_win64.dll` / `oodle-data-shared.dll`). Oodle is proprietary software of RAD Game
Tools / Epic Games and is **not** open source. This project:

- **never includes, commits or redistributes an Oodle binary** in the repository or in a release
  archive (`oo2core*` is git-ignored and the release packaging must exclude `oo2core*.dll` and
  `oodle*.dll`);
- relies on other tools to obtain it at run time on the user's own PC: retoc downloads
  `oo2core_9_win64.dll` (v2.9.10) next to `retoc.exe` and CUE4Parse downloads
  `oodle-data-shared.dll` next to the tool, both from the third-party GitHub repository
  [WorkingRobot/OodleUE](https://github.com/WorkingRobot/OodleUE) (a mirror of Oodle from
  Unreal Engine's source; GitHub reports no license for that repository), or the user supplies their own
  copy;
- uses the NuGet wrappers `Oodle.NET` and `OodleSharp` (MIT), which contain no Oodle binary.

Whether obtaining Oodle this way is permitted depends on your Unreal Engine / Oodle license
terms; that is the user's responsibility. Offline use of ACR Extra Stages only needs
decompression of the user's own game files and compression of the user's own mod files.

### SixLabors.ImageSharp

Version 2.1.13 (the one `Ego-Engine-Modding` pins) is licensed under the **plain Apache
License 2.0** (verified in the `v2.1.13` tag's `LICENSE` and in the package's `.nuspec`). Newer
major versions (3.x and later) use the *Six Labors Split License*, which is Apache-2.0 for
open-source projects and for use as a dependency of open-source projects, and requires a paid
license for closed-source commercial use. This project is open source, so either would be
acceptable, but do not upgrade past 2.1.13 without re-reading the license that applies. Version
2.1.13 has known security advisories (`NuGetAudit` is disabled for the `dr2-extract` build);
it is used only offline on your own local game files.

### Zstandard

`Ego-Engine-Modding` ships a prebuilt `libzstd.dll` (copied next to `dr2-extract`). Zstandard
is dual-licensed BSD-3-Clause / GPL-2.0 by Meta Platforms, Inc. (upstream `LICENSE` and
`COPYING`); we use it under the **BSD** license (copyright (c) Meta Platforms, Inc. and
affiliates, all rights reserved). Redistributing the DLL requires reproducing the BSD notice
from <https://github.com/facebook/zstd/blob/dev/LICENSE>.

### retoc

retoc is MIT-licensed only (copyright (c) 2025 Truman Kilen and Archengius), unlike the
MIT OR Apache-2.0 choice of this project. retoc is not linked into this project: it is a
separate executable the build scripts call. If a release ships `retoc.exe`, the MIT notice
below must ship with it. retoc in turn depends on crates from
[trumank/repak](https://github.com/trumank/repak) (Apache-2.0 as reported by GitHub for the
repository) and others; `cargo install` pulls them from source and they are not part of our
archive.

### Local patch to ego.nefsedit

`tools/dr2-extract/vendor-patches/ego.nefsedit-aes-padding.patch` is our modification of
ego.nefsedit (MIT). It is distributed under the same MIT license as the file it modifies;
the modified code remains copyright (c) 2020 victorbush.

### Optional development tool

`tools/stage-validate` (not part of releases) depends on the `acr_shm` crate from
[acr-rewind](https://github.com/cherrymcgerry/acr-rewind) (MIT OR Apache-2.0, same author) and
on common Rust crates (anyhow, clap, ctrlc, serde, serde_json, windows) that are
MIT/Apache-2.0 dual-licensed; see its `Cargo.lock` for versions.

## Transitive packages

NuGet packages in the dependency closure of `acr-ref`, `acr-additive` and `dr2-extract`
(from each project's `project.assets.json`; licenses from each package's `.nuspec`, cross-checked
against CUE4Parse's own `NOTICE`):

| Package | Version | License |
|---|---|---|
| Blake3 | 3.0.2 | BSD-2-Clause (native BLAKE3 code: CC0-1.0 OR Apache-2.0) |
| BouncyCastle.Cryptography | 2.6.2 | MIT |
| CommunityToolkit.Diagnostics | 8.4.2 | MIT |
| CommunityToolkit.HighPerformance | 8.4.0 / 8.4.2 | MIT |
| FixedMathSharp | 4.0.1 | MIT |
| Fmod5Sharp | 3.1.0 | MIT |
| GenericReader | 2.3.0 | MIT |
| IndexRange | 1.1.0 | MIT |
| Infrablack.UE4Config | 0.7.2.97 | MIT |
| K4os.Compression.LZ4 / .Streams | 1.3.8 | MIT |
| K4os.Hash.xxHash | 1.0.8 | MIT |
| LZMA-SDK | 22.1.1 | MIT |
| MemoryPack (+ Core, Generator) | 1.21.4 | MIT |
| Microsoft.Bcl.Memory | 10.0.12 | MIT |
| Microsoft.Extensions.* (Logging, DependencyInjection, Options, Primitives, Abstractions) | 8.0.x | MIT |
| Microsoft.NETCore.Platforms, NETStandard.Library | 1.1.0 / 1.6.1 | Microsoft .NET metapackages (license given only as a Microsoft wlink URL in the .nuspec; no code from them is shipped, not re-verified) |
| NAudio.Core | 2.2.1 | MIT |
| OffiUtils | 3.2.0 | MIT |
| OggVorbisEncoder | 1.2.2 | MIT |
| Oodle.NET, OodleSharp | 2.2.0 / 0.0.1 | MIT (wrappers only; no Oodle binary inside) |
| Serilog, Serilog.Sinks.Console | 4.3.1 / 6.1.1 | Apache-2.0 |
| SharpGLTF (Core, Runtime, Toolkit) | 1.0.6 | MIT |
| SubstreamSharp | 1.0.3 | MIT |
| System.Data.DataSetExtensions | 4.5.0 | MIT |
| System.IO.Abstractions, TestableIO.System.IO.Abstractions(.Wrappers), Testably.Abstractions.FileSystem.Interface | 22.0.14 / 9.0.0 | MIT |
| System.IO.Hashing, System.IO.Packaging, System.Numerics.Tensors | 10.0.x | MIT |
| VGAudio | 2.2.1 | MIT |
| Zlib-ng.NET | 1.2.0 | MIT |
| ZstdSharp.Port | 0.7.2 / 0.8.8 | MIT |

CUE4Parse's `NOTICE` additionally credits material under the MS-PL (DotNetZip), ISC (vgmstream
portions), zlib (crunch) and Apache-2.0 (SwiftShader ETC decoder) licenses. Its full text is at
<https://github.com/FabianFG/CUE4Parse/blob/master/NOTICE>; if a release bundles `CUE4Parse.dll`
the NOTICE file must be reproduced with it.

## Copyright notices

The components below are licensed under the MIT License, with the copyright holders named:

- ego.nefsedit: Copyright (c) 2020 victorbush
- Ego-Engine-Modding: Copyright (c) 2017 Petar Tasev
- retoc: Copyright (c) 2025 Truman Kilen and Archengius
- BCnEncoder.Net: Copyright 2020 Nomi Lakkala (MIT)
- Newtonsoft.Json: Copyright (c) 2007 James Newton-King
- Iced: Copyright (C) 2018-present iced project and contributors
- K4os.Compression.LZ4 / K4os.Hash.xxHash: Copyright (c) 2017 Milosz Krajewski
- VGAudio: Copyright (c) 2016 Thealexbarney
- Microsoft .NET libraries: Copyright (c) .NET Foundation and Contributors

The MIT License:

```
MIT License

Copyright (c) <year> <copyright holders listed above>

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

CUE4Parse (Apache-2.0), Serilog (Apache-2.0) and SixLabors.ImageSharp 2.1.13 (Apache-2.0) are
licensed under the Apache License, Version 2.0, the full text of which is in
[LICENSE-APACHE](LICENSE-APACHE) (the standard, unmodified terms) and at
<https://www.apache.org/licenses/LICENSE-2.0>.