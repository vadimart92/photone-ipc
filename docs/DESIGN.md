# photone-ipc — Final design (v1)

Status: implementation-ready. Base: **DESIGN 2** (API-fidelity-and-simplicity-first), with every `must_fix_in_winner` item from the three judges applied and the `best_ideas_from_others` grafted where at least two judges agreed. Every place where the sources disagree is resolved in §0.

Ground truth (empirically verified on the dev box, overrides everything else): `[LibraryImport("kernelbase.dll")]` for `VirtualAlloc2`/`MapViewOfFile3`/`UnmapViewOfFile2`; one placeholder of `G + 2D` split twice; three `MapViewOfFile3(MEM_REPLACE_PLACEHOLDER)` at section offsets 0, G, G; child `VirtualAlloc2(BaseAddress = creatorBase)` succeeds ⇒ same VA (fallback `null`); teardown with plain `UnmapViewOfFile` ×3 (or `UnmapViewOfFile2(GetCurrentProcess(), v, MEM_PRESERVE_PLACEHOLDER)` + `VirtualFree`) then `CloseHandle(section)`. Reference: `docs/RESEARCH.md`.

---

## 0. Provenance and resolved contradictions

### 0.1 Judge must-fix items → where they land

| # | Item (all judges unless noted) | Applied in |
|---|---|---|
| 1 | Swappable signal layer (`SignalBackend` + `NamedEventBackend`), reserved layout words, `SignalBackendId` in the header | §6, §4 |
| 2 | Reader `Dispose` with a pending wait: flag → wake own slot → wait for the wait loop to exit → complete VTS → close handles | §5.9 |
| 3 | No read-only mirror content check in openers; creator-only write/verify/restore self-test before `InitState = 1`; openers assert `mirror == data + D` | §3.4 |
| 4 | `CreateReader` sweeps dead/stuck slots (any process) before `TooManyReadersException` | §5.6 |
| 5 | Writer's kernel wait includes laggard process handles; laggard set refreshed every wake/slice; `Exchange(WriterWaiting, 0)` on every exit | §5.3 |
| 6 | `OpenProcess` `ERROR_ACCESS_DENIED` ⇒ alive (poll mode); only 87 / start-time mismatch / signaled handle ⇒ dead | §5.7 |
| 7 | Creator writes `CreatorStartTime` then `CreatorPid` first; opener aborts early if the creator dies mid-init; `InitializationTimeout` option; typed exceptions for 2 / 183 | §3.3, §3.4 |
| 8 | At claim: `WaitFor = long.MaxValue`, `ResetEvent` (via `SignalBackend.OnSlotClaimed`) | §5.6 |
| 9 | `WaitSync(int, TimeSpan)` uses `Timeout.InfiniteTimeSpan` explicitly; `TryRead(0)` returns an empty chunk (documented) | §2 |
| 10 | Live-reader eviction is not in v1; if added, `Dead` is a tombstone until the evictee acks or its process dies | §5.7 |
| 11 | `_min ≤ every Active ReadCursor` invariant in comments + debug assert; JoinStorm and ReaderKilled tests are the gate | §5.11, §9 |
| 12 (judge 1/2) | `Advance` checks slot ownership word before `Interlocked.Exchange(ReadCursor)` | §5.4 |
| 13 (judge 1/2) | Packed slot word `pid:32 \| seq:30 \| state:2`, `ClaimTick` via `GetTickCount64` | §4.2 |
| 14 (judge 0/2) | Events named by random 64-bit `InstanceId`; `NameChars` removed | §3.1, §6.2 |
| 15 (judge 2) | Local reader ref-count defers the unmap; writer `Dispose` drops an outstanding bucket before `Closed` | §3.5 |
| 16 (judge 0/1) | `WriterWaitFor` "crossing" filter in `Advance` (absolute cursor, D0 semantics) | §5.4, §5.10 |
| 17 (judge 1) | Spin loops poll `W` every `pause`; async spin default 5 µs | §5.5, §7 |
| 18 (judge 0/1) | Internal counters (`Signals`, `KernelWaits`, `SpuriousWakes`, `Scans`, `Evictions`) for tests/benchmarks | §5.12 |

### 0.2 Contradictions between sources and their resolution

| Topic | Positions | Resolution |
|---|---|---|
| Async mechanism | D0/D1: per-process `WaitDispatcher` multiplexing WFMO; D2: one waiter thread per reader. Judge 1 wanted the dispatcher inside the named-event transport; judge 2 said "do NOT graft the dispatcher — it cannot host handle-less backends". | **Per-reader waiter thread** (D2). It is the only shape every candidate backend fits (a thread with a stable TID is what `NtAlertThreadByThreadId` needs; spinning/`WaitOnAddress` backends have no handle to multiplex). Cost: one parked OS thread per reader that has ever suspended an async `Wait`; documented. |
| Where to reserve backend words | Judge 0: lines 3 and 5 of the header; judge 1: slot offsets 48/56 + header 260/56; judge 2: 476..511. | Lines 3 and 5 **stay empty** (they are the adjacent-line-prefetch partners of the two writer-stored lines 2 and 4, the reason D2 left them empty). Backend words: per slot at 48 (`WaiterThreadId`) and 56 (`BackendWord`); writer-side at 388 (`WriterWaiterThreadId`) and 408 (`WriterBackendWord`) on the `WriterWaiting` line (same change cadence); `SignalBackendId` at 56 (line 0); 128 B backend-private area at 2560..2687. |
| Writer terminated: exception or `false` | D2: `WriterTerminatedException` from `Wait`; D0/D1: `Wait` returns `false` with `Status == WriterTerminated`. | **`false` + `RingReader.Status`**. A crashed writer and a closed writer both mean "no more data"; a reader loop should not need a `catch` for a normal failure mode. `WriterTerminatedException` is dropped from the surface. |
| `WriterWaitFor` filter | D0 has it (judge 0 verified it correct); D2 omits it (judge 0 called it a later optimisation); judge 1 required a "crossing" variant. | Adopted as the crossing variant (`oldR < target && newR ≥ target`), argument in §5.10. Reader `Dispose` and eviction never filter. |
| Reserve-end/commit fence | D0: single `Interlocked.Exchange(W)`; D1/D2: `Volatile.Write(W)` + `Interlocked.MemoryBarrier()`. | `Interlocked.Exchange(ref Hdr.WriteCursor, w)`: one call site, release + StoreLoad in one locked op, impossible to forget the fence. |
| Teardown API | dotnet-specifics §10.14: "always `UnmapViewOfFile2`"; win32-mapping §5 (verified): plain `UnmapViewOfFile` frees a placeholder-backed view completely. | `UnmapViewOfFile` ×3 (kernel32, one parameter, verified). `UnmapViewOfFile2` is not imported. |
| 64 KiB vs page granularity for views | dotnet-specifics/sync-protocol: 64 KiB multiples required; win32-mapping (verified): page granularity suffices inside placeholders. | All views are 64 KiB multiples anyway (header = G, `D % G == 0`), so both statements hold. |
| `Volatile.ReadBarrier/WriteBarrier` | dotnet-specifics recommends them; sync-protocol: one-directional, unsafe for Dekker. | Never used. All Dekker points use `Interlocked.*`. |
| Preferred base address | D2: FNV(name) (breaks for handle-only sharing); D0/D1: hashed from `InstanceId` with probes. | `AddressHint.For(instanceId, total)`: up to 4 probes in the quiet window `[0x4000_0000_0000, 0x5000_0000_0000)`, then `null`. Openers try `CreatorBase`, then `null`. |
| MaxReaders | D1: 64; D0/D2: 32. | **32**: writer WFMO set = 1 + ≤32 laggards ≤ 64 without special-casing; `ScanMin` = 32 lines. `MaxReaders` is in the header for a future `Version = 2`. |
| `Wait` naming | D1: `WaitAsync`/`Wait`; D0/D2: `Wait` (awaitable)/`WaitSync`. | Sketch verbatim: `await reader.Wait(100)` compiles; `WaitSync` is the blocking twin. |
| Header self-test on open | D0: compare 4 KiB; D1: compare two bytes; D2: compare one byte. Judges: all three race a live writer. | No content compare in openers. |
| Writer takeover / `OldestAvailable` / `Extension` area / descriptor strings / public statistics | D0/D1 have them; judge 1 asked to cut the surface. | Not in v1. `WriterEpoch`, slot `Flags` and a reserved area keep the layout ready. |
| `WaitForSpace` timeout semantics | D1: `GetBucket(count, timeout, ct)`; D2: infinite only. | v1: `GetBucket` blocks without timeout (backpressure contract); `TryGetBucket` is the non-blocking form. A timeout overload is a non-breaking addition later. |

---

## 1. Solution and project layout

```
E:\GitHub\photone-ipc\
  global.json
  Directory.Build.props
  Directory.Packages.props
  Photone.Ipc.slnx
  docs\RESEARCH.md, docs\DESIGN.md
  src\Photone.Ipc\
    Photone.Ipc.csproj
    AssemblyInfo.cs
    RingBuffer.cs                 RingBuffer<T>: Create/Open/Dispose, properties, DuplicateSectionHandleTo
    RingBuffer.Writer.cs          GetBucket/TryGetBucket/EndWrite/ScanMin/WaitForSpace/SignalReaders/laggards
    RingBuffer.Readers.cs         CreateReader, SweepDeadSlots, Evict, ReaderReleased
    RingReader.cs                 TryRead/Advance/WaitSync/BlockUntil/Dispose, Status
    RingReader.Async.cs           Wait(...) => ValueTask<bool>, IValueTaskSource<bool>, waiter thread
    Bucket.cs                     ref struct Bucket<T>
    Chunk.cs                      readonly struct Chunk<T> (ReadOnlyMemory<T> Data)
    Options.cs                    RingBufferOptions, ReaderOptions
    ReaderStatus.cs               enum
    Exceptions.cs
    SafeSectionHandle.cs
    Signaling\WaitOutcome.cs      enum
    Signaling\SignalBackend.cs    abstract class (the swappable layer) + SignalBackendId constants + factory
    Signaling\NamedEventBackend.cs
    Internal\Kernel.cs            the typed layer over the CsWin32-generated imports (DEVIATIONS 51), constants, error helpers
    Internal\Layout.cs            constants, ControlBlock, ReaderSlot, SlotWord helpers, static asserts
    Internal\Capacity.cs          ChooseCapacity, TypeHash (FNV-1a)
    Internal\AddressHint.cs       preferred-base probes
    Internal\MirroredSection.cs   placeholder + 3 views (create / open / teardown); the ONLY mapping code
    Internal\MappedMemory.cs      MemoryManager<T> over the data region + mirror: the owner behind Chunk.Data
    Internal\ProcessLiveness.cs   IsAlive, OpenLaggard, start-time compare
    Internal\SpinClock.cs         Stopwatch-bounded spin helper
    Internal\Counters.cs          internal diagnostics counters
    Internal\TestHooks.cs         internal static hooks used by TestChild (claim-and-die, slow-init)
  tests\Photone.Ipc.Tests\        xunit.v3, in-process + cross-process tests
  tests\Photone.Ipc.TestChild\    console apphost; verbs: reader | echo | writer | crash-reader | claim-and-die | slow-init | hold-name | spin-reader
  bench\Photone.Ipc.Benchmarks\   BenchmarkDotNet microbenchmarks + `--cross` custom harness mode
```

### 1.1 Exact file contents

`global.json`
```json
{
  "sdk": { "version": "10.0.112", "rollForward": "latestPatch" },
  "test": { "runner": "Microsoft.Testing.Platform" }
}
```

`Directory.Build.props`
```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AnalysisLevel>latest</AnalysisLevel>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <Deterministic>true</Deterministic>
  </PropertyGroup>
</Project>
```

`Directory.Packages.props`
```xml
<Project>
  <ItemGroup>
    <PackageVersion Include="xunit.v3" Version="4.0.1" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="4.0.0" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="18.10.1" />
    <PackageVersion Include="BenchmarkDotNet" Version="0.15.8" />
  </ItemGroup>
</Project>
```

`src\Photone.Ipc\Photone.Ipc.csproj`
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <IsAotCompatible>true</IsAotCompatible>
    <EnableTrimAnalyzer>true</EnableTrimAnalyzer>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <InvariantGlobalization>true</InvariantGlobalization>
    <RootNamespace>Photone.Ipc</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Photone.Ipc.Tests" />
    <InternalsVisibleTo Include="Photone.Ipc.TestChild" />
    <InternalsVisibleTo Include="Photone.Ipc.Benchmarks" />
  </ItemGroup>
</Project>
```
(LangVersion is left at the SDK default = C# 14. Zero package dependencies.)

`src\Photone.Ipc\AssemblyInfo.cs`
```csharp
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
[assembly: SupportedOSPlatform("windows")]
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
```

`tests\Photone.Ipc.Tests\Photone.Ipc.Tests.csproj`
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <IsPackable>false</IsPackable>
    <TieredPGO>true</TieredPGO>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <ProjectReference Include="..\..\src\Photone.Ipc\Photone.Ipc.csproj" />
    <ProjectReference Include="..\Photone.Ipc.TestChild\Photone.Ipc.TestChild.csproj" />
  </ItemGroup>
</Project>
```

`tests\Photone.Ipc.TestChild\Photone.Ipc.TestChild.csproj`
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <UseAppHost>true</UseAppHost>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Photone.Ipc\Photone.Ipc.csproj" />
  </ItemGroup>
</Project>
```

`bench\Photone.Ipc.Benchmarks\Photone.Ipc.Benchmarks.csproj`
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <Optimize>true</Optimize>
    <ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>
    <TieredPGO>true</TieredPGO>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="BenchmarkDotNet" />
    <ProjectReference Include="..\..\src\Photone.Ipc\Photone.Ipc.csproj" />
    <ProjectReference Include="..\..\tests\Photone.Ipc.TestChild\Photone.Ipc.TestChild.csproj" />
  </ItemGroup>
</Project>
```

`Photone.Ipc.slnx`
```xml
<Solution>
  <Folder Name="/src/"><Project Path="src/Photone.Ipc/Photone.Ipc.csproj" /></Folder>
  <Folder Name="/tests/">
    <Project Path="tests/Photone.Ipc.Tests/Photone.Ipc.Tests.csproj" />
    <Project Path="tests/Photone.Ipc.TestChild/Photone.Ipc.TestChild.csproj" />
  </Folder>
  <Folder Name="/bench/"><Project Path="bench/Photone.Ipc.Benchmarks/Photone.Ipc.Benchmarks.csproj" /></Folder>
</Solution>
```

Runtime guards in `Create`/`Open`: `OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134)` and `Environment.Is64BitProcess`, else `PlatformNotSupportedException`. No `System.Diagnostics.Process` or reflection in the library.

---

## 2. Public API

Namespace `Photone.Ipc`. All types `sealed`. `T : unmanaged` everywhere.

```csharp
namespace Photone.Ipc;

public sealed class RingBufferOptions
{
    /// Writer spin budget in GetBucket before a kernel wait. Zero = block immediately. Default 20 µs.
    public TimeSpan SpinTime { get; init; } = TimeSpan.FromMicroseconds(20);
    /// Slice used while the writer is blocked (liveness backstop for readers whose process handle is unavailable). Default 10 ms.
    public TimeSpan LivenessCheckInterval { get; init; } = TimeSpan.FromMilliseconds(10);
    /// How long Open waits for InitState == 1 (creator liveness is checked meanwhile). Default 5 s.
    public TimeSpan InitializationTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// Exact 64 KiB-aligned base to request (creator only). null = hashed hint in the quiet window; 0 = no hint.
    public ulong? PreferredBaseAddress { get; init; }
    /// Touch every data page once at creation (removes ~4 µs/page first-touch faults from the hot path). Default true.
    public bool PreFault { get; init; } = true;
}

public sealed class ReaderOptions
{
    /// Spin budget of WaitSync before a kernel wait. Default 20 µs.
    public TimeSpan SpinTime { get; init; } = TimeSpan.FromMicroseconds(20);
    /// Spin budget of Wait (async) on the caller's thread before suspending. Default 5 µs.
    public TimeSpan AsyncSpinTime { get; init; } = TimeSpan.FromMicroseconds(5);
}

public enum ReaderStatus { Active = 0, WriterClosed = 1, WriterTerminated = 2, Evicted = 3, Disposed = 4 }

public sealed class SafeSectionHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeSectionHandle();                             // required by the LibraryImport return marshaller
    public SafeSectionHandle(nint handle, bool ownsHandle); // wrap a DuplicateHandle'd / inherited value
    protected override bool ReleaseHandle();
}

public sealed unsafe class RingBuffer<T> : IDisposable where T : unmanaged
{
    /// Creates a new section; this process is THE writer. name == null => "Local\photone.{Guid:N}".
    /// A user name without a namespace prefix is used verbatim under "Local\photone." ; "Local\..." / "Global\..." are honoured verbatim.
    public static RingBuffer<T> Create(long minCapacity, string? name = null, RingBufferOptions? options = null);
    /// Opens an existing buffer by name (reader role: CreateReader works, GetBucket throws InvalidOperationException).
    public static RingBuffer<T> Open(string name, RingBufferOptions? options = null);
    /// Opens from a section handle obtained via DuplicateSectionHandleTo / inheritance. Ownership of the handle transfers.
    public static RingBuffer<T> Open(SafeSectionHandle section, RingBufferOptions? options = null);
    /// DuplicateHandle(section) into another process; the value is meaningful only there. Requires PROCESS_DUP_HANDLE.
    public nint DuplicateSectionHandleTo(int targetProcessId);

    // writer side (creator only)
    public Bucket<T> GetBucket(int count);                       // blocks (spin, then kernel) until count free; 1 <= count <= Capacity
    public bool TryGetBucket(int count, out Bucket<T> bucket);   // non-blocking

    // reader side (any role, any process)
    public RingReader<T> CreateReader(ReaderOptions? options = null);   // TooManyReadersException after a dead-slot sweep

    public string? Name { get; }              // null when opened by handle
    public ulong InstanceId { get; }
    public long Capacity { get; }             // elements, power of two
    public int ElementSize { get; }
    public long DataBytes { get; }
    public bool IsWriter { get; }
    public bool IsWriterClosed { get; }       // WriterState == Closed
    public bool IsMappedAtCreatorAddress { get; }
    public ulong BaseAddress { get; }         // placeholder base in this process
    public ulong CreatorBaseAddress { get; }
    public long WriteCursor { get; }          // Volatile.Read
    public long FreeSpace { get; }            // writer only; rescans
    public int ActiveReaderCount { get; }     // popcount(ActiveMask)
    public long EvictedReaders { get; }
    public void Dispose();                    // writer: Commit(0) outstanding bucket, publish Closed, wake readers; all: unmap when the last local reader is gone
}

public ref struct Bucket<T> : IDisposable where T : unmanaged
{
    public Span<T> Span { get; }              // exactly count elements, contiguous through the mirror; empty after Commit/Dispose
    public int Length { get; }
    public long Cursor { get; }               // absolute element index of Span[0]
    public void Commit(int count);            // 0 <= count <= Length; exactly once; the unpublished tail is dropped (next bucket starts at W + count)
    public void Dispose();                    // without Commit => Commit(0); idempotent
}

public readonly struct Chunk<T> where T : unmanaged
{
    public ReadOnlyMemory<T> Data { get; }    // a window into the ring itself: Data.Span to read it (DEVIATIONS 56)
    public int Length { get; }
    public long Cursor { get; }
}

public sealed unsafe class RingReader<T> : IDisposable, IValueTaskSource<bool> where T : unmanaged
{
    /// Awaitable. true: >= count readable. false: timeout, or the writer closed/terminated and fewer than count will ever arrive
    /// (drain with TryRead/Available; see Status). Throws OperationCanceledException, ReaderEvictedException, ObjectDisposedException,
    /// ArgumentOutOfRangeException (count > Capacity). count == 0 => true. One outstanding Wait/WaitSync per reader.
    /// NOTE: a Chunk<T> may cross an await (Data is ReadOnlyMemory<T>), but it points into the ring: do not Advance past a chunk while an awaited operation still reads it.
    public ValueTask<bool> Wait(int count, CancellationToken cancellationToken = default);
    public ValueTask<bool> Wait(int count, TimeSpan timeout, CancellationToken cancellationToken = default);
    public bool WaitSync(int count);                              // == WaitSync(count, Timeout.InfiniteTimeSpan)
    public bool WaitSync(int count, TimeSpan timeout);            // TimeSpan.Zero == poll once; Timeout.InfiniteTimeSpan == forever

    public bool TryRead(int count, out Chunk<T> chunk);          // exactly count or false; count == 0 => true with an empty chunk; never blocks
    public void Advance(int count);                               // 0 <= count <= (cached W - R); may be less than the last chunk

    public long Available { get; }            // reloads W; W - R
    public long ReadCursor { get; }
    public ReaderStatus Status { get; }       // cached + volatile loads of the slot word and WriterState; probes writer liveness at most once per LivenessCheckInterval (polling readers see WriterTerminated)
    public bool IsCompleted { get; }          // Status is WriterClosed/WriterTerminated && Available == 0
    public int Slot { get; }                  // 0..31
    public void Dispose();                    // releases the slot, wakes a blocked writer, cancels a pending Wait with ObjectDisposedException
}

public class PhotoneIpcException : Exception { public int NativeErrorCode { get; } }   // 0 if not Win32
public sealed class RingBufferLayoutException : PhotoneIpcException { }              // magic/version/element size/type/size mismatch, unknown backend
public sealed class RingBufferNotFoundException : PhotoneIpcException { }            // Open: ERROR_FILE_NOT_FOUND
public sealed class RingBufferAlreadyExistsException : PhotoneIpcException { }       // Create: ERROR_ALREADY_EXISTS
public sealed class RingBufferInitializationException : PhotoneIpcException { }      // opener: InitState never became 1, or creator died mid-init
public sealed class TooManyReadersException : PhotoneIpcException { }                // all 32 slots active after a sweep
public sealed class ReaderEvictedException : PhotoneIpcException { public int SlotIndex { get; } }
```

The sketch compiles verbatim:

```csharp
var buffer = RingBuffer<float>.Create(1 << 20, "demo");            // section "Local\photone.demo"
using (var bucket = buffer.GetBucket(1024)) { bucket.Span.Fill(0); bucket.Commit(1000); }

var reader = buffer.CreateReader();                                  // or RingBuffer<float>.Open("demo").CreateReader() elsewhere
await reader.Wait(100);
reader.TryRead(100, out var chunk); Use(chunk.Data.Span); reader.Advance(90);
```

Semantics table (fixed):

| Operation | Rule |
|---|---|
| `GetBucket(n)` | exactly `n`; `1 ≤ n ≤ Capacity` else `ArgumentOutOfRangeException`; `InvalidOperationException` if a bucket is outstanding, `!IsWriter`, or writer closed; `ObjectDisposedException`; zero readers ⇒ never blocks |
| `Commit(k)` | `0 ≤ k ≤ n`; next bucket starts at `W + k` (no holes); second `Commit` throws |
| `Bucket.Dispose` w/o Commit | `Commit(0)` |
| `CreateReader` | starts at head (sees only later commits); single-consumer object; usable from any process/role |
| `TryRead(n)` | exactly `n` or `false`; chunk valid until `Advance` moves past it |
| `Advance(k)` | `k ≤ Wcached − R` else `ArgumentOutOfRangeException`; `ReaderEvictedException` if the slot was taken |
| `Wait(n)` / `WaitSync(n)` | `n ≤ Capacity` else throws; `n == 0` ⇒ `true`; `false` ⇒ check `Status` and `Available` |
| Reader `Dispose` | idempotent; wakes the writer if it was blocked; pending `Wait` completes with `ObjectDisposedException` |
| `RingBuffer.Dispose` (writer) | outstanding bucket ⇒ `Commit(0)`; `WriterState = Closed`; all slot events set; readers keep draining |
| `RingBuffer.Dispose` (any) | methods throw `ObjectDisposedException`; the mapping stays alive until the last local `RingReader` is disposed |

---

## 3. Native layer

### 3.1 P/Invoke declarations (`Internal\Kernel.cs`)

> Superseded by DEVIATIONS 51-55: the declarations below are generated by CsWin32 from the Win32 metadata (`NativeMethods.txt`), and `Kernel` is the
> typed layer over them. The signatures and the flag values are the same; the module of `VirtualAlloc2` / `MapViewOfFile3` is the
> `api-ms-win-core-memory-l1-1-6` API set rather than `kernelbase.dll`.

```csharp
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Photone.Ipc.Internal;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SYSTEM_INFO
{
    public ushort wProcessorArchitecture; public ushort wReserved; public uint dwPageSize;
    public void* lpMinimumApplicationAddress; public void* lpMaximumApplicationAddress;
    public nuint dwActiveProcessorMask; public uint dwNumberOfProcessors; public uint dwProcessorType;
    public uint dwAllocationGranularity; public ushort wProcessorLevel; public ushort wProcessorRevision;
}

internal static unsafe partial class Kernel
{
    private const string KernelBase = "kernelbase.dll";   // VirtualAlloc2 / MapViewOfFile3 are NOT exported by kernel32 (verified)
    private const string Kernel32  = "kernel32.dll";

    // VirtualAlloc2 flags
    public const uint MEM_RESERVE = 0x00002000, MEM_RESERVE_PLACEHOLDER = 0x00040000, PAGE_NOACCESS = 0x01, PAGE_READWRITE = 0x04;
    // MapViewOfFile3 flags (separate group: MEM_REPLACE_PLACEHOLDER shares its value with VirtualFree's MEM_DECOMMIT)
    public const uint MEM_REPLACE_PLACEHOLDER = 0x00004000;
    // VirtualFree flags
    public const uint MEM_RELEASE = 0x00008000, MEM_PRESERVE_PLACEHOLDER = 0x00000002;
    // sections / access
    public const uint FILE_MAP_WRITE = 0x0002, FILE_MAP_READ = 0x0004, FILE_MAP_ALL_ACCESS = 0x000F001F;
    public const uint SYNCHRONIZE = 0x00100000, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, PROCESS_DUP_HANDLE = 0x0040;
    public const uint DUPLICATE_SAME_ACCESS = 0x2;
    public const uint EVENT_MODIFY_STATE = 0x0002;
    // waits
    public const uint WAIT_OBJECT_0 = 0, WAIT_ABANDONED = 0x80, WAIT_TIMEOUT = 0x102, WAIT_FAILED = 0xFFFFFFFF, INFINITE = 0xFFFFFFFF;
    // errors
    public const int ERROR_FILE_NOT_FOUND = 2, ERROR_ACCESS_DENIED = 5, ERROR_INVALID_HANDLE = 6, ERROR_NOT_ENOUGH_MEMORY = 8,
                     ERROR_INVALID_PARAMETER = 87, ERROR_ALREADY_EXISTS = 183, ERROR_INVALID_ADDRESS = 487,
                     ERROR_MAPPED_ALIGNMENT = 1132, ERROR_COMMITMENT_LIMIT = 1455;
    public static readonly nint INVALID_HANDLE_VALUE = -1;

    [LibraryImport(KernelBase, SetLastError = true)]
    public static partial void* VirtualAlloc2(nint process, void* baseAddress, nuint size, uint allocationType, uint pageProtection, void* extendedParameters, uint parameterCount);

    [LibraryImport(KernelBase, SetLastError = true)]
    public static partial void* MapViewOfFile3(SafeHandle fileMapping, nint process, void* baseAddress, ulong offset, nuint viewSize, uint allocationType, uint pageProtection, void* extendedParameters, uint parameterCount);

    [LibraryImport(Kernel32, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool VirtualFree(void* address, nuint size, uint freeType);

    [LibraryImport(Kernel32, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnmapViewOfFile(void* baseAddress);

    [LibraryImport(Kernel32, EntryPoint = "CreateFileMappingW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeSectionHandle CreateFileMapping(nint file, void* securityAttributes, uint protect, uint maxSizeHigh, uint maxSizeLow, string? name);

    [LibraryImport(Kernel32, EntryPoint = "OpenFileMappingW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeSectionHandle OpenFileMapping(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, string name);

    [LibraryImport(Kernel32, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport(Kernel32)] public static partial void GetSystemInfo(SYSTEM_INFO* info);
    [LibraryImport(Kernel32)] public static partial nint GetCurrentProcess();          // pseudo handle -1; never close
    [LibraryImport(Kernel32)] public static partial ulong GetTickCount64();

    [LibraryImport(Kernel32, EntryPoint = "CreateEventW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeWaitHandle CreateEvent(void* securityAttributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, string? name);   // create-or-open (183 on open)

    // signal-path imports take nint on purpose: no SafeHandle AddRef/Release per call
    [LibraryImport(Kernel32, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetEvent(nint handle);
    [LibraryImport(Kernel32, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ResetEvent(nint handle);
    [LibraryImport(Kernel32, SetLastError = true)] public static partial uint WaitForSingleObject(nint handle, uint milliseconds);
    [LibraryImport(Kernel32, SetLastError = true)] public static partial uint WaitForMultipleObjects(uint count, nint* handles, [MarshalAs(UnmanagedType.Bool)] bool waitAll, uint milliseconds);

    [LibraryImport(Kernel32, SetLastError = true)]
    public static partial SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport(Kernel32, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport(Kernel32, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DuplicateHandle(nint sourceProcess, nint sourceHandle, nint targetProcess, out nint targetHandle, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint options);
}
```

18 imports. Not imported: `UnmapViewOfFile2`, `OpenEventW` (CreateEventW opens existing), `WaitOnAddress` (process-local), `CreateFileMapping2`, `VirtualQuery` (tests only), `MEM_EXTENDED_PARAMETER` (hints go through `BaseAddress`). Read errors with `Marshal.GetLastPInvokeError()` immediately after every call, also on success for `CreateFileMappingW`/`CreateEventW`. `SafeSectionHandle` is public (see §2), `ReleaseHandle => Kernel.CloseHandle(handle)`.

### 3.2 Section and virtual layout

```
section (pagefile-backed, PAGE_READWRITE | SEC_COMMIT), size = G + D, G = 65536:
  [0, 4096)        ControlBlock (§4)
  [4096, G)        zero (reserved)
  [G, G + D)       data

VA in every process (one placeholder of G + 2D):
  base            view 0  <- section [0, G)          Header
  base + G        view 1  <- section [G, G + D)      Data
  base + G + D    view 2  <- section [G, G + D)      Mirror  (== Data + D)
```

One section/one placeholder: one name or handle to hand over, one atomic reservation (same-address succeeds or fails as a unit), section-relative offsets identical in every process, one teardown. Readers map everything `PAGE_READWRITE` (they must write their slot line anyway; read-only data views are a later hardening).

Naming: `Create(name)`: `null` ⇒ `Local\photone.{Guid:N}`; name starting with `Local\` or `Global\` ⇒ verbatim; otherwise `Local\photone.{name}`. `Global\` creation needs `SeCreateGlobalPrivilege` (ERROR_ACCESS_DENIED is reported with that hint). Kernel events are named from `SectionId`, never from the user name (§6.2). With a `RingBufferPool` the name belongs to a small alias section instead of the ring (§15.4).

### 3.3 Create-and-map (creator) — `RingBuffer<T>.Create`

```
guards: Windows >= 10.0.17134, 64-bit process
G = SYSTEM_INFO.dwAllocationGranularity (throw PhotoneIpcException if != 65536); page = dwPageSize
(C, D) = Capacity.Choose(minCapacity, sizeof(T))                        // §7
sectionName = Normalize(name); instanceId, sectionId = two random non-zero u64 (RandomNumberGenerator.Fill)     // with options.Pool: §15.2
total = G + 2D

 1. section = CreateFileMappingW(INVALID_HANDLE_VALUE, null, PAGE_READWRITE, (uint)((G+D) >> 32), (uint)(G+D), sectionName)
    err = GetLastPInvokeError()                                          // read even on success
    invalid  -> PhotoneIpcException(err)  (5 on Global\ -> message mentions SeCreateGlobalPrivilege; 1455/8 -> commit limit)
    err == 183 -> section.Dispose(); throw RingBufferAlreadyExistsException
 2. mapping = MirroredSection.Create(section, D, candidates: AddressHint.Candidates(sectionId, total, options.PreferredBaseAddress))
    // = VirtualAlloc2 placeholder at each candidate (487 => next), then null; split at G and G+D; three MapViewOfFile3; assert mirror == data + D
 3. creator self-test (only the creator, before anybody can see InitState == 1):
      data[0] = 0xA5; assert mirror[0] == 0xA5; data[D-1] = 0x5A; assert mirror[D-1] == 0x5A; data[0] = 0; data[D-1] = 0
      failure -> RingBufferLayoutException("mirror mismatch"), unwind
 4. hdr.CreatorStartTime and hdr.CreatorPid are stored FIRST (step 5 a/b), then if options.PreFault: for every page p:
      Volatile.Write(ref data[p], 0) and Volatile.Read(ref mirror[p])   // both views get their PTEs; openers do Volatile.Read on both views (post-review #32)
 5. header init (section is zero; plain stores unless noted):
      hdr.CreatorStartTime = GetProcessTimes(GetCurrentProcess()).creation        // FIRST
      Volatile.Write(ref hdr.CreatorPid, Environment.ProcessId)                  // SECOND (openers use Pid != 0 && StartTime != 0 to check liveness)
      Magic, Version = 2, ControlBytes = 4096, ElementSize = sizeof(T), MaxReaders = 32, Capacity = C, DataBytes = D, DataOffset = G,
      TypeHash = Fnv1a32(typeof(T).FullName), SignalBackendId = SignalBackendId.NamedEvent, LayoutFlags = 0,
      CreatorBase = base, InstanceId, ReservationBytes = total, SectionId,
      WriteCursor = 0, ReserveEnd = 0, WriterState = Active, WriterPid = pid, WriterStartTime = creatorStart, WriterEpoch = 1,
      WriterWaiting = 0, WriterWaitFor = 0, masks = 0, ReaderGeneration = 0, EvictedReaders = 0, all slots zero (Free, seq 0, pid 0)
 6. backend = SignalBackend.Create(SignalBackendId.NamedEvent, sectionId, hdr)    // creates the 33 events (§6.2)
 7. Interlocked.MemoryBarrier(); Volatile.Write(ref hdr.InitState, 1)             // release: everything above becomes visible
 8. keep `section` open for the buffer's lifetime (the name dies with the last handle, not the last view)

Unwind on any failure after step 2: mapping.Dispose() (§3.5), backend?.Dispose(), section.Dispose(), rethrow.
```

### 3.4 Open-and-map (any other process) — `RingBuffer<T>.Open(string)` / `Open(SafeSectionHandle)`

```
 1. section = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, false, sectionName)   // or the supplied handle
    invalid: err == 2 -> RingBufferNotFoundException; else PhotoneIpcException(err)
 2. peek = MapViewOfFile3(section, 0, null, 0, G, 0 /*no placeholder*/, PAGE_READWRITE, null, 0)
    null: err == 5 -> RingBufferLayoutException("section smaller than 64 KiB"); else PhotoneIpcException
 3. wait for init (deadline = now + options.InitializationTimeout):
      loop:
        if Volatile.Read(ref peek.InitState) == 1: break
        pid = Volatile.Read(ref peek.CreatorPid); st = Volatile.Read(ref peek.CreatorStartTime)
        if pid != 0 && st != 0 && !ProcessLiveness.IsAlive(pid, st): throw RingBufferInitializationException("creator died during initialization")
        if now >= deadline: throw RingBufferInitializationException("timeout")
        spin 1 µs for the first 1 ms, then Thread.Yield(), then Thread.Sleep(1) after 10 ms (cold path)
 4. validate (all after the acquire load of InitState), else RingBufferLayoutException with a precise message:
      Magic == PHOTONE1, Version == 2, ControlBytes == 4096, MaxReaders == 32, DataOffset == 65536,
      ElementSize == sizeof(T), Capacity power of two, DataBytes == Capacity * ElementSize, DataBytes % 65536 == 0,
      TypeHash == 0 || TypeHash == Fnv1a32(typeof(T).FullName), SignalBackendId known to this library, InstanceId != 0
    copy D, C, CreatorBase, InstanceId, SectionId, SignalBackendId
    (Magic == PHOTLINK instead: the name is a pooled buffer's alias; resolve it first, §15.4)
 5. UnmapViewOfFile(peek)
 6. mapping = MirroredSection.Open(section, D, candidates: [CreatorBase, null])
    IsMappedAtCreatorAddress = (base == CreatorBase)                  // 487 on the first candidate => range occupied here
    ERROR_ACCESS_DENIED from MapViewOfFile3 of view 1/2 => RingBufferLayoutException("section smaller than the header claims"), unwind
    assert mirror == data + D (placement is deterministic; NO content compare — a live writer may store into data[0] between two loads)
 7. backend = SignalBackend.Create(hdr.SignalBackendId, SectionId, hdr)   // opens the existing events (create-or-open, 183 expected)
 8. IsWriter = false. No header writes.
```

### 3.5 `MirroredSection` and teardown

`MirroredSection : SafeHandle` (handle = base, `IsInvalid => handle == 0`), fields: `SafeSectionHandle Section`, `nuint DataBytes`, `byte* Header`, `byte* Data`, `byte* Mirror => Data + DataBytes`, `bool AtRequestedAddress`. Construction tracks `Span<bool> isView = stackalloc bool[3]` exactly as in RESEARCH win32-mapping §11: on failure, for each piece `isView[i] ? UnmapViewOfFile(piece) : VirtualFree(piece, 0, MEM_RELEASE)` — but `VirtualFree` only on the lowest base of a not-yet-split placeholder (after split 1 fails, one call on `base` frees everything; after split 2 fails, `base` and `base+G`).

`ReleaseHandle()` (critical finalizer, also called by `Dispose`):
```
UnmapViewOfFile(Header); UnmapViewOfFile(Data); UnmapViewOfFile(Mirror)   // VA becomes MEM_FREE; no placeholders remain (verified)
Section.Dispose()                                                           // last handle => name disappears; memory dies with the last view/handle anywhere
```

`RingBuffer<T>.Dispose()`:
```
if Interlocked.Exchange(ref _disposed, 1) != 0: return
if IsWriter: CloseWriter()                                 // §5.8
ReleaseLocalRef()                                          // _localRefs starts at 1 for the buffer; +1 per live RingReader created by it
ReleaseLocalRef(): if Interlocked.Decrement(ref _localRefs) == 0: ReleaseNative()
ReleaseNative(): close cached laggard process handles; _backend.Dispose(); _mapping.Dispose()
```
A `RingReader` created from this buffer stays fully usable after the buffer is disposed (it holds the pointers; the mapping is released by the last reader's `Dispose`). `Bucket` is a ref struct and cannot outlive its frame; a `Chunk` may be stored, but its `Data` is the mapping itself and keeps nothing alive (DEVIATIONS 56). Process death: the kernel frees views, handles and placeholders; the section survives while any other process holds a handle or view.

### 3.6 Address hint (`Internal\AddressHint.cs`)

```
Candidates(instanceId, total, preferred):
  if preferred is ulong p: if p == 0 -> yield null only; else require p % 65536 == 0 (ArgumentException); yield p; yield null; return
  stride = max(2 MiB, RoundUpPow2(total)); slots = 0x1000_0000_0000 / stride
  h = SplitMix64(instanceId)
  for probe in 0..3: yield 0x4000_0000_0000 + ((h + probe * 0x9E3779B97F4A7C15) % slots) * stride
  yield null
```
The 16 TiB window `[0x4000_0000_0000, 0x5000_0000_0000)` is far from heaps/DLLs/stacks/GC segments; identical addresses in peers succeed with high probability. Correctness never depends on it: shared memory holds only cursors and offsets.

---

## 4. Shared-memory layout (`Internal\Layout.cs`)

Constants: `Magic = 0x31454E4F544F4850` ("PHOTONE1" LE), `Version = 4` (§16), `ControlBytes = 4096`, `HeaderViewBytes = 65536` (the control view; the data follows it, then the tag reserve, if any, §16.2), `DataOffset = 65536`, `MaxReaders = 32`, `SlotBase = 512`, `SlotBytes = 64`, `BackendAreaOffset = 2560`, `BackendAreaBytes = 128`.

Static constructor asserts: `Unsafe.SizeOf<ControlBlock>() == 4096`, `Unsafe.SizeOf<ReaderSlot>() == 64`, every `Interlocked`/`Volatile` field offset `% 8 == 0`, `WriteCursor` offset `% 128 == 0`. All accesses go through `ref ControlBlock Hdr => ref Unsafe.AsRef<ControlBlock>(_hdr)` and `ref ReaderSlot SlotRef(int i) => ref Unsafe.AsRef<ReaderSlot>(_hdr + 512 + 64 * i)`.

### 4.1 `ControlBlock` (`LayoutKind.Explicit, Size = 4096`)

| Offset | Type | Field | Line | Written by | Read by |
|---|---|---|---|---|---|
| 0 | u64 | `Magic` | 0 | creator once | opener once |
| 8 | u32 | `Version` = 4 | 0 | | |
| 12 | u32 | `ControlBytes` = 4096 | 0 | | |
| 16 | u32 | `ElementSize` | 0 | | |
| 20 | u32 | `MaxReaders` = 32 | 0 | | |
| 24 | i64 | `Capacity` (C) | 0 | | |
| 32 | i64 | `DataBytes` (D) | 0 | | |
| 40 | i64 | `DataOffset` = 65536 | 0 | | |
| 48 | u32 | `TypeHash` (FNV-1a 32 of UTF-16 `typeof(T).FullName`) | 0 | | |
| 52 | i32 | `InitState` (0 initializing, 1 ready; `Volatile.Write` last) | 0 | creator | opener spin (acquire) |
| 56 | u32 | `SignalBackendId` (1 = NamedEvent) | 0 | creator | opener |
| 60 | u32 | `LayoutFlags` (bit 0 `Pooled`: the section belongs to a `RingBufferPool`, §15) | 0 | creator | opener |
| 64 | u64 | `CreatorBase` | 1 | creator | opener |
| 72 | i32 | `CreatorPid` (written 2nd, `Volatile.Write`) | 1 | creator | opener init spin |
| 76 | i32 | pad | 1 | | |
| 80 | i64 | `CreatorStartTime` (FILETIME; written 1st) | 1 | creator | opener init spin |
| 88 | u64 | `InstanceId` (random ≠ 0; identifies the buffer: a pooled section gets a new one on every reuse) | 1 | creator | opener |
| 96 | u64 | `ReservationBytes` = 64 KiB + 2D | 1 | | diagnostics |
| 104 | u64 | `SectionId` (random ≠ 0; identifies the section for its whole life; names the events) | 1 | creator | opener |
| 112 | i32 | `TagMode` (0 none, 1 in-process, 2 cross-process; §16) | 1 | creator | opener |
| 116 | | pad | 1 | | |
| 120 | i64 | `TagReserveBytes` (the tag reserve after the data: `TagFormat.ReserveBytes` with cross-process tags, else 0; §16.2) | 1 | creator | opener |
| **128** | **i64** | **`WriteCursor` (W)** | **2** | **writer, `Interlocked.Exchange` per Commit** | **readers, `Volatile.Read` (polled)** — nothing else on this line |
| 136 | i64 | `TagEnd` (§16.4) | 2 | writer, `Volatile.Write` before `WriteCursor` on a commit that carries tags | readers, after loading `W` |
| 144..191 | | reserved (never written) | 2 | | |
| 192..255 | | **empty** — prefetch-pair partner of line 2 | 3 | | |
| 256 | i64 | `ReserveEnd` (E, informational) | 4 | writer per GetBucket/Commit | diagnostics |
| 264 | i32 | `WriterState` (0 None, 1 Active, 2 Closed) | 4 | writer | reader slow path |
| 268 | i32 | `WriterPid` | 4 | creator | reader join |
| 272 | i64 | `WriterStartTime` | 4 | creator | reader join |
| 280 | u32 | `WriterEpoch` = 1 (reserved for takeover) | 4 | creator | |
| 284..319 | | reserved | 4 | | |
| 320..383 | | **empty** — prefetch-pair partner of line 4 | 5 | | |
| 384 | i32 | `WriterWaiting` (0/1) | 6 | writer `Exchange` on block/unblock; one reader `Exchange(0)` per writer sleep | every `Advance` (`Volatile.Read`; line stays Shared) |
| 388 | i32 | `WriterWaiterThreadId` (backend word; 0 for NamedEvent) | 6 | writer before blocking | backends |
| 392 | i64 | `WriterWaitFor` (absolute min reader cursor that frees enough space) | 6 | writer before `Exchange(WriterWaiting,1)` | `Advance` when `WriterWaiting != 0` |
| 400 | u64 | `WriterWaitSinceTick` (GetTickCount64; diagnostics) | 6 | writer | |
| 408 | i64 | `WriterBackendWord` (backend word) | 6 | backends | backends |
| 416..447 | | reserved | 6 | | |
| 448 | u64 | `WaitersMask` (bit i ⇒ reader i published intent to block) | 7 | readers `Or/And`; writer `And` when consuming | writer per Commit (`Volatile.Read`) |
| 456 | u64 | `ActiveMask` (hint; slot word is authoritative) | 7 | readers/writer on join/leave/evict | diagnostics |
| 464 | u32 | `ReaderGeneration` | 7 | `Interlocked.Increment` on join/leave/evict | diagnostics |
| 468 | u32 | `EvictedReaders` | 7 | evictor | diagnostics |
| 472 | i32 | `LastEvictedPid` | 7 | evictor | diagnostics |
| 476..511 | | reserved | 7 | | |
| 512 + 64·i | `ReaderSlot` | `Slots[0..31]` | 8..39 | slot owner; evictor CAS | writer `ScanMin` |
| 2560..2687 | | **SignalBackend area** (backend-private, zero for NamedEvent) | 40..41 | backends | backends |
| 2688 | u64 | `TagVersion` (seqlock, §16.6) | 42 | writer after a commit that carries tags | joining readers |
| 2696 | i64 | `TagSnapshotEnd` | 42 | writer (odd version) | joining readers |
| 2704 | i64 | `TagSnapshotW` | 42 | writer (odd version) | joining readers |
| 2712 | i32 | `TagStateUsed` | 42 | writer (odd version) | joining readers |
| 2716 | i32 | `TagStateCount` | 42 | writer (odd version) | diagnostics |
| 2720 | i32 | `TagSnapshotRing` (the ring of `TagSnapshotEnd`) | 42 | writer (odd version) | joining readers |
| 2728 | i64 | `TagSnapshotRingStart` (the log position of that ring's generation) | 42 | writer (odd version) | joining readers |
| 2736 | i64 | `TagTableCommitted` (only grows) | 42 | writer, after the commit, before the table uses it | joining readers |
| 2744..2751 | | reserved | 42 | | |
| 2752 | i64[19] | `TagRingCommitted` (per ring size class; only grow) | 43..45 | writer, after the commit, before records lie there | readers of shared tags |
| 2904..4095 | | reserved (zero) | 45..63 | | |

Line-pair rule (x64 adjacent-line prefetcher pairs 128-byte-aligned line pairs): lines stored on the writer's fast path (2: per Commit, 4: per GetBucket) have empty partners (3, 5), so a reader polling `W` never pulls a line the writer is about to store, and vice versa. Lines 6/7 are a pair but both change only on block/join/evict.

### 4.2 `ReaderSlot` (`LayoutKind.Explicit, Size = 64`)

| Offset | Type | Field | Written by |
|---|---|---|---|
| 0 | i64 | `Word` = `pid:32 \| seq:30 \| state:2` — CAS'd as one word | owner (claim/activate/free), evictor (CAS) |
| 8 | i64 | `ReadCursor` (R_i) | owner: `Interlocked.Exchange` per Advance, `Volatile.Write` at join |
| 16 | i64 | `WaitFor` (absolute W the reader waits for; `long.MaxValue` when idle) | owner, plain store before `Or(WaitersMask)` |
| 24 | i64 | `ProcessStartTime` (FILETIME; PID-reuse guard) | owner right after the claim CAS |
| 32 | u64 | `ClaimTick` (GetTickCount64 at claim; stuck-claim reclaim) | owner right after the claim CAS |
| 40 | i32 | `Flags` (bit0 MappedAtCreatorAddress; bit1 reserved RewindRequested) | owner |
| 44 | i32 | `EvictReason` (0 none, 1 Dead, 2 StuckClaim, 3 Lag[reserved]) | evictor |
| 48 | i32 | `WaiterThreadId` (backend word; 0 for NamedEvent) | owner via backend |
| 52 | i32 | reserved | |
| 56 | i64 | `BackendWord` (backend word: wake token / futex word) | backends |

```csharp
internal static class SlotState { public const int Free = 0, Claimed = 1, Active = 2, Dead = 3; }
internal static class SlotWord
{
    public static long Make(int state, uint seq, int pid) => ((long)pid << 32) | ((long)(seq & 0x3FFF_FFFF) << 2) | (uint)state;
    public static int  State(long w) => (int)(w & 3);
    public static uint Seq(long w)   => (uint)((w >> 2) & 0x3FFF_FFFF);
    public static int  Pid(long w)   => (int)(w >> 32);
}
```
A `Claimed` slot always identifies its claimant atomically, so the "`Pid == 0` for > 1 s" heuristic disappears; the 30-bit sequence plus the PID makes every CAS ABA-proof. `Free` keeps the sequence (`Make(Free, seq, 0)`) so the next claimant gets `seq + 1`.

Line ownership: line 0/1 creator once; line 2 writer; line 4 writer; line 6 writer (store) + readers (one `Exchange` per writer sleep); line 7 readers RMW (rare) + writer `And` when consuming bits; slot `i` owner stores/RMW, writer loads on scan, evictor CAS. Two readers never write the same line; the writer never stores to a line a reader spins on except `WriteCursor`, which *is* the message.

---

## 5. Synchronization protocol

Notation: `W = Hdr.WriteCursor`; writer-local `_w` (== W), `_e` (reserve end, authoritative), `_min` (cached minimum), `_outstanding`; reader-local `_r` (== own `ReadCursor`), `_wc` (cached W), `_bit = 1UL << slot`, `_word = Make(Active, seq, pid)`. `s = sizeof(T)`, `mask = C - 1`, `off(cursor) = (cursor & mask) * s`. Cursors are absolute `long` element counts.

Primitives: `Volatile.Read` = acquire, `Volatile.Write` = release, `Interlocked.*` = atomic RMW + full fence (StoreLoad included), `Interlocked.MemoryBarrier()` = full fence. `Volatile.ReadBarrier/WriteBarrier` are never used.

### 5.1 Writer: GetBucket / TryGetBucket

```
GetBucket(n):
    ObjectDisposedException.ThrowIf(_disposed != 0, this)
    if (!IsWriter || _closed) throw InvalidOperationException
    if ((uint)(n - 1) >= (uint)C) throw ArgumentOutOfRangeException
    if (_outstanding) throw InvalidOperationException("one outstanding bucket")
    if (C - (_e - _min) < n)                     // cached view insufficient (invariant: _min <= true min, so this only under-estimates)
        SlowGetBucket(n)                         // [NoInlining] §5.3: scan and wait under a local ref, then publish _outstanding behind a full fence and re-check _closed
    _outstanding = true                          // (Reserve, inlined; after SlowGetBucket it is already published)
    long start = _e; _e += n                     // (ReserveEnd is NOT stored: it shares a line with WriterState; post-review #27)
    return new Bucket<T>(this, new Span<T>(_data + off(start), n), start)

TryGetBucket(n, out b): same checks; if short: _min = ScanMin(); if still short { b = default; return false }; then as above.
```
Span validity: `off(start) ≤ (C-1)·s`, length `n·s ≤ D` ⇒ the span ends before `data + 2D` (inside the mirror).

### 5.2 Writer: Commit (`Bucket.Commit` / `Bucket.Dispose` → `EndWrite`)

```
Bucket.Commit(k): if (_committed >= 0) throw InvalidOperationException; if ((uint)k > (uint)Length) throw AOOR; _committed = k; _owner.EndWrite(k); _span = default
Bucket.Dispose():  if (_committed < 0) { _committed = 0; _owner.EndWrite(0); _span = default; }

EndWrite(k):
    _w += k; _e = _w                                          // shrink the reservation: next bucket starts at W + k (no holes)
    Interlocked.Exchange(ref Hdr.WriteCursor, _w)             // RELEASE of all bucket data stores + STORE-LOAD fence (Dekker, §5.10) in one locked op
    _counters.Commits++
    ulong m = Volatile.Read(ref Hdr.WaitersMask)              // stays Shared in the writer's L1 while nobody blocks
    if (m != 0) SignalReaders(m, _w)                          // [NoInlining]; ~never on a warm pipeline
    _outstanding = false

SignalReaders(m, w):
    while (m != 0):
        i = TrailingZeroCount(m); bit = 1UL << i; m &= m - 1
        if (Volatile.Read(ref Slot(i).WaitFor) <= w)          // only readers whose request is now satisfied
        {
            Interlocked.And(ref Hdr.WaitersMask, ~bit)        // consume the flag FIRST ...
            _backend.WakeReader(i); _counters.Signals++       // ... then signal (safe order, §5.10)
        }
```

### 5.3 Writer: ScanMin and WaitForSpace

```
SlowGetBucket(n):                                             // [NoInlining]
    if (!TryAddLocalRef()) throw ObjectDisposedException      // refused once the native resources are released
    try:
        ObjectDisposedException.ThrowIf(_disposed != 0, this) // after the ref: Dispose may have started, but nothing is unmapped while the ref is held
        _min = ScanMin()
        if (C - (_e - _min) < n) WaitForSpace(n)
    finally:
        ReleaseLocalRef()                                     // releases the mapping if a Dispose on another thread already dropped the buffer's ref
    _outstanding = true                                       // the reservation, published ...
    Interlocked.MemoryBarrier()                               // ... [full fence] before _closed is loaded (Dekker pair with CloseWriter, see below)
    if (Volatile.Read(ref _closed) != 0)                      // a Dispose on another thread got here first
        { _outstanding = false; throw ObjectDisposedException }   // [NoInlining] AbandonReservation: touches no shared memory
                                                              // back in GetBucket, Reserve hands out the bucket (§5.1)

ScanMin():                                                    // slow path only; 32 acquire loads
    long min = long.MaxValue
    for i in 0..31:
        long w = Volatile.Read(ref Slot(i).Word)              // acquire: if Active, the cursor stored before Active is visible
        if (State(w) != Active) continue
        min = Math.Min(min, Volatile.Read(ref Slot(i).ReadCursor))
    _counters.Scans++
    return min == long.MaxValue ? _e : min                    // no readers => everything is free (vmcircbuffer semantics)
    // Debug.Assert after every assignment _min = ScanMin(): for every Active slot, _min <= Volatile.Read(ReadCursor)

WaitForSpace(n):
    long target = _e + n - C                                  // min reader cursor that frees n elements (fixed for this episode; _e == _w here)
    // phase 1: spin, rescanning at most every ~1 µs, polling with pause in between
    clock = SpinClock.Start(_options.SpinTime)
    while (!clock.Expired) { Thread.SpinWait(1); if (clock.Tick(1 µs)) { _min = ScanMin(); if (C - (_e - _min) >= n) return; } }
    // phase 2: kernel
    _counters.KernelWaits++
    Volatile.Write(ref Hdr.WriterWaitSinceTick, GetTickCount64())
    while (true)
    {
        RefreshLaggards()                                     // §5.7: every Active slot with ReadCursor == _min gets a validated cached process handle (or poll mode); dead ones evicted now
        Volatile.Write(ref Hdr.WriterWaitFor, target)         // release store BEFORE the flag
        _backend.OnWriterBlocking(_hdr)                       // e.g. record WriterWaiterThreadId; no-op for NamedEvent
        Interlocked.Exchange(ref Hdr.WriterWaiting, 1)        // publish intent [full fence]
        _min = ScanMin()                                      // re-check AFTER the fence (Dekker)
        if (C - (_e - _min) >= n) { Interlocked.Exchange(ref Hdr.WriterWaiting, 0); return; }
        uint ms = LivenessCheckIntervalMs                     // backstop slice (poll-mode laggards, stale sets, disposal)
        WaitOutcome rc = _backend.WaitForSpace(_laggardHandles.AsSpan(0, _laggardCount), ms, out int exited)
        Interlocked.Exchange(ref Hdr.WriterWaiting, 0)        // we are awake; a reader that consumed the flag already signalled (stale set: harmless)
        switch (rc)
        {
            case Signaled:      continue                      // a reader advanced / disposed / stale set: loop re-scans
            case ProcessExited: Evict(_laggardSlot[exited], _laggardWord[exited], EvictReason.Dead); continue
            case Timeout:       SweepLaggards(); ObjectDisposedException.ThrowIf(_disposed != 0, this); continue   // WFSO(h,0) on cached handles; IsAlive() for poll-mode laggards
            case Failed:        throw PhotoneIpcException(GetLastPInvokeError(), "WaitForMultipleObjects")
        }
    }
```
The laggard set is recomputed on every iteration, so a reader that becomes the sole blocker after the episode started (judge 2's deadlock) is always covered. Reader-crash wake latency is the kernel's process-signal latency (< 1 ms) when a handle is available and one `LivenessCheckInterval` when it is not.

**Dispose from another thread** is the way to abort a writer blocked here (post-review #26). The local reference keeps the mapping valid under the scan and the wait; `CloseWriter` wakes the space event, both wait phases see `_disposed`, and the call throws `ObjectDisposedException`. The reference ends with the wait: the reservation that follows touches no shared memory, but the bucket it returns outlives the call, so a disposal must not miss it. If one did, `CloseWriter` would find no bucket, `_closedWithBucket` would stay false, and the last `ReleaseLocalRef` would unmap the section, or return it to the pool, where the next `Create` can take it, while the caller fills a span into it: an access violation, or writes into another buffer's shared memory. Dekker pair: `SlowGetBucket` stores `_outstanding` and passes a full fence before it loads `_closed`; `CloseWriter` stores `_closed` with `Interlocked.Exchange` before it loads `_outstanding` (§5.8). Either `CloseWriter` sees the reservation, drops it with `EndWrite(0)` and sets `_closedWithBucket` (the mapping is released, never pooled, as for any bucket outstanding at `Dispose`: a late store faults), or the call sees `_closed` and throws `ObjectDisposedException` without handing out a span. When both happen the call throws and the mapping is still released; the concurrent stores of `_outstanding = false` store the same value. `CloseWriter` does not wait for the call. Tests: `Create_DisposeBetweenTheSpaceWaitAndTheReservation_GetBucketThrows_TheNextBufferReusesTheSection` and `Create_DisposeAfterTheReservationWasPublished_ReleasesTheSectionInsteadOfPoolingIt_GetBucketThrows` (`TestHooks.AfterSpaceWait` / `AfterReservationPublished` dispose on another thread at exactly those two points).

**The fast path** (`GetBucket` without a wait, and `TryGetBucket`) is unchanged: `CheckWriter` loads `_closed` before `Reserve` stores `_outstanding`, so a `Dispose` on another thread that lands between the two can still miss the reservation, and a preemption there makes that window arbitrarily long. Such a call races a writer that is not blocked, which is not the supported way to abort one. Covering it costs on every bucket or on every `Dispose`:

* the slow path's order without the fence (store `_outstanding`, then load `_closed`) leaves only a store-buffer-latency window, but measured 0.3-0.9 ns more per `WriteRead_Protocol` round than this layout (one interleaved series: 18.9 / 19.1 / 21.6 ns against 18.5 / 18.6 / 20.7 ns for buckets of 256 / 4096 / 65536 floats; the unchanged code 18.2 / 18.3 / 20.4 ns);
* the same order with the fence is exact, and adds a locked instruction per bucket;
* the unfenced order plus `FlushProcessWriteBuffers` (`Interlocked.MemoryBarrierProcessWide`) in `CloseWriter` before its `_outstanding` load is exact too, for 1.1 µs (idle process) to 6.5 µs (three busy threads) and an interprocessor interrupt to every core that runs a thread of the process, on every writer `Dispose`.

`SlowGetBucket` stays `void`, and the bucket is built by the inlined `Reserve` in `GetBucket` on both paths: in the same series, the unfenced order with `SlowGetBucket` returning the bucket (a struct return path in `GetBucket`) measured 20.0 / 20.2 / 21.9 ns, 0.3-1.2 ns more than the order alone, although the fast path executed the same instructions.

### 5.4 Reader: TryRead / Available / Advance

```
TryRead(n, out chunk):
    ThrowIfDisposed(); if ((uint)n > (uint)C) throw AOOR
    if (_wc - _r < n) { _wc = Volatile.Read(ref Hdr.WriteCursor); if (_wc - _r < n) { chunk = default; return false; } }   // ACQUIRE: later data loads see everything published <= _wc
    chunk = new Chunk<T>(_mem.Slice(idx(_r), n), _r); return true              // _mem: the data region + mirror as ReadOnlyMemory<T> (DEVIATIONS 56); idx(c) == c & mask

Available: _wc = Volatile.Read(ref Hdr.WriteCursor); return _wc - _r

Advance(k):
    if ((uint)k > (uint)(_wc - _r)) throw AOOR                // validated against the cached W; W is monotone so it holds for the real W too
    if (k == 0) return
    if (Volatile.Read(ref Slot.Word) != _word) ThrowEvicted() // one L1 load of our own line, BEFORE the cursor store: a slot we no longer own is never written
    long old = _r; _r += k
    Interlocked.Exchange(ref Slot.ReadCursor, _r)             // RELEASE of all prior data loads + full fence (one xchg)
    if (Volatile.Read(ref Hdr.WriterWaiting) != 0) SignalWriterIfCrossing(old)   // [NoInlining]; line stays Shared while the writer is not blocked

SignalWriterIfCrossing(old):
    long t = Volatile.Read(ref Hdr.WriterWaitFor)             // stored by the writer before its Exchange(WriterWaiting,1), so visible after our acquire of the flag
    if (old < t && _r >= t)                                   // only the Advance that can complete "min >= target" pays (§5.10)
        if (Interlocked.Exchange(ref Hdr.WriterWaiting, 0) == 1) _backend.WakeWriter()   // exactly one reader pays the syscall per writer sleep
```

### 5.5 Reader: WaitSync and the shared blocking loop

```
WaitSync(n, timeout):
    ThrowIfDisposed(); if ((uint)n > (uint)C) throw AOOR; if (n == 0) return true
    long target = _r + n
    if (_wc >= target || (_wc = Volatile.Read(ref Hdr.WriteCursor)) >= target) return true   // fast path; the cached W is consulted first (it only rises)
    if (timeout == TimeSpan.Zero) return false
    BeginWait()                                                 // Exchange(_waitOutstanding, 1) [full fence]; then if (_disposing != 0) { _waitOutstanding = 0; throw ObjectDisposedException }  (Dekker with Dispose, post-review #31)
    try {
        if (SpinUntil(target, _options.SpinTime)) return true
        return BlockUntil(target, ToDeadline(timeout), honorCancel: false)
    } finally { _waitOutstanding = 0 }

SpinUntil(target, budget):                                     // polls W on every pause; timestamp every 16 iterations
    clock = SpinClock.Start(budget); int i = 0
    while (true) {
        if (Volatile.Read(ref Hdr.WriteCursor) >= target) { _wc = ...; _counters.SpinSuccesses++; return true; }
        Thread.SpinWait(1)
        if ((++i & 15) == 0 && clock.Expired) return false
    }

BlockUntil(target, deadline, honorCancel):                    // used by WaitSync (caller thread) and by the async waiter thread
    while (true)
    {
        if (Volatile.Read(ref _disposing) != 0) throw new ObjectDisposedException(...)
        if (Volatile.Read(ref Slot.Word) != _word) { _status = Evicted; throw new ReaderEvictedException(_slot); }
        Slot.WaitFor = target                                  // plain store; ordered before the mask bit by the RMW below
        Interlocked.Or(ref Hdr.WaitersMask, _bit)              // publish "I will block" [full fence]
        long w = Volatile.Read(ref Hdr.WriteCursor)            // re-check AFTER the fence
        if (w >= target) { Withdraw(); _wc = w; return true; }
        if (CheckWriterGone()) { Withdraw(); _wc = Volatile.Read(W); return _wc >= target; }   // drain what is published; false if fewer than n remain
        if (honorCancel && Volatile.Read(ref _cancelRequested) != 0) { Withdraw(); throw new OperationCanceledException(_ct); }
        uint ms = SliceMs(deadline)                            // remaining until deadline; capped at LivenessCheckInterval when _writerProc == 0 (poll mode); INFINITE otherwise
        WaitOutcome rc = _backend.WaitForData(_slot, _writerProc, ms)
        Withdraw()                                             // Interlocked.And(ref Hdr.WaitersMask, ~_bit); the writer may already have consumed it: harmless
        _counters.KernelWaits++
        switch (rc)
        {
            case Signaled:      if (Volatile.Read(W) < target) _counters.SpuriousWakes++; continue
            case ProcessExited: _writerExited = true; continue                 // CheckWriterGone decides Closed vs Terminated
            case Timeout:       if (_writerProc == 0) PollWriterLiveness(); if (Stopwatch.GetTimestamp() >= deadline) return false; continue
            case Failed:        throw PhotoneIpcException(...)
        }
    }
    // Withdraw(): Interlocked.And(ref Hdr.WaitersMask, ~_bit); Slot.WaitFor = long.MaxValue

CheckWriterGone():
    int ws = Volatile.Read(ref Hdr.WriterState)
    if (ws == Closed) { _status = WriterClosed; return true; }
    if (_writerExited) { _status = WriterTerminated; return true; }
    return false
PollWriterLiveness(): if (!ProcessLiveness.IsAlive(Hdr.WriterPid, Hdr.WriterStartTime)) _writerExited = true   // poll mode only (ACCESS_DENIED)
```
`_wc` is only ever raised, so `Advance`'s validation against it stays sound. Every wake re-checks the condition; nothing assumes "woken ⇒ condition true".

### 5.6 Reader creation (join) — `RingBuffer<T>.CreateReader`

```
CreateReader(opts):
    ThrowIfDisposed(); pid = Environment.ProcessId; st = ProcessLiveness.OwnStartTime (cached per process)
    for attempt in 0..1:
        for i in 0..31:
            long w = Volatile.Read(ref Slot(i).Word)
            if (State(w) != Free) continue
            uint seq = (Seq(w) + 1) & 0x3FFF_FFFF
            long claimed = Make(Claimed, seq, pid)
            if (Interlocked.CompareExchange(ref Slot(i).Word, claimed, w) != w) continue           // lost the race; next slot   [full fence]
            Slot(i).ProcessStartTime = st; Slot(i).ClaimTick = GetTickCount64(); Slot(i).WaitFor = long.MaxValue
            Slot(i).Flags = IsMappedAtCreatorAddress ? 1 : 0; Slot(i).EvictReason = 0; Slot(i).WaiterThreadId = 0; Slot(i).BackendWord = 0
            _backend.OnSlotClaimed(i, &Slot(i))                                                    // NamedEvent: ResetEvent(slot event) — discards a stale set from a previous owner
            Slot(i).ReadCursor = Volatile.Read(ref Hdr.WriteCursor)                                // (a) provisional start = head
            long active = Make(Active, seq, pid)
            if (Interlocked.CompareExchange(ref Slot(i).Word, active, claimed) != claimed)         // (b) publish [full fence]; fails only if a sweeper reclaimed a "stuck" claim (>10 s)
                throw new ReaderEvictedException(i, "claim reclaimed")
            long w2 = Volatile.Read(ref Hdr.WriteCursor)                                           // (c) re-read AFTER the fence
            Volatile.Write(ref Slot(i).ReadCursor, w2)                                             // (d) adopt the newest head (monotone bump)
            Interlocked.Or(ref Hdr.ActiveMask, 1UL << i); Interlocked.Increment(ref Hdr.ReaderGeneration)
            Interlocked.Increment(ref _localRefs)
            start = w2; if the buffer has tags this instance can see: tags = Join(w2, out start)       // (e) §16.5 / §16.6: start = max(w2, snapshot W)
            Interlocked.Exchange(ref Slot(i).ReadCursor, start)                                    // (f) publish [full fence] ...
            if (WriterWaiting != 0 && start >= WriterWaitFor && Exchange(WriterWaiting, 0) == 1) WakeWriter()   // ... then wake a writer this join unblocked
            var reader = new RingReader<T>(this, i, active, cursor: start, opts, tags)
            reader.ResolveWriterProcess()                                                          // §5.7 (dead-at-join / poll mode)
            return reader
            (on an exception after (b), e.g. a malformed tag snapshot: ReleaseFailedClaim, as a disposed reader releases its slot (§5.9): clear the
             waiter bit and WaitFor, zero ProcessStartTime and ClaimTick, CAS the word to Free, clear the mask, bump the generation, wake a waiting writer)
        if (attempt == 0) SweepDeadSlots()                                                         // §5.7; any process may run it
    throw new TooManyReadersException()
```
Why (c)/(d) (the writer never laps a joiner): the writer reserves against `_min` from some `ScanMin`. If that scan saw the slot Active it read `ReadCursor ≥ w1`, so `_min ≤ R_new`. If it did not, the scan's loads follow the full fence of the writer's last Commit (`Exchange(W)`) and the reader's load (c) follows the full fence of (b); by Dekker (c) sees the `W` store that preceded the scan, so `w2 ≥ W_at_scan ≥ every old cursor ≥ _min`. Either way `E ≤ _min + C ≤ R_new + C`. The scan reads all 32 slot words; `ActiveMask` is not load-bearing.

Why (f) (post-review, 2026-09-17): a writer blocked in `BlockForSpace` may have scanned the provisional cursor of (a), and a commit made after that load can
leave it below the writer's target (a space target `E + count - C`), while (d) and the tag bump of (e) store more without waking anybody: the writer
would sleep a liveness slice. The fenced store followed by the flag load is the `Advance` side of the Dekker pair
(§5.10).

### 5.7 Liveness, eviction, dead writer

```
ProcessLiveness.IsAlive(pid, startTime):                       // never on a hot path
    h = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid)
    if h invalid: err = GetLastPInvokeError(); return err != ERROR_INVALID_PARAMETER     // 87 => no such PID => dead; 5 => exists but inaccessible => ALIVE (never evict what cannot be proven dead)
    using h: if (!GetProcessTimes(h, out creation, ...) || creation != startTime) return false   // PID reused => original owner dead
             return WaitForSingleObject(h, 0) != WAIT_OBJECT_0

RefreshLaggards():                                             // writer, once per WaitForSpace iteration
    _laggardCount = 0
    for i in 0..31:
        long w = Volatile.Read(ref Slot(i).Word)
        if (State(w) != Active || Volatile.Read(ref Slot(i).ReadCursor) != _min) continue
        entry = _laggards[i]                                   // cached per slot: (Word, SafeProcessHandle?, nint Raw, bool PollMode)
        if (entry.Word != w): close old; entry = Open(w):
              h = OpenProcess(SYNCHRONIZE | PQLI, false, Pid(w))
              invalid + 87 -> Evict(i, w, Dead); continue
              invalid + 5  -> entry = PollMode (no handle)
              GetProcessTimes(h).creation != Slot(i).ProcessStartTime -> Evict(i, w, Dead); continue
              else entry = (w, h, h.DangerousGetHandle() after DangerousAddRef, false)
        if (!entry.PollMode) { _laggardHandles[_laggardCount] = entry.Raw; _laggardSlot[_laggardCount] = i; _laggardWord[_laggardCount] = w; _laggardCount++ }
        else _pollLaggards.Add(i)
    // _laggardCount <= 32 => WFMO set <= 33 handles

SweepLaggards():                                               // on every Timeout slice while blocked
    for each cached handle entry: if (WaitForSingleObject(entry.Raw, 0) == WAIT_OBJECT_0) Evict(i, entry.Word, Dead)
    for each poll-mode laggard: if (!IsAlive(Pid(w), Slot(i).ProcessStartTime)) Evict(i, w, Dead)
    _min = ScanMin()

SweepDeadSlots():                                              // CreateReader (any process) when no Free slot is found; also exposed internally for tests
    for i in 0..31:
        long w = Volatile.Read(ref Slot(i).Word)
        switch State(w):
          Claimed: st = Volatile.Read(ref Slot(i).ProcessStartTime)                     // identity fields are zeroed before a slot is freed, so a non-zero value belongs to THIS claimant
                   dead = st == 0 ? !ProcessExists(Pid(w)) : !IsAlive(Pid(w), st)         // st == 0: claim CAS done, identity not yet stored: only "no such PID" counts
                   if (dead) Evict(i, w, StuckClaim)                                      // a claim of a LIVE process is never reclaimed, however old (post-review #28)
          Active:  if (!IsAlive(Pid(w), Volatile.Read(ref Slot(i).ProcessStartTime))) Evict(i, w, Dead)
          Dead:    (tombstone; v1 never leaves one) -> nothing

Evict(i, expectedWord, reason):                                // ABA-proof: expects the full word
    long dead = Make(Dead, Seq(expectedWord), Pid(expectedWord))
    if (Interlocked.CompareExchange(ref Slot(i).Word, dead, expectedWord) != expectedWord) return false   // slot changed under us (released/re-claimed): never evict the new owner
    Slot(i).EvictReason = reason; Hdr.LastEvictedPid = Pid(expectedWord)
    Interlocked.And(ref Hdr.WaitersMask, ~bit); Interlocked.And(ref Hdr.ActiveMask, ~bit)
    close the writer's cached laggard entry for i (if this process is the writer)
    Interlocked.Increment(ref Hdr.EvictedReaders); Interlocked.Increment(ref Hdr.ReaderGeneration); _counters.Evictions++
    Slot(i).ProcessStartTime = 0; Slot(i).ClaimTick = 0; Slot(i).WaitFor = long.MaxValue   // identity hygiene (post-review #28)
    Interlocked.Exchange(ref Slot(i).Word, Make(Free, Seq(expectedWord), 0))   // v1: only DEAD owners are ever evicted, so Dead -> Free immediately is safe
    return true
```
**Live-reader eviction (not in v1).** If a `MaxReaderLag` policy is added later, `Evict` must leave the slot in `Dead` (tombstone) and only the evictee (on its next `Advance`/`Wait`/`Dispose`, which observe `Word != _word`) or its process death may move it to `Free`. Otherwise a live evictee's `Exchange(ReadCursor)` can land in a re-claimed slot and over-estimate the new owner's cursor. The `Word` check before the cursor store in `Advance` (§5.4) is the second line of defence.

Dead writer (reader side):
```
ResolveWriterProcess():                                        // at join
    if (Volatile.Read(ref Hdr.WriterState) == Closed) { _status = WriterClosed; return; }
    h = OpenProcess(SYNCHRONIZE | PQLI, false, Hdr.WriterPid)
    invalid + 87                                       -> _writerExited = true                       // dead at join: Wait returns per the drained state immediately
    invalid + 5                                        -> _writerProc = 0 (poll mode; liveness polled every LivenessCheckInterval inside BlockUntil)
    GetProcessTimes(h).creation != Hdr.WriterStartTime -> _writerExited = true; close h
    else _writerProcHandle = h; _writerProc = raw (DangerousAddRef)
```
Every kernel wait of the reader includes `_writerProc`, so a writer crash wakes it immediately; `[R, W)` remains fully published and drainable; `Wait` returns `false` with `Status == WriterTerminated` when `count` cannot be satisfied. `TryRead`/`Advance` keep working on the published range. Writer takeover is out of scope (`WriterEpoch`/`WriterPid` reserved).

### 5.8 Writer close

```
CloseWriter():                                                  // from RingBuffer.Dispose (writer role) or the finalizer; idempotent
    if (Interlocked.Exchange(ref _closed, 1) != 0) return       // [full fence] before _outstanding is loaded: Dekker pair with SlowGetBucket (§5.3)
    if (_outstanding) { _closedWithBucket = true; EndWrite(0) } // drop the open bucket; its span may still be in use on another thread: never pooled (§15.2)
    Volatile.Write(ref Hdr.WriterState, Closed)
    Interlocked.MemoryBarrier()
    _backend.WakeAllReaders()                                   // 32 SetEvents, once, regardless of masks
    _backend.WakeWriter()                                       // a GetBucket blocked on another thread wakes, sees _disposed and throws (post-review #26)
```
A reader that published its bit after the barrier sees `Closed` on its re-check; one that published before is woken by the broadcast.

### 5.9 Reader disposal (including a pending wait)

```
Dispose():
    if (Interlocked.Exchange(ref _disposed, 1) != 0) return
    GC.SuppressFinalize(this)
    Interlocked.Exchange(ref _disposing, 1); Volatile.Write(ref _cancelRequested, 1)   // full fence: Dekker pair with BeginWait
    while (Volatile.Read(ref _waitOutstanding) != 0)
    {
        _backend.WakeReader(_slot)                              // wakes OUR OWN blocked wait (sync on another thread, or the waiter thread) — that is what it is blocked on, not _arm
        Thread.Sleep(1)                                         // every wait path clears _waitOutstanding in its finally; bounded by one wake
    }
    StopWaiterThread()                                          // under _gate: if a waiter thread exists { _exit = 1; _arm.Set(); Join }
    ReleaseSlot():                                              // shared state is touched only if Slot.Word == _word (we still own the slot)
    Interlocked.And(ref Hdr.WaitersMask, ~_bit); Slot.WaitFor = long.MaxValue
    _backend.OnSlotReleased(_slot, &Slot)
    Slot.ProcessStartTime = 0; Slot.ClaimTick = 0                // identity hygiene (post-review #28)
    if (Interlocked.CompareExchange(ref Slot.Word, Make(Free, Seq(_word), 0), _word) == _word)   // fails harmlessly if we were evicted meanwhile
    {
        Interlocked.And(ref Hdr.ActiveMask, ~_bit); Interlocked.Increment(ref Hdr.ReaderGeneration)
    }
    if (Volatile.Read(ref Hdr.WriterWaiting) != 0 && Interlocked.Exchange(ref Hdr.WriterWaiting, 0) == 1) _backend.WakeWriter()   // unconditional (no crossing filter): the writer must re-scan without us
    release writer process handle (DangerousRelease + Dispose); _ctr.Dispose()
    _status = Disposed
    _owner.ReaderReleased()                                     // Interlocked.Decrement(_localRefs) == 0 => unmap
```
No handle is ever closed while a wait is in progress on it: the wait exits first (observed via `_waitOutstanding`/`Join`), then the slot is released, then handles go.

**Finalizers (post-review #30).** `~RingReader` runs only for an abandoned reader (no wait in progress, waiter thread retired): `ReleaseSlot()`, release the writer process handle, `_owner.ReaderReleased()`. `~RingBuffer` publishes `Closed` (writer) and drops its own local ref. Both follow the local ref-count protocol, so the GC's finalization order does not matter; `Dispose` suppresses them.

### 5.10 Lost-wakeup argument (Dekker, both directions)

Each waiting party performs **store(flag) → full fence → load(condition)**; each signalling party performs **store(condition) → full fence → load(flag)**. With a StoreLoad fence on both sides it is impossible that both loads return stale values: either the waiter sees the new condition and does not block, or the signaller sees the flag and signals.

| Direction | Waiter (store; fence; load) | Signaller (store; fence; load) |
|---|---|---|
| reader ← data | `Or(WaitersMask, bit)` [RMW]; `Volatile.Read(W)` | `Exchange(WriteCursor)` [RMW]; `Volatile.Read(WaitersMask)` |
| writer ← space | `Exchange(WriterWaiting, 1)` [RMW]; `ScanMin()` loads | `Exchange(ReadCursor)` [RMW]; `Volatile.Read(WriterWaiting)` |
| join | `Exchange/CAS(Word → Active)` [RMW]; `Volatile.Read(W)` (c) | `Exchange(WriteCursor)` [RMW]; `ScanMin()` loads |

Residual races are all benign and bounded to one spurious wake: signal after the waiter already returned ⇒ the auto-reset event stays set ⇒ the next wait returns immediately ⇒ loop re-checks; signal before the waiter reaches the kernel ⇒ set is remembered; timeout racing `And` + `SetEvent` ⇒ stale set; two commits in a row ⇒ the first consumes the bit, the second sees mask 0; `WaitFor > newW` ⇒ bit left set and re-evaluated by every later Commit (exactly the "wait for ≥ n" semantics; each Commit is store-fence-load again); writer close ⇒ broadcast.

Safe orders: reader **set bit → re-check → wait**; writer **check → clear bit → SetEvent**. The reverse orders can lose a wake (a bit cleared after `SetEvent` can re-arm against a set that already happened).

**Crossing filter (`WriterWaitFor`).** The writer stores `WriterWaitFor = target` (release) before `Exchange(WriterWaiting, 1)`; its post-fence `ScanMin` saw `min < target`. The condition becomes true only when the last reader X with `R_X < target` advances to `≥ target`. X's `Exchange(R_X)` and the writer's scan form a Dekker pair: the scan did not see the new `R_X`, so X's acquire load of `WriterWaiting` returns 1 from this episode, and the subsequent load of `WriterWaitFor` returns this episode's `target` (it precedes the writer's fence). X's `old < target` (the scan saw a value `≤ old` that was `< target`; an earlier crossing Advance would itself have signalled or been seen by the scan) and `new ≥ target`, so X signals. Readers with `old ≥ target` are not blockers; readers with `new < target` have not freed enough. A stale flag from a previous episode can only cause an extra signal (any reader that clears the flag always signals), never a missing one. Reader `Dispose` and eviction bypass the filter.

### 5.11 Invariants (comments + debug asserts + test oracles)

- I1 `R_i ≤ W ≤ E` for every Active `i`; all monotone; `E` drops only to `W`.
- I2 `E ≤ _min + C` for every reservation and `_min ≤ min_i R_i` over readers visible or becoming visible (§5.6). Debug assert after every `ScanMin`.
- I3 bytes of `[R_i, W)` are never stored by the writer while `R_i` is unchanged.
- I4 data stores of `[W, W')` happen-before `Exchange(W')`; data loads of `[R, R+k)` happen-before `Exchange(R+k)`.
- I5 no lost wakeups (§5.10).
- I6 exactly one holder per `(slot, seq, pid)`; only the holder writes `ReadCursor`/`WaitFor`; only the holder or an evictor with a matching CAS leaves Active.
- I7 exactly one live writer; only it stores `W`, `E`, `WriterWaiting = 1`, `WriterWaitFor`.
- I8 every span ≤ C elements from a masked offset lies inside `[data, data + 2D)`.
- I9 nothing but `CreatorPid`/`CreatorStartTime` is trusted before `InitState == 1` is observed with acquire semantics.

### 5.12 Internal counters (`Internal\Counters.cs`)

`internal struct Counters { public long Commits, Scans, KernelWaits, Signals, SpinSuccesses, SpuriousWakes, Evictions; }` — one per `RingBuffer` (writer side) and one per `RingReader`; plain increments on slow paths only (`Commits` is the one fast-path increment, a writer-local field). Exposed as `internal Counters Counters` for tests and benchmarks ("no `SetEvent` when nobody waits" asserts `Signals == 0`).

---

## 6. The swappable signaling layer

### 6.1 Contract (`Signaling\SignalBackend.cs`)

The protocol (WaitersMask / WriterWaiting / WaitFor / WriterWaitFor Dekker handshakes, spin phases, ScanMin, join, eviction) lives entirely in `RingBuffer`/`RingReader` and never touches a kernel primitive. The backend is called only at the "now block" and "now signal" points.

```csharp
namespace Photone.Ipc.Signaling;

internal enum WaitOutcome { Signaled = 0, Timeout = 1, ProcessExited = 2, Failed = 3 }

internal static class SignalBackendId { public const uint NamedEvent = 1; /* 2 = NtAlertThread, 3 = RawNtEvent, 4 = WaitOnAddress (in-process), 5 = Spin — reserved */ }

internal abstract unsafe class SignalBackend : IDisposable
{
    public abstract uint Id { get; }

    /// Factory: creator passes the configured id; openers pass hdr->SignalBackendId. Unknown id => RingBufferLayoutException.
    public static SignalBackend Create(uint id, ulong instanceId, ControlBlock* hdr, bool isCreator);

    // ---- reader side (called only by the slot owner) ----
    /// At claim, before the slot becomes Active: discard stale state of a previous owner (ResetEvent), publish backend words (e.g. WaiterThreadId).
    public abstract void OnSlotClaimed(int slot, ReaderSlot* s);
    /// At release: clear backend words.
    public abstract void OnSlotReleased(int slot, ReaderSlot* s);
    /// Block until woken, until `writerProcess` (0 = none) is signaled, or until timeoutMs elapses. Called AFTER the Dekker handshake;
    /// spurious returns are allowed (the caller re-checks). A backend that cannot wait on a process handle must return Timeout no later than timeoutMs.
    public abstract WaitOutcome WaitForData(int slot, nint writerProcess, uint timeoutMs);

    // ---- writer side ----
    /// Called immediately before Exchange(WriterWaiting, 1): publish backend words (e.g. WriterWaiterThreadId).
    public abstract void OnWriterBlocking(ControlBlock* hdr);
    /// Block until WakeWriter, until one of `processes` is signaled (exitedIndex), or until timeoutMs. processes.Length <= 32. Spurious returns allowed.
    public abstract WaitOutcome WaitForSpace(ReadOnlySpan<nint> processes, uint timeoutMs, out int exitedIndex);

    // ---- signals (callable from ANY process that has the buffer open, including the waiter's own — Dispose/cancel use them) ----
    public abstract void WakeReader(int slot);       // after the caller consumed the WaitersMask bit
    public abstract void WakeWriter();               // after Exchange(WriterWaiting, 0) == 1
    public abstract void WakeAllReaders();           // writer close; also used by tests

    public abstract void Dispose();
}
```

Rules every backend must satisfy: (1) a wake issued after the waiter's flag store is never lost (it may be delivered early and consumed as a spurious wake); (2) `WakeReader(slot)` from the owner's own process wakes the owner's pending `WaitForData` (needed by cancel/dispose); (3) no state outside the reserved layout words (`SignalBackendId`, per-slot `WaiterThreadId`/`BackendWord`, `WriterWaiterThreadId`/`WriterBackendWord`, area 2560..2687), so switching backends never changes the layout; (4) nothing is done on `Commit`/`Advance` unless a peer is provably blocked (the caller guarantees this by gating on the masks).

### 6.2 `NamedEventBackend` (v1, `Id = 1`)

Objects: 32 auto-reset events `Local\photone.{SectionId:x16}.r{i:D2}` and one auto-reset event `Local\photone.{SectionId:x16}.space`, all created-or-opened with `CreateEventW(null, manualReset: false, initialState: false, name)` in `Create` (creator: 33 creates; opener: 33 opens, `ERROR_ALREADY_EXISTS` is the normal result). Handles are `SafeWaitHandle`s kept for the backend's lifetime; raw `nint`s are cached after one `DangerousAddRef` (released in `Dispose`). ~0.3 ms once per process per buffer. `Global\` sections get `Global\` events (same prefix as the section name). The names follow the section, not the buffer: a pooled section keeps its events for every buffer it serves (§15).

```
OnSlotClaimed(i, s):  ResetEvent(_slot[i])                       // stale set from a previous owner cannot cause a wrong-condition wake
OnSlotReleased(i, s): nothing
WaitForData(i, writerProc, ms):
    nint* h = stackalloc nint[2]; h[0] = _slot[i]; uint n = 1; if (writerProc != 0) { h[1] = writerProc; n = 2; }
    rc = WaitForMultipleObjects(n, h, false, ms)
    WAIT_OBJECT_0 -> Signaled; WAIT_OBJECT_0 + 1 -> ProcessExited; WAIT_TIMEOUT -> Timeout; else Failed
OnWriterBlocking(hdr): nothing
WaitForSpace(procs, ms, out idx):
    nint* h = stackalloc nint[33]; h[0] = _space; copy procs to h[1..]; rc = WaitForMultipleObjects(1 + procs.Length, h, false, ms)
    WAIT_OBJECT_0 -> Signaled; WAIT_OBJECT_0 + k (k >= 1) -> ProcessExited, idx = k - 1; WAIT_TIMEOUT -> Timeout; else Failed
WakeReader(i):  SetEvent(_slot[i])
WakeWriter():   SetEvent(_space)
WakeAllReaders(): for i in 0..31 SetEvent(_slot[i])
```
Auto-reset + at most one waiter per event (enforced by the single-outstanding-wait rule; `WaitSync` and async `Wait` share `_waitOutstanding`) means a stale set is always consumed by the owner's next wait as a harmless spurious wake.

### 6.3 Adding an alternative without a layout change

1. Add an `Id` constant; implement `SignalBackend`; register it in `SignalBackend.Create`.
2. The creator writes the chosen id into `hdr->SignalBackendId`; openers instantiate the backend the header names (an unknown id fails `Open` with `RingBufferLayoutException` — a peer never silently mismatches).
3. Use only the reserved words. How the candidates map:
   - **NtAlertThreadByThreadId / NtWaitForAlertByThreadId**: `OnSlotClaimed` stores the waiter's TID in `s->WaiterThreadId` (the per-reader waiter thread and the `WaitSync` caller both write it before blocking); `WakeReader(i)` = `NtAlertThreadByThreadId(s->WaiterThreadId)`; `WaitForData` = `NtWaitForAlertByThreadId(timeout)` (cannot wait on the process handle ⇒ returns `Timeout` every `LivenessCheckInterval`; the caller's loop polls). Writer side symmetric with `WriterWaiterThreadId`.
   - **Raw `NtSetEvent` / `NtWaitForSingleObject`**: identical objects to `NamedEventBackend`, direct syscalls; no new words.
   - **`WaitOnAddress` (in-process only)**: `WaitForData` = `WaitOnAddress(&s->BackendWord, &expected, 8, ms)`; `WakeReader` = `Interlocked.Increment(s->BackendWord)` + `WakeByAddressSingle`; refuse `Open` from another process (documented; process-local).
   - **Pure spin / core pinning**: `WaitForData` spins until `Volatile.Read(hdr->WriteCursor) >= s->WaitFor` or `s->BackendWord` changes (wake token incremented by `WakeReader`) or the slice expires; `WaitForSpace` spins until `hdr->WriterWaiting == 0` (a reader clears it before `WakeWriter`) or a process handle signals (`WaitForSingleObject(h, 0)` every ~100 µs). No kernel object at all.
4. The benchmark harness selects the backend with `--backend <id>` (creator option added to `RingBufferOptions` when a second backend exists; v1 has no option).

---

## 7. Async `Wait` with zero steady-state allocation (`RingReader.Async.cs`)

Mechanism: one lazily created waiter thread per `RingReader<T>` (`IsBackground = true`, 256 KiB stack, name `photone-wait-r{i}`) plus an embedded `ManualResetValueTaskSourceCore<bool>` (`RunContinuationsAsynchronously = true`, set once). The reader *is* the `IValueTaskSource<bool>`.

```csharp
private ManualResetValueTaskSourceCore<bool> _vts;
private Thread? _waiter; private readonly AutoResetEvent _arm = new(false);   // process-local "a request is pending"
private readonly object _gate = new(); private bool _armPending;          // guards _waiter / _armPending against the idle-retire race
private long _asyncTarget, _asyncDeadline; private int _cancelRequested, _exit, _waitOutstanding, _disposing;
private CancellationTokenRegistration _ctr; private CancellationToken _ct;

public ValueTask<bool> Wait(int count, TimeSpan timeout, CancellationToken ct)
{
    ThrowIfDisposed(); if ((uint)count > (uint)C) throw new ArgumentOutOfRangeException(nameof(count)); if (count == 0) return new(true);
    long target = _r + count;
    if (_wc >= target || (_wc = Volatile.Read(ref Hdr.WriteCursor)) >= target) return new ValueTask<bool>(true);   // hot path: no state, no thread
    if (timeout == TimeSpan.Zero) return new(false);                                                        // poll: no spin (same as WaitSync)
    if (SpinUntil(target, _options.AsyncSpinTime)) return new(true);                                        // 5 µs on the caller's thread
    if (ct.IsCancellationRequested) return ValueTask.FromCanceled<bool>(ct);
    BeginWait();                                                                                            // Exchange(_waitOutstanding, 1) + _disposing re-check (Dekker with Dispose)
    _vts.Reset(); _asyncTarget = target; _asyncDeadline = ToDeadline(timeout); _cancelRequested = 0; _ct = ct;
    _ctr = ct.CanBeCanceled ? ct.UnsafeRegister(static (s, _) => ((RingReader<T>)s!).OnCancel(), this) : default;
    Arm();                    // lock (_gate) { _armPending = true; start the waiter thread if none } ; _arm.Set()  -- the one syscall on the arm path
    return new ValueTask<bool>(this, _vts.Version);
}
private void OnCancel() { Volatile.Write(ref _cancelRequested, 1); _backend.WakeReader(_slot); }          // wake our own thread; a stale set is harmless

private void WaiterLoop()
{
    while (true)
    {
        if (!_arm.WaitOne(WaiterIdleExitMs /* 5 s */)) { lock (_gate) { if (!_armPending) { _waiter = null; return; } } continue; }   // idle: retire (an abandoned reader becomes collectable)
        if (Volatile.Read(ref _exit) != 0) return;
        lock (_gate) { _armPending = false; }
        bool result = false; Exception? ex = null;
        try { result = BlockUntil(_asyncTarget, _asyncDeadline, honorCancel: true); }
        catch (Exception e) { ex = e; }                       // OperationCanceledException / ReaderEvictedException / ObjectDisposedException / PhotoneIpcException
        _ctr.Dispose(); _ctr = default;
        Volatile.Write(ref _waitOutstanding, 0);                      // before completion: the continuation may call Wait again
        if (ex != null) _vts.SetException(ex); else _vts.SetResult(result);   // continuation queued to the thread pool
    }
}
bool IValueTaskSource<bool>.GetResult(short t) => _vts.GetResult(t);
ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short t) => _vts.GetStatus(t);
void IValueTaskSource<bool>.OnCompleted(Action<object?> c, object? s, short t, ValueTaskSourceOnCompletedFlags f) => _vts.OnCompleted(c, s, t, f);
```

Allocation profile: fast and spin paths allocate nothing; the suspending path allocates nothing after the thread exists (`UnsafeRegister` with a static lambda uses pooled nodes; the VTS core is a struct field; exceptions allocate only when thrown). Completion happens exactly once, on the waiter thread, for every outcome (data, EOF, timeout, cancel, eviction, dispose). The `ValueTask` must be awaited exactly once (standard contract). Trade-off: one parked OS thread per reader that has ever suspended an async `Wait`; a multiplexing implementation can replace `WaiterLoop` behind the same `IValueTaskSource` later without API change.

---

## 8. Capacity and index arithmetic (`Internal\Capacity.cs`)

```csharp
internal static (long capacity, long dataBytes) Choose(long minCapacity, int s)
{
    if (s <= 0) throw new ArgumentOutOfRangeException(nameof(s));
    if (minCapacity < 1) minCapacity = 1;
    int kMin = Math.Max(0, 16 - BitOperations.TrailingZeroCount((uint)s));            // C*s ≡ 0 (mod 2^16)  <=>  k + tz(s) >= 16
    int kReq = minCapacity == 1 ? 0 : 64 - BitOperations.LeadingZeroCount((ulong)(minCapacity - 1));   // ceil(log2)
    int k = Math.Max(kMin, kReq);
    if (k > 30) throw new ArgumentOutOfRangeException(nameof(minCapacity), "capacity would exceed 2^30 elements");
    long capacity = 1L << k;
    long dataBytes = checked(capacity * s);
    if (dataBytes > (1L << 40)) throw new ArgumentOutOfRangeException(nameof(minCapacity), "data region would exceed 1 TiB");
    Debug.Assert(dataBytes % 65536 == 0);
    return (capacity, dataBytes);
}
internal static uint TypeHash(Type t) => Fnv1a32(t.FullName!.AsSpan());   // over UTF-16 code units; deterministic across processes (string.GetHashCode is randomized)
```
Examples: `float`, 1 000 000 → C = 2^20, D = 4 MiB; `byte`, 1 → 65536; 12-byte struct, 1 → 16384 (192 KiB = 3 × 64 KiB); 24-byte struct, 100 000 → 131072 (3 MiB). Offsets: `(cursor & (C-1)) * s` — mask the element index, then scale. Every span is ≤ C elements so it ends before `data + 2D`. `C ≤ 2^30` keeps every `Span<T>` length an `int`. Cursors are `long` and never wrap in practice (debug assert `≥ 0`). vmcircbuffer's lcm rounding is deliberately not ported (mask beats modulo).

---

## 9. Test plan (`tests\Photone.Ipc.Tests`, xunit.v3 4.0.1 under MTP)

Conventions: unique names `$"photone-test-{Environment.ProcessId}-{Guid.NewGuid():N}"`; cross-process tests in `[Collection("ipc")]` with `DisableParallelization = true`; child = `Path.Combine(AppContext.BaseDirectory, "Photone.Ipc.TestChild.exe")` started with `ProcessStartInfo.ArgumentList`; readiness via a line on stdout, never `Thread.Sleep`. TestChild verbs: `reader <name> <count> <checksum>`, `echo <nameIn> <nameOut> <n>`, `writer <name> <count> [--crash-after k]`, `crash-reader <name>` (claims, reads nothing, waits on stdin, then `Environment.FailFast`), `claim-and-die <name>` (`TestHooks.AfterClaim = () => Environment.FailFast(...)`), `slow-init <name> <ms> [--die]` (`TestHooks.BeforeInitState`), `hold-name <name>` (opens and keeps the handle), `spin-reader <name> <count>` (spin-only reader, `SpinTime = ∞`, reports `Counters`).

**Layout / native**
1. `Layout_SizesAndOffsets` — 4096/64; every listed offset via `Marshal.OffsetOf`; sync fields `% 8 == 0`; `WriteCursor` at 128; lines 3 and 5 contain no fields.
2. `Capacity_Rounding` — theory over `s ∈ {1,2,3,4,8,12,16,24,64,100}` × requests: pow2, `≥ request`, `D % 65536 == 0`, `k > 30` throws.
3. `Create_MirrorAliases` — write via `Data`, read via `Mirror`; wrap-around span fill at `D-8..D+8`.
4. `Create_ThenCreateSameName_ThrowsAlreadyExists`; `Open_MissingName_ThrowsNotFound`; `Open_WrongElementType_ThrowsLayout`; `Open_ForeignSection_TimesOut` (raw 64 KiB section, 200 ms timeout → `RingBufferInitializationException`); `Open_SectionTooSmall_ThrowsLayout` (raw 4 KiB section); `Open_UnknownBackendId_ThrowsLayout` (patch header via test hook).
5. `Create_Anonymous_HasGuidName`; `Name_Normalization` (`"demo"` → `Local\photone.demo`; explicit prefixes verbatim).
6. `AddressHint_DeterministicAndAligned`; `Open_SameProcess_FallsBackToAnotherAddress` (creator's range is occupied in the same process; data still shared).
7. `Dispose_ReleasesVA` (`VirtualQuery` reports `MEM_FREE`; `OpenFileMappingW` fails with 2); `Dispose_WithLiveLocalReader_DefersUnmap`; `Finalizer_Unmaps`.

**Writer / bucket**
8. `GetBucket_ExactCount_Contiguous_AcrossWrap`; `GetBucket_TooLarge_Throws`; `GetBucket_Zero_Throws`; `GetBucket_WhileOutstanding_Throws`; `GetBucket_NotWriter_Throws`; `GetBucket_AfterClose_Throws`.
9. `Commit_Less_ShrinksReservation` (next bucket cursor == W + k); `Commit_Twice_Throws`; `Dispose_WithoutCommit_DropsReservation`; `Commit_Zero`.
10. `NoReaders_WriterNeverBlocks` (10 × C); `FullCapacity_Usable` (bucket of exactly C when empty, then `TryGetBucket(1)` false).

**Reader**
11. `LateReader_StartsAtHead`; `SeveralReaders_IndependentCursors` (vmcircbuffer `several_readers`); `TryRead_ExactOrFalse`; `TryRead_Zero_EmptyChunk`; `Advance_Partial`; `Advance_TooMuch_Throws`; `Advance_Zero_NoOp`.
12. `Writer_BlocksOnSlowReader_ThenUnblocks` (reader advances after 200 ms; elapsed ≥ 150 ms; `Counters.KernelWaits == 1`).
13. `Reader_WaitSync_UnblocksOnCommit`; `Reader_WaitSync_Timeout_False`; `Reader_WaitSync_ZeroTimeout_Polls`; `Reader_Wait_Async_CompletesOnCommit`; `Reader_Wait_Async_Timeout_False`; `Reader_Wait_Async_Cancel_ThrowsOCE`; `Reader_Wait_Concurrent_Throws`; `Reader_Wait_NotWokenBelowThreshold` (wait 100, commit 50 → `WaitersMask` bit still set and no signal; commit 50 more → wakes).
14. `WriterClose_ReadersDrain_ThenFalse_WithStatusClosed`; `Reader_IsCompleted`.
15. `ReaderDispose_WakesBlockedWriter`; `ReaderDispose_WithPendingAsyncWait_CompletesWithObjectDisposed` (and the waiter thread exits); `ReaderDispose_WhileWaitSyncOnOtherThread_Unblocks`.
16. `TooManyReaders_33rd_Throws`; `SlotReuse_AfterDispose_SeqIncrements`; `SweepDeadSlots_ReclaimsFakeDeadSlot` (test hook writes a dead PID + wrong start time into a slot).
17. `WriterWaitFor_CrossingFilter_OnlyLaggardSignals` (2 readers, one laggard; `Counters.Signals` of the fast reader == 0 while the writer is blocked).
18. `Fuzz_InProcess` — random bucket sizes ≤ C/2, random advances, 4 readers (sync/async/spin mix), 10 s, sequence-numbered elements + checksums; invariant `Available == W − R`.
19. `JoinStorm` — readers join/leave at high rate on 4 threads while the writer streams; checksums never fail (gate for §5.6).
20. `ZeroAlloc_HotPath` — `GC.GetAllocatedBytesForCurrentThread()` delta == 0 across 1 M `GetBucket/Commit/TryRead/Advance` and across satisfied `Wait`s and across suspended-then-completed async `Wait`s after warm-up.

**Cross-process**
21. `CrossProcess_ReaderSeesData` (1 M elements, checksum).
22. `CrossProcess_SameBaseAddress_BestEffort` (child prints `IsMappedAtCreatorAddress`; hard-assert correctness, soft-assert equality in ≥ 1 of 5 spawns).
23. `CrossProcess_FallbackAddress` (child pre-reserves the creator's range with `VirtualAlloc` before `Open`; `IsMappedAtCreatorAddress == false`; data correct).
24. `CrossProcess_OpenByDuplicatedHandle` (`DuplicateSectionHandleTo(child.Id)`, value passed via stdin; `Name == null`; events still work).
25. `CrossProcess_Echo_RoundTrip` (A→B, B→A; 100 k round trips; reports RTT p50/p99; asserts payload).
26. `CrossProcess_ReaderKilled_WriterResumes` — `crash-reader` child; parent fills the ring, blocks, kills the child; `GetBucket` returns within 200 ms (process handle in the wait set) and `EvictedReaders == 1`; slot reusable. **Acceptance gate.**
27. `CrossProcess_ReaderBecomesLaggardAfterEpisodeStart` — two children: A (live laggard) and B (dead, ahead of A); A advances past B; writer must resume without waiting forever (judge 2 deadlock scenario).
28. `CrossProcess_ClaimAndDie_Reclaimed` — `claim-and-die` child; `CreateReader` × 32 in the parent succeeds after the sweep.
29. `CrossProcess_WriterKilled_ReaderDrainsThenTerminated` — child writer commits 1000 then `FailFast`; parent: `Wait(1000)` true, then `Wait(1)` false within 100 ms with `Status == WriterTerminated`; async variant.
30. `CrossProcess_WriterCloses_ReaderCompletes` (clean Dispose → `false`, `Status == WriterClosed`).
31. `CrossProcess_TornInit_CreatorDies` — `slow-init --die` child; parent `Open` throws `RingBufferInitializationException("creator died")` within 300 ms, not after 5 s; `CrossProcess_SlowInit_OpenerWaits` (300 ms delay, open succeeds).
32. `CrossProcess_NameLifetime` — `hold-name` child keeps a handle; creator disposes; a third process can still open; after the child exits, `Open` fails with not-found.
33. `CrossProcess_MultipleReaderProcesses_Broadcast` (3 children verify the full stream).
34. `CrossProcess_NoSyscallWhenNobodyWaits` — `spin-reader` child; parent writer's `Counters.Signals == 0` and `KernelWaits == 0` over 1 M commits. **Acceptance test for the signaling layer.**
35. `CrossProcess_PidReuse_Guard` — test hook writes a stale PID with a wrong start time; writer evicts.
36. `CrossProcess_Fuzz_4Readers_30s` — random sizes, checksums, one reader killed and re-spawned every 5 s.
37. `CrossProcess_JoinStorm` — 8 children attach/detach 1000× each while streaming.
38. `CrossProcess_Latency_Smoke` — 10 000 ping-pongs; p50 < 20 µs blocked / < 2 µs spinning (loose regression bound).

---

## 10. Benchmark plan (`bench\Photone.Ipc.Benchmarks`)

BenchmarkDotNet (`[MemoryDiagnoser]`, `[DisassemblyDiagnoser(maxDepth: 1)]`, `Job.Default.WithGcConcurrent(false).WithAffinity(...)`):
- `Writer_GetBucket_Commit` (16 / 1024 elements; 0 readers, 1 spinning in-process reader). Target ≤ 40 ns, 0 B.
- `Reader_TryRead_Advance` (16 / 1024). Target ≤ 30 ns, 0 B.
- `Wait_Sync_AlreadyAvailable`, `Wait_Async_AlreadyAvailable`. Target ≤ 5 ns, 0 B (no `_waitOutstanding` exchange on the fast path).
- `Wait_Async_SuspendResume` (in-process writer thread commits after the reader parks). Target 0 B/op after warm-up.
- `Commit_WithBlockedReader` vs `Commit_WithSpinningReader` (isolates `SetEvent` cost and the `WaitersMask` gate).
- `ScanMin_32Slots_Hot` / `_RemoteDirty`.
- Disassembly check: hot methods contain no `call` except the `[NoInlining]` slow paths and no allocation helpers.

Custom harness (`Photone.Ipc.Benchmarks.exe --cross ...`, peer = TestChild `echo`; both processes pinned to distinct physical cores via `Process.ProcessorAffinity`, High-performance power plan, ring pre-faulted, `ThreadPriority.Highest` for ping-pong only):
- Ping-pong RTT/2 with 8-byte elements, 1 M round trips after 100 k warm-up; HdrHistogram-style p50/p90/p99/p99.9/max; variants `SpinTime ∈ {∞, 20 µs, 0}` × {sync, async}. Targets: spinning p50 ≤ 300 ns; blocked p50 5–20 µs.
- Throughput: 64 MiB ring, buckets 4 KiB / 64 KiB / 1 MiB, 1/2/4 reader processes with checksum; GB/s per reader and commits/s (target > 10 GB/s single reader).
- Wake cost: commit rate with 0 / 1 blocked / 1 spinning reader.
- Fault-injection timing: reader kill → writer resume; writer kill → readers' `Wait` returns `false`.
- Join storm under load (10 kHz attach/detach): checksums + throughput degradation.
- `--backend <id>` switch reserved for the next phase (named events only in v1); every run prints `Counters` so backends can be compared on `Signals`/`KernelWaits`/`SpuriousWakes`.

---

## 11. Risks and open questions

1. **Same VA is best effort** (ASLR, DLLs, GC segments). Nothing depends on it; `IsMappedAtCreatorAddress` is informational. Remote mapping into a spawned child (`PROCESS_VM_OPERATION`) could give a guarantee later.
2. **One waiter thread per async-suspending reader.** Fine for a handful; hundreds would need a multiplexing loop behind the same `IValueTaskSource` (no API change).
3. **`OpenProcess` ACCESS_DENIED across users/integrity levels** ⇒ poll mode (`LivenessCheckInterval` granularity); never evict on it. Cross-user sharing is "same user recommended" in v1; an SDDL option is a v1.1 candidate.
4. **Live-but-stuck reader stalls the writer forever** — the requested backpressure contract. `MaxReaderLag` (opt-in, Dead tombstone protocol per §5.7) is the escape hatch for later.
5. **`SEC_COMMIT` charges G + D at creation**; multi-GB rings may hit 1455. `SEC_RESERVE` + on-demand commit is a `LayoutFlags` bit for later.
6. **Spinning burns a core** per waiting party; every budget is an option; `SpinTime = 0` for oversubscribed hosts.
7. **`TypeHash` over `typeof(T).FullName`** may differ for the same layout across assemblies; `ElementSize` is the hard check.
8. **`Wait` returns `false` on writer termination instead of throwing** (deviation from D2). If exception semantics are preferred, add `RingReader.ThrowIfWriterTerminated()`; the decision is isolated to `CheckWriterGone` callers.
9. **Claims are never reclaimed from live processes** (post-review #28): a process paused forever between its two claim CASes holds a slot until it dies, exactly like a paused Active reader. Only dead-process claims are swept.
10. **Writer takeover** is out of scope; `WriterEpoch`/`WriterPid` CAS are reserved. A pagefile section dies with its last handle, so a restarted writer needs a keeper handle anyway.
11. **ARM64** is untested; the protocol is written to acquire/release + full-fence rules and should be correct.
12. **Cross-session** (`Global\`) needs `SeCreateGlobalPrivilege` to create; default `Local\`.

---

## 12. Implementation checklist (create in this order; each step builds)

1. `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `Photone.Ipc.slnx` (§1.1).
2. `src\Photone.Ipc\Photone.Ipc.csproj`, `AssemblyInfo.cs`.
3. `src\Photone.Ipc\SafeSectionHandle.cs`.
4. `src\Photone.Ipc\Internal\Kernel.cs` — all 18 imports, constants, `SYSTEM_INFO` (§3.1).
5. `src\Photone.Ipc\Internal\Layout.cs` — constants, `ControlBlock`, `ReaderSlot`, `SlotState`, `SlotWord`, static asserts (§4).
6. `src\Photone.Ipc\Internal\Capacity.cs` — `Choose`, `TypeHash`/`Fnv1a32` (§8).
7. `src\Photone.Ipc\Internal\AddressHint.cs` (§3.6).
8. `src\Photone.Ipc\Internal\MirroredSection.cs` — `Create`/`Open`/`ReleaseHandle`, unwind tracking (§3.3–3.5).
9. `src\Photone.Ipc\Internal\ProcessLiveness.cs` — `OwnStartTime`, `IsAlive`, `ProcessExists`, `OpenLaggard` (§5.7).
10. `src\Photone.Ipc\Internal\SpinClock.cs`, `Internal\Counters.cs`, `Internal\TestHooks.cs`.
11. `src\Photone.Ipc\Signaling\WaitOutcome.cs`, `Signaling\SignalBackend.cs`, `Signaling\NamedEventBackend.cs` (§6).
12. `src\Photone.Ipc\Exceptions.cs`, `Options.cs`, `ReaderStatus.cs` (§2).
13. `src\Photone.Ipc\Bucket.cs`, `Chunk.cs`.
14. `src\Photone.Ipc\RingBuffer.cs` — `Create`, `Open` ×2, header init/validation, properties, `DuplicateSectionHandleTo`, `Dispose`/ref-count (§3.3, §3.4, §3.5).
15. `src\Photone.Ipc\RingBuffer.Writer.cs` — `GetBucket`, `TryGetBucket`, `EndWrite`, `SignalReaders`, `ScanMin`, `WaitForSpace`, `RefreshLaggards`, `SweepLaggards`, `CloseWriter` (§5.1–5.3, §5.8).
16. `src\Photone.Ipc\RingBuffer.Readers.cs` — `CreateReader`, `SweepDeadSlots`, `Evict`, `ReaderReleased` (§5.6, §5.7).
17. `src\Photone.Ipc\RingReader.cs` — `TryRead`, `Available`, `Advance`, `SignalWriterIfCrossing`, `WaitSync`, `SpinUntil`, `BlockUntil`, `CheckWriterGone`, `ResolveWriterProcess`, `Status`, `Dispose` (§5.4, §5.5, §5.7, §5.9).
18. `src\Photone.Ipc\RingReader.Async.cs` — `Wait` ×2, `WaiterLoop`, `IValueTaskSource<bool>` (§7).
19. `tests\Photone.Ipc.TestChild\Photone.Ipc.TestChild.csproj`, `Program.cs` (verbs of §9).
20. `tests\Photone.Ipc.Tests\Photone.Ipc.Tests.csproj`, `LayoutTests.cs`, `CapacityTests.cs`, `MappingTests.cs`, `WriterTests.cs`, `ReaderTests.cs`, `WaitTests.cs`, `LifetimeTests.cs`, `FuzzTests.cs`, `CrossProcessTests.cs`, `ChildProcess.cs` (helper) (§9).
21. `bench\Photone.Ipc.Benchmarks\Photone.Ipc.Benchmarks.csproj`, `Program.cs` (switcher + `--cross`), `HotPathBenchmarks.cs`, `WaitBenchmarks.cs`, `CrossProcessHarness.cs`, `Histogram.cs` (§10).
22. Run: `dotnet build E:\GitHub\photone-ipc\Photone.Ipc.slnx -c Release`, `dotnet test E:\GitHub\photone-ipc\tests\Photone.Ipc.Tests\Photone.Ipc.Tests.csproj -c Release`; gate = tests 19, 26, 27, 34 green.


## 13. Post-review changes

The implementation was reviewed after stage C; 22 confirmed findings were fixed and are listed in `docs/REVIEW-NOTES.md`; the resulting deviations from this document are items 26-35 of `docs/DEVIATIONS.md`. The pseudo-code above has been updated in place where the protocol changed (blocked-writer local ref, no hot-path `ReserveEnd` store, dead-only claim sweep with identity zeroing, `Status` liveness probe, Dekker between `BeginWait` and `Dispose`, finalizers, waiter-thread idle retirement, pre-faulting of both views, access-denied liveness through creation times, session-split detection in the named-event backend).

## 14. Adaptive spinning, the async waiter and synchronous continuations (phase 2, step 1)

Measured motivation (`bench --latency`, `docs/SIGNALING-PROBE.md`): a kernel wake costs ~10-20 µs after a short idle and ~30-60 µs once the core
has idled for a millisecond (deep C-state); spinning costs 0.1-0.3 µs of latency and a core while it lasts. v1 spun a fixed 20 µs before every
kernel wait (55% of a core at 50 µs gaps for no gain) and completed every suspended `await` through an event, the waiter thread and a thread-pool
continuation (12-100 µs, up to two cores). Both are replaced; the shared-memory layout and the wake protocol of §5 are unchanged.

### 14.1 `SpinPolicy` (`Internal\SpinPolicy.cs`)

One instance per waiting party: `RingReader._spin` (data waits, both `WaitSync` and the waiter thread; one wait at a time, so no sharing),
`RingReader._idleSpin` (the waiter thread waiting for its next request), `RingBuffer._spaceSpin` (writer waiting for space).

```
state: initial = ToTicks(SpinTime); max = max(initial, ToTicks(MaxSpinTime ?? SpinTime)); window = initial;
       envelope = initial / 2; share = 256 (fixed-point 1.0); fixed = SpinTime < 0 (window = +inf) || max == 0 (window = 0)
OnSatisfied(waited):                                  // every wait that got its condition, spun or blocked (not timeouts/closure/cancel)
    if fixed: return
    short = waited <= max
    share += ((short ? 256 : 0) - share) >> 3         // EWMA, weight 1/8
    if short: envelope = max(waited, envelope - envelope >> 3)
    window = share < 128 ? 0 : clamp(2 * envelope, initial, max)
WaitCore(target, deadline, honorCancel):              // RingReader; WaitForSpace is the same shape on the writer
    start = now; budget = min(window, deadline - start)
    if budget > 0 && SpinUntil(target, budget, honorCancel): if adaptive: OnSatisfied(now - start); return true
    ok = BlockUntil(target, deadline, honorCancel)     // §5.5 unchanged
    if ok: OnSatisfied(now - start)
    return ok
```

Properties: the decision to spin needs a majority of recent waits within `max`, so a single early wake-up in a stream of long gaps (jitter)
cannot switch spinning on, and a single long gap in dense traffic cannot switch it off. The window covers twice the recent short gaps and is never
below `SpinTime` while spinning, so with the default (`MaxSpinTime = null` = `SpinTime`) the window is exactly `SpinTime` or zero.
`SpinUntil` gives up early on dispose, cancellation (when honoured) and the cold checks of §5.5; those waits then block or throw and are not
reported. An earlier haltpoll-style variant (grow on one short blocked wait, halve on a long one) oscillated under wake-up jitter (34% of a core
at 50 µs gaps) and was replaced.

### 14.2 Arm / park handshake of the waiter thread (`RingReader.Async.cs`)

Process-local fields: `_request` (a `Wait` is armed and not yet taken), `_waiterParked` (the waiter is blocked, or about to block, on the
process-local `_arm` auto-reset event), `_waiter` (the thread, or null), `_gate` (lock; thread creation and retirement only).

```
Wait (caller, not on the waiter thread):
    fast path; zero timeout; caller spin min(AsyncSpinTime, _spin.window); cancelled?
    BeginWait()                                          // §5.9 Dekker with Dispose
    _vts.Reset(); token = _vts.Version; _asyncTarget/_asyncDeadline/_ct = ...; _cancelRequested = 0; register ct
    Arm(); return ValueTask(this, token)
Arm:
    Exchange(_request, 1)                               // store [full fence]
    if Volatile.Read(_waiterParked) != 0 || _waiter == null: ArmSlow()
ArmSlow:
    lock _gate: if _waiter == null { _waiterParked = 0; start thread; return }
    _arm.Set()                                          // counted as ArmSignals
WaiterLoop:
    loop:
        if _exit: return
        if Exchange(_request, 0) != 0: Serve(); continue
        idleStart = now
        if SpinForRequest(_idleSpin.window): _idleSpin.OnSatisfied(now - idleStart); continue
        Exchange(_waiterParked, 1)                      // store [full fence]
        if Volatile.Read(_request) == 0 && !_exit:      // load
            if !_arm.WaitOne(5 s):
                lock _gate: if _request == 0 { _waiter = null; return }   // retire; _waiterParked stays 1 so the next Arm starts a thread
        _waiterParked = 0
        if _request != 0: _idleSpin.OnSatisfied(now - idleStart)
Serve:
    result/ex = WaitCore(_asyncTarget, _asyncDeadline, honorCancel: true)
    _ctr.Dispose(); _waitOutstanding = 0                // before completion: the continuation may call Wait again
    _vts.SetResult/SetException                         // inline when AllowSynchronousContinuations (the default), else thread pool
```

No lost request: `Arm` (store `_request`, fence, load `_waiterParked`) and the park (store `_waiterParked`, fence, load `_request`) form a Dekker
pair, so either the waiter sees the request and does not block, or `Arm` sees the waiter parked and signals `_arm` (a stale set is consumed as
a spurious wake: the loop re-reads `_request`). Retirement re-reads `_request` under `_gate`; `ArmSlow` publishes `_request` before taking
`_gate`, so a request is either seen by the retiring waiter or finds `_waiter == null` and starts a new thread. `_asyncTarget`/`_asyncDeadline`/
`_ct` are written before the fenced `_request` store and read after the waiter's `Exchange(_request, 0)`: happens-before holds.

### 14.3 Synchronous continuations and waits on the waiter thread

`ReaderOptions.AllowSynchronousContinuations` (default true) sets `_vts.RunContinuationsAsynchronously = false`: the continuation of a
suspended `Wait` runs inside `SetResult`, on the waiter thread (unless the awaiter captured a synchronization context). A `Wait` called on
that thread cannot be served by the loop (the loop is below it on the stack), so it runs synchronously instead:

```
Wait on the waiter thread: fast path; zero timeout; cancelled?
    BeginWait(); register ct (OnCancel sets _cancelRequested and wakes our slot event)
    return completed ValueTask(WaitCore(target, deadline, honorCancel: ct.CanBeCanceled))   // exceptions -> faulted / cancelled ValueTask
    finally: unregister; _waitOutstanding = 0
```

An `await` loop over the reader therefore migrates onto the waiter thread at its first suspension and then never suspends while it keeps up:
no arm, no thread hop, no system call beyond the kernel wait itself. Sync-over-async on this reader inside the loop completes synchronously
instead of deadlocking. `Dispose` on the waiter thread: `CompletePendingRequestOnDispose` completes an armed request that the loop has not
taken with `ObjectDisposedException` (otherwise `Dispose` would wait for itself), and `StopWaiterThread` sets `_exit` without joining itself;
the loop exits when control returns to it, before touching `_arm` or the mapping.

### 14.4 Results (same laptop, same session, `bench --latency`; p50 latency, reader process CPU)

| reader | 10 µs | 50 µs | 200 µs | 1 ms | 5 ms |
|---|---:|---:|---:|---:|---:|
| `WaitSync` v1 (fixed 20 µs) | 0.2 µs, 96% | 17 µs, 55% | 21 µs, 16% | 28 µs, 4% | 37 µs, 1% |
| `WaitSync` default | 0.2 µs, 96% | 17 µs, 17% | 20 µs, 5% | 44 µs, 1% | 51 µs, 1% |
| `WaitSync`, `MaxSpinTime = 1 ms` | 0.2 µs, 100% | 0.3 µs, 95% | 0.3 µs, 97% | 0.6 µs, 98% | 35 µs, 1% |
| `await` v1 (pool continuation) | 12 µs, 219% | 20 µs, 131% | 26 µs, 86% | 62 µs, 31% | 102 µs, 15% |
| `await` default (inline) | 0.2 µs, 100% | 18 µs, 18% | 19 µs, 5% | 27 µs, 2% | 35 µs, 2% |
| `await` default, `MaxSpinTime = 1 ms` | 0.3 µs, 95% | 0.3 µs, 98% | 0.4 µs, 97% | 0.9 µs, 96% | 35 µs, 2% |

Tests: `AdaptiveWaitTests` (policy arithmetic, learned spinning in real waits for readers and the writer, inline continuations on the waiter
thread, zero allocation on the inline path, `Dispose` and cancellation on the waiter thread, arm handshake under random gaps).

## 15. Buffer pool (`RingBufferPool.cs`, `Internal\PooledMapping.cs`)

A process that creates or opens many buffers one after another pays, per buffer, for a new section (commit charge, prototype PTEs), a placeholder and three views, a page fault per page per view when pre-faulting, 33 named events, and on release for the unmaps and for freeing the pages. `RingBufferPool` keeps the mapping of a released buffer and reuses it for the next buffer of the same size; a mapping left unused for `IdleTimeout` is released. Pooling is opt-in per buffer: `RingBufferOptions.Pool`, on `Create` and on `Open`.

### 15.1 What it saves

`Photone.Ipc.Benchmarks.exe --lifecycle` on the dev box (p50; pre-fault on; the creator creates a buffer, a peer process opens it by name, joins as a reader, reads one element and releases it, then the creator disposes; both sides time their own calls):

| data size | creator `Create` | creator `Dispose` | peer `Open` + `CreateReader` | peer release |
|---|---|---|---|---|
| 64 KiB | 308 µs → **35 µs** | 113 µs → **31 µs** | 241 µs → **44 µs** | 68 µs → **8 µs** |
| 1 MiB | 1.0 ms → **41 µs** | 293 µs → **31 µs** | 864 µs → **54 µs** | 143 µs → **10 µs** |
| 16 MiB | 12.1 ms → **96 µs** | 2.9 ms → **32 µs** | 11.0 ms → **137 µs** | 1.1 ms → **10 µs** |
| 64 MiB | 49.3 ms → **265 µs** | 12.0 ms → **33 µs** | 43.8 ms → **321 µs** | 5.6 ms → **11 µs** |

What remains with a pool: the alias section (about 19 µs to create, 14 µs to resolve), one `NtQueryObject` (about 1 µs), the header re-initialisation, and the pre-fault pass over pages that are already resident (about 4 µs per MiB per side; it keeps page faults off the hot path when the working set was trimmed while the mapping was idle, and is skipped with `PreFault = false`).

### 15.2 Creator

```
Create(minCapacity, name, options { Pool = pool }):
  (C, D) = Capacity.Choose(...); name = Normalize(name); global = name starts with Global\
  alias = CreateFileMappingW(64 KiB, name)                  // the buffer's name: RingBufferAlreadyExistsException before anything else is acquired
  a = MapHeaderPeek(alias); a.CreatorStartTime, then a.CreatorPid   // same offsets and order as in the control block
  instanceId = random
  entry = pool.RentForCreate(D, global, options.PreferredBaseAddress)   // §15.3; InitState is 0 on return
  if entry is null:                                         // a new section under a pool-owned name
      sectionId = random; entry = map Local|Global\photone.pool.{Guid:N} (§3.3 steps 1-4 and 6)
  else:                                                     // reuse: same placeholder, views, resident pages and events
      clear the control block except BackendArea[0]; the new InstanceId FIRST (a stale opener gives up on it, §15.3); creator identity again
      if pool.ClearOnReuse: zero the data region
      if options.PreFault: touch every page of both views again (resident pages: a pass over memory, not a fault per page)
  WriteHeader(C, D, instanceId, entry.SectionId, base, LayoutFlags = Pooled)   // §3.3 step 5
  alias record: Magic = PHOTLINK, Version = 1, SectionId, DataBytes, InstanceId, TargetName = the pool-owned section name
  TestHooks.BeforeInitState; MemoryBarrier; ring.InitState = 1; alias.InitState = 1; unmap the alias view
  failure after the mapping was obtained: it is released, never returned (it may be half-initialised); the alias is closed
```

Release (`ReleaseNative`, once the buffer and every reader it created are disposed): laggard handles closed, alias closed (the name disappears once no opener holds it either), `pool.Return(entry)`. Three cases release the mapping instead of returning it:

* a release triggered by a finalizer (a buffer or reader that was never disposed): its `SafeHandle`s may already be queued for finalization in the same collection, and a pool must never keep a handle whose release is scheduled;
* a writer closed with a bucket outstanding: another thread may still be storing into the bucket's span (unsupported), and such a store must fault on unmapped memory rather than land in the buffer the pool hands the section to next;
* a disposed pool.

Without a pool, a call racing `Dispose` at worst faults on unmapped memory. With a pool, a section released too early can belong to another buffer a moment later, possibly one shared with other processes. So every call that can run on one thread while `Dispose` runs on another holds a local reference for as long as it touches the section: `GetBucket`'s slow path while it scans and waits (as before), `CreateReader` (the reference it takes first becomes the reader's) and `DuplicateSectionHandleTo` (a duplicate made after a reuse would silently reach the other buffer). A reference does not cover what a call hands out: the bucket `GetBucket` reserves after its wait outlives both the reference and the call, and a disposal that missed the reservation would return the section to the pool under it, so the next `Create` could take the section while the caller still fills a span into it. The reservation is paired with `CloseWriter` instead (Dekker, §5.3): the disposal finds it, and the mapping is released as for any bucket outstanding at `Dispose` (above), or the call throws `ObjectDisposedException`. (A `GetBucket` that did not wait is not covered: it races a writer that is not blocked, and covering it costs on every bucket or every `Dispose`, §5.3.) Each `PooledMapping` also carries an idle flag: a second `Return` of the same mapping throws instead of listing it twice.

### 15.3 The reuse check

A pooled section may serve a new buffer only when nothing else can still observe the previous one. Every participant holds the section handle for as long as it maps the section (`MirroredSection` owns it, the header peek holds it, a duplicated handle is a handle), so the rule is: the system-wide handle count of the section is 1, the pool's own handle. `NtQueryObject(ObjectBasicInformation).HandleCount` counts the handles of all processes (about 1 µs); the handles of a terminated process are closed by the time it is signaled (measured). A view whose handle was closed is not counted: this library never leaves one, except a parked opener mapping (§15.5), which does not look at the section until it holds a handle again.

```
TryClaim(entry):                                    // under the pool lock; newest idle entries of the size first, at most 16 per Create
  Interlocked.Exchange(ref hdr.InitState, 0)        // store, full fence
  if HandleCount(section) == 1: return true         // load
  Volatile.Write(ref hdr.InitState, 1)              // still held (or the query failed): the closed buffer stays exactly as it was
  return false
```

Every opener obtains its handle with a system call (`OpenFileMappingW`, or the `DuplicateHandle` that produced the handle it was given) before it loads `InitState` and `InstanceId`. Dekker: if the pool's load missed the opener's handle, the opener's load came after the pool's store, so the opener sees `InitState == 0`, waits (§3.4 step 3) and then finds another `InstanceId`. An opener that came through an alias knows which instance it wants and reports `RingBufferNotFoundException` ("no longer exists"), without waiting for the re-initialisation to finish: the reuse stores the new `InstanceId` before its slow parts (clearing or touching a large data region), and `WaitForInit` with an expected instance returns as soon as it sees a different non-zero id; an opener with a duplicated handle is never in that position, because its handle has existed since the duplication and the claim cannot succeed. If the pool does see the opener's handle, it restores `InitState` and the opener attaches to the closed buffer, exactly as without a pool. A busy entry stays idle and is checked again by later `Create`s until it expires.

### 15.4 Names: the alias section

A section's kernel name cannot change, so a reused section cannot carry the next buffer's name. The buffer's name belongs to a separate 64 KiB section holding an `AliasBlock` (`Internal\Layout.cs`): `Magic` "PHOTLINK" at 0, `Version` at 8, `TargetNameLength` at 12, `SectionId` at 16, `DataBytes` at 24, `InitState` at 52, `CreatorPid` at 72, `CreatorStartTime` at 80, `InstanceId` at 88, `TargetName` (UTF-16, at most 256) at 128. `InitState`, the creator identity and `InstanceId` share the control block's offsets (asserted by `Layout.Verify`), so `Open` maps and waits for either kind of section the same way and dispatches on `Magic` afterwards:

```
OpenCore(section, name):
  peek; WaitForInit(peek)
  if Magic == PHOTLINK:
      (target, expected, sectionId) = alias record; unmap; keep the alias handle for the buffer's lifetime   // as a direct section handle keeps its name alive
      section = OpenFileMappingW(target)              // missing: RingBufferNotFoundException ("no longer exists")
      if options.Pool: TryRevive(sectionId, expected) // §15.5
      peek the target; WaitForInit; Magic == PHOTONE1 && InstanceId != expected: RingBufferNotFoundException
  Validate
  Pooled && no alias && opened by name: RingBufferNotFoundException   // a pool-owned section's own name is not a buffer name
  map (§3.4 steps 5-7); options.Pool && Pooled: the buffer parks its mapping when released (§15.5)
```

Differences from a direct section: while the buffer lives, `Create(name)` and `Open(name)` behave the same. After the writer disposed, a late `Open(name)` attaches to the closed buffer only until its section is reused, and reports `RingBufferNotFoundException` afterwards. `Name` is the user's name; `DuplicateSectionHandleTo` duplicates the ring section.

### 15.5 Opener: parking and revival

An opener that passed a pool keeps its views and its 33 event handles when it releases a pooled buffer, but closes the section handle (`MirroredSection.DetachSection`): a parked mapping must not count as a holder, or the creator could never reuse the section. The pages stay mapped in this process (the views keep them alive) and are not read while parked.

```
TryRevive(pool, section /* just opened */, sectionId, expected):
  parked = pool.TakeParked(sectionId); none: return null (map afresh)
  WaitForInit(parked header)                           // loads through the parked views, after the handle exists (the Dekker pair of §15.3)
  Magic != PHOTONE1 || SectionId != sectionId || InstanceId != expected || DataBytes differ: pool.Return(parked); return null
  Validate (wrong T: pool.Return(parked), rethrow)
  if options.PreFault: read-touch both views
  parked.AttachSection(section); a RingBuffer over the parked views and events
```

Reading the expected random 64-bit `InstanceId` through the parked views, after opening the section that the alias (or the handle) names, shows that both are the same section. The events are named from `SectionId`, so they are still the right ones. For `Open(SafeSectionHandle)` the expected instance is the one read through that handle's peek.

### 15.6 Expiry, bounds, disposal

Idle entries (creator sections and parked opener mappings) are kept in return order. `IdleTimeout` (default 30 s; zero = keep nothing; infinite = until `Trim`/`Dispose`) is enforced by one `System.Threading.Timer` armed for the oldest entry's expiry and re-armed after every expiry pass. Nothing runs while the pool is empty, and a return never moves the wake-up earlier. The timer holds the pool through a weak reference and captures no execution context. `MaxIdleBytes` (data-region bytes plus the tag memory a creator committed, default unbounded) releases the longest-idle entries when a return exceeds it; an entry larger than the bound is released at once. Unmapping always happens outside the lock. `Trim()` releases every idle entry; `Dispose()` also stops pooling (later returns are released, `Create` with a disposed pool maps a new section every time); a pool that is never disposed releases its idle entries from its finalizer. `RingBufferPool.Shared` is a process-wide pool with the defaults whose `Dispose` only trims.

### 15.7 Limits

* Reuse needs the same `DataBytes` and namespace (`Local\` or `Global\`), and the same base address when `PreferredBaseAddress` is set; the element type may differ.
* Readers in other processes that keep a closed buffer open keep its section from being reused (by design): new buffers get new sections meanwhile, and the idle entry expires normally.
* Without `ClearOnReuse` a reused data region still holds the previous buffer's bytes: invisible through the API (a reader starts at the head), visible to a process that reads the raw mapping of the new buffer.
* The pool does not isolate the users of successive buffers from each other, with or without `ClearOnReuse`: a process that mapped an earlier buffer can keep a view of the section without holding a handle (a parked opener mapping does exactly that), which the handle count cannot see, and would see the later buffers. Buffers that serve parties who must not see each other's data must not share a pool.
* Idle memory stays committed and mostly resident; `IdleTimeout` and `MaxIdleBytes` bound it. A parked opener mapping keeps the section's pages alive in the system even after the creator's pool released its own side.
* Only a disposed buffer returns its mapping; a finalized one does not (§15.2).

## 16. Stream tags (`ITag.cs`, `TagMode.cs`, `JsonTagSerializer.cs`, `RingBuffer.Tags.cs`, `RingReader.Tags.cs`, `Internal\TagWriter.cs`, `Internal\LocalTagLog.cs`, `Internal\SharedTagLog.cs`, `Internal\TagReader.cs`, `Internal\LocalTagReader.cs`, `Internal\SharedTagReader.cs`, `Internal\TagViews.cs`, `Internal\TagFormat.cs`)

Requested 2026-09-17: the writer attaches tags (messages) to elements while it writes; tags cross processes serialized (JSON for a start); a chunk
carries the tags of its elements; a tag type declares itself persistent, and a reader can ask for the last persistent tag of every key. Follow-ups
the same day: `AddTag` sets `Offset`, `buffer.AddTag` without a bucket, `ReadLastTagValues` as a span; then no tag capacity (memory allocated as the
tags need it) and no serialization in process. The first revision had a fixed tag log and table between the control view and the data, and a commit
waited while the log was full; this section describes the revision that replaced it.

### 16.1 API

```csharp
public interface ITag
{
    static virtual bool IsPersistent => false;   // a persistent tag is state: the last one per key stays readable (ReadLastTagValues)
    ulong Offset { get; set; }                   // absolute element index (Chunk.StartOffset / Bucket.StartOffset space); set by AddTag and from the record
    string Key { get; }
}

public enum TagMode { None, InProcess, CrossProcess }

var options = new RingBufferOptions { Tags = TagMode.CrossProcess, TagSerializer = new JsonTagSerializer() };   // writer
using var buffer = RingBuffer<float>.Create(1 << 20, "sdr", options);
buffer.AddTag(new SampleRate { Hz = 48_000 });                                    // no bucket: the next element the writer publishes
using (var bucket = buffer.GetBucket(1024))
{
    bucket.AddTag(new BurstStart(), 100);                                          // element 100 of the bucket; published by Commit
    bucket.Commit(1024);
}

using var own = buffer.CreateReader();                                             // the writer's own reader: the very instances added above

var tags = new JsonTagSerializer().Register<SampleRate>();                         // reader process
using var opened = RingBuffer<float>.Open("sdr", new RingBufferOptions { TagSerializer = tags });
using var reader = opened.CreateReader();
reader.TryRead(100, out var chunk);
foreach (ITag tag in chunk.Tags.Span) { }                                          // StartOffset <= Offset < StartOffset + Length, offset order
reader.Advance(100);
ReadOnlySpan<ITag> state = reader.ReadLastTagValues();                             // last persistent tag per key with Offset < ReadCursor, no copy
```

| Call | Rule |
|---|---|
| `RingBufferOptions.Tags` | creator; `None` (default): no tags; `InProcess`: tag objects for the readers of the writer's own buffer, never serialized; `CrossProcess`: also serialized into shared memory for readers of every process. No capacity |
| `RingBufferOptions.TagSerializer` | per process; required by a `CrossProcess` creator (`Create` throws `ArgumentException`); readers of an opened buffer deserialize with it and deliver `UnknownTag`s without it; ignored by `InProcess` |
| `RingBufferOptions.MaxUnreadTags` | creator; the most tags the writer keeps for the slowest reader before a commit waits for it. 0 (default) = no limit |
| `RingBufferOptions.MaxUnreadTagBytes` | creator, `CrossProcess` only (in-process tags have no serialized size); the same limit in record bytes. 0 (default) = no limit |
| `RingBuffer.Tags`, `RingBuffer.MaxUnreadTags` | the creator's mode and limits, on openers too (an opener of an `InProcess` buffer sees the mode, its readers see no tags) |
| `Bucket.AddTag<TTag>(tag, index = 0)` | `0 <= index < Length`; sets `tag.Offset = StartOffset + index`; `TTag` concrete (it decides persistence and the type name); `CrossProcess`: serialized at once, a key over 65535 UTF-8 bytes throws |
| `RingBuffer.AddTag<TTag>(tag)` | writer; sets `tag.Offset = WriteCursor` (the first element of the outstanding bucket, if any); pending until a commit publishes at least one element, through commits that publish none; dropped if the writer closes first |
| `Bucket.Commit(k)` | publishes the tags with `Offset < StartOffset + k` with the elements, drops the rest; never waits for tags |
| `Chunk.Tags` | `ReadOnlyMemory<ITag>`: the chunk's tags, in offset order, equal offsets in add order; reader-owned memory, valid until `Advance` passes it |
| `RingReader.ReadLastTagValues()` | `ReadOnlySpan<ITag>` over the reader's own array (no allocation; valid until the next `TryRead`/`Advance`): the last persistent tag of every key with `Offset < ReadCursor`, one per key in first-seen order, including tags written before the reader joined |
| `JsonTagSerializer` | `Register<TTag>(name?)` (default name: full type name); the parameterless constructor is reflection-based (`RequiresUnreferencedCode`/`RequiresDynamicCode`); `JsonTagSerializer(JsonSerializerOptions)` with a source-generated resolver is AOT-safe |
| `UnknownTag` | a tag whose type name the reader does not know, or whose payload failed to deserialize (`Error`); keeps `Offset`, `Key`, `Persistent`, `Payload` |

Who gets what. A reader created by the writer's own `RingBuffer` instance reads the writer's object log (§16.5) and gets the instances that were added,
in either mode, without serialization. A reader of a `RingBuffer` opened by name or handle, in any process including the writer's, reads the records in
shared memory (§16.6-§16.7) with `CrossProcess`, and no tags with `InProcess`. Because the writer's readers share the instances, a tag must not be changed
after it was added, and an instance must not be added twice (`AddTag` sets its `Offset`).

Deviations from the requested sketch: `IsPersistent` is `static virtual` with a default (an interface with a `static abstract` member cannot be a type
argument, so `ReadOnlyMemory<ITag>` would not compile); `Offset` has a setter, because `AddTag` sets it; the chunk's tags are those of its own elements
(`StartOffset <= Offset < StartOffset + Length`), so a tag is delivered exactly once per pass and never ahead of its element. `Chunk.StartOffset` /
`Bucket.StartOffset` are `Cursor` as `ulong`.

### 16.2 Layout (version 4)

```
 section:      [control view 64 KiB][data D][tag reserve R (CrossProcess only)]
 views:        [header 64 KiB][data D][mirror D]         (MirroredSection, as before; DataOffset = 64 KiB again)
               + tag views, mapped on demand by each RingBuffer instance (TagViews)
 tag reserve:  [table region 2 GiB][ring 0: 64 KiB][ring 1: 128 KiB] ... [ring 18: 16 GiB]            R = TagFormat.ReserveBytes ≈ 34 GiB
```

* **Reserved, committed on demand.** A section with a tag reserve is created `SEC_RESERVE`. Reserving costs neither memory nor commit charge (measured on
  this machine: a 1 TiB section is created in 0.04-0.2 ms and charges no paged pool and no commit). `MirroredSection.Create(commit: true)` commits the
  control view and the data right after mapping them, before the mirror self-test. The writer commits tag memory through its own view, in doubling
  steps. A commit belongs to the section: pages committed through one view are readable and writable through every view in every process, also through
  views mapped before the commit (measured, cross-process). Section pages cannot be decommitted (`VirtualFree(MEM_DECOMMIT)` on a view fails with 87),
  so tag memory stays committed until the section is destroyed: a buffer keeps its high-water mark.
* **Views.** Mapping a view charges page tables for its whole size (measured: about 2 MiB per GiB, per view, whether the pages are committed or not), so
  the reserve is never mapped as a whole: `TagViews` maps a ring when somebody first touches it, and a table view that covers what is needed (larger ones
  as the table grows; older table views stay mapped, a joiner may still be copying from one). The views of a `RingBuffer` instance are shared by its
  writer and the readers it created and released in `ReleaseNative`, once the writer is closed and every one of those readers is gone, so a view is
  never unmapped under a reader. The writer maps read/write, openers read-only.
* **The opener's peek commits.** An opener can find the section's name before the creator has committed the control view. `MapHeaderPeek` commits its
  64 KiB view before loading anything; committing is idempotent, preserves the creator's stores, and is a no-op on a committed section.
* **Control block.** `TagMode` (112), `TagReserveBytes` (120: `TagFormat.ReserveBytes` with `CrossProcess`, otherwise 0), `TagEnd` (136, on line 2 next
  to `WriteCursor` as before), line 42: the snapshot seqlock (`TagVersion`, `TagSnapshotEnd`, `TagSnapshotW`, `TagStateUsed`, `TagStateCount`,
  `TagSnapshotRing`, `TagSnapshotRingStart`) and `TagTableCommitted`; lines 43-45: `TagRingCommitted[19]`. A committed size only grows; the writer stores
  it after `VirtualAlloc` succeeded and before any record or table byte lies beyond the previous value. `Validate` checks the mode and that the reserve
  size matches it; openers load the mode once and compute every tag view from constants and the validated `DataBytes`.
* **Pool.** A section is reused only for the same `DataBytes` and `TagReserveBytes`. The pool keeps, per section and in its own process, how much of
  every ring and of the table the creators that used the section committed (the union: pages stay committed), not trusting the control block, which
  other processes can write. `MaxIdleBytes` counts the data region and that tag memory; `ClearOnReuse` also zeroes it.

`TagEnd` shares line 2 with `WriteCursor` (the only field that does): the writer stores it right before `WriteCursor` and only on commits that carry
tags, readers load it right after `WriteCursor`, so the line sees no traffic pattern it did not already have.

### 16.3 Records

One record = 24-byte `TagRecordHeader` { `RecordBytes` i32 (multiple of 8), `Flags` u16 (bit 0 persistent, bit 1 jump), `KeyBytes` u16, `Offset` u64,
`TypeNameBytes` u16, `NextRing` u16, `PayloadBytes` i32 } + key (UTF-8) + type name (UTF-8) + payload + zero padding. The rings and the table hold the
same records. A **jump record** ends a ring generation: `RecordBytes = 24`, `Flags = 2`, `NextRing` = the ring the log continues in, nothing else.

Log positions are absolute byte counts that only grow (`TagEnd`). A **generation** is a stretch of the log in one ring: it starts at log position `S` in
ring `c` (of `L_c = 64 KiB << c` bytes), and position `p` lives at physical offset `(p - S) & (L_c - 1)`. A record may wrap within its ring and is then
copied in two parts (tags are not the zero-copy path); a record never spans rings. Position 0 is ring 0, start 0. Records are in offset order: a commit
appends the tags of its prefix sorted by offset (stable), after every earlier commit's.

### 16.4 Writer: publishing tags, rings that grow and shrink

Writer-local: the pending tags (`TagWriter`); the object log (§16.5); and with `CrossProcess` (`SharedTagLog`): the current generation (ring, start,
bytes appended), the committed bytes of every ring and of the table, a queue of reserved groups `(position, length, AppendW, ring)` covering
`[tail, end)` (a group is what one commit appended to one ring: its records share `AppendW` and are released together), the number of reserved groups
per ring, the last persistent record of every key, and the snapshot state.

```
EndWriteWithTags(k):                                                 // NoInlining; EndWrite diverts here while tags are pending
  Interlocked.Exchange(_committing, 1); if _disposed: throw ObjectDisposedException   // touch nothing: the closing thread drops the bucket
  try:
      select the pending tags with Offset < W + k, sorted by (Offset, add order)
      with a tag limit and no room for them: EnsureTagRoom            // the only wait a commit does for tags; see "Limiting the tags" below
      reserve the object log's chunks and key slots for them                // everything that can fail comes before the appends
      CrossProcess, bytes = their records:
          if !Fits(bytes) or ShrinkDue: Free(_min); if still: _min = ScanMin(); Free(_min);
              if it still does not fit: SweepDeadSlots() (at most once per liveness interval); if that evicted: _min = ScanMin(); Free(_min)
          Prepare(bytes):                                           // every step that can fail, before anything changes
              next = current ring; if !Fits(bytes): next = LargerRing(bytes)
              else if ShrinkDue: next = live + bytes + 24 <= L/16 ? SmallerRing(bytes) : current (and look again after another period)
              state: the last record of each key in the commit; buffers for new keys and grown records; room for the keys; the table size check
              group slots; commit: the jump slot and the first `bytes` of `next`, or the range of the records; the table
              if next != current: write Jump(next) at end; reserve it (AppendW = W); end += 24; generation = (next, start = end)
      append (allocates nothing): every tag to the object log; CrossProcess: its record at end, reserved with AppendW = W; the last persistent record
          of a key replaces the key's record (writer-local)
      publish the object log's count; Volatile.Write(TagEnd, end)    // BEFORE the write cursor
      (on any exception so far: forget the bucket's tags, keep the ones added to the buffer, _e = W, the bucket is no longer outstanding; nothing was published)
      W += k; Interlocked.Exchange(WriteCursor, W); signal readers (§5.2)
      object-log snapshot (§16.5); shared snapshot (§16.6)            // AFTER the write cursor
  finally: _committing = 0

Fits(bytes)      = live + bytes + 24 <= L         live = end - max(start, tail): what the current ring still holds; 24: room for the jump that may end it
Free(min)        : drop reserved records with AppendW < min (and count them off their ring); tail = oldest remaining position (or end)
LargerRing(bytes): the smallest free ring from max(2L, 2(bytes + 24)) up, else from bytes + 24 up; free = not the current ring, no reserved group in it;
                   none: InvalidOperationException
ShrinkDue        : ring > 0 and at least 4L appended in this generation
SmallerRing      : the smallest free ring of at least 8(live + bytes + 24) that is at least 4 times smaller than the current one, or the current one
commits          : 64 KiB-aligned doubling steps up to the ring's size (a range that wraps commits the whole ring); the stored size follows the commit
```

**Reuse rule.** The bytes of a record appended while the published write cursor was `A` may be overwritten once the minimum reader cursor exceeds `A`.
Proof: let reader `r` not have loaded record `X` of commit `j` (`A = W(j-1)`, `W(j)` stored after `TagEnd(j)`). `r` publishes a cursor `R` only after
loading tags with `_tagLoadedW ≥ R` (§16.7), where `_tagLoadedW` is a write cursor `r` loaded before an end load. That end load did not see `X`, so it
came before `TagEnd(j)` was stored, and so did the write-cursor load before it; that load cannot have seen `W(j)`, hence `R ≤ _tagLoadedW ≤ A`. A joining
reader's `R ≥ w2 ≥ _min` holds as in §5.6, and its own loads follow §16.6. The cached `_min` only under-estimates. So no reader that can still load `X`
has a cursor above `A`, and `Free` never releases bytes somebody may still read. Freeing is writer-local: nothing is published.

The rule covers rings as well as records. A ring starts a new generation only when none of its records is reserved, that is when every record ever
placed in it was released by the rule; a jump record is a record like any other, appended with its commit's `W` and released the same way. Within a
generation, `Fits` keeps the reserved records and the new ones apart modulo `L`.

**Dead readers.** A reader whose process died keeps its cursor, and with it every later record, reserved until it is evicted. The data ring evicts it
only when the writer blocks for space (§5.7), which dense tags can be far from: the log would grow for a reader that will never read, up to "no free
ring". So before a commit makes the log move to a larger ring, the writer sweeps the slots for dead processes (at most once per liveness interval, since
a sweep checks the process of every reader) and frees again if it evicted one.

**Limiting the tags (optional).** `MaxUnreadTags` / `MaxUnreadTagBytes` bound what the writer keeps for the slowest reader. A commit that would exceed
the limit waits on the reader cursor that releases the oldest tags (`WaitForMin`, the wait `GetBucket` uses for space), then releases and re-checks:

```
EnsureTagRoom(count, bytes):                                     // NoInlining; only when a limit is set and the cached counters do not fit
    if count or bytes exceed the limit by themselves: InvalidOperationException (the bucket is dropped; no reader could make room)
    local ref (Dispose on another thread ends the wait with ObjectDisposedException; CloseWriter wakes it)
    loop: _min = ScanMin(); release what the readers passed; if it fits: return
          WaitForMin(oldest tag's release cursor + 1)
```

Unread tags are counted where each mode already knows them: the shared log counts the records whose bytes are still reserved (§16.4) and their bytes; the
object log follows a cursor over the chunk chain, moved only by a commit that is at its limit, that releases the entries with an offset below the minimum
reader cursor (entries are not cleared: a joiner with an older snapshot still replays them). Without a limit neither is tracked and the check is one
comparison per commit that carries tags; readers and the element path are untouched either way.

Nothing breaks a deadlock here: a reader waiting for more elements than are published, while a commit waits for that reader to read past tags, stops
both, and only `Dispose` (or a reader's timeout) ends it. The limit has to leave room for the tags of the largest chunk a reader waits for. That is why
it is off by default.

Measured (`TagBenchmarks.OneTag_InProcess` against `OneTag_InProcess_Limited`, `MaxUnreadTags = 4096`, a reader that keeps up, palindromic runs
limited / unlimited / unlimited / limited): 145.7 ns against 145.9 ns per tagged commit, inside the noise of the machine. The releases are what could
cost: a commit that reaches the limit scans the reader cursors once and walks the entries it releases, whole chunks at a time, so the amortized cost is
a comparison per tag. A very large limit is the wrong way to say "no limit": it keeps that many entries alive before the first release.

**No waiting without a limit, and no tag deadlock.** A commit never waits for tags: when its records do not fit behind the ones readers still need, the
log moves to a larger ring. Tag memory is still bounded, by the data back-pressure: every reserved record has `AppendW ≥ min`, so its offset lies in
`[min, W + bucket)`, at most `Capacity` plus one bucket of elements; the rings hold the tags of those elements, about twice over while they grow. The
first revision's deadlock (a full log while every reader holding old tags waits for more elements than are published) cannot occur, and with it went
the 1 s detector and `TagLogFullException`. After a burst, the generation moves back to a small ring; the large ring's pages stay committed but
untouched, so the system can page them out.

**Failure atomicity.** The ring view mapping, the commits, the table size check, "no free ring large enough", and every allocation the appends need
(the object log's chunks and key slots, group slots, buffers for new or grown state records, room for new keys) can fail; they all run before the jump
record is written, and the appends and publications after it allocate nothing and cannot fail. A failed commit publishes nothing: its bucket is dropped,
as by `Commit(0)`, and tags added to the buffer stay pending. The writer stays usable.

**The table.** The last record of every key, in the order the keys appeared. `PublishSnapshot` rewrites a record that kept its size in place, lays the
table out again from the first record whose size changed, and appends new keys, so a commit's cost follows what changed rather than the table's size
(state buffers are sized in powers of two, so a record whose size drifts a little is not reallocated).

**Tags added to the buffer.** `RingBuffer.AddTag` stages a *sticky* tag at `W`, the next element to be published (with a bucket outstanding, its first
element). `W` moves only when a commit publishes elements, and that commit's selection (`Offset < W + k`, `k >= 1`) includes every sticky tag, so its
offset stays right. A commit that publishes nothing selects no tag: it drops the bucket's own tags and keeps the sticky ones (their records moved to
the front of the staging area, in the same order). A tag cannot go out before its element: both snapshots require every tag before their end to have an
offset below their write cursor, and a joining reader would otherwise take state from an element that does not exist yet.

**Dispose during a commit with tags.** Such a commit can map and commit memory, so `Dispose` on another thread must be kept from dropping the bucket under
it (both threads would run `EndWrite`: the write cursor could go back, the tags be lost, the views be released under the copy). Dekker pair: the commit
stores `_committing = 1` with a full fence and then loads `_disposed`; `Dispose` stores `_disposed` with a full fence and then, in `CloseWriter`, loads
`_committing`. Either the commit sees the disposal and leaves without touching anything (the closing thread drops the bucket), or `CloseWriter` sees the
commit and waits, a millisecond at a time, until it has left.

### 16.5 The writer's own readers: the object log

`LocalTagLog` holds the tags as objects: chunks of 256 entries `(tag, key, offset, persistent)` linked by `Next`, a published entry count, and a snapshot.
The writer appends to the last chunk (setting `Next` before the first entry of a new chunk), and a commit publishes the count before the write cursor,
as `TagEnd` (the count is a pinned `long`: readers load it through the same pointer load as `TagEnd`). After the write cursor, once 256 entries have
accumulated since the current snapshot, it publishes a new immutable snapshot `{W, End, Chunk, Index, State}` with a reference store (initially
`{0, 0, first chunk, 0, []}`): every entry before `End` has an offset below `W`, `State` is the last persistent tag of every key among them in first-seen
order, and entry `End` is item `Index` of `Chunk`.

```
LocalTagReader.Join (claim step (e), w2 published):
  S = the snapshot                                                   // one reference load: immutable, so consistent
  R = max(w2, S.W); E = the published count                          // E loaded after S
  state = S.State; replay entries [S.End, E): Offset < R: persistent ones into the state; the rest queued
  position = E; _tagLoadedW = R
```

Why it is exact. Entries before `S.End` have offsets below `S.W ≤ R`, and `S.State` folds them. An entry after `E` belongs to a commit that published its
count after the joiner's `E` load, hence after the `S` load, hence after the commit that published `S` (which published its own count and write cursor
before `S`): that commit starts at `W ≥ S.W`, and its write cursor store follows the joiner's load of `w2`, so its offsets are at least `R`. A snapshot
that is 255 entries old is as exact as a fresh one; renewing it bounds the replay and releases the chunks before it.

Reclamation needs no rule: the writer references its last chunk and the snapshot, each reader its current chunk and its queue. A chunk becomes garbage once
every reader has moved past it and the snapshot no longer points into it, so the tag objects live exactly as long as a reader can still reach them.
Loading is a walk over references: no serialization, no copy. `InProcess` never calls the serializer; `CrossProcess` serializes each tag once, for
shared memory, and the writer's readers still get the objects.

### 16.6 Joining readers of shared memory: the snapshot

After the write cursor of a commit that appended records, the writer publishes, as a seqlock:

```
PublishSnapshot(W):  Interlocked.Exchange(TagVersion, odd); [if the table changed: rewrite it, TagStateUsed, TagStateCount];
                     TagSnapshotEnd = end; TagSnapshotW = W; TagSnapshotRing, TagSnapshotRingStart = the current generation; Volatile.Write(TagVersion, even)
```

The odd store is fenced: a large table copy may use non-temporal stores, which are not ordered with earlier stores until they are flushed. The table
memory was committed in `Prepare`, so nothing here can fail.

```
SharedTagReader.Join (claim step (e), w2 published):
loop:
  v1 = TagVersion; odd → back off
  TS, WS, used, ring, start = the snapshot; E = TagEnd; tableCommitted = TagTableCommitted (after used); W = WriteCursor (after WS)
  sane = 0 <= used <= min(tableCommitted, Array.MaxLength), TS <= E <= TS + R_reserve, WS <= W, 0 <= start <= TS, ring < 19
  copy table[0, used) through a table view of at least used bytes
  walk [TS, E) from (ring, start), copying every record except jumps; for each:
      ring < 19, start <= p; committed = TagRingCommitted[ring]; header range committed; header valid (length <= E - p and <= L, jump fields);
      the whole record committed; a jump: ring = NextRing, start = p + 24; every 64 records: TagVersion == v1, else stop
  if TagVersion == v1: break                                        // stable (and insane or a failed walk ⇒ RingBufferLayoutException)
  back off (spin, yield, sleep); while the version is odd, every 10 ms: writer closed or dead ⇒ position = TagEnd, then R = max(w2, W) (W loaded after
  TagEnd), no state. A changed even version means a live writer: retry
R = max(w2, WS)                                                      // CreateReader then publishes R with a fence and wakes a waiting writer (§5.6 (f))
state = the table's records; copied records with Offset < R: state (persistent ones), the rest: the first queued tags
position = E; generation = the walk's last (ring, start); _tagLoadedW = R
```

Why it is exact:

* **Consistent copies.** Writer stores `odd`, data, `even` in that order; the joiner loads `v1`, data, `v2`. On TSO an unchanged even version means no
  writer store fell between the two loads.
* **The copied bytes are intact.** Records in `[TS, E)` belong to commits after the snapshot's commit. Their bytes are reused, by wrapping within a ring or
  by a new generation in the ring, only by a commit that follows the commits that appended them, and each of those published its own snapshot first,
  which changed the version.
* **No generation is missed.** The snapshot names the generation that holds `TS`; a generation that begins inside `[TS, E)` is announced by its jump
  record, which lies in `[TS, E)`.
* **Nothing is missed or doubled.** The table holds the last persistent record of every key among all records before `TS`, whose offsets are all below
  `WS ≤ R`. Records in `[TS, E)` are split at `R`. A record after `E` belongs to a commit whose `TagEnd` store follows the joiner's `E` load, and `w2`
  and `WS` were loaded before it, so its offset is at least `R`: `_tagLoadedW = R` is right, and so is the reuse rule for this reader.
* **Memory safety without trusting the snapshot.** Every committed size the walk uses was stored by the writer after the commit it describes, and sizes
  only grow, so any value it loads, even from a torn snapshot, describes committed memory. Ring indices are bounded, lengths are bounded by the ring and
  by the committed size, and the table copy by `TagTableCommitted`: torn or corrupt values make the walk stop or copy bytes that the version check then
  discards, never fault. The walk is also bounded (`E - TS` at most the reserve, as a reader's `Load`), so hostile jump records cannot keep it going. A
  stable but corrupt snapshot is reported as `RingBufferLayoutException`, and the claim gives its slot back as a disposed reader does (§5.6).
* **Start cursor, livelock, writer gone.** As before: `WS` is published after the write cursor, so `R ≤ W`; a retry needs another tagged commit; a join
  that keeps losing holds the writer back through `w2` until the writer waits, and its fenced cursor store then wakes it (§5.6 (f)); a writer that died
  inside `PublishSnapshot` leaves the version odd, and the joiner starts at the final write cursor, loaded after `TagEnd`, without state. Only an odd
  version leads there: a writer that closed normally left an even one, so a joiner that lost a race against the writer's last commit still copies the
  state.

### 16.7 Readers

```
_tagThreshold = min(_tagLoadedW, first queued Offset)                // long.MaxValue without tags
_tagEnd       = &TagEnd (shared) or &count (the writer's object log)
TryRead(n): ...data as §5.4...; if _tagThreshold < R + n:
                if no queued Offset < R + n and *_tagEnd == position: _tagLoadedW = _wc (inline: sparse tags, nothing new)
                else tags = TagsForRead(R + n)
            chunk = (span, R, tags)
            TagsForRead(end): if _tagLoadedW < end: RefreshTags(); return first queued Offset < end ? the reader's TagReader : null
Advance(k): ...checks...; if _tagThreshold < R + k: TagsForAdvance(R + k); then store the cursor (§5.4)
            TagsForAdvance(R'): if _tagLoadedW < R': RefreshTags(); if first queued Offset < R': Consume(R')
RefreshTags(): w = _wc (loaded earlier); E = *_tagEnd; if E != position: Load(E); _tagLoadedW = w
Load(E), object log: entries [position, E) along the chunks → queue (Offset ≥ R) or state
Load(E), shared:     E - position > R_reserve ⇒ RingBufferLayoutException; per record: the ring's view (mapped on first use); the header range, then the
                     record, checked against TagRingCommitted (cached, reloaded once when a range lies beyond it); header validated; a jump switches
                     the generation; otherwise parsed in place (a wrapping record is copied first) and deserialized (a record behind the reader that is
                     not persistent is skipped)
Consume(R'): queued tags with Offset < R' leave the queue (entries are not cleared); persistent ones become the key's last value
chunk.Tags (on access): the queued tags with Offset < StartOffset + Length
```

Loading happens at most once per new write cursor and always before the cursor store that passes the tags, which is what §16.4 relies on. A shared
record is deserialized by the reader's serializer, which gets `Offset` set from the record (so a tag type need not serialize it); an unknown type name
or a serializer exception yields an `UnknownTag`. Key and type-name strings are interned per reader. The last persistent tag of each key lives in an
array (one slot per key, in first-seen order, a new array when it grows), which `ReadLastTagValues` returns as a span. The queue is never compacted in
place and consumed entries are not cleared: a new array replaces the queue when its end is reached, so `ReadOnlyMemory<ITag>` handed out in a chunk keeps
its contents, also when the reader advanced into the chunk only partly.

Cost: COSTS

### 16.8 Limits

* Shared tag memory: rings up to 16 GiB each. A commit whose records do not fit, together with the records readers still need, in any free ring throws
  `InvalidOperationException` and is dropped. The persistent-tag table holds up to `Array.MaxLength` bytes (just under 2 GiB). Keys and type names take
  at most 65535 UTF-8 bytes.
* A reader that dies holds tag memory until a sweep finds it: before the log grows, or when the writer blocks for space.
* With `MaxUnreadTags` / `MaxUnreadTagBytes`, tags become back-pressure: sized too small for the chunks readers wait for, writer and readers deadlock
  until something is disposed (§16.4). A commit whose own tags exceed the limit throws `InvalidOperationException` and is dropped.
* Committed tag memory is never decommitted while the section exists; it counts against the system commit limit, and a pool keeps it with the section.
  `RingBuffer.TagMemory` reports it (`TagMemoryInfo`: the committed sizes, the current ring, the ring switches, the unread tags and bytes the writer holds,
  and the persistent keys); a pooled section's total includes what the buffers before this one committed, and `RingBufferPool.IdleTagBytes` reports what
  idle mappings keep. An opener reads the committed sizes and the key count from the control block; the writer's own counters are 0 there.
* The persistent-tag table is the one part that only grows: a key never gives its slot back while the buffer lives. An unbounded set of keys fills the
  2 GiB region and the commit is then dropped with `InvalidOperationException`.
* The writer's readers share the tag instances: a tag changed or added again after `AddTag` is seen changed by all of them.
* `InProcess` tags are invisible to every opener, also an opener in the writer's process.
* A writer that dies in the middle of `PublishSnapshot` leaves joining readers without persistent state; readers already attached keep theirs.
