# EtwBootTraceAnalyzer

Attributes boot (or app-launch) latency to the specific processes and drivers that caused it,
from an ETW trace. Rather than dumping the kernel event stream, it reconstructs a causal chain:

```
svchost.exe (DiskSvc) blocked on a read for 220 ms (diskservice.dll)
  -> blocked on a read for 120 ms (config.dat)
  -> ran on CPU for 30 ms
  -> woke svchost.exe (IndexingSvc), which ran on CPU for 60 ms
  -> woke explorer.exe
```

then ranks processes by how much of that chain they're responsible for.

## Why this isn't just an event dump

WPA/xperf will happily show you every CSwitch, every disk I/O, every DPC in a boot trace - that's
the easy 80%. The useful 20% is answering "which of these 2 million events actually mattered,
and why." This project's `WaitChainCriticalPathAnalyzer` does that by walking the kernel's own
scheduling causality backward:

1. Start from a "boot complete" milestone - the ETW `Thread/ReadyThread` event that woke the
   thread you care about (by default, the last thread readied anywhere in the trace).
2. `ReadyThread` records *who* woke it: the thread executing at the moment the wake-up fired.
   Walk to that thread.
3. Was it woken by a disk I/O completion? Attribute the I/O's full duration to the process that
   issued it, then recurse from when *that* I/O was issued.
4. Was it running on an otherwise-idle core when a driver's DPC/ISR interrupted it? Attribute the
   time to that driver (resolved to a module name from `ImageLoad` address ranges, no PDBs
   required).
5. Otherwise it was just running - attribute the CPU time between its last `CSwitch`-in and this
   wake-up to its process, then recurse from when it was scheduled in.
6. Repeat until the chain bottoms out (no earlier `ReadyThread` to explain the wake-up) or a
   hardware interrupt is reached (interrupts are exogenous - there's no earlier "why").

The result is a chronological list of `CriticalPathSegment`s that only contains time actually on
the critical path - a process that ran for 500ms in the background but never blocked anything
downstream doesn't show up, no matter how much CPU it used. `OffenderRanking` then sums segments
per process to answer "who cost the most wall-clock time."

Everything upstream of that (CPU sample aggregation, disk I/O summaries, DPC/ISR-by-driver
totals) is straightforward aggregation and exists mostly as supporting context in the report.

## Project layout

| Project | Target | Purpose |
|---|---|---|
| `EtwBootTraceAnalyzer.Core` | `net8.0` | Event model, SQLite storage, JSON import/export, all analysis (cross-platform, fully unit tested) |
| `EtwBootTraceAnalyzer.Capture` | `net8.0-windows` | Live kernel ETW session capture + offline `.etl` parsing via [TraceEvent](https://github.com/microsoft/perfview) |
| `EtwBootTraceAnalyzer.Cli` | `net8.0` / `net8.0-windows` | `etwboot` command-line tool |
| `EtwBootTraceAnalyzer.Tests` | `net8.0` | xUnit tests, including a synthetic boot trace fixture with a known answer |

`Capture` only builds meaningfully on Windows (it P/Invokes `StartTrace`/`ProcessTrace` through
TraceEvent), so `Core`, the `net8.0` leg of `Cli`, and `Tests` are the platform-independent path:
they compile and run anywhere .NET 8 does, using a portable JSON/SQLite export as the trace
format instead of a live session.

## Usage

```bash
# Run the full pipeline against a built-in synthetic trace - no ETW, no Windows required.
dotnet run --project src/EtwBootTraceAnalyzer.Cli -- demo

# ...and export it so you can re-analyze without regenerating it.
dotnet run --project src/EtwBootTraceAnalyzer.Cli -- demo --save-json trace.json --save-sqlite trace.db

# Analyze a previously exported trace.
dotnet run --project src/EtwBootTraceAnalyzer.Cli -- analyze --json trace.json
dotnet run --project src/EtwBootTraceAnalyzer.Cli -- analyze --sqlite trace.db --session synthetic-boot

# Pick a specific milestone instead of "last thread readied in the trace".
dotnet run --project src/EtwBootTraceAnalyzer.Cli -- analyze --json trace.json --milestone-process explorer.exe

# Diff two independently-captured traces (e.g. before/after applying a fix) to get an actual
# improvement number, not just "where did the time go in this one boot".
dotnet run --project src/EtwBootTraceAnalyzer.Cli -- compare --before before.json --after after.json
```

`analyze` only ever tells you where the time went in *one* trace - the "82% of the critical path"
kind of number. It says nothing about whether a fix helped. `compare` is what answers that: it
diffs two `BootAnalysisReport`s (matched by process name, since pids aren't stable across boots)
and reports the change in total critical-path time plus the per-process delta. That's the
distinction between "found 3 services responsible for 40% of boot delay" (an `analyze` claim) and
"reduced boot time by X%" (a `compare` claim) - the second one needs two real captures, not one.

On Windows (`dotnet run -f net8.0-windows`), two more commands are available:

```powershell
# Capture a live kernel session (needs Administrator).
etwboot capture --out trace.json --duration 30

# Parse a trace already captured with wpr/xperf.
etwboot import-etl boot.etl --out trace.json
```

## Capturing a real boot trace, and validating against WPA

1. `wpr -start GeneralProfile -start CPU -filemode` before reboot (or use the built-in
   "Windows Performance Recorder" boot-trace profile), then `wpr -stop boot.etl` after logon.
2. `etwboot import-etl boot.etl --out boot.json` to convert it (this is the offline path in
   `EtwBootTraceCapture.LoadFromEtl`, which doesn't need an active kernel session).
3. `etwboot analyze --json boot.json` to get the ranked offender list.
4. Cross-check: open `boot.etl` in WPA, add the **CPU Usage (Precise)** and **Disk I/O** graphs
   for the top offending process/timespan this tool reports, and confirm the same stall is
   visible there. WPA's own **Generic Events** view on `Thread/ReadyThread` is what this
   project's wait-chain analyzer is modeled on, so the two should agree by construction - if
   they don't, that's a bug in the correlation window or module resolution, not a difference in
   methodology.

## Data model notes (why fields are shaped the way they are)

- Timestamps are milliseconds relative to trace start (`TimeStampRelativeMSec` in TraceEvent
  terms), so everything sorts and diffs cleanly without touching `DateTime`.
- `DiskIoEvent.IssuingProcessId`/`IssuingThreadId` are the thread that *issued* the I/O, not
  whatever DPC context happened to run the completion - this mirrors how TraceEvent itself
  back-patches `DiskIO/Read|Write` completion events from the matching `DiskIO/*Init` event via
  the IRP pointer.
- `DpcIsrEvent.RoutineModule` is resolved by binary-searching the DPC/ISR routine address against
  `ImageLoad` ranges captured in the same session (`ModuleRangeResolver`) - module-level
  attribution, the same thing WPA gives you without a symbol server. Falls back to a raw hex
  address when the module can't be identified from the capture window.
- Every "who woke whom" lookup in `WaitChainCriticalPathAnalyzer` is backed by a
  thread-keyed, timestamp-sorted index with binary search rather than a linear scan - built once
  per analysis so a multi-million-event session doesn't turn each hop of the backward walk into
  an O(n) scan.

## Testing

```bash
dotnet test tests/EtwBootTraceAnalyzer.Tests
```

The synthetic fixture (`SyntheticBootTraceGenerator`) encodes a boot narrative with a known
answer - a disk-bound service that should dominate the critical path (82% in the fixture) and a
CPU-bound one that shouldn't - so the critical-path and ranking tests assert against exact
expected durations and process attribution, not just "it doesn't throw."
