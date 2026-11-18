# Contributing to SentinelCore AI

Thanks for your interest. The project is a preview and its code base is in a deliberate freeze while real-world
validation is pending, so small and precise contributions are the most useful: bug reports, translation fixes and
documentation fixes.

## Before you open a pull request

1. Build both configurations with locked dependencies:

   ```powershell
   dotnet restore SentinelCore.sln --locked-mode
   dotnet build SentinelCore.sln -c Debug --no-restore
   dotnet build SentinelCore.sln -c Release --no-restore
   ```

2. Run the audited synthetic runner described in the [README](README.md#tests). Do not run the whole test suite with
   `dotnet test`: part of it queries a real Windows installation.
3. Do not add code that writes to the system, starts a Windows tool (`sfc`, `dism`, `chkdsk`, `reagentc`, installers),
   requests elevation, or opens a closed build gate.
4. Keep UI text in the localization tables. New entries go at the **end** of a table because XAML refers to them by
   position (`T####`). Never translate persisted values, enum names or identifiers.

## Licensing of contributions

SentinelCore AI's own code is available under `MIT OR Apache-2.0`
([LICENSE-MIT](LICENSE-MIT), [LICENSE-APACHE](LICENSE-APACHE)).

**By submitting a pull request you agree that your contribution is licensed under both licenses, at the user's
option, with no additional terms,** and you confirm that you wrote it or have the right to submit it under those
licenses. If you cannot agree to that, say so in the pull request and it will not be merged.

Two exceptions:

- The application catalog adapted from Win11Debloat stays **MIT only**. Contributions to it are accepted under MIT
  and are not offered under Apache-2.0. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
- Do not submit code copied from a project whose license is incompatible with MIT or Apache-2.0.

This policy applies from the date it is published in the repository. It is not a contributor license agreement and
does not transfer copyright.
