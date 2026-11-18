# Third-party notices

SentinelCore AI's own source code is available under your choice of the MIT License ([LICENSE-MIT](LICENSE-MIT))
or the Apache License 2.0 ([LICENSE-APACHE](LICENSE-APACHE)). The materials listed here are **not** relicensed by
that choice: they stay under their own terms, reproduced or referenced below.

## 1. Win11Debloat — MIT License (adapted material)

The SentinelCore AI documentary application catalog (`src/SentinelCore.Application/DebloatCatalogData.json`,
embedded in the Application assembly and kept for compatibility with recorded history) adapts application
identifiers and configuration definitions from [Raphire/Win11Debloat](https://github.com/Raphire/Win11Debloat),
commit `5d88bfe9ef2be5a8a643166a6400eb78a30c46ec`. The provenance of the Registry definitions behind the
Personalization catalog (`TweakCatalog`, `T2bCatalog`) is recorded against the same commit.

Upstream scripts, executors and risk labels are not executed or adopted as authorization. SentinelCore
implementation and risk gates are separate.

**Licensing consequence:** the adapted catalog material is available **only** under the MIT License below. It
cannot be offered under Apache-2.0 by this project. The rest of the repository is dual-licensed.

### Win11Debloat license text

```text
MIT License

Copyright (c) 2020 Raphire (https://github.com/Raphire)

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

## 2. NuGet packages shipped in published builds

These packages are referenced by the product projects and resolved through locked restore
(`packages.lock.json`). They are not vendored in this repository; each package carries its own license text,
which must accompany any binary distribution.

| Package | Version | License (SPDX) |
| --- | --- | --- |
| Microsoft.Data.Sqlite (and `.Core`) | 10.0.14 | MIT |
| System.Management | 10.0.14 | MIT |
| System.CodeDom (transitive dependency of System.Management) | 10.0.14 | MIT |
| System.Diagnostics.EventLog | 10.0.14 | MIT |
| SQLitePCLRaw (`bundle_e_sqlite3`, `core`, `provider.e_sqlite3`, `lib.e_sqlite3`) | 2.1.13 | Apache-2.0 |
| SQLite (native library bundled by SQLitePCLRaw) | — | Public domain |

The .NET runtime is included in self-contained publishes and is distributed under its own MIT license by the
.NET Foundation and Microsoft.

## 3. Test-only packages (not shipped)

`xunit` 2.9.3 (Apache-2.0), `xunit.runner.visualstudio` 3.1.5 (Apache-2.0) and `Microsoft.NET.Test.Sdk` 17.14.1
(MIT) are used by the test project only and are not part of published builds. Their transitive test tooling
(`Microsoft.TestPlatform.*` and `Microsoft.CodeCoverage` 17.14.1, `Newtonsoft.Json` 13.0.3) is MIT-licensed and is not
shipped either.

## Verification status

The license identifiers in sections 2 and 3 were read from the `license` expression in each package's `.nuspec`
(local NuGet cache) on 18 November 2026. The SQLite public-domain statement and the bundled license files
inside each package were not re-checked here. Re-check both before distributing a binary build.
