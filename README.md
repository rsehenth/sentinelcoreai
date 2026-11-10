<div align="center">

<h1>SentinelCore AI</h1>

<p><strong>Evidence-based diagnostics for Windows.</strong><br/>
Local. Offline. Read-only by design.</p>

<p>
<img src="https://img.shields.io/badge/status-preview-orange" alt="status: preview"/>
<img src="https://img.shields.io/badge/version-0.3.0--preview.8-blue" alt="version 0.3.0-preview.8"/>
<img src="https://img.shields.io/badge/.NET-10-512BD4" alt=".NET 10"/>
<img src="https://img.shields.io/badge/platform-Windows%2011%20x64-0078D4" alt="Windows 11 x64"/>
<img src="https://img.shields.io/badge/license-MIT-green" alt="license: MIT"/>
</p>

</div>

---

> **Development status — stage 6 of 7 is complete.** Stages 1 through 6 are implemented and compile; stage 7
> (final testing and homologation) has not started. The shipped build is **read-only**: Personalization,
> Debloat and Repairs are implemented in code, but real execution stays behind a closed build gate and is
> only demonstrated with clearly marked synthetic data. To date this application has never changed a Windows
> setting, removed an application, or run a repair tool.
>
> In short: the product builds, the test suite passes, and the diagnostic pages were validated against real
> Windows data — the controlled-maintenance side is code-complete but not homologated.

## Why this exists

Windows already records almost everything that goes wrong. The hard part is reading it without turning noise
into conclusions. SentinelCore AI answers questions like *"did this machine log anything relevant in the last
24 hours?"*, *"what changed since the last hardware snapshot?"*, *"was this session worse than the previous
one, and on which evidence?"* — and hands you the record behind every answer: source, timestamp, duration,
coverage, and limitations.

Five principles shape the whole design:

| Principle | What it means in practice |
| --- | --- |
| **Evidence, not opinion** | Every conclusion traces back to a technical record. Correlation is not causality: shared timing or identity does not prove cause, and the absence of a relation does not prove the absence of a problem. |
| **Read-only by default** | Diagnostics never repair, optimize, or execute commands. |
| **Local and offline** | No telemetry, no external API, no data sent anywhere. Inaccessible information becomes a *declared limitation* — never a reason to bypass permissions. |
| **Durable history** | Immutable Markdown is the source of truth; the SQLite index is rebuildable. Nothing is rewritten or deleted automatically. |
| **Honest about limits** | Partial coverage, cancellation, and unavailability are explicit. Null means *unavailable*, not zero. |

## What it does

Thirteen pages. Diagnostic pages read real Windows data; maintenance pages are implemented with real
execution disabled.

| Page | What it does | State |
| --- | --- | --- |
| **Overview** | Basic and full audits with confirmation, progress, cancellation, and per-type validity | Implemented, validated |
| **Results** | Evidence by category for the last audit, linked to each module's history | Implemented, validated |
| **Hardware** | Eleven categories — CPU, memory, motherboard/BIOS, GPU, monitors, storage, network, audio, USB, drivers, other devices — plus snapshot comparison | Implemented, validated |
| **Stability & Events** | Windows Event Log and Windows Error Reporting metadata, from 24 hours to 30 days | Implemented, validated |
| **Performance** | Sessions from 30 seconds to 3 minutes: CPU, memory, processes, storage, with execution and coverage tracked separately | Implemented, validated |
| **Network** | TCP/UDP endpoints and transport statistics, no packet capture, remote IPs omitted by default | Implemented, validated |
| **Graphics & GPU** | Adapters, monitors, drivers, display events, TDR, and WER metadata | Implemented, validated |
| **Correlation** | Timeline and deterministic relations across already-persisted history, each with rule and rationale | Implemented, validated |
| **History** | Audits saved on this machine, reopening previous results | Implemented, validated |
| **Monitoring** | Authorized 5, 15, or 30-minute sessions with checkpoints and manual incident notes | Implemented, validated |
| **Personalization & Debloat** | Nine HKCU visual tweaks, plus Appx/MSIX inventory with classification, dependencies, and per-app preview | Implemented, **real execution gated** |
| **Windows Repairs** | Closed catalog for SFC, DISM, CHKDSK, and WinRE, with an isolated broker and eight operations | Implemented, **real execution gated** |
| **Recovery Center** | Unified history, integrity checks, conflicts, and lookup of interrupted audits | Implemented, read-only |

> About the "AI" in the name: AI assistance is a **future** capability, deliberately last in the queue. Today
> there is no LLM call, AI API, or remote processing anywhere in the product.

## How it works

```text
Windows APIs · WMI · Event Log · WER
              │
              ▼
        collectors ──► typed contracts (source, time, coverage, limitations)
              │
              ▼
   Markdown records (immutable, SHA-256)  +  SQLite index (rebuildable)
              │
              ▼
       correlation ──► WPF presentation ──► local reports
```

## Where it stands

| Stage | Scope | Status |
| --- | --- | --- |
| **Feature phases 0–5** | Foundation, hardware, events/WER, performance, network, graphics, correlation, monitoring | Delivered, with quality gates and recorded real-run evidence |
| **Integration stages 1–5** | Evidence-to-origin navigation, diagnostic journey, recovery/tweaks, Appx/MSIX inventory, native repairs | **Implemented in code** |
| **Integration stage 6** | Whole-product integration: shell, histories, coordinated journeys, isolated broker packaging | **Implemented and compiled** (static close-out, 10 Nov 2026) |
| **Integration stage 7** | Final testing and homologation: integrated journeys, interruptions, concurrency, reopen, final package | **Not started** |
| **Future** | Real validation of tweaks/debloat/repairs, signed distribution, installer, AI assistance | Planned |

Still missing before this leaves preview: functional homologation of the integrated journeys, a single
authorized real-world validation pass in an isolated environment (including UAC, broker IPC, and the native
tools), and distribution packaging/signing.

## Getting started

**Requirements:** Windows 11 x64; a standard user account (the manifest is `asInvoker`, no elevation prompt).
Running a published build needs nothing else — it is self-contained. Building requires the .NET 10.0.403 SDK
(pinned in `global.json`) and NuGet access for the first restore.

```powershell
# build and test
dotnet restore SentinelCore.sln --locked-mode
dotnet build SentinelCore.sln -c Debug --no-restore
dotnet build SentinelCore.sln -c Release --no-restore
dotnet test SentinelCore.sln -c Release --no-build

# or use the local-SDK wrapper (no global install, caches kept in .tools)
.\scripts\dotnet.ps1 restore SentinelCore.sln --locked-mode
.\scripts\dotnet.ps1 test SentinelCore.sln -c Release --no-build

# run from source
dotnet run --project src/SentinelCore.App

# produce a self-contained package
.\scripts\publish.ps1
.\artifacts\publish\0.3.0-preview.8\win-x64\SentinelCoreAI.exe
```

Ship the whole output folder, including native DLLs. The publish script pins the real-execution gates to
`false` and refuses to overwrite an existing package without `-Force`. There is no MSI, no signing, and no
service — and trimming must not be used with WPF.

Tests use isolated temporary directories and fictitious data: no test touches production history, the real
Registry, or starts `sfc.exe`, `dism.exe`, `chkdsk.exe`, or `reagentc.exe`.

## Data and privacy

Everything lives in `%LOCALAPPDATA%\SentinelCoreAI\`:

```text
memory/     immutable records (audits, snapshots, investigations, sessions, transactions)
            as Markdown with SHA-256, plus derived indexes such as MEMORY.md and RECOVERY.md
reports/    human-readable reports, rebuildable from the records
data/       sentinelcore.db — SQLite index (schema 12), rebuildable from Markdown
logs/       application failures and consistency warnings
```

No passwords, tokens, cookies, documents, browsing data, project contents, or network traffic are collected,
and nothing is uploaded. Files are not encrypted: protection depends on your Windows profile permissions, and
SHA-256 detects accidental corruption — not deliberate tampering. To exercise the UI without touching real
history, point the app at an isolated root:

```powershell
SentinelCoreAI.exe --data-root D:\isolated\root
```

## Repository layout

```text
src/      12 projects: App (WPF), Application, Domain, Persistence, Windows collectors,
          modifications, debloat (incl. isolated native adapters), and the repair broker
tests/    xUnit suite plus an audited selection of synthetic tests
scripts/  local build, publishing, verification hosts, and utilities
tasks/    planning and tracking
```

Layers are acyclic: **Domain** defines contracts, **Application** orchestrates, **Infrastructure** collects,
**Persistence** stores, and **App** presents — the WPF app never issues WMI queries directly.

## Documentation

Long-form documents — architecture, security model, roadmap, validation evidence, and per-delivery reports — are
maintained privately and are not part of this repository. This README is the public reference; decisions and
limits recorded above reflect the same internal evidence.

## Known limitations

- **Not homologated.** Stage 7 has not started; integrated journeys have not been exercised end to end.
- **No real maintenance operations.** Tweaks, app removal, and repair tools are simulated only. No Windows
  setting has been changed and no `sfc`/`dism`/`chkdsk`/`reagentc` has been executed by this build.
- **Observed state is not a diagnosis.** A reported `OK` or `Healthy` does not prove hardware integrity, and
  correlation between events does not establish cause.
- **Coverage can be partial.** Event and WER reads are bounded by count, size, and time; partial results are
  never presented as totals.
- **Windows 11 only.** The product targets Windows 11 x64 and does not support other platforms.

## License

Released under the [MIT License](LICENSE) — use it, modify it, ship it, as long as the copyright notice and
the license text stay with it. Third-party references and their licenses are listed in
[third-party notices](THIRD-PARTY-NOTICES.md).
