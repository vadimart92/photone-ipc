# photone-ipc research appendix

Four research reports produced before the design (verbatim).


---

# REPORT: research:win32-mapping

All empirical questions are now answered. Final findings from round 3: a view base that is page- but not 64K-aligned works inside a placeholder; `MapViewOfFile3` without `MEM_REPLACE_PLACEHOLDER` at a placeholder address fails with 487; a non-64K-aligned `BaseAddress` hint without a placeholder fails with 1132 (docs claim rounding); `MEM_TOP_DOWN` is accepted with placeholders; `SEC_RESERVE` sections work with placeholders and commit-on-demand through one view is visible in the mirror; a `FILE_MAP_READ` handle can map `PAGE_READONLY` into a placeholder. Here is the reference.

# Double-mapped (mirrored) ring buffer on Windows from .NET — implementation reference

Scope: Windows 10 1803+ / Windows 11, x64 (ARM64 notes included), .NET 8/9/10 with `[LibraryImport]`. Everything marked **[verified]** was executed on this dev box (Windows 11 Pro 10.0.26200, x64, .NET SDK 10.0.112) with the exact P/Invoke declarations shown in section 9; the harness lives at `C:\Users\Admin\AppData\Local\Temp\claude\E--GitHub-photone-ipc\1f1c1aea-e7c4-43f4-a5f8-47ebdcd222c5\scratchpad\vmtest\` (`Native.cs`, `Program.cs`, `Round2.cs`, `Round3.cs`). Everything marked **[doc]** is from Microsoft Learn (URLs at the end). Where the docs and the OS disagree, the observed OS behaviour is stated explicitly.

Machine facts [verified]: `dwPageSize = 0x1000`, `dwAllocationGranularity = 0x10000`, `lpMinimumApplicationAddress = 0x10000`, `lpMaximumApplicationAddress = 0x7FFFFFFEFFFF`, `sizeof(MEM_EXTENDED_PARAMETER) = 16`, `sizeof(MEMORY_BASIC_INFORMATION) = 48`.

---

## 0. The mechanism in one paragraph

1. `VirtualAlloc2(MEM_RESERVE | MEM_RESERVE_PLACEHOLDER, PAGE_NOACCESS)` reserves one contiguous range of VA that nothing else can land in.
2. `VirtualFree(MEM_RELEASE | MEM_PRESERVE_PLACEHOLDER)` carves that range into adjacent sub-placeholders (page granular).
3. A pagefile-backed section (`CreateFileMappingW(INVALID_HANDLE_VALUE, …)`) holds the actual memory.
4. `MapViewOfFile3(section, …, MEM_REPLACE_PLACEHOLDER)` drops a view of the section into each sub-placeholder — the same section offset twice for the data region — giving `[header][data][data-mirror]` contiguous in VA. A `Span<T>` starting anywhere in `data` and extending up to `N` bytes past its end is valid memory that aliases the start of `data`.
5. A second process opens the same section by name/handle and repeats 1–4 (optionally asking for the same base address).

---

## 1. `VirtualAlloc2` with `MEM_RESERVE | MEM_RESERVE_PLACEHOLDER`

Signature [doc]:
```cpp
PVOID VirtualAlloc2(
  [in, optional]      HANDLE                 Process,          // NULL = current process; else needs PROCESS_VM_OPERATION
  [in, optional]      PVOID                  BaseAddress,      // NULL = system chooses; else multiple of allocation granularity (64 KiB)
  [in]                SIZE_T                 Size,             // "must be a multiple of the page size" (see note)
  [in]                ULONG                  AllocationType,
  [in]                ULONG                  PageProtection,
  [in, out, optional] MEM_EXTENDED_PARAMETER *ExtendedParameters,
  [in]                ULONG                  ParameterCount);
```
Requirements [doc]: min client Windows 10 (placeholder flags: 1803 in practice, see §4), header `memoryapi.h`, lib `onecore.lib`, DLL listed as `Kernel32.dll` but **the real export is in `kernelbase.dll` / `api-ms-win-core-memory-l1-1-6.dll`** — see §4.

Flag values (SDK 10.0.26100 `winnt.h`, [verified]):

| Constant | Value |
|---|---|
| `MEM_COMMIT` | `0x00001000` |
| `MEM_RESERVE` | `0x00002000` |
| `MEM_REPLACE_PLACEHOLDER` | `0x00004000` (same numeric value as `MEM_DECOMMIT` in `VirtualFree`, different function) |
| `MEM_RESERVE_PLACEHOLDER` | `0x00040000` |
| `MEM_RESET` | `0x00080000` |
| `MEM_TOP_DOWN` | `0x00100000` |
| `MEM_LARGE_PAGES` | `0x20000000` |
| `PAGE_NOACCESS` | `0x01` |
| `PAGE_READONLY` | `0x02` |
| `PAGE_READWRITE` | `0x04` |

Rules for creating a placeholder:

- `AllocationType` must be exactly `MEM_RESERVE | MEM_RESERVE_PLACEHOLDER` (optionally `| MEM_TOP_DOWN`), `PageProtection` must be `PAGE_NOACCESS`. [doc]
  - `MEM_RESERVE_PLACEHOLDER` alone → `ERROR_INVALID_PARAMETER` (87). [verified]
  - `MEM_RESERVE | MEM_COMMIT | MEM_RESERVE_PLACEHOLDER` → 87. [verified]
  - `PAGE_READWRITE` instead of `PAGE_NOACCESS` → 87. [verified]
  - `MEM_RESERVE | MEM_RESERVE_PLACEHOLDER | MEM_TOP_DOWN` → OK (returned `0x7FF5EC210000`). [verified]
- `Size`: docs say "must be a multiple of the page size"; in practice it is rounded up to a page (`Size = 100` gave a 4 KiB placeholder; `Size = 0` → 87). A placeholder's size need **not** be a multiple of 64 KiB (`0x1000` and `0x11000` placeholders both succeed), but its *base* is always 64 KiB aligned when `BaseAddress == NULL`. [verified]
- `BaseAddress != NULL`:
  - must be a multiple of the allocation granularity (64 KiB). A page-aligned but not 64 KiB-aligned address fails with `ERROR_INVALID_ADDRESS` (487), **not** `ERROR_MAPPED_ALIGNMENT`. [verified]
  - if any byte of `[BaseAddress, BaseAddress+Size)` is not free (reserved, committed, mapped, or another placeholder) → 487, nothing is allocated. [verified] That is the whole "address taken" signal for the cross-process same-address attempt (§7).
  - any supplied `MEM_ADDRESS_REQUIREMENTS` must be all zeros when `BaseAddress != NULL`. [doc]
- `MEM_ADDRESS_REQUIREMENTS` (extended parameter `MemExtendedParameterAddressRequirements`) is **not needed** for the ring buffer. It is useful only to (a) constrain the address range (e.g. pick a "quiet" region so that the same address is likely free in other processes) or (b) request alignment > 64 KiB (e.g. 2 MiB for huge-page-friendly layouts). Struct [doc]:
  ```cpp
  typedef struct _MEM_ADDRESS_REQUIREMENTS {
    PVOID  LowestStartingAddress;  // multiple of allocation granularity, NULL = no limit
    PVOID  HighestEndingAddress;   // inclusive; must be (multiple of granularity) - 1 and <= lpMaximumApplicationAddress; NULL = no limit
    SIZE_T Alignment;              // power of 2, 0 = allocation granularity, else >= allocation granularity
  } MEM_ADDRESS_REQUIREMENTS;
  ```
  A 2 MiB-aligned placeholder via this parameter works. [verified] An all-zero structure is equivalent to omitting it. [doc]
- Placeholders show up in `VirtualQuery` as `State = MEM_RESERVE (0x2000)`, `Type = MEM_PRIVATE (0x20000)`, `Protect = 0`, `AllocationProtect = PAGE_NOACCESS (0x1)`, each split piece with its own `AllocationBase`. [verified]
- A placeholder counts as *reserved* VA only; no commit charge. [doc]

## 2. Splitting / coalescing / releasing placeholders with `VirtualFree`

```cpp
BOOL VirtualFree([in] LPVOID lpAddress, [in] SIZE_T dwSize, [in] DWORD dwFreeType);
BOOL VirtualFreeEx([in] HANDLE hProcess, [in] LPVOID lpAddress, [in] SIZE_T dwSize, [in] DWORD dwFreeType); // remote variant, PROCESS_VM_OPERATION
```
| Constant | Value | Use |
|---|---|---|
| `MEM_DECOMMIT` | `0x00004000` | not applicable to placeholders (→ 487) [verified] |
| `MEM_RELEASE` | `0x00008000` | release a placeholder (or a private allocation) back to free |
| `MEM_COALESCE_PLACEHOLDERS` | `0x00000001` | with `MEM_RELEASE`: merge adjacent placeholders |
| `MEM_PRESERVE_PLACEHOLDER` | `0x00000002` | with `MEM_RELEASE`: split a placeholder, or turn a replaced private allocation back into a placeholder |

**Split** = `VirtualFree(lpAddress, dwSize, MEM_RELEASE | MEM_PRESERVE_PLACEHOLDER)`. The range `[lpAddress, lpAddress+dwSize)` becomes its own placeholder; the remainder(s) of the original placeholder become separate placeholders (a split in the middle yields three pieces). Exact constraints, determined empirically because the docs do not state them [verified]:

- Granularity is the **page size (4 KiB), not 64 KiB**. Splitting 4 KiB off the front of a 192 KiB placeholder, and 8 KiB out of its middle at +16 KiB, both succeed.
- `lpAddress` must be page aligned (base+100 → 87) and `dwSize` must be a non-zero page multiple (100 → 87; 0 → 87).
- `dwSize` must be strictly smaller than the placeholder: `dwSize == whole placeholder` → 487; `dwSize > placeholder` → 87.
- `lpAddress` may be any page-aligned address inside the placeholder (not only its base).
- A split is atomic with respect to other threads: the pieces are still reserved, nobody can allocate into them.

**Coalesce** = `VirtualFree(base, totalSize, MEM_RELEASE | MEM_COALESCE_PLACEHOLDERS)`; `base`/`totalSize` must exactly cover a run of adjacent placeholders (all of them must currently be placeholders — if one piece is a mapped view or already free it fails with 487). [doc+verified]

**Release** = `VirtualFree(pieceBase, 0, MEM_RELEASE)`; `dwSize` must be 0 (else 87) and `lpAddress` must be the base of that piece (each split piece is its own allocation base, so pieces can be released individually, even while neighbouring pieces are mapped views). [verified]

## 3. Sections: create / open / transfer

### 3.1 `CreateFileMappingW` [doc]
```cpp
HANDLE CreateFileMappingW(
  [in]           HANDLE                hFile,                    // INVALID_HANDLE_VALUE ((HANDLE)-1) => pagefile-backed; then size is mandatory
  [in, optional] LPSECURITY_ATTRIBUTES lpFileMappingAttributes,  // NULL => not inheritable, default DACL from creator token
  [in]           DWORD                 flProtect,                // PAGE_READWRITE (0x04) [| SEC_COMMIT 0x08000000 | SEC_RESERVE 0x04000000 | SEC_LARGE_PAGES 0x80000000 | SEC_NOCACHE 0x10000000 | SEC_WRITECOMBINE 0x40000000]
  [in]           DWORD                 dwMaximumSizeHigh,        // (uint)(size >> 32)
  [in]           DWORD                 dwMaximumSizeLow,         // (uint)size
  [in, optional] LPCWSTR               lpName);                  // NULL = unnamed
```
- Min OS: XP. DLL: `kernel32.dll` (exported there, [verified]).
- `SEC_COMMIT` is the default when no `SEC_*` attribute is given; it commits the whole section at creation (commit charge = section size, failure `ERROR_COMMITMENT_LIMIT` 1455 / `ERROR_NOT_ENOUGH_MEMORY` 8 if the pagefile cannot back it). `SEC_RESERVE` instead reserves; pages are committed later by `VirtualAlloc(MEM_COMMIT)` on a view — and a commit done through one view is immediately visible as committed in the mirror view (prototype-PTE state is per section page). Once committed they cannot be decommitted with `VirtualFree`. [doc+verified] Use `SEC_RESERVE` if you want multi-GB rings whose commit grows on demand; otherwise `SEC_COMMIT`.
- Initial content of a pagefile-backed section is zero. [doc]
- Section sizes are not required to be 64 KiB multiples (a `64K+1` section maps as a `0x11000` view), but make the *data region* a 64 KiB multiple anyway (§10).
- **Existing name**: returns a handle to the existing object (with *its* size, not the size you passed — [verified]: second create with 256 KiB returned the existing 64 KiB object) and `GetLastError() == ERROR_ALREADY_EXISTS (183)`. This is the only way to tell "created" from "opened"; capture `Marshal.GetLastPInvokeError()` immediately after the call, even on success. [verified]
- Name clash with an event/mutex/semaphore/timer/job of the same name → `ERROR_INVALID_HANDLE` (6). [doc]
- `CreateFileMapping2` (memoryapi.h, min **Windows 10 build 20348** per docs; the 26100 SDK header guards it with `NTDDI_WIN10_RS5`) takes a 64-bit `MaximumSize`, a `DesiredAccess`, separate `PageProtection`/`AllocationAttributes` and extended parameters (NUMA). Exported from `kernelbase.dll` / `api-ms-win-core-memory-l1-1-7.dll`, **not** `kernel32.dll`. [verified] It works (a 4 GiB + 64 KiB pagefile section was created), but `CreateFileMappingW` is sufficient and has wider OS support; use `CreateFileMapping2` only if you want NUMA-node placement.

### 3.2 Naming: `Local\` vs `Global\` [doc+verified]
- No prefix → session-local namespace for interactive processes, global for services. `Local\` = explicit per-session, `Global\` = cross-session. Prefixes are case sensitive; the rest of the name may contain anything except `\`.
- Creating a file mapping in `Global\` from a non-zero session requires `SeCreateGlobalPrivilege` (held by admins/elevated processes and services; **not** by a normal interactive token — a non-elevated shell got `ERROR_ACCESS_DENIED` (5) [verified]). Opening an existing `Global\` object needs no privilege.
- Recommendation: default to `Local\photone.<name>`; only use `Global\` when talking to a service or across RDP/fast-user-switch sessions.
- **Object lifetime vs name lifetime [verified, important]**: the name is removed from the object namespace when the *last handle* is closed — even if views are still mapped. After the creator did `CloseHandle(section)` while its views were still mapped, `OpenFileMappingW` by name failed with `ERROR_FILE_NOT_FOUND` (2). The memory itself lives on until the last view is unmapped, but nobody can find it by name any more. Keep the section handle open in the creator for the buffer's whole lifetime (store it in the `RingBuffer` object; do not `CloseHandle` after mapping as the Microsoft sample does).
- When the last handle is closed **and** the last view is unmapped, the object is destroyed and its contents are gone; a later create with the same name yields a fresh zeroed section. [doc+verified]

### 3.3 `OpenFileMappingW` [doc]
```cpp
HANDLE OpenFileMappingW([in] DWORD dwDesiredAccess, [in] BOOL bInheritHandle, [in] LPCWSTR lpName);
```
Access rights (`winnt.h`): `FILE_MAP_COPY 0x0001`, `FILE_MAP_WRITE 0x0002 (= SECTION_MAP_WRITE)`, `FILE_MAP_READ 0x0004 (= SECTION_MAP_READ)`, `FILE_MAP_EXECUTE 0x0020`, `FILE_MAP_ALL_ACCESS 0x000F001F (= SECTION_ALL_ACCESS)`. Sections do **not** support `SYNCHRONIZE`. [doc]
- Writer/creator side: `FILE_MAP_ALL_ACCESS` or `FILE_MAP_READ | FILE_MAP_WRITE`.
- Readers must still be able to *write* the control block (their cursor, PID, wait flags), so readers also need `FILE_MAP_READ | FILE_MAP_WRITE` on the header view; for the data views a reader may open a second, read-only handle and map `PAGE_READONLY` (a `FILE_MAP_READ` handle + `PAGE_READONLY` view into a placeholder works [verified]; a `FILE_MAP_READ` handle + `PAGE_READWRITE` view → `ERROR_ACCESS_DENIED` (5) [verified]).
- Missing name → `ERROR_FILE_NOT_FOUND` (2). [verified]
- The handle is `CloseHandle`d when done; the object persists while any handle or view exists. [doc]

### 3.4 Handle transfer alternatives
- **DuplicateHandle by PID [verified]**: `OpenProcess(PROCESS_DUP_HANDLE (0x0040), FALSE, pid)` then `DuplicateHandle(GetCurrentProcess(), section, hTarget, &dup, 0, FALSE, DUPLICATE_SAME_ACCESS (0x2))`; `dup` is a small integer valid only in the target, send it over your control channel (pipe, stdin, header block). The child mapped the section through the duplicated value with no name at all. Works between 32- and 64-bit processes (handle is resized). Requires that the caller can open the target with `PROCESS_DUP_HANDLE` (same user, or `SeDebugPrivilege`; impossible against protected processes). `DUPLICATE_CLOSE_SOURCE = 0x1`.
- **Inheritance [verified]**: create the section with `SECURITY_ATTRIBUTES { nLength = 24 (x64), lpSecurityDescriptor = NULL, bInheritHandle = TRUE }`; the raw handle value is then identical in the child. `System.Diagnostics.Process.Start` passes `bInheritHandles = TRUE` on Windows, so the child received the exact value (0x2E4) without any extra work. Caveat: every inheritable handle leaks into every child you start, so prefer `DuplicateHandle` or names.
- **Remote mapping [verified]**: with a process handle having `PROCESS_VM_OPERATION (0x0008)`, the creator can itself call `VirtualAlloc2(hChild, …placeholder…)`, `VirtualFreeEx(hChild, …split…)`, and `MapViewOfFile3(section, hChild, …)` — placing the views *inside the other process* at an address of its choosing; the child later just `UnmapViewOfFile`s them. A process handle lacking `PROCESS_VM_OPERATION` gives `ERROR_ACCESS_DENIED` (5) from `MapViewOfFile3`. Useful if you want the creator to guarantee identical addresses in a child it spawns.

## 4. `MapViewOfFile3` with `MEM_REPLACE_PLACEHOLDER`

```cpp
PVOID MapViewOfFile3(
  [in]                HANDLE                 FileMapping,         // section handle (event handle → ERROR_INVALID_HANDLE 6 [verified])
  [in]                HANDLE                 Process,             // NULL works for the current process [verified] (docs mark it [in], the SDK sample passes nullptr); else needs PROCESS_VM_OPERATION
  [in, optional]      PVOID                  BaseAddress,         // with MEM_REPLACE_PLACEHOLDER: exact base of the placeholder piece
  [in]                ULONG64                Offset,              // byte offset into the section — 64-bit, no hi/lo split
  [in]                SIZE_T                 ViewSize,            // with MEM_REPLACE_PLACEHOLDER: exact placeholder size (0 is NOT accepted → 87 [verified])
  [in]                ULONG                  AllocationType,      // 0 | MEM_RESERVE 0x2000 | MEM_REPLACE_PLACEHOLDER 0x4000 | MEM_LARGE_PAGES 0x20000000
  [in]                ULONG                  PageProtection,      // PAGE_READWRITE / PAGE_READONLY (must be compatible with section protection and handle access)
  [in, out, optional] MEM_EXTENDED_PARAMETER *ExtendedParameters, // NULL
  [in]                ULONG                  ParameterCount);     // 0
```
Requirements [doc]: **min client Windows 10 version 1803 (RS4)**, header `memoryapi.h` (guarded `NTDDI_WIN10_RS4` in the 26100 SDK, together with `VirtualAlloc2`), lib `onecore.lib`, `api_location` lists `api-ms-win-core-memory-l1-1-6.dll` … `-9.dll`, `Kernel32.dll`, `onecore.lib`.

**Which DLL to import from in .NET [verified on 26200]**: `kernel32.dll` does **not** export `VirtualAlloc2`, `MapViewOfFile3`, `UnmapViewOfFile2`, `CreateFileMapping2` (nor `WaitOnAddress`); `kernelbase.dll` and the API set `api-ms-win-core-memory-l1-1-6.dll` do. Use `[LibraryImport("kernelbase.dll")]` (a real file, always present on Win10+, and what the API-set forwarders resolve to) or `"api-ms-win-core-memory-l1-1-6.dll"` (the documented contract name since 1803). `onecore.lib` is a C++ import library, meaningless for P/Invoke. `VirtualFree`, `UnmapViewOfFileEx`, `UnmapViewOfFile`, `CreateFileMappingW`, `OpenFileMappingW`, `VirtualFreeEx`, `GetSystemInfo`, `DuplicateHandle`, `OpenProcess`, events etc. are in `kernel32.dll` as usual.

Rules with `MEM_REPLACE_PLACEHOLDER`:
- `BaseAddress` and `ViewSize` must **exactly** match one placeholder piece [doc+verified]: 64 KiB view into a 128 KiB placeholder → 487; view at placeholder+64 KiB (not a piece base) → 487; view larger than the placeholder (192 KiB into 128 KiB) → 5; 4 KiB view into an 8 KiB piece → 487. `ViewSize` is rounded up to a page before the comparison (`64K-100` into a 64 KiB placeholder succeeded). A view can never span two placeholder pieces — split them so each view has its own piece of exactly the view size, or coalesce first.
- `Offset` must be page aligned (100 → `ERROR_MAPPED_ALIGNMENT` 1132) but need **not** be 64 KiB aligned: offset 4 KiB with a placeholder works, and so does a view whose base is page- but not 64 KiB-aligned (`…E2000`). [verified] Without `MEM_REPLACE_PLACEHOLDER`, offset must be a 64 KiB multiple (offset 4 KiB → 1132) [verified] and a non-64 KiB-aligned `BaseAddress` hint fails with 1132 instead of being "rounded down" as the docs say. [verified]
- Mapping at a placeholder address **without** the flag → 487; `MEM_REPLACE_PLACEHOLDER | MEM_RESERVE` → 87. [verified]
- Only data/pagefile-backed sections can replace placeholders (no `SEC_IMAGE`). [doc]
- `ViewSize` beyond the end of the section → `ERROR_ACCESS_DENIED` (5), not 87. [verified]
- Result region: `State = MEM_COMMIT`, `Type = MEM_MAPPED (0x40000)`, `Protect = PageProtection`. [verified]
- Costs [verified, 256 MiB data, 3 views]: create section + placeholder + 2 splits + 3 maps = **0.15 ms**; first touch of 256 MiB through view 1 = 264 ms (~4 µs/page soft fault); touching the same pages through the mirror = 192 ms (PTE fill only, no new physical pages); teardown = 66 ms. Pre-fault the data region once at startup if latency matters.

## 5. Unmapping and teardown

```cpp
BOOL UnmapViewOfFile  ([in] LPCVOID lpBaseAddress);                                   // kernel32, XP+
BOOL UnmapViewOfFileEx([in] PVOID BaseAddress, [in] ULONG UnmapFlags);                // kernel32, Win8+
BOOL UnmapViewOfFile2 ([in] HANDLE Process, [in] PVOID BaseAddress, [in] ULONG UnmapFlags); // kernelbase, Win10 1703+
```
`UnmapFlags`: `MEM_UNMAP_WITH_TRANSIENT_BOOST 0x1`, `MEM_PRESERVE_PLACEHOLDER 0x2`.

Observed semantics [verified]:
- **`UnmapViewOfFile2` requires a real process handle: `Process = NULL` → `ERROR_INVALID_HANDLE` (6)**; `GetCurrentProcess()` (pseudo handle `-1`) works. This is asymmetric with `MapViewOfFile3`, which accepts NULL. For the local process use `UnmapViewOfFileEx` (no process parameter) or pass `GetCurrentProcess()`.
- `MEM_PRESERVE_PLACEHOLDER` turns the view back into a placeholder (`MEM_RESERVE / MEM_PRIVATE`); you must then `VirtualFree(MEM_RELEASE)` it (or coalesce and release once).
- Plain `UnmapViewOfFile` / `…Ex(…, 0)` on a view that replaced a placeholder releases the VA outright (`MEM_FREE`); **no placeholder is left behind and nothing else needs freeing** — this does not leak.
- `VirtualFree(view, 0, MEM_RELEASE)` on a mapped view fails (87); `UnmapViewOfFile` on a bare placeholder fails (487). Track which pieces are views and which are still placeholders.
- `UnmapViewOfFile2` with an address *inside* the view (base+4 KiB) actually succeeded and unmapped the whole view; docs require the exact base — do not rely on this.
- Unmapping twice → 487.
- Releasing an untouched placeholder piece while adjacent pieces are still mapped views is fine.

Two correct teardown orders:

A) Simplest (recommended): `UnmapViewOfFile(hdr); UnmapViewOfFile(data); UnmapViewOfFile(mirror); CloseHandle(section);` — result: one `MEM_FREE` region, nothing leaked. On a partial-failure path, `VirtualFree(piece, 0, MEM_RELEASE)` every piece that is still a placeholder and `UnmapViewOfFile` every piece that became a view.

B) Reuse the reservation: `UnmapViewOfFileEx(x, MEM_PRESERVE_PLACEHOLDER)` ×3 → `VirtualFree(base, total, MEM_RELEASE | MEM_COALESCE_PLACEHOLDERS)` → either re-split and re-map (e.g. resize in place) or `VirtualFree(base, 0, MEM_RELEASE)`.

Process exit: the kernel unmaps all views, closes all handles and frees all placeholders of the dying process; children in the tests exited without unmapping anything and the parent's views/section were unaffected. The section survives as long as any *other* process still has a handle or a view. Dirty pages of a pagefile-backed section are never "written to a file"; they simply persist in the section until it is destroyed. [doc+verified]

## 6. `[header 64 KiB][data N][data mirror N]` in one placeholder — exact sequence [verified]

Constraints: `N` is a page multiple (use a 64 KiB multiple, §10); the section is `64K + N` bytes; the VA reservation is `64K + 2N`. The 64 KiB header is a separate view of section offset 0; both data views map section offset 64 KiB.

```
G = dwAllocationGranularity (0x10000); N = data bytes

1. section = CreateFileMappingW(INVALID_HANDLE_VALUE, NULL/inheritable-SA, PAGE_READWRITE [| SEC_RESERVE], hi(G+N), lo(G+N), L"Local\\photone.<name>")
   - check GetLastError()==ERROR_ALREADY_EXISTS to know whether you created or opened
2. base = VirtualAlloc2(NULL, requestedOrNULL, G + 2N, MEM_RESERVE | MEM_RESERVE_PLACEHOLDER, PAGE_NOACCESS, NULL, 0)
3. VirtualFree(base,       G, MEM_RELEASE | MEM_PRESERVE_PLACEHOLDER)   // -> [base, base+G) and [base+G, base+G+2N)
4. VirtualFree(base + G,   N, MEM_RELEASE | MEM_PRESERVE_PLACEHOLDER)   // -> [base+G, base+G+N) and [base+G+N, base+G+2N)
5. hdr    = MapViewOfFile3(section, NULL, base,         0, G, MEM_REPLACE_PLACEHOLDER, PAGE_READWRITE, NULL, 0)
6. data   = MapViewOfFile3(section, NULL, base + G,     G, N, MEM_REPLACE_PLACEHOLDER, PAGE_READWRITE, NULL, 0)
7. mirror = MapViewOfFile3(section, NULL, base + G + N, G, N, MEM_REPLACE_PLACEHOLDER, PAGE_READWRITE, NULL, 0)
   invariants: mirror == data + N; data[i] and mirror[i] alias; hdr is disjoint from both
8. keep `section` open for the lifetime of the buffer (§3.2)
Failure unwinding: for each of the three pieces, UnmapViewOfFile if it is a view, else VirtualFree(piece, 0, MEM_RELEASE); CloseHandle(section) (only the creator's handle keeps the name alive).
```
Verified results: a 256 KiB pattern written through `data` reads back identically through `mirror`; a write through `mirror[N-1]` is visible at `data[N-1]`; a `Span<byte>(data + N - 8, 16).Fill()` wraps into `data[0..8)`; the header stays untouched. Page-granular variants ([4 KiB][4 KiB mirror]) also work, so a header smaller than 64 KiB is possible, but keep 64 KiB so the *data* offset stays 64 KiB-aligned (needed for the non-placeholder fallback map and for any legacy `MapViewOfFileEx` consumer).

## 7. Cross-process mapping at the creator's address

Feasible and verified end to end. The reader process:

```
1. section = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, name)      (or a DuplicateHandle'd / inherited handle)
2. base = VirtualAlloc2(NULL, creatorBase, G + 2N, MEM_RESERVE | MEM_RESERVE_PLACEHOLDER, PAGE_NOACCESS, NULL, 0)
   - success ⇒ identical addresses in both processes (child got 0x2AD7F5E0000 == parent) [verified]
   - NULL + ERROR_INVALID_ADDRESS (487) ⇒ something (a DLL, heap, GC segment, thread stack…) already occupies part of that range in this process
3. fallback: base = VirtualAlloc2(NULL, NULL, G + 2N, …)  → different address, same memory [verified: child got 0x1CE130A0000 with all contents intact]
4. split + 3× MapViewOfFile3 exactly as in §6 (offsets are section-relative, so identical in both processes)
```
Notes:
- The placeholder must be created first only if you want atomicity: `MapViewOfFile3(…, BaseAddress = fixed, AllocationType = 0)` (i.e. `MapViewOfFileEx` semantics) also works at a fixed free address without any placeholder — three fixed-address maps produced the same adjacent layout [verified] — but they are three independent operations, so another thread can allocate in between and you end up with a partial layout to unwind. One placeholder covering `G + 2N` either succeeds or fails as a unit. Always use the placeholder.
- "Requested address is taken" is signalled *only* by 487 from step 2. Do not try to split or map into a range you did not obtain.
- Where the creator's address is chosen: `VirtualAlloc2(NULL, NULL, …)` returns something in the process's normal bottom-up region (~`0x1B4_xxxx_xxxx` for this .NET process), which is precisely where other processes' heaps also live. To make same-address success likely, have the creator ask for a 64 KiB-aligned address in a sparsely used part of the 128 TiB user space (e.g. `MEM_ADDRESS_REQUIREMENTS { LowestStartingAddress = 0x4000_0000_0000, HighestEndingAddress = 0x4FFF_FFFF_FFFF }` or simply try a fixed `0x4000_0000_0000 + hash(name) * 64K`, falling back to NULL). Readers then retry that exact address. This is a heuristic, not a guarantee — the design must never *depend* on equal addresses (store offsets, not pointers, in shared memory; treat equal addresses as an optimisation).
- Alternative with a guarantee for child processes you spawn: map remotely into the child from the creator (§3.4) before the child starts running its own code.
- Store the creator's `base`, `N`, page/granularity, and a layout version in the header; readers validate them before mapping.
- Mixed bitness: a 32-bit reader can share the section but never the address; .NET 10 is 64-bit on x64, so just assert `Environment.Is64BitProcess`.

## 8. Error handling

Observed `GetLastError()` values [verified unless noted]:

| Code | Name | Where it was observed |
|---|---|---|
| 2 | `ERROR_FILE_NOT_FOUND` | `OpenFileMappingW` on a missing name; on a name whose last handle was closed |
| 5 | `ERROR_ACCESS_DENIED` | `CreateFileMappingW("Global\\…")` without `SeCreateGlobalPrivilege`; `MapViewOfFile3` `PAGE_READWRITE` through a `FILE_MAP_READ` handle; view extends past section end; `MapViewOfFile3` with a process handle lacking `PROCESS_VM_OPERATION` |
| 6 | `ERROR_INVALID_HANDLE` | `UnmapViewOfFile2(Process = NULL)`; `MapViewOfFile3` with a non-section handle; [doc] name clash with another object type |
| 8 / 1455 | `ERROR_NOT_ENOUGH_MEMORY` / `ERROR_COMMITMENT_LIMIT` | [doc] `SEC_COMMIT` section too large for the commit limit; VA exhaustion |
| 87 | `ERROR_INVALID_PARAMETER` | bad flag combos (`MEM_RESERVE_PLACEHOLDER` without `MEM_RESERVE`, `+MEM_COMMIT`, `PAGE_READWRITE` placeholder, `MEM_REPLACE_PLACEHOLDER|MEM_RESERVE`); `ViewSize = 0` with `MEM_REPLACE_PLACEHOLDER`; `Size = 0`; split with `dwSize = 0`, non-page `dwSize`, unaligned `lpAddress`, `dwSize` > placeholder; `MEM_RELEASE` with `dwSize != 0`; `VirtualFree(MEM_RELEASE)` on a mapped view |
| 183 | `ERROR_ALREADY_EXISTS` | `CreateFileMappingW` / `CreateEventW` returned a handle to an existing object (success!) |
| 487 | `ERROR_INVALID_ADDRESS` | placeholder requested at an occupied or non-64 KiB-aligned address; view size/base not exactly one placeholder piece; map at a placeholder without `MEM_REPLACE_PLACEHOLDER`; split with `dwSize` == whole placeholder; coalesce over a non-placeholder piece; `MEM_DECOMMIT` on a placeholder; `UnmapViewOfFile` on a bare placeholder; unmapping twice |
| 1132 | `ERROR_MAPPED_ALIGNMENT` | `Offset` not page aligned (with placeholder) / not 64 KiB aligned (without); non-64 KiB `BaseAddress` hint without placeholder |
| 1460 | `ERROR_TIMEOUT` | `WaitOnAddress` timed out |

Created-vs-opened detection: `handle != null && Marshal.GetLastPInvokeError() == 183` ⇒ opened existing (then validate header magic/version/size; the object has *its* size, not yours). If you must be the creator, call `OpenFileMappingW` first and treat success as "already exists", or create with a fresh unique name. In .NET always read the error via `Marshal.GetLastPInvokeError()` (or `GetLastWin32Error()`) right after a `SetLastError = true` P/Invoke; both succeed-with-183 and failure cases need it.

## 9. C# P/Invoke declarations (compiled and exercised with .NET 10, C# 14, `AllowUnsafeBlocks`)

```csharp
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.Win32.SafeHandles;

public sealed class SafeSectionHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeSectionHandle() : base(ownsHandle: true) { }                 // required by the LibraryImport generator for return values
    public SafeSectionHandle(nint h, bool owns) : base(owns) { SetHandle(h); } // wrap a DuplicateHandle'd / inherited value
    protected override bool ReleaseHandle() => Native.CloseHandle(handle);
}

/// winnt.h: typedef struct DECLSPEC_ALIGN(8) MEM_EXTENDED_PARAMETER {
///   struct { DWORD64 Type : 8; DWORD64 Reserved : 56; };     // one 64-bit unit; Type = low 8 bits (little-endian bitfield on a DWORD64 base)
///   union  { DWORD64 ULong64; PVOID Pointer; SIZE_T Size; HANDLE Handle; DWORD ULong; };
/// };  sizeof == 16 on x64 and x86 (align 8).  MEM_EXTENDED_PARAMETER_TYPE_BITS == 8.
[StructLayout(LayoutKind.Sequential)]
public struct MEM_EXTENDED_PARAMETER
{
    public ulong TypeAndReserved;   // bits 0..7 = MEM_EXTENDED_PARAMETER_TYPE, bits 8..63 must be 0
    public ulong Value;             // the union; for Pointer store (ulong)(nuint)ptr, for ULong store the 32-bit value (upper 32 bits 0)
    public MEM_EXTENDED_PARAMETER_TYPE Type
    {
        get => (MEM_EXTENDED_PARAMETER_TYPE)(TypeAndReserved & 0xFF);
        set => TypeAndReserved = (TypeAndReserved & ~0xFFUL) | ((ulong)value & 0xFF);
    }
}

public enum MEM_EXTENDED_PARAMETER_TYPE : ulong           // winnt.h enum order
{
    MemExtendedParameterInvalidType = 0,
    MemExtendedParameterAddressRequirements = 1,           // Value = pointer to MEM_ADDRESS_REQUIREMENTS
    MemExtendedParameterNumaNode = 2,                      // Value = node number
    MemExtendedParameterPartitionHandle = 3,
    MemExtendedParameterUserPhysicalHandle = 4,
    MemExtendedParameterAttributeFlags = 5,                // Value = MEM_EXTENDED_PARAMETER_NONPAGED 0x2 | _NONPAGED_LARGE 0x8 | _NONPAGED_HUGE 0x10 | _EC_CODE 0x40 ...
    MemExtendedParameterImageMachine = 6,
    MemExtendedParameterMax = 7,
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct MEM_ADDRESS_REQUIREMENTS
{
    public void* LowestStartingAddress;   // multiple of allocation granularity or NULL
    public void* HighestEndingAddress;    // (multiple of granularity) - 1, inclusive, or NULL
    public nuint Alignment;               // 0 or power of two >= allocation granularity
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct SYSTEM_INFO
{
    public ushort wProcessorArchitecture;  // PROCESSOR_ARCHITECTURE_AMD64 = 9, ARM64 = 12, INTEL = 0
    public ushort wReserved;
    public uint dwPageSize;
    public void* lpMinimumApplicationAddress;
    public void* lpMaximumApplicationAddress;
    public nuint dwActiveProcessorMask;
    public uint dwNumberOfProcessors;
    public uint dwProcessorType;
    public uint dwAllocationGranularity;
    public ushort wProcessorLevel;
    public ushort wProcessorRevision;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct MEMORY_BASIC_INFORMATION   // for diagnostics / unwinding (48 bytes on x64)
{
    public void* BaseAddress; public void* AllocationBase; public uint AllocationProtect; public ushort PartitionId;
    public nuint RegionSize; public uint State; public uint Protect; public uint Type;
}

[StructLayout(LayoutKind.Sequential)]
public struct SECURITY_ATTRIBUTES { public uint nLength; public nint lpSecurityDescriptor; public int bInheritHandle; } // nLength = sizeof (24 on x64)

public static unsafe partial class Native
{
    // ---- constants (winnt.h, SDK 10.0.26100) ----
    public const uint MEM_COMMIT = 0x00001000, MEM_RESERVE = 0x00002000, MEM_DECOMMIT = 0x00004000, MEM_RELEASE = 0x00008000,
        MEM_FREE = 0x00010000, MEM_PRIVATE = 0x00020000, MEM_MAPPED = 0x00040000,
        MEM_REPLACE_PLACEHOLDER = 0x00004000, MEM_RESERVE_PLACEHOLDER = 0x00040000,
        MEM_COALESCE_PLACEHOLDERS = 0x00000001, MEM_PRESERVE_PLACEHOLDER = 0x00000002,
        MEM_UNMAP_WITH_TRANSIENT_BOOST = 0x00000001, MEM_TOP_DOWN = 0x00100000, MEM_LARGE_PAGES = 0x20000000;
    public const uint PAGE_NOACCESS = 0x01, PAGE_READONLY = 0x02, PAGE_READWRITE = 0x04, PAGE_WRITECOPY = 0x08;
    public const uint SEC_COMMIT = 0x08000000, SEC_RESERVE = 0x04000000, SEC_LARGE_PAGES = 0x80000000, SEC_NOCACHE = 0x10000000;
    public const uint FILE_MAP_COPY = 0x0001, FILE_MAP_WRITE = 0x0002, FILE_MAP_READ = 0x0004, FILE_MAP_EXECUTE = 0x0020, FILE_MAP_ALL_ACCESS = 0x000F001F;
    public const uint DUPLICATE_CLOSE_SOURCE = 0x1, DUPLICATE_SAME_ACCESS = 0x2;
    public const uint PROCESS_TERMINATE = 0x0001, PROCESS_VM_OPERATION = 0x0008, PROCESS_DUP_HANDLE = 0x0040,
        PROCESS_QUERY_INFORMATION = 0x0400, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, SYNCHRONIZE = 0x00100000;
    public const uint EVENT_MODIFY_STATE = 0x0002, EVENT_ALL_ACCESS = 0x001F0003;
    public const uint WAIT_OBJECT_0 = 0, WAIT_ABANDONED = 0x80, WAIT_TIMEOUT = 0x102, WAIT_FAILED = 0xFFFFFFFF, INFINITE = 0xFFFFFFFF;
    public const int ERROR_FILE_NOT_FOUND = 2, ERROR_ACCESS_DENIED = 5, ERROR_INVALID_HANDLE = 6, ERROR_NOT_ENOUGH_MEMORY = 8,
        ERROR_INVALID_PARAMETER = 87, ERROR_ALREADY_EXISTS = 183, ERROR_INVALID_ADDRESS = 487, ERROR_MAPPED_ALIGNMENT = 1132,
        ERROR_COMMITMENT_LIMIT = 1455, ERROR_TIMEOUT = 1460;
    public static readonly nint INVALID_HANDLE_VALUE = -1;

    const string KernelBase = "kernelbase.dll";   // or "api-ms-win-core-memory-l1-1-6.dll"; kernel32.dll does NOT export these four
    const string Kernel32 = "kernel32.dll";

    // ---- placeholders / views (kernelbase) ----
    [LibraryImport(KernelBase, SetLastError = true)]
    public static partial void* VirtualAlloc2(nint Process, void* BaseAddress, nuint Size, uint AllocationType, uint PageProtection,
                                              MEM_EXTENDED_PARAMETER* ExtendedParameters, uint ParameterCount);

    [LibraryImport(KernelBase, SetLastError = true)]
    public static partial void* MapViewOfFile3(SafeHandle FileMapping, nint Process, void* BaseAddress, ulong Offset, nuint ViewSize,
                                               uint AllocationType, uint PageProtection, MEM_EXTENDED_PARAMETER* ExtendedParameters, uint ParameterCount);

    [LibraryImport(KernelBase, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnmapViewOfFile2(nint Process, void* BaseAddress, uint UnmapFlags);   // Process must be GetCurrentProcess(), not 0

    [LibraryImport(KernelBase, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeSectionHandle CreateFileMapping2(nint File, SECURITY_ATTRIBUTES* SecurityAttributes, uint DesiredAccess, uint PageProtection,
                                                               uint AllocationAttributes, ulong MaximumSize, string? Name,
                                                               MEM_EXTENDED_PARAMETER* ExtendedParameters, uint ParameterCount);

    // ---- classic memory APIs (kernel32) ----
    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool VirtualFree(void* lpAddress, nuint dwSize, uint dwFreeType);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool VirtualFreeEx(nint hProcess, void* lpAddress, nuint dwSize, uint dwFreeType);

    [LibraryImport(Kernel32, SetLastError = true)]
    public static partial nuint VirtualQuery(void* lpAddress, MEMORY_BASIC_INFORMATION* lpBuffer, nuint dwLength);

    [LibraryImport(Kernel32, EntryPoint = "CreateFileMappingW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeSectionHandle CreateFileMapping(nint hFile, SECURITY_ATTRIBUTES* lpFileMappingAttributes, uint flProtect,
                                                              uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string? lpName);

    [LibraryImport(Kernel32, EntryPoint = "OpenFileMappingW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeSectionHandle OpenFileMapping(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, string lpName);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnmapViewOfFileEx(void* BaseAddress, uint UnmapFlags);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnmapViewOfFile(void* lpBaseAddress);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint hObject);

    [LibraryImport(Kernel32)]
    public static partial void GetSystemInfo(SYSTEM_INFO* lpSystemInfo);   // dwAllocationGranularity / dwPageSize; or Environment.SystemPageSize for the page

    [LibraryImport(Kernel32)]
    public static partial nint GetCurrentProcess();                        // pseudo handle (-1); never CloseHandle it

    [LibraryImport(Kernel32)]
    public static partial uint GetCurrentProcessId();

    // ---- processes / handles ----
    [LibraryImport(Kernel32, SetLastError = true)]
    public static partial SafeProcessHandle OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DuplicateHandle(nint hSourceProcessHandle, nint hSourceHandle, nint hTargetProcessHandle, out nint lpTargetHandle,
                                               uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwOptions);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(SafeHandle hProcess, out uint lpExitCode);   // STILL_ACTIVE = 259

    // ---- events / waits ----
    [LibraryImport(Kernel32, EntryPoint = "CreateEventW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeWaitHandle CreateEvent(SECURITY_ATTRIBUTES* lpEventAttributes, [MarshalAs(UnmanagedType.Bool)] bool bManualReset,
                                                     [MarshalAs(UnmanagedType.Bool)] bool bInitialState, string? lpName);

    [LibraryImport(Kernel32, EntryPoint = "OpenEventW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeWaitHandle OpenEvent(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, string lpName);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetEvent(SafeHandle hEvent);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ResetEvent(SafeHandle hEvent);

    [LibraryImport(Kernel32, SetLastError = true)]
    public static partial uint WaitForSingleObject(SafeHandle hHandle, uint dwMilliseconds);

    // ---- futex-style waits (kernelbase; SAME-PROCESS ONLY, see pitfalls) ----
    [LibraryImport(KernelBase, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WaitOnAddress(void* Address, void* CompareAddress, nuint AddressSize, uint dwMilliseconds);
    [LibraryImport(KernelBase)] public static partial void WakeByAddressSingle(void* Address);
    [LibraryImport(KernelBase)] public static partial void WakeByAddressAll(void* Address);
}
```
.NET notes: `[LibraryImport]` needs the method `static partial` in a `partial` class, `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`, and a `SafeHandle` return type with a public parameterless constructor; `SafeHandle` parameters are `DangerousAddRef`/`Release`d by the generated stub (fine for setup paths; none of these calls sit on the hot path). `StringMarshalling.Utf16` + the explicit `…W` `EntryPoint` avoids the A/W lookup. Read errors with `Marshal.GetLastPInvokeError()`. `SafeProcessHandle`/`SafeWaitHandle` come from `Microsoft.Win32.SafeHandles`; `SafeWaitHandle` plugs directly into `new EventWaitHandle(false, EventResetMode.AutoReset) { SafeWaitHandle = h }` so readers can `await` via `ThreadPool.RegisterWaitForSingleObject` or `WaitHandle.WaitOneAsync`-style helpers. `SECURITY_ATTRIBUTES*` may be `null`.

## 10. Pitfalls (each with the reason)

1. Import `VirtualAlloc2` / `MapViewOfFile3` / `UnmapViewOfFile2` / `CreateFileMapping2` / `WaitOnAddress` from `kernelbase.dll` (or the api-ms-win-core-memory-l1-1-6 API set) — `kernel32.dll` has no such exports and the stub throws `EntryPointNotFoundException` at first call. [verified]
2. `UnmapViewOfFile2(Process = NULL)` fails with 6 while `MapViewOfFile3(Process = NULL)` succeeds — pass `GetCurrentProcess()` or use `UnmapViewOfFileEx`. [verified]
3. `ViewSize` and `BaseAddress` must equal one placeholder piece exactly; `ViewSize = 0` is rejected (87) and a view can never straddle two pieces (487). [verified]
4. Placeholder `BaseAddress` must be 64 KiB aligned (487 otherwise), even though placeholder *splits* and *sizes* are page granular. [verified]
5. Keep the data region a multiple of 64 KiB: the section offset of the data views then stays 64 KiB aligned, which the no-placeholder fallback and every legacy `MapViewOfFile(Ex)` consumer require (1132 otherwise). [verified]
6. `CreateFileMappingW` succeeds with 183 when the name exists and hands you the *existing* object with *its* size — check `GetLastPInvokeError()` on success and validate the header. [verified]
7. Names die with the last handle, not the last view: closing the creator's section handle makes `OpenFileMappingW` fail with 2 for every later reader even though the memory is still mapped. Keep the handle open. [verified]
8. The section (and all data) is destroyed when the last handle *and* last view go away; a late reader gets a brand-new zeroed section under the same name — put a magic + generation counter in the header. [verified]
9. `Global\` creation needs `SeCreateGlobalPrivilege` (5 without it); default to `Local\`. [verified]
10. `LARGE_INTEGER` splitting: `CreateFileMappingW` wants `(uint)(size >> 32), (uint)size`; `MapViewOfFile3.Offset` is a plain `ULONG64`; old `MapViewOfFile` wants `FileOffsetHigh/Low` — mixing them up silently maps the wrong region.
11. `SEC_COMMIT` charges the whole section against the commit limit at creation; double mapping does *not* double the charge (both views share the same section pages) but does double the VA. Use `SEC_RESERVE` + `VirtualAlloc2(MEM_COMMIT)` for very large rings; a commit through either view commits the shared page for both. [verified]
12. First touch costs ~4 µs/page (264 ms for 256 MiB) — pre-fault at startup, not on the hot path. [verified]
13. `WaitOnAddress`/`WakeByAddress*` are per-process: a waiter in process B blocked on a shared-memory address did not wake when process A changed the value and called `WakeByAddressAll`; it timed out (1460). Cross-process wakeups need a kernel object (named/duplicated event, or a per-reader event stored as a name/handle in the header), with spinning first and a "waiter present" flag so the writer skips `SetEvent` when nobody sleeps. [verified]
14. Plain `UnmapViewOfFile` on a placeholder-backed view frees the VA outright (no placeholder left) — do not `VirtualFree` it afterwards (87); conversely, a piece that is still a placeholder must be `VirtualFree(…,0,MEM_RELEASE)`d, never unmapped (487). Track piece state during unwinding. [verified]
15. `VirtualFree` splits need page-aligned `lpAddress`, non-zero page-multiple `dwSize` strictly smaller than the piece (87/487 otherwise). [verified]
16. `MapViewOfFile3` without `MEM_REPLACE_PLACEHOLDER` at a placeholder address → 487; adding `MEM_RESERVE` to `MEM_REPLACE_PLACEHOLDER` → 87. [verified]
17. Same-address mapping in another process is best effort: 487 from `VirtualAlloc2(requestedBase)` means "taken", fall back to `NULL`. Never store raw pointers in shared memory; store offsets from the data base. [verified]
18. `Process.Start` on Windows inherits every inheritable handle into every child; prefer names or `DuplicateHandle` over inheritable `SECURITY_ATTRIBUTES` unless you control all spawns. [verified]
19. A `FILE_MAP_READ` handle cannot map `PAGE_READWRITE` (5); readers still need write access for the control block, so open `FILE_MAP_READ | FILE_MAP_WRITE` (optionally a second read-only handle for the data views). [verified]
20. Wow64 / 32-bit: irrelevant for .NET on x64, but a 32-bit peer can share the section, never the address, and cannot double-map large rings (2–4 GiB VA). Assert `Environment.Is64BitProcess`.
21. ARM64 Windows also uses 4 KiB pages and 64 KiB allocation granularity, but never hardcode either — read `SYSTEM_INFO` (`Environment.SystemPageSize` gives only the page). `MapViewOfFile3`'s doc explicitly ties the placeholder offset rule to "the underlying page size granted by VirtualAlloc2".
22. Large pages (`SEC_LARGE_PAGES` + `MEM_LARGE_PAGES`) need `SeLockMemoryPrivilege`, 2 MiB-multiple sizes/offsets/alignment and a 2 MiB-aligned placeholder (`MEM_ADDRESS_REQUIREMENTS.Alignment`); leave for later.
23. Dead-reader detection: store each reader's PID (and optionally process start time) in the header; the writer does `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, …)` + `WaitForSingleObject(h, 0) == WAIT_OBJECT_0` (or `GetExitCodeProcess != 259`) to detect exit — PIDs are recycled, so compare start time (`GetProcessTimes`) or keep the process handle open from registration time. `OpenProcess` fails with 87 for PID 0 and 5 for protected/system processes.
24. `MEM_REPLACE_PLACEHOLDER` (0x4000) and `MEM_DECOMMIT` (0x4000) share a value — they are for different functions; do not merge flag enums across `VirtualAlloc2`/`VirtualFree`.
25. `MEM_EXTENDED_PARAMETER` is a bit-field over a `DWORD64`: `Type` occupies the *low* 8 bits of the first qword (MSVC allocates bit-fields from bit 0); writing the type into a separate byte field with `LayoutKind.Explicit` offset 0 would also work on little-endian, but keep the 16-byte two-qword layout so the union half is 8-aligned. [verified sizeof 16]

## 11. Microsoft's ring-buffer sample (verbatim) and its C# translation

The Microsoft Learn `VirtualAlloc2` page currently labels this **"Scenario 1. Create a circular buffer by mapping two adjacent views of the same shared memory section."** (the NUMA example is Scenario 2, address-range/alignment is Scenario 3). Reproduced verbatim:

```cpp
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>

//
// This function creates a ring buffer by allocating a pagefile-backed section
// and mapping two views of that section next to each other. This way if the
// last record in the buffer wraps it can still be accessed in a linear fashion
// using its base VA.
//

void*
CreateRingBuffer (
    unsigned int bufferSize,
    _Outptr_ void** secondaryView
    )
{
    BOOL result;
    HANDLE section = nullptr;
    SYSTEM_INFO sysInfo;
    void* ringBuffer = nullptr;
    void* placeholder1 = nullptr;
    void* placeholder2 = nullptr;
    void* view1 = nullptr;
    void* view2 = nullptr;

    GetSystemInfo (&sysInfo);

    if ((bufferSize % sysInfo.dwAllocationGranularity) != 0) {
        return nullptr;
    }

    //
    // Reserve a placeholder region where the buffer will be mapped.
    //

    placeholder1 = (PCHAR) VirtualAlloc2 (
        nullptr,
        nullptr,
        2 * bufferSize,
        MEM_RESERVE | MEM_RESERVE_PLACEHOLDER,
        PAGE_NOACCESS,
        nullptr, 0
    );

    if (placeholder1 == nullptr) {
        printf ("VirtualAlloc2 failed, error %#x\n", GetLastError());
        goto Exit;
    }

    //
    // Split the placeholder region into two regions of equal size.
    //

    result = VirtualFree (
        placeholder1,
        bufferSize,
        MEM_RELEASE | MEM_PRESERVE_PLACEHOLDER
    );

    if (result == FALSE) {
        printf ("VirtualFreeEx failed, error %#x\n", GetLastError());
        goto Exit;
    }

    placeholder2 = (void*) ((ULONG_PTR) placeholder1 + bufferSize);

    //
    // Create a pagefile-backed section for the buffer.
    //

    section = CreateFileMapping (
        INVALID_HANDLE_VALUE,
        nullptr,
        PAGE_READWRITE,
        0,
        bufferSize, nullptr
    );

    if (section == nullptr) {
        printf ("CreateFileMapping failed, error %#x\n", GetLastError());
        goto Exit;
    }

    //
    // Map the section into the first placeholder region.
    //

    view1 = MapViewOfFile3 (
        section,
        nullptr,
        placeholder1,
        0,
        bufferSize,
        MEM_REPLACE_PLACEHOLDER,
        PAGE_READWRITE,
        nullptr, 0
    );

    if (view1 == nullptr) {
        printf ("MapViewOfFile3 failed, error %#x\n", GetLastError());
        goto Exit;
    }

    //
    // Ownership transferred, don't free this now.
    //

    placeholder1 = nullptr;

    //
    // Map the section into the second placeholder region.
    //

    view2 = MapViewOfFile3 (
        section,
        nullptr,
        placeholder2,
        0,
        bufferSize,
        MEM_REPLACE_PLACEHOLDER,
        PAGE_READWRITE,
        nullptr, 0
    );

    if (view2 == nullptr) {
        printf ("MapViewOfFile3 failed, error %#x\n", GetLastError());
        goto Exit;
    }

    //
    // Success, return both mapped views to the caller.
    //

    ringBuffer = view1;
    *secondaryView = view2;

    placeholder2 = nullptr;
    view1 = nullptr;
    view2 = nullptr;

Exit:

    if (section != nullptr) {
        CloseHandle (section);
    }

    if (placeholder1 != nullptr) {
        VirtualFree (placeholder1, 0, MEM_RELEASE);
    }

    if (placeholder2 != nullptr) {
        VirtualFree (placeholder2, 0, MEM_RELEASE);
    }

    if (view1 != nullptr) {
        UnmapViewOfFileEx (view1, 0);
    }

    if (view2 != nullptr) {
        UnmapViewOfFileEx (view2, 0);
    }

    return ringBuffer;
}

int __cdecl wmain()
{
    char* ringBuffer;
    void* secondaryView;
    unsigned int bufferSize = 0x10000;

    ringBuffer = (char*) CreateRingBuffer (bufferSize, &secondaryView);

    if (ringBuffer == nullptr) {
        printf ("CreateRingBuffer failed\n");
        return 0;
    }

    //
    // Make sure the buffer wraps properly.
    //

    ringBuffer[0] = 'a';

    if (ringBuffer[bufferSize] == 'a') {
        printf ("The buffer wraps as expected\n");
    }

    UnmapViewOfFile (ringBuffer);
    UnmapViewOfFile (secondaryView);
}
```

C# translation, extended to the `[header][data][mirror]` layout, named section, created-vs-opened detection, same-address attempt and correct unwinding (all calls are the ones verified above):

```csharp
using System.Runtime.InteropServices;
using static Native;

public sealed unsafe class MirroredSection : IDisposable
{
    public byte* Header { get; private set; }     // 64 KiB control block, section offset 0
    public byte* Data { get; private set; }       // N bytes, section offset 64 KiB
    public byte* Mirror => Data + DataSize;       // same N bytes again, contiguous after Data
    public nuint DataSize { get; }
    public bool CreatedNew { get; }
    public bool AtRequestedAddress { get; }
    private SafeSectionHandle _section;           // keep open: the name lives only while a handle exists
    private byte* _base; private nuint _total;
    private static readonly SYSTEM_INFO Si = GetSi();
    private static SYSTEM_INFO GetSi() { SYSTEM_INFO s; GetSystemInfo(&s); return s; }
    public static nuint Granularity => Si.dwAllocationGranularity;
    public static nuint PageSize => Si.dwPageSize;

    public MirroredSection(string name, nuint dataSize, void* requestedBase = null, bool openOnly = false)
    {
        nuint g = Granularity;
        if (dataSize == 0 || dataSize % g != 0) throw new ArgumentException("data size must be a non-zero multiple of the allocation granularity");
        DataSize = dataSize; _total = g + 2 * dataSize;
        ulong sectionSize = (ulong)(g + dataSize);

        // 1. section (create-or-open, or open-only)
        if (openOnly)
        {
            _section = OpenFileMapping(FILE_MAP_READ | FILE_MAP_WRITE, false, name);
            if (_section.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "OpenFileMappingW");
            CreatedNew = false;
        }
        else
        {
            _section = CreateFileMapping(INVALID_HANDLE_VALUE, null, PAGE_READWRITE, (uint)(sectionSize >> 32), (uint)sectionSize, name);
            int err = Marshal.GetLastPInvokeError();               // must be read even on success
            if (_section.IsInvalid) throw new System.ComponentModel.Win32Exception(err, "CreateFileMappingW");
            CreatedNew = err != ERROR_ALREADY_EXISTS;
        }

        // 2. one placeholder for [hdr][data][mirror]; try the creator's address first, then anywhere
        byte* b = null;
        if (requestedBase != null)
        {
            b = (byte*)VirtualAlloc2(0, requestedBase, _total, MEM_RESERVE | MEM_RESERVE_PLACEHOLDER, PAGE_NOACCESS, null, 0);
            AtRequestedAddress = b != null;                          // NULL + ERROR_INVALID_ADDRESS (487) => range occupied here
        }
        if (b == null)
            b = (byte*)VirtualAlloc2(0, null, _total, MEM_RESERVE | MEM_RESERVE_PLACEHOLDER, PAGE_NOACCESS, null, 0);
        if (b == null) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "VirtualAlloc2");
        _base = b;

        // pieces: 0 = header placeholder/view, 1 = data, 2 = mirror; track state for unwinding
        bool[] isView = new bool[3];
        try
        {
            // 3./4. split into three placeholders
            if (!VirtualFree(b, g, MEM_RELEASE | MEM_PRESERVE_PLACEHOLDER)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "VirtualFree(split 1)");
            if (!VirtualFree(b + g, dataSize, MEM_RELEASE | MEM_PRESERVE_PLACEHOLDER)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "VirtualFree(split 2)");

            // 5./6./7. replace each placeholder with a view
            Header = Map(b, 0, g); isView[0] = true;
            Data = Map(b + g, g, dataSize); isView[1] = true;
            byte* mirror = Map(b + g + dataSize, g, dataSize); isView[2] = true;
            System.Diagnostics.Debug.Assert(mirror == Data + dataSize);
        }
        catch
        {
            // unwind: views are unmapped (VA becomes free), untouched placeholders are released
            byte*[] pieces = { b, b + g, b + g + dataSize };
            for (int i = 0; i < 3; i++)
            {
                if (isView[i]) UnmapViewOfFile(pieces[i]);
                else VirtualFree(pieces[i], 0, MEM_RELEASE);     // ok even if the piece was never split off (releases the whole remaining placeholder)
            }
            _section.Dispose();
            throw;
        }
    }

    private byte* Map(byte* at, ulong offset, nuint size)
    {
        void* v = MapViewOfFile3(_section, 0, at, offset, size, MEM_REPLACE_PLACEHOLDER, PAGE_READWRITE, null, 0);
        if (v == null) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "MapViewOfFile3");
        return (byte*)v;
    }

    public void Dispose()
    {
        if (_base == null) return;
        // plain unmaps free the VA completely (no placeholders remain); order does not matter
        UnmapViewOfFile(Header); UnmapViewOfFile(Data); UnmapViewOfFile(Data + DataSize);
        Header = null; Data = null; _base = null;
        _section.Dispose();   // last handle: name disappears; memory dies with the last view/handle anywhere
    }
}
```

Usage pattern that mirrors the desired `RingBuffer<T>` API: the creator constructs `new MirroredSection("Local\\photone.ring", N)` and writes `Header` (magic, layout version, `N`, creator base address, cursors, reader table); a reader constructs `new MirroredSection(name, N, requestedBase: creatorBase, openOnly: true)` after reading the parameters from a tiny bootstrap (or from a first 64 KiB `MapViewOfFile3(section, 0, null, 0, 64K, 0, …)` peek). `Span<T>` views: `new Span<T>((T*)(Data + byteOffset), count)` may extend up to `N` bytes past `Data + N` without any copy.

## Sources (Microsoft Learn, fetched 2026-09-16)

- VirtualAlloc2 (incl. verbatim ring-buffer sample): https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-virtualalloc2
- MapViewOfFile3: https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-mapviewoffile3
- VirtualFree (MEM_PRESERVE_PLACEHOLDER / MEM_COALESCE_PLACEHOLDERS): https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-virtualfree
- UnmapViewOfFile2: https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-unmapviewoffile2
- UnmapViewOfFileEx: https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-unmapviewoffileex
- UnmapViewOfFile: https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-unmapviewoffile
- MEM_EXTENDED_PARAMETER: https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-mem_extended_parameter
- MEM_EXTENDED_PARAMETER_TYPE: https://learn.microsoft.com/en-us/windows/win32/api/winnt/ne-winnt-mem_extended_parameter_type
- MEM_ADDRESS_REQUIREMENTS: https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-mem_address_requirements
- CreateFileMappingW: https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-createfilemappingw
- CreateFileMapping2: https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-createfilemapping2
- OpenFileMappingW: https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-openfilemappingw
- File mapping security and access rights: https://learn.microsoft.com/en-us/windows/win32/memory/file-mapping-security-and-access-rights
- Kernel object namespaces (Global\ / Local\): https://learn.microsoft.com/en-us/windows/win32/termserv/kernel-object-namespaces
- Memory protection constants: https://learn.microsoft.com/en-us/windows/win32/memory/memory-protection-constants
- SYSTEM_INFO: https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/ns-sysinfoapi-system_info
- DuplicateHandle: https://learn.microsoft.com/en-us/windows/win32/api/handleapi/nf-handleapi-duplicatehandle
- OpenProcess: https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-openprocess
- Process security and access rights: https://learn.microsoft.com/en-us/windows/win32/procthread/process-security-and-access-rights
- CreateEventW: https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-createeventw
- OpenEventW: https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-openeventw
- WaitOnAddress: https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-waitonaddress
- .NET P/Invoke source generation (LibraryImport): https://learn.microsoft.com/en-us/dotnet/standard/native-interop/pinvoke-source-generation
- Constant values cross-checked against Windows SDK 10.0.26100 `um\winnt.h`, `um\memoryapi.h`, `shared\sdkddkver.h`; DLL exports checked with `NativeLibrary.TryGetExport` on Windows 11 10.0.26200.

---

# REPORT: research:sync-protocol

# photone-ipc — Cross-process SPMC mirrored ring buffer: synchronization protocol reference

Scope: one writer process/thread, N ≤ 64 independent readers (broadcast, per-reader cursor), writer gated by the slowest reader, .NET 10 on Windows x64 (ARM64 kept correct), data region double-mapped with `VirtualAlloc2` placeholders + `MapViewOfFile3`.

Terminology used throughout:

| Symbol | Meaning |
|---|---|
| `C` | capacity in elements (power of two) |
| `s` | `sizeof(T)` (`T : unmanaged`) |
| `D` | data bytes = `C * s`, multiple of 64 KiB |
| `W` | published write cursor (64-bit, elements, monotone) — shared |
| `E` | writer's reserve end = `W + size of outstanding bucket` — shared (informational) and writer-local (authoritative) |
| `R_i` | read cursor of reader slot `i` (64-bit, elements, monotone) — shared |
| `M` | writer's cached minimum over active `R_i` — writer-local |

All cursors are absolute element counts since creation. They are never reduced to ring offsets except at the moment a span is formed: `offset = (cursor & (C-1)) * s`.

---

## 1. Shared control block layout

### 1.1 Section layout

```
section offset      size          contents
0                   4096          ControlBlock (header lines + reader slots)   ← page 0
4096 .. 65535       —             unused (padding so data starts at a 64 KiB boundary)
65536               D             data
```

Section size = `65536 + D`. Views in every process:

```
placeholder reservation:  [base, base + 65536 + 2*D)     (VirtualAlloc2 MEM_RESERVE|MEM_RESERVE_PLACEHOLDER)
split → three placeholders (VirtualFree MEM_RELEASE|MEM_PRESERVE_PLACEHOLDER twice)
view 0: base            ← section [0, 65536)          (control block)
view 1: base + 65536    ← section [65536, 65536+D)    (data)
view 2: base + 65536+D  ← section [65536, 65536+D)    (data, mirror)
```

`MapViewOfFile3` requires offsets/sizes that are multiples of the allocation granularity (64 KiB), which is why the control block gets its own 64 KiB and `D` must be a 64 KiB multiple (see §8). The creator writes its `base` into the header; a joining process first tries `VirtualAlloc2(lpAddress: base, ...)`, and on `ERROR_INVALID_ADDRESS` falls back to `lpAddress: null`. Nothing in the protocol depends on the address matching — every shared field is an offset or a cursor.

### 1.2 ControlBlock (explicit layout, 64-byte lines)

```csharp
[StructLayout(LayoutKind.Explicit, Size = 4096)]
internal struct ControlBlock
{
    // ---- line 0: immutable identity (written once by the creator before publishing the name)
    [FieldOffset(0)]   public ulong Magic;          // 'PHOTONE1'
    [FieldOffset(8)]   public uint  Version;        // layout version; open() rejects mismatches
    [FieldOffset(12)]  public uint  HeaderSize;     // 4096
    [FieldOffset(16)]  public uint  ElementSize;    // s; open<T>() rejects if sizeof(T) != s
    [FieldOffset(20)]  public uint  MaxReaders;     // N ≤ 64
    [FieldOffset(24)]  public long  Capacity;       // C (power of two)
    [FieldOffset(32)]  public long  DataBytes;      // D
    [FieldOffset(40)]  public long  DataOffset;     // 65536
    [FieldOffset(48)]  public int   TypeHash;       // optional: hash of typeof(T).FullName for sanity
    [FieldOffset(52)]  public int   InitState;      // 0 = initializing, 1 = ready (Volatile.Write last)

    // ---- line 1: creator info (immutable)
    [FieldOffset(64)]  public ulong CreatorBase;    // VA of the placeholder base in the creator
    [FieldOffset(72)]  public int   CreatorPid;
    [FieldOffset(80)]  public long  CreatorStartTime; // FILETIME of process creation (PID-reuse guard)

    // ---- line 2: THE hot line. Written only by the writer (Commit), read by every reader.
    [FieldOffset(128)] public long  WriteCursor;    // W

    // ---- line 3: writer bookkeeping. Written by writer on every GetBucket/Commit; readers rarely read it.
    [FieldOffset(192)] public long  ReserveEnd;     // E (informational: dead-writer recovery, "oldest" join)
    [FieldOffset(200)] public int   WriterState;    // 0 none, 1 active, 2 closed (EOF)
    [FieldOffset(204)] public int   WriterPid;
    [FieldOffset(208)] public long  WriterStartTime;

    // ---- line 4: writer-waiting flag. Written rarely (writer blocks), read by readers on every Advance.
    //      Kept away from line 3 so per-bucket ReserveEnd stores do not invalidate readers' copies.
    [FieldOffset(256)] public int   WriterWaiting;  // 0/1

    // ---- line 5: reader registry. Written by readers on join/leave/wait (rare), read by writer on every Commit.
    [FieldOffset(320)] public ulong WaitersMask;    // bit i set ⇒ reader i is (about to be) blocked in the kernel
    [FieldOffset(328)] public ulong ActiveMask;     // bit i set ⇒ slot i is Active (scan hint; slot state is authoritative)
    [FieldOffset(336)] public uint  ReaderGeneration; // incremented on every join/leave/evict

    // ---- line 6..7: reserved / stats

    // ---- lines 8..(8+N): reader slots, 64 B each, at 512 + 64*i   (N=32 → ends at 2560; N=64 → 4608 ⇒ HeaderSize 8192)
}

[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct ReaderSlot
{
    [FieldOffset(0)]   public long  StateSeq;       // (Sequence << 32) | State  — CAS'd as ONE 64-bit word (ABA-proof)
    [FieldOffset(8)]   public long  ReadCursor;     // R_i, written by the owning reader (Advance), read by writer
    [FieldOffset(16)]  public long  WaitFor;        // absolute cursor the reader is waiting to see W reach
    [FieldOffset(24)]  public int   Pid;
    [FieldOffset(32)]  public long  ProcessStartTime;
    [FieldOffset(40)]  public int   Flags;          // reserved (e.g. RewindRequested)
}

internal static class SlotState { public const int Free = 0, Claimed = 1, Active = 2, Dead = 3; }
```

Validate at startup with `Debug.Assert(Unsafe.SizeOf<ControlBlock>() == 4096)` and offset checks; never use auto layout for anything that lives in the section.

### 1.3 Why this partitioning (false sharing)

A cache line is the unit of coherence. When core A stores to a line, every other core's copy is invalidated and their next load is a ~50–100 ns cross-core miss. The rule: **each line has at most one writer, and lines written frequently by one party are not read on the fast path by another party unless that read *is* the communication.**

- `WriteCursor` alone on line 2: readers poll it; the writer stores it once per Commit. That miss is the message itself. Nothing else the writer touches per bucket (`ReserveEnd`) shares the line, so readers spinning on `W` are not disturbed by reservations.
- `WriterWaiting` on its own line: every reader loads it on every `Advance`. It changes only when the writer blocks. If it shared a line with `ReserveEnd`, every `GetBucket` would invalidate every reader's copy.
- `WaitersMask`/`ActiveMask`/`ReaderGeneration` on line 5: the writer loads `WaitersMask` on every Commit (one load, line stays Shared in its L1 for as long as no reader blocks). Readers write it only on the slow path.
- One slot per line: reader `i`'s `Advance` stores to its own line only; the writer's min-scan touches N lines but only on its slow path (§2.5). Two readers never write the same line.
- Immutable lines 0–1 are read once at open and cached in managed fields.

Writer-local and reader-local state (`M`, local `E`, outstanding-bucket flag, cached event/process handles) live in ordinary managed objects, not in the section.

---

## 2. Memory ordering in .NET

### 2.1 Primitives

| Primitive | Semantics | x64 codegen | ARM64 codegen |
|---|---|---|---|
| plain read/write | none; JIT may hoist out of loops, reorder, cache in registers | mov | ldr/str |
| `Volatile.Read` | acquire load: later loads/stores cannot move before it | mov + compiler barrier | `ldar`/`ldapr` |
| `Volatile.Write` | release store: earlier loads/stores cannot move after it | mov + compiler barrier | `stlr` |
| `Interlocked.*` (Exchange, CompareExchange, Increment, Or, And) | atomic RMW **plus full fence** (StoreLoad included) | `lock xchg/cmpxchg/...` | LSE `casal`/`ldaxr-stlxr` + `dmb ish` |
| `Interlocked.MemoryBarrier()` | full fence | `lock or [rsp],0` (mfence-equivalent) | `dmb ish` |
| `Volatile.ReadBarrier()` / `WriteBarrier()` (.NET 9+) | acquire-only / release-only fence — **not** StoreLoad | nop | `dmb ishld` / `dmb ish` |

Cross-process shared memory is ordinary cache-coherent memory; the model is identical to in-process. Naturally aligned 8-byte loads/stores are single-copy atomic on both x64 and ARM64 (all cursor fields are 8-aligned, so no tearing). For unmanaged memory use `Volatile.Read(ref Unsafe.AsRef<long>(p))` / `Interlocked.CompareExchange(ref Unsafe.AsRef<long>(p), …)`; the section is never moved by the GC so this is safe.

x64 is TSO: the only reordering the hardware does is **store → later load** (store buffer). ARM64 is weakly ordered: loads and stores reorder freely unless `ldar/stlr/dmb` say otherwise. Writing to the acquire/release contract makes the code correct on both; the full fence is needed in exactly the places that rely on StoreLoad ordering (Dekker/Peterson-style "publish, then check").

### 2.2 Per-operation requirements

**GetBucket(n) (writer):**
```
free = C - (E_local - M)                          // all writer-local, no barriers
if (free < n) { M = ScanMin(); free = ...; if (free < n) → wait (§3.4) }
E_local += n;  Volatile.Write(ref hdr.ReserveEnd, E_local)   // release, informational
return span over data at ((E_local - n) & (C-1)) * s, length n*s
```
`ScanMin()` uses acquire loads (§2.5). The data region is then filled with **plain** stores — no ordering is needed on them individually.

**Commit(k) (writer):**
```
Volatile.Write(ref hdr.WriteCursor, W + k);       // release: all plain data stores happen-before W becomes visible
Interlocked.MemoryBarrier();                      // StoreLoad: see §3.3 — required for the waiter protocol
if (Volatile.Read(ref hdr.WaitersMask) != 0) SignalWaiters(newW);
```
The single full fence also serves the reader-join argument in §5.2 (it orders the `W` store before the next `ScanMin` loads).

**TryRead(n) (reader):**
```
w = Volatile.Read(ref hdr.WriteCursor);           // acquire: data loads after this see everything published ≤ w
avail = w - R_local
if (avail < n) return false
span over ((R_local) & (C-1)) * s, length n*s     // plain loads of data
```
Cache `w` in the reader: only re-load `W` when `avail` is insufficient (Disruptor-style). Note `R_local == hdr.slot.ReadCursor` always; the reader owns it.

**Advance(k) (reader):**
```
R_local += k
Volatile.Write(ref slot.ReadCursor, R_local);     // release: every data load of the consumed region is ordered before the cursor store
Interlocked.MemoryBarrier();                      // StoreLoad: required for the writer-waiting protocol (§3.4)
if (Volatile.Read(ref hdr.WriterWaiting) != 0) SignalWriter();
```
Release ordering of prior *loads* matters on ARM64: without it the CPU could still be reading data the writer is now free to overwrite. On x64 LoadStore reordering never happens, but the `Volatile.Write` is still needed as a compiler barrier. Cheaper equivalent to the pair: `Interlocked.Exchange(ref slot.ReadCursor, R_local)` (one locked instruction ≈ 15–25 cycles, includes both the release and the full fence).

**ScanMin() (writer):**
```
min = long.MaxValue
foreach set bit i in Volatile.Read(ref hdr.ActiveMask)   // hint only
    ss = Volatile.Read(ref slot[i].StateSeq)             // acquire: if Active, the cursor store that preceded Active is visible
    if (State(ss) != Active) continue
    r = Volatile.Read(ref slot[i].ReadCursor)
    min = Math.Min(min, r)
if (min == long.MaxValue) min = E_local  (no readers: everything is free)
```
Reading a cursor that is concurrently advancing is harmless — any value read is ≤ the true current value, so the writer only ever under-estimates free space. Reading a slot that is concurrently being released is harmless for the same reason. The scan must also include slots whose bit is not in `ActiveMask` but whose state is Active? No: the join protocol (§5.2) sets the slot Active *then* sets the mask bit, both with full fences, and the writer's subsequent scan after its own fence sees at least one of them; to keep the argument simple and cheap, scan **all N slots** (N cache lines, ~1 µs worst case, slow path only) and use `ActiveMask` only as a fast "is anybody there" check. This removes the mask from the correctness argument entirely.

### 2.3 Where a full fence is required and why

The pattern "I store my flag, then I check your condition; you store your condition, then you check my flag" (Dekker) requires that **at least one** party observes the other's store. Under acquire/release alone, both stores can sit in store buffers while both loads read stale values — on x64 this is exactly the one reordering TSO permits, and on ARM64 it is trivially permitted. Both parties need a StoreLoad fence between their store and their load. In .NET that means `Interlocked.*` on the store (cheapest; the RMW *is* the fence) or `Volatile.Write` + `Interlocked.MemoryBarrier()`.

Four places:

| Party | Store | Fence | Load |
|---|---|---|---|
| Reader about to block | `Interlocked.Or(ref WaitersMask, bit)` (after plain-storing `WaitFor`) | included | `Volatile.Read(W)` |
| Writer Commit | `Volatile.Write(W)` | `Interlocked.MemoryBarrier()` | `Volatile.Read(WaitersMask)` |
| Writer about to block | `Interlocked.Exchange(ref WriterWaiting, 1)` | included | `ScanMin()` |
| Reader Advance | `Volatile.Write(R_i)` | `Interlocked.MemoryBarrier()` (or `Interlocked.Exchange`) | `Volatile.Read(WriterWaiting)` |

Plus the join protocol (§5.2): reader `Interlocked.Exchange(StateSeq → Active)` then `Volatile.Read(W)`; writer `Volatile.Write(W)` + fence (already in Commit) then scan.

Cost: ~20–40 cycles per fence on x64, ~same on ARM64. One per Commit and one per Advance is negligible relative to a 4 KB memcpy (~100 ns).

Never use `Volatile.ReadBarrier/WriteBarrier` for these — they are one-directional fences and do not order a store before a later load.

### 2.4 Things that are NOT needed

- No fence on the data stores/loads themselves.
- No `Interlocked.MemoryBarrierProcessWide` (that is `FlushProcessWriteBuffers`, ~microseconds, meant for lock-free code that avoids fences on the fast path entirely; not applicable).
- No `volatile` on the managed mirror fields (`R_local`, `E_local`, `M`) — they are single-thread owned.
- No fences in `TryRead`/`GetBucket` when the cached values suffice — this is what makes the hot path a handful of instructions.

### 2.5 Cached-minimum invariant

`M` is a value `ScanMin()` returned at some earlier time. Every active reader's cursor is monotone, and readers that joined since started at a cursor ≥ the `W` they observed ≥ `M` (proved in §5.2). Therefore `M ≤ true_min` always, and `C - (E - M) ≤ true_free`. Using stale `M` is safe; it is refreshed only when it is not enough. On a warm pipeline with fast readers, `ScanMin` runs roughly once per `C/n` buckets.

---

## 3. Wait strategy

### 3.1 Spin phase

Order of escalation for both directions (reader waiting for data, writer waiting for space):

1. **Busy spin with pause**, time-bounded. `X86Base.Pause()` on x64 (`pause`: ~40 cycles pre-Skylake, ~140 on Skylake+), `ArmBase.Yield()` on ARM64; `Thread.SpinWait(1)` is the portable wrapper (normalized since .NET Core 3.0 so one iteration is roughly the same wall time across CPUs). Do **not** count iterations; measure with `Stopwatch.GetTimestamp()` (RDTSC-backed, ~20 ns) every ~16 iterations and stop after `spinMicros` (default 20–50 µs for latency-sensitive use; 0 for throughput-only / oversubscribed boxes). The loop body is a `Volatile.Read` of `W` (or a `ScanMin` for the writer, backed off: scan every ~1 µs, not every iteration, so N lines are not hammered).
2. **`Thread.Yield()`** a few times (≈ `SwitchToThread`, gives up the core to a ready thread on the same processor only; ~1 µs when nothing to run). Bounded, e.g. 10–20 times.
3. **Kernel wait** (§3.2).

Do not use the BCL `SpinWait` struct's default escalation: it ends in `Thread.Sleep(1)`, which is ≥1 ms (≥15.6 ms unless the timer resolution has been raised) and destroys tail latency. Use it at most for the pause/yield bookkeeping, or write the loop by hand.

Spinning on a line the writer stores to every commit costs one cross-core miss per commit per spinning reader — that is the same traffic a wakeup would cause, so it is not a throughput concern. It *is* a CPU-usage concern (a spinning reader burns a core); make `spinMicros` a per-reader option.

### 3.2 Kernel primitive: named auto-reset Event per reader slot + one Event for the writer

Objects (created by the creator with `CreateEventW`, opened by others with `OpenEventW` or a second `CreateEventW`, all in the `Local\` namespace, e.g. `Local\photone.{name}.r{i}` and `Local\photone.{name}.space`):

- **One auto-reset event per reader slot.** Auto-reset: a `SetEvent` wakes exactly one waiter and resets; there is at most one waiter (the slot owner), so no "wake all" semantics are needed, and a stale set is consumed by the next wait as a harmless spurious wakeup.
- **One auto-reset event for the writer** ("space").

Rejected alternatives:
- *One shared auto-reset event for all readers*: wakes one reader only — broadcast broken.
- *One shared manual-reset event*: needs Reset by someone, which races with late waiters (lost wakeup) unless paired with a generation counter that readers compare after wake; `PulseEvent` is documented as unreliable. Workable (eventcount pattern) but strictly more complex than per-slot events, and it wakes every blocked reader on every commit even if they wanted more data.
- *Semaphore with `ReleaseSemaphore(waiters)`*: works for broadcast but leaks counts on timeouts; no benefit.
- *`WaitOnAddress` / `WakeByAddressAll`*: **process-local.** The implementation keeps a per-process hash table of waiting threads keyed by address (in `ntdll`, `RtlWaitOnAddress`), and `WakeByAddress*` only looks in the caller's table; a writer in process A cannot wake a reader in process B even though the physical page is shared. Usable only for readers living in the writer's own process; not worth a second code path.
- *Keyed events (`NtCreateKeyedEvent`/`NtWaitForKeyedEvent`/`NtReleaseKeyedEvent`)*: named, cross-process, futex-like (key = any pointer-sized value, e.g. slot address), zero per-waiter kernel object. Undocumented; `NtReleaseKeyedEvent` blocks until a waiter exists (needs timeout 0 to be non-blocking). Interesting but not supportable; events are within ~1 µs of it anyway.
- *Linux futex equivalent*: there is none on Windows for cross-process; events are the primitive.

Costs (typical, Windows 11, modern x64): `SetEvent` when nobody waits ≈ 0.3–0.7 µs (pure syscall); when a waiter must be readied ≈ 1–3 µs on the caller (includes scheduler work and possibly an IPI); waiter observes the wake 5–20 µs after `SetEvent` on an idle core, worse (50–150 µs) if the target core was in a deep C-state. This is why the writer must not pay a `SetEvent` per commit, and why spinning is on by default for latency-sensitive readers.

Handles: the writer opens each slot's event lazily on first signal and caches it in an array indexed by slot (event objects are named and persistent while any handle is open; the creator keeps all of them open). Each reader opens its own slot event once at claim. Also open the peer's **process handle** (`OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, pid)`) and put it into the wait set — see §5.

### 3.3 Reader-side protocol and lost-wakeup analysis

```
Wait(n):
  target = R_local + n
  if (Volatile.Read(W) >= target) return                 // fast path, no barriers beyond acquire
  spin/yield phase ... (return as soon as W >= target)
  loop:
    slot.WaitFor = target;                                // plain store, ordered by the following RMW
    Interlocked.Or(ref hdr.WaitersMask, 1UL << i);        // publish "I will block"  [full fence]
    if (Volatile.Read(W) >= target) {                     // re-check AFTER the fence
        Interlocked.And(ref hdr.WaitersMask, ~bit);       // withdraw (the writer may still signal: harmless)
        return;
    }
    rc = WaitForMultipleObjects([myEvent, writerProcess], any, timeoutSlice)
    if (rc == writerProcess signaled) → writer dead → EOF/throw (after draining what W already covers)
    if (rc == WAIT_TIMEOUT) { Interlocked.And(mask, ~bit); check liveness; if (elapsed>=timeout) return false; }
    // event signaled or timeout: loop (re-check condition; event may have been a stale/spurious set)
```

```
Commit(k):
  Volatile.Write(ref W, newW)
  Interlocked.MemoryBarrier()
  mask = Volatile.Read(ref hdr.WaitersMask)
  if (mask == 0) return                                   // 99.x% of commits: zero syscalls
  foreach bit i in mask:
      if (Volatile.Read(ref slot[i].WaitFor) <= newW) {   // only wake readers whose request is satisfied
          Interlocked.And(ref hdr.WaitersMask, ~bit);     // consume the flag (prevents repeated SetEvent per commit)
          SetEvent(readerEvent[i]);
      }
```

State machine for one reader/writer pair, tracking (flag ∈ {0,1}, condition ∈ {unsat, sat}, reader ∈ {running, blocked}, event ∈ {reset, set}):

- Reader does `flag=1; fence; load W`. Writer does `store W; fence; load flag`. Both fences are StoreLoad. By the Dekker property, **it is impossible that the reader loads the old `W` and the writer loads `flag=0`.** Either the reader sees the new `W` and returns without blocking, or the writer sees `flag=1` and calls `SetEvent`.
- If the writer signals after the reader already returned (both saw each other): the event becomes *set* with nobody waiting; the reader's next `WaitForSingleObject` returns immediately → spurious wake → re-check → re-wait. Bounded: one spurious wake per such race.
- If the writer signals *before* the reader reaches `WaitForSingleObject` (reader re-checked, saw old `W`, got preempted): the event is *set*; the reader's wait returns immediately with the condition now true. No loss.
- Reader timeout while the writer is between `And` and `SetEvent`: event ends up set; next wait spurious. No loss.
- Writer commits twice quickly: first commit consumes the flag and signals; second commit sees mask=0 and does nothing; the reader wakes once and re-checks against the newest `W`. Correct.
- `WaitFor` filter: if `WaitFor > newW` the writer does not clear the bit and does not signal; the flag stays set and the next commit re-evaluates. The reader remains blocked exactly until a commit reaches `WaitFor`, which is the requested semantics. Dekker still holds per commit because each commit does store-fence-load.
- Writer close: `WriterState=Closed` (Volatile.Write), fence, then `SetEvent` on **every** slot event regardless of masks (cheap, once), so readers observe EOF.

Spurious wakeups are always tolerated by re-checking the condition in a loop; nothing assumes "woken ⇒ data available".

### 3.4 Writer waiting for space (reverse direction)

```
WaitForSpace(n):
  spin: M = ScanMin() (rate-limited); if (C - (E_local - M) >= n) return
  loop:
    Interlocked.Exchange(ref hdr.WriterWaiting, 1)       // publish  [full fence]
    M = ScanMin()                                         // re-check AFTER the fence
    if (C - (E_local - M) >= n) { Volatile.Write(ref hdr.WriterWaiting, 0); return; }
    rc = WaitForMultipleObjects([spaceEvent, readerProcess handles of the readers with R_i == M ...], any, 100ms)
    Volatile.Write(ref hdr.WriterWaiting, 0)  — or leave it and let readers consume it (see below)
    if (rc is a process handle or WAIT_TIMEOUT) RunEviction()   // §5.3
```

Reader `Advance` (already shown): `Volatile.Write(R_i)`; full fence; `if (Volatile.Read(WriterWaiting) != 0)` → `if (Interlocked.Exchange(ref hdr.WriterWaiting, 0) == 1) SetEvent(spaceEvent)`. The `Exchange` makes exactly one reader pay the syscall per writer sleep; others see 0. Same Dekker argument with roles swapped. The reader's per-Advance cost when the writer is not waiting is one acquire load of a line that stays Shared in its cache (~1 ns) plus the fence (~10 ns).

Optionally the writer only sets `WriterWaiting` when it needs to block, never in the spin phase, so fast readers never see it set.

---

## 4. Async wait: `ValueTask<bool> WaitAsync(int n, TimeSpan timeout, CancellationToken ct)`

### 4.1 Options

| Option | Allocation | Wake path | Verdict |
|---|---|---|---|
| `ThreadPool.RegisterWaitForSingleObject(event, cb)` per wait | `RegisteredWaitHandle` + callback state (~200 B), plus `TaskCompletionSource` | BCL wait thread (`WaitForMultipleObjects` on ≤63 handles) → thread pool → continuation | Fine for cold paths; allocs and two hops |
| Dedicated waiter thread **per reader** blocking in `WaitForSingleObject` | none after setup | kernel → that thread → completes VTS → thread pool | One OS thread per reader; wasteful with many readers |
| **Per-process wait dispatcher thread** multiplexing pending async waits with `WaitForMultipleObjectsEx`, per-reader pooled `ManualResetValueTaskSourceCore<bool>` | zero on steady state | kernel → dispatcher → `SetResult` → thread pool continuation | **Recommended** |
| `Task.Run(() => WaitForSingleObject(...))` | Task + closure; blocks a pool thread | — | No |
| IOCP-based (`NtAssociateWaitCompletionPacket`) | zero | kernel → IOCP thread | Best latency in theory; undocumented API, skip |

A reader has **at most one outstanding wait** (sync or async) by contract, so a single reusable `ManualResetValueTaskSourceCore<bool>` embedded in the `Reader` object is sufficient: the `ValueTask` returned is `(this, core.Version)`, awaited once. That gives zero allocations per wait. Do not mix a sync `Wait` and an async `WaitAsync` concurrently on the same reader — they would both wait on the same auto-reset event and only one would be woken.

### 4.2 Code shape

```csharp
public sealed partial class RingReader<T> : IValueTaskSource<bool>
{
    private ManualResetValueTaskSourceCore<bool> _vts;      // RunContinuationsAsynchronously = true (set once)
    private CancellationTokenRegistration _ctr;
    private long _deadlineTicks;                            // Stopwatch ticks; long.MaxValue = infinite
    private long _asyncTarget;
    private int  _asyncPending;                             // 0/1, guards single-outstanding-wait

    public ValueTask<bool> WaitAsync(int n, TimeSpan timeout = default, CancellationToken ct = default)
    {
        if ((uint)n > (uint)_capacity) ThrowArgument();
        long target = _cursor + n;
        if (Volatile.Read(ref Hdr.WriteCursor) >= target) return new ValueTask<bool>(true);   // hot path
        if (_spinMicrosAsync > 0 && SpinUntil(target, _spinMicrosAsync)) return new ValueTask<bool>(true);
        if (ct.IsCancellationRequested) return ValueTask.FromCanceled<bool>(ct);

        if (Interlocked.Exchange(ref _asyncPending, 1) != 0) ThrowConcurrentWait();
        _vts.Reset();
        _asyncTarget = target;
        _deadlineTicks = timeout == Timeout.InfiniteTimeSpan || timeout == default
                         ? long.MaxValue : Stopwatch.GetTimestamp() + ToTicks(timeout);

        // publish waiting flag, then re-check (Dekker, §3.3)
        Slot.WaitFor = target;
        Interlocked.Or(ref Hdr.WaitersMask, _bit);
        if (Volatile.Read(ref Hdr.WriteCursor) >= target)
        {
            Interlocked.And(ref Hdr.WaitersMask, ~_bit);
            _asyncPending = 0;
            return new ValueTask<bool>(true);
        }
        if (ct.CanBeCanceled)
            _ctr = ct.UnsafeRegister(static (s, tok) => ((RingReader<T>)s!).OnCancel(tok), this);

        WaitDispatcher.Instance.Register(this);             // enqueue + SetEvent(control) — the only syscall
        return new ValueTask<bool>(this, _vts.Version);
    }

    // IValueTaskSource<bool>
    bool IValueTaskSource<bool>.GetResult(short token) { var r = _vts.GetResult(token); return r; }
    ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _vts.GetStatus(token);
    void IValueTaskSource<bool>.OnCompleted(Action<object?> c, object? s, short token, ValueTaskSourceOnCompletedFlags f)
        => _vts.OnCompleted(c, s, token, f);

    // called by the dispatcher thread only
    internal void CompleteFromDispatcher(WakeReason reason)
    {
        _ctr.Dispose(); _ctr = default;
        Interlocked.And(ref Hdr.WaitersMask, ~_bit);        // cheap even if writer already cleared it
        _asyncPending = 0;                                  // before SetResult: continuation may call WaitAsync again
        switch (reason)
        {
            case WakeReason.Signaled:   _vts.SetResult(true);  break;   // caller re-checks TryRead; spurious wake ⇒ TryRead false
            case WakeReason.Timeout:    _vts.SetResult(false); break;
            case WakeReason.Canceled:   _vts.SetException(new OperationCanceledException(_ctr.Token)); break;
            case WakeReason.WriterDead: _vts.SetException(new WriterTerminatedException()); break;
        }
    }
    private void OnCancel(CancellationToken tok) => WaitDispatcher.Instance.RequestCancel(this);
}
```

Dispatcher (one per process, lazily started, `IsBackground = true`, optionally `ThreadPriority.AboveNormal`):

```csharp
internal sealed class WaitDispatcher
{
    private readonly SafeWaitHandle _control;                // auto-reset, poked by Register/RequestCancel
    private readonly ConcurrentQueue<Command> _cmds;         // (Register | Cancel, reader) — pooled nodes or a lock-free ring
    private readonly List<RingReader> _pending;              // ≤ 62 per thread; spawn another dispatcher thread beyond that
    private readonly IntPtr[] _handles;                      // [0]=control, [1]=writerProcess (per buffer; see note), [2..]=slot events

    private void Loop()
    {
        while (true)
        {
            DrainCommands();                                 // add/remove readers, complete cancellations
            int count = BuildHandleArray();                  // handles[0]=control, then event of each pending reader
            uint timeoutMs = ComputeNearestDeadlineMs();     // INFINITE if none
            uint rc = WaitForMultipleObjectsEx(count, _handles, bWaitAll: false, timeoutMs, bAlertable: false);
            if (rc == WAIT_TIMEOUT) { CompleteExpired(WakeReason.Timeout); continue; }
            int idx = (int)(rc - WAIT_OBJECT_0);
            if (idx == 0) continue;                          // control: loop → DrainCommands
            var r = _pending[idx - 1];
            _pending.RemoveAt(idx - 1);
            r.CompleteFromDispatcher(r.WriterIsDead ? WakeReason.WriterDead : WakeReason.Signaled);
        }
    }
}
```

Notes:
- The continuation runs on the thread pool (`RunContinuationsAsynchronously = true`), never on the dispatcher thread, so a slow consumer cannot stall other readers' wakeups. Cost: ~1–5 µs extra hop. Users who need the last microseconds use sync `Wait` with spinning.
- Timeouts: computed from `Stopwatch.GetTimestamp()`, never `Environment.TickCount` (32-bit wrap). The dispatcher's `WaitForMultipleObjectsEx` timeout is the nearest deadline; expired readers get `SetResult(false)`.
- Cancellation: the registration callback only enqueues a command and pokes the control event; the dispatcher completes the VTS. This keeps the "complete exactly once, from one thread" invariant trivial. `CancellationTokenRegistration.Dispose()` is called before completion (it is safe to call from the dispatcher; the callback, if it races, finds the reader no longer pending and is a no-op).
- Writer death: each buffer's writer process handle is included in the dispatcher's handle array (one per distinct buffer with pending waiters); when it signals, all pending readers of that buffer complete with `WriterDead` after `WriterState` shows the writer did not close cleanly (a clean close sets `Closed` and signals all events itself).
- More than 62 concurrent async waiters in one process: the dispatcher spawns a second thread (each with its own control event). Rare in practice.
- With >64 total handles per `WaitForMultipleObjects`, or if process handles are unavailable (access denied), fall back to a 100 ms slice timeout and PID/start-time liveness polling.

---

## 5. Reader slot lifecycle across processes

### 5.1 State machine (per slot; `StateSeq` = `(seq << 32) | state`, CAS'd as one 64-bit word)

```
Free(seq)  --reader CAS-->  Claimed(seq+1)  --reader Exchange-->  Active(seq+1)
Active(seq)  --reader CAS (Dispose)-->  Free(seq)
Active(seq)  --writer CAS (eviction)-->  Dead(seq)  --writer Exchange-->  Free(seq)
Claimed(seq) stuck (reader died between claim and activate) --writer, after liveness check-->  Free(seq)
```

Encoding the sequence with the state makes every transition ABA-proof: a writer that decided to evict `(Active, 7)` cannot accidentally evict a new reader that meanwhile did `Free(7) → Claimed(8) → Active(8)` in the same slot — its CAS expects `(Active,7)` and fails. The reader's handle to its slot is `(index, seq)`; every slow-path operation (Wait, Dispose) verifies `StateSeq == (Active, mySeq)` and throws `ReaderEvictedException` otherwise.

### 5.2 Claim (join) protocol and its correctness

```
for i in 0..N-1:
    ss = Volatile.Read(ref slot[i].StateSeq)
    if (State(ss) != Free) continue
    if (Interlocked.CompareExchange(ref slot[i].StateSeq, Make(Claimed, Seq(ss)+1), ss) != ss) continue
    slot[i].Pid = myPid; slot[i].ProcessStartTime = myStart; slot[i].WaitFor = 0
    slot[i].ReadCursor = Volatile.Read(ref hdr.WriteCursor)            // (a) provisional start = head
    Interlocked.Exchange(ref slot[i].StateSeq, Make(Active, Seq(ss)+1)) // (b) publish; full fence
    w2 = Volatile.Read(ref hdr.WriteCursor)                             // (c) re-read after the fence
    Volatile.Write(ref slot[i].ReadCursor, w2)                          // (d) adopt the newest head (monotone bump)
    Interlocked.Or(ref hdr.ActiveMask, 1UL<<i); Interlocked.Increment(ref hdr.ReaderGeneration)
    open event i, open writer process handle
    return reader(i, seq+1, cursor = w2)
throw TooManyReaders
```

Claim: the writer never overwrites unread data of the new reader, i.e. from the time the reader becomes visible to the writer, every writer reservation satisfies `E ≤ R_new + C`.

Argument. The writer reserves using `M`, a value returned by some `ScanMin()`. Two cases for the scan that produced the `M` used by a reservation:

1. *The scan observed the slot as Active.* Then it read the reader's cursor `r ≥ w1` (the value stored at (a); (d) only increases it), so `M ≤ r ≤ R_new` and `E ≤ M + C ≤ R_new + C`. Safe.
2. *The scan did not observe the slot as Active.* The writer's loads in that scan follow the full fence of its most recent Commit; the reader's load (c) follows the full fence of (b). Dekker: since the writer's load of `StateSeq` did not see (b), the reader's load (c) sees the writer's `W` store that preceded that fence — that is, `w2 ≥ W_at_scan`. Every old reader's cursor at scan time is `≤ W_at_scan`, so `M ≤ W_at_scan ≤ w2 = R_new`, hence `E ≤ M + C ≤ R_new + C`. Safe.

Without step (c)/(d), case 2 breaks: `w1` could be far behind `M` if the reader was descheduled between (a) and (b), and the writer could lap it. Without the full fence in Commit, case 2 also breaks. Both are cheap; keep both.

Starting position alternatives:
- **Head (default, above):** the reader sees only data committed after it joined. Deterministic and always safe.
- **Oldest available:** semantically "give me everything still in the ring". A reader cannot compute a safe backward position on its own — there is no way to observe the writer's *next* reservation. Safe implementation is a **writer-assisted rewind**: the reader joins at head, sets `slot.Flags |= RewindRequested`, and waits; the writer, at its next `GetBucket` (a point where it knows `E` exactly and will gate every future reservation on the slot), does `slot.ReadCursor = max(E_local - C, 0)` *(a writer-side store into a reader's cursor, the only exception to "reader owns its cursor")*, clears the flag, and signals the slot event. The reader must not touch its cursor until the flag clears. Provide as `CreateReader(ReaderStart.OldestAvailable)`, document the round-trip.

### 5.3 Dead-reader detection and eviction

The writer only cares about readers when it is blocked for space, so liveness checks live entirely on the writer's slow path:

1. When `WaitForSpace` first blocks, for every Active slot with `R_i == M` (the laggards) open a process handle: `OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, slot.Pid)`; cache it by `(slot, seq)`.
2. **PID reuse guard:** `GetProcessTimes(h, &creation, ...)` and compare with `slot.ProcessStartTime` (stored by the reader at claim, from `GetProcessTimes(GetCurrentProcess())`). Mismatch ⇒ the original reader is dead. (`OpenProcess` failing with `ERROR_INVALID_PARAMETER` ⇒ no such PID ⇒ dead. `ERROR_ACCESS_DENIED` ⇒ different user/integrity; fall back to a heartbeat policy or refuse cross-user attach — decide per deployment.)
3. Include the laggards' process handles in the writer's `WaitForMultipleObjects` set together with `spaceEvent`. A reader crash then wakes the writer immediately instead of after a timeout. Also re-check with `WaitForSingleObject(h, 0) == WAIT_OBJECT_0` after any timeout.
4. **Evict:** `Interlocked.CompareExchange(ref slot.StateSeq, Make(Dead, seq), Make(Active, seq))`; on success: `Interlocked.And(ref hdr.WaitersMask, ~bit)`, `Interlocked.And(ref hdr.ActiveMask, ~bit)`, close the cached handles, then `Interlocked.Exchange(ref slot.StateSeq, Make(Free, seq))` (Dead is a transient state to keep the two-step observable for diagnostics/counters). `Increment(ReaderGeneration)`. Then `M = ScanMin()` again — the writer is no longer gated by that slot.
5. Slots stuck in `Claimed` (reader died between claim and Active) are reclaimed the same way, using the PID if it was written or a claim-age heuristic if not (write `Pid` *before* the Claimed CAS? No — the slot isn't ours yet. Instead: store Pid immediately after the CAS; treat `Claimed` with `Pid == 0` older than 1 s of writer blocking as abandoned).

Policy for **live-but-stuck** readers (debugger-paused, deadlocked): by default the writer waits forever (backpressure is the contract). Offer an opt-in `MaxReaderLag` timeout that evicts live readers; because that reader is alive, it must detect eviction itself: check `StateSeq` on every `Wait` slow path and, cheaply, on every `Advance` (its own slot line is in its L1; one load + compare ≈ 1 ns) and throw `ReaderEvictedException`.

Never evict on the writer's fast path: no liveness syscalls unless the writer is actually blocked.

### 5.4 Dead writer

- The writer stores `WriterPid`/`WriterStartTime` and sets `WriterState = Active` at creation; sets `WriterState = Closed` on clean dispose (after the final Commit, with a fence) and signals every slot event.
- Readers open the writer's process handle at attach and include it in every kernel wait (`WaitForMultipleObjects([event, writerProc])` — no extra cost). If it signals and `WriterState != Closed`, the reader can still drain `[R_i, W)` (that data is fully published) and then gets `WriterTerminatedException` / `Wait` returns EOF. Spinning readers check `WriterState` and the process handle only when escalating to the kernel or every ~100 ms of spinning.
- Writer takeover: a new writer process may `CompareExchange(WriterPid, myPid, deadPid)` after verifying the old PID is dead (same start-time guard), then adopt `E = ReserveEnd`, `W = WriteCursor` (the outstanding bucket at `[W, E)` is discarded: `E_local = W`). Readers see the new `WriterPid` on their next slow path and re-open the process handle. Optional feature; document that the section persists only while some process holds a handle (pagefile-backed sections vanish when the last handle closes, so a "keeper" handle or a file-backed section is needed for true writer restart).

---

## 6. Bucket semantics

```csharp
public ref struct Bucket<T> where T : unmanaged
{
    public Span<T> Span { get; }          // exactly n elements, contiguous (mirror guarantees it)
    public void Commit(int count);        // 0 ≤ count ≤ Span.Length; exactly once
    public void Dispose();                // no Commit ⇒ publishes nothing; the reservation is dropped
}
```

Decisions:

- **`GetBucket(n)` returns exactly `n` elements**, blocking until `n` are free. `n ≤ C` is enforced (`ArgumentOutOfRangeException`; `n > C` can never be satisfied). Provide `TryGetBucket(n, out bucket)` (non-blocking) and `GetBucket(min, max)` (returns whatever is free in `[min, max]`, blocks until ≥ `min`) for streaming producers that want to fill all available space. Exact-`n` is the primitive because framing protocols need it and it maps 1:1 to the user's sketch.
- **Contiguity:** the span starts at `base + 65536 + ((E_old) & (C-1)) * s` and has `n*s` bytes; since `(E_old & (C-1)) ≤ C-1` and `n ≤ C`, the span ends before `base + 65536 + 2D`, inside the mirror. Writes that fall past `D` land in the first `n*s - (D - off)` bytes of the data page set — the same physical memory. No copy, no split.
- **Commit(k):** `k ≤ n`; publishes `[W, W+k)`; the remaining `n-k` elements are *discarded from the reservation*, i.e. `E_local = W + k` and the next bucket starts at `W + k` (not at `W + n`). Otherwise there would be unpublishable holes. Commit exactly once; a second Commit throws.
- **Dispose without Commit:** `E_local = W` (drop). `Commit(0)` is the same thing explicitly.
- **Single outstanding bucket:** the writer object keeps `bool _outstanding`; `GetBucket` throws `InvalidOperationException` if set; `Commit/Dispose` clear it. It is a writer-local plain field (single writer thread by contract). For debug builds, `Interlocked.CompareExchange(ref _outstanding, 1, 0)` additionally catches multi-thread misuse at ~zero cost. The bucket being a `ref struct` prevents storage in fields/async state and escaping the stack frame, which is what makes `using var bucket = ...` in the sketch safe.
- After Commit/Dispose the `Span` is logically dead. The `ref struct` cannot enforce that; document it and zero the bucket's internal length on Commit so `Span` returns empty afterwards.
- `Commit` must be called from the same thread as `GetBucket` (no cross-thread handoff), keeping "writer = one thread" a single, cheap invariant.

Writer object contract: exactly one `RingWriter<T>` per buffer; enforced by `WriterPid` CAS at creation (§5.4).

---

## 7. Reader semantics

```csharp
public ref struct Chunk<T> { public ReadOnlySpan<T> Span { get; } }

bool TryRead(int n, out Chunk<T> chunk);       // false if fewer than n available; chunk.Span.Length == n
Chunk<T> ReadAvailable(int max = int.MaxValue); // 0..min(available, max) elements (streaming form)
long Available { get; }                         // W - R, refreshes W
void Advance(int k);                            // 0 ≤ k ≤ Available (validated); consumes
void Wait(int n, ...); ValueTask<bool> WaitAsync(int n, ...);
```

Decisions:

- **`TryRead(n)` returns exactly `n`** or false. Rationale against the sketch `Wait(100); TryRead(100, out chunk); Advance(90)`: the sketch reads a *known* amount and then consumes *less*, which is the exact-`n` framing shape (e.g. a parser consumed 90 elements of a 100-element window and wants the rest to remain readable together with what comes next). "Up to `n`" would silently hand the caller a shorter span after `Wait(100)` succeeded only if `W` regressed, which cannot happen; so "up to n" adds no capability on the `TryRead` path — it is a different call, `ReadAvailable`. Providing both keeps each unambiguous.
- **Chunk validity:** `chunk.Span` covers `[R, R+n)`. It stays valid (stable, never overwritten) until the reader's cursor moves past it — the writer's gate `E ≤ R + C` guarantees the region `[R, W)` is never reserved. After `Advance(k)`, the first `k` elements of the chunk are no longer protected; the caller must not read them (the `ref struct` prevents storing the chunk, and `Advance` is on the reader, so the type system cannot enforce this; document it).
- **`Advance(k)`:** `k ≤ (W_cached - R)` checked with the cached `W` (no reload needed: if it passes against the cached value it passes against the real one). Throws otherwise — an over-advance would move the cursor into unpublished/partially written territory. `Advance` is the only operation with a fence (§2.2).
- **Overflow protection:** reads are always within `[R, W)`; `W ≤ E ≤ R + C` by the writer's gate; therefore no read region is ever concurrently written. There is no "overrun" state for a reader by construction — the cost is that a slow reader stalls the writer (backpressure), which is the requested contract; the eviction policy in §5.3 is the escape hatch.
- **Span contiguity:** `[R & (C-1)) * s, + n*s)` with `n ≤ C` lies inside the mirrored 2D range, as for buckets.
- A `Reader` is single-threaded (one consumer); readers in the writer's own process use the same views and the same code.
- `Wait(n)` with `n > C` throws; `n == 0` returns immediately.

---

## 8. Index arithmetic and capacity selection

- Cursors are `long` element counts. At 10 G elements/s a signed 64-bit cursor wraps after ~29 years; treat wrap as impossible (assert in debug). Differences (`W - R`) are always small non-negative numbers; never compare cursors with `<` across a hypothetical wrap — always subtract.
- Offset: `((cursor & (C-1)) * s)` bytes. Power-of-two `C` replaces a 64-bit division (20–40 cycles) with an AND. Element count first, then multiply by `s` — never mask a byte offset with an element mask.
- Constraint: `D = C * s` must be a multiple of 64 KiB (`G = 65536`). In general the admissible `D` are multiples of `lcm(G, s) = G * s / gcd(G, s)`, and since `G = 2^16`, `gcd(G, s) = 2^min(16, tz(s))` where `tz(s) = BitOperations.TrailingZeroCount(s)`. With a power-of-two `C = 2^k`: `D = 2^k * s = 2^(k + tz(s)) * odd(s)`, which is a multiple of `2^16` **iff `k + tz(s) ≥ 16`**. So a power-of-two element capacity always exists for every `s`; the price is a minimum size of `2^(16 - tz(s))` elements (e.g. `s = 12` ⇒ `tz = 2` ⇒ `C ≥ 16384` ⇒ `D = 196608 = 3 × 64 KiB`).

```csharp
static (long capacity, long dataBytes) ChooseCapacity(long requestedElements, int s)
{
    if (s <= 0) throw new ArgumentOutOfRangeException(nameof(s));
    int kMin = Math.Max(0, 16 - BitOperations.TrailingZeroCount(s));
    int kReq = requestedElements <= 1 ? 0 : 64 - BitOperations.LeadingZeroCount((ulong)(requestedElements - 1)); // ceil(log2)
    int k = Math.Max(kMin, kReq);
    long capacity = 1L << k;
    long dataBytes = checked(capacity * s);
    if (dataBytes > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(requestedElements), "Span<T> length limit");
    Debug.Assert(dataBytes % 65536 == 0);
    return (capacity, dataBytes);
}
```

`Span<T>` lengths are `int`; keep `C ≤ int.MaxValue` and `D ≤ int.MaxValue` so a full-capacity bucket is representable (2 GiB data cap, 4 GiB VA per mapping; if larger rings are ever needed, buckets would be capped at `int.MaxValue / s` elements).

Also validate at open: `ElementSize == sizeof(T)`, `Capacity` power of two, `DataBytes == Capacity * ElementSize`, `DataBytes % 65536 == 0`, `HeaderSize`/`Version`/`Magic`.

---

## 9. Invariants and subtle bugs

### 9.1 Invariants

- **I1 (publication):** `R_i ≤ W ≤ E` for every active reader `i`, at all times, in every process's view (all three are monotone non-decreasing; `W` only via Commit, `E` only via GetBucket/Commit/Dispose-drop where the drop sets `E = W`, never below `W`).
- **I2 (no overrun):** every reservation satisfies `E ≤ M + C` where `M ≤ min_i R_i` over all readers that are or will become visible (§2.5 + §5.2). Hence `E ≤ R_i + C` for all `i`, so the ring positions `[E, E + n)` never intersect any `[R_i, W)`.
- **I3 (data stability):** for each reader, the bytes of `[R_i, W)` are never stored to by the writer while `R_i` is unchanged. Follows from I2.
- **I4 (visibility):** data stores for `[W, W')` happen-before the `Volatile.Write(W')`; a reader whose `Volatile.Read` returns `≥ W'` observes them. Symmetrically, a reader's data loads of `[R, R+k)` happen-before its `Volatile.Write(R+k)`; the writer's subsequent stores there are ordered after its acquire load of that cursor.
- **I5 (no lost wakeup):** for each (reader-flag, condition) pair, the store-fence-load pattern is used on both sides; therefore "reader blocked ∧ condition true ∧ nobody will signal" is unreachable.
- **I6 (slot ownership):** for each `(slot, seq)` exactly one party holds it in `Claimed/Active`; only the holder writes `ReadCursor/WaitFor/Pid` (except writer-assisted rewind, which is flag-serialized), and only the holder or the writer (eviction) leaves the Active state.
- **I7 (single writer):** exactly one process has `WriterPid == its pid ∧ alive`; only that process stores `W`, `E`, `WriterWaiting = 1`.
- **I8 (contiguity):** every span handed out is `≤ C` elements starting at a masked offset, so it lies inside `[data, data + 2D)`.

### 9.2 Subtle bugs to avoid

1. **No StoreLoad fence between publishing the waiting flag and re-checking `W`** (or between `Volatile.Write(W)` and reading the waiters mask): both sides read stale values, the reader blocks forever with data available. `Volatile.Write` + `Volatile.Read` is *not* enough on x64, let alone ARM64.
2. **Spinning on a plain read** (`while (hdr.WriteCursor < target)`): the JIT hoists the load; infinite loop in Release builds. Always `Volatile.Read` in loops.
3. **Positions instead of cursors** (storing `W mod C`): cannot distinguish full from empty, and free-space arithmetic breaks at wrap. Keep 64-bit absolute cursors; mask only when forming a span.
4. **Reader cursors sharing a cache line** (e.g. `long[] cursors` in the header): every `Advance` by any reader invalidates every other reader's line and the writer's; 5–10× throughput loss. One 64-byte line per slot, and nothing else the writer stores per bucket on the `WriteCursor` line.
5. **Join without the re-read-after-fence step** (§5.2 (c)/(d)): a reader descheduled between reading `W` and publishing Active can start behind the writer's cached minimum and get lapped silently.
6. **Cached minimum never invalidated after blocking**: the writer flags `WriterWaiting`, then re-checks using stale `M` instead of rescanning — deadlock. Always `ScanMin()` after the flag store.
7. **Eviction CAS on `state` only**: the slot was released and re-claimed by another reader between the writer's liveness check and its CAS; the new, live reader gets evicted. CAS the combined `(seq, state)` word.
8. **PID reuse**: a dead reader's PID is recycled by an unrelated process; the writer waits on it forever. Compare process creation time (`GetProcessTimes`) or hold a `SYNCHRONIZE` handle opened at first sight.
9. **One auto-reset event shared by all readers** (only one wakes) or manual-reset + `PulseEvent` (documented unreliable): broadcast wakeups are lost. Per-slot auto-reset events.
10. **Plain store of the read cursor in `Advance`** (no release): on ARM64 the cursor store can become visible before the data loads complete; the writer overwrites data still being read. Even on x64 the compiler barrier matters.
11. **`Commit(k)` not shrinking the reservation** (next bucket starts at `W + n` instead of `W + k`): unpublishable holes; readers see garbage-length regions and free space is mis-accounted.
12. **Bucket/chunk larger than `C`**, or masking the byte offset instead of the element index: the span runs off the end of the mirror or lands at a misaligned element. Validate `n ≤ C` and mask elements, then multiply by `s`.
13. **`D` not a multiple of 64 KiB** (e.g. `C = 1000 floats`): placeholder split or `MapViewOfFile3` fails with `ERROR_INVALID_PARAMETER` / `ERROR_MAPPED_ALIGNMENT`. Use §8.
14. **Auto/sequential managed layout for the header** (or `Size`/`Pack` differences between versions): offsets differ between builds; corrupt cross-process state. `LayoutKind.Explicit`, size assert, and `Version`/`Magic` check on open.
15. **`SpinWait.SpinOnce()` default escalation** → `Thread.Sleep(1)`: latency spikes of 1–16 ms. Time-bound your own spin and go straight to the kernel wait.
16. **`Environment.TickCount` deadlines** wrap after 24.9 days; use `Stopwatch.GetTimestamp()`/`Environment.TickCount64`.
17. **Completing the `ValueTask` inline on the dispatcher thread** (`RunContinuationsAsynchronously = false`): one slow continuation stalls all async readers in the process; re-entrancy into `WaitAsync` from inside `SetResult` corrupts the VTS. Set the flag once and clear `_asyncPending` before `SetResult`.
18. **Sync `Wait` and async `WaitAsync` concurrently on one reader** (or two threads calling `Wait`): two waiters on one auto-reset event; one wakeup is consumed by the wrong one. Enforce single outstanding wait.
19. **Writer `SetEvent` on every commit regardless of waiters**: 1–3 µs syscall per bucket, throughput collapses. Gate on `WaitersMask` after the fence; consume the bit before signaling so repeated commits don't re-signal.
20. **Reader clearing its waiter bit *before* the kernel wait** or the writer clearing the bit *after* `SetEvent` in a way that races a timeout re-arm: think it through with the state machine in §3.3; the safe order is reader: set-bit → re-check → wait; writer: check → clear-bit → SetEvent.
21. **Handle leaks** in the eviction/liveness loop (`OpenProcess` per poll): cache per `(slot, seq)`, close on eviction/Dispose. Also `OpenEvent` per signal.
22. **Two writers**: a second process assumes writership without the `WriterPid` CAS + liveness check; two producers store `W` and reserve overlapping regions. CAS the writer PID; keep the start-time guard.
23. **Reading `ActiveMask`/`WaitersMask` only** and skipping the slot state: masks are hints/aggregates updated *after* the slot transition; the slot word is the authority for correctness.
24. **Forgetting `Volatile.Write(InitState = 1)` last** when the creator initializes the header while another process races to open it: the opener must spin on `InitState` (acquire) before trusting any other field.
25. **Assuming the second process mapped at the same address**: only cursors and offsets go into shared memory; any pointer stored in a message must be rebased by the reader (`ptr - CreatorBase + myBase`).

---

## 10. Performance notes

### 10.1 Expected numbers (x64 desktop, Windows 11, cores pinned, no hyperthread sharing)

| Path | Expected |
|---|---|
| Writer `GetBucket` + `Commit` (cache-hot, no scan, nobody waiting) | 15–40 ns (mask, two volatile stores, one fence, one load) |
| Reader `TryRead` + `Advance` (cache-hot) | 10–30 ns |
| Cross-process one-way latency, reader **spinning** | 100–300 ns (one cache-line transfer for `W`, one for the data line(s)); ping-pong RTT 0.3–0.8 µs |
| Cross-process one-way latency, reader **blocked** in the kernel | 5–20 µs (SetEvent syscall ~1 µs + scheduler + wake); +tens of µs if the target core was in C6 |
| Async wake (dispatcher → thread pool continuation) | +2–8 µs over sync blocked |
| Throughput, 64 KB buckets, 1 reader, memcpy-style fill/consume | memory-bandwidth bound: 10–20 GB/s single core |
| Throughput, 4 KB buckets | ~50 ns overhead per bucket vs ~200 ns memcpy ⇒ still >10 GB/s; 8+ readers add a `ScanMin` roughly every `C/n` buckets |
| `ScanMin` with 32 slots | 32 loads; ~0.1 µs cache-hot, ~1–2 µs if all lines were modified remotely |

Spinning readers on the writer's `W` line each cost the writer one invalidation broadcast per commit (handled by the cache hierarchy, negligible up to ~8 readers).

### 10.2 Benchmarks to build

1. **Ping-pong latency:** two buffers (A→B, B→A), one 8-byte element per message, process A: `GetBucket(1); Commit(1); Wait(1); Advance(1)`, process B mirrored. Report RTT/2 with HdrHistogram (p50/p90/p99/p99.9/max) over ≥1 M round trips after 100 k warm-up. Run three variants: spin-only, spin 20 µs then block, block-only (`spinMicros = 0`), and both sync and async.
2. **Throughput:** writer fills `GetBucket(k)` for `k ∈ {4 KiB, 64 KiB}` bytes (touch every element or memcpy from a source), `C` = 64 MiB; 1, 2, 4 readers each consuming with `ReadAvailable` + checksum; report GB/s per reader and writer commits/s. Verify checksums (correctness under load is the point).
3. **Wake-cost microbench:** commit rate with one reader blocked vs. spinning (isolates `SetEvent` cost and the `WaitersMask` gate).
4. **Fault injection:** kill a reader mid-stream (`Process.Kill`) while the writer is blocked → writer must resume within the liveness slice (<1 ms with process handles in the wait set); kill the writer → readers must throw within one slice.
5. **Join storm:** readers attach/detach at 10 kHz while the writer streams; checksums must never fail (tests §5.2).

Tooling: BenchmarkDotNet is fine for the in-process hot-path microbenchmarks (`[DisassemblyDiagnoser]` to confirm no calls/allocs in `TryRead`/`Commit`; `[MemoryDiagnoser]` for zero-alloc); the cross-process runs need a custom harness (BenchmarkDotNet's process model gets in the way). Use `dotnet-counters`/ETW `ThreadPool` and `CSwitch` events to confirm no unexpected context switches on the spin path.

### 10.3 Machine setup for reproducible numbers

- Pin threads: `SetThreadAffinityMask`/`Process.ProcessorAffinity`; put writer and reader on different physical cores (skip SMT siblings), same NUMA node/CCD (cross-CCD on Zen adds ~100 ns per line transfer).
- Power: "High performance"/"Ultimate" plan, disable core parking, consider `PROCESS_POWER_THROTTLING_EXECUTION_SPEED` off; C-state depth dominates blocked-wake latency.
- Priority: `ThreadPriority.Highest` or `REALTIME_PRIORITY_CLASS` for the ping-pong test only; do not rely on it in the library.
- Timer resolution is irrelevant as long as `Sleep` is never used; if `WaitForSingleObject` with timeouts is used in the spin→block escalation, remember timeout granularity is ~1–16 ms; use `INFINITE` plus the process handle instead of short timeouts.
- Large pages (`SEC_LARGE_PAGES`) reduce TLB misses for multi-GB rings but need `SeLockMemoryPrivilege` and a 2 MiB-multiple `D`; the double mapping still works. Optional.
- Warm the pages (touch the entire ring once in the creator) so first-touch page faults don't land in the benchmark; pagefile-backed sections are demand-zero.

---

## Appendix A — Hot-path pseudo-code summary

```
Writer.GetBucket(n):
    check n ≤ C, !outstanding
    if (C - (E - M) < n): M = ScanMin(); if still short: WaitForSpace(n)   // §3.4
    outstanding = true; E += n; Volatile.Write(hdr.ReserveEnd, E)
    return Bucket(dataBase + ((E-n) & mask) * s, n)

Writer.Commit(k):            // k ≤ n
    E = W + k
    Volatile.Write(hdr.WriteCursor, E); W = E
    Interlocked.MemoryBarrier()
    if (Volatile.Read(hdr.WaitersMask) != 0) SignalReaders(W)
    outstanding = false

Reader.TryRead(n):
    if (Wc - R < n) { Wc = Volatile.Read(hdr.WriteCursor); if (Wc - R < n) return false }
    chunk = (dataBase + (R & mask) * s, n); return true

Reader.Advance(k):           // k ≤ Wc - R
    R += k
    Interlocked.Exchange(ref slot.ReadCursor, R)          // release + full fence
    if (Volatile.Read(hdr.WriterWaiting) != 0 && Interlocked.Exchange(ref hdr.WriterWaiting, 0) == 1) SetEvent(space)

Reader.Wait(n):              // §3.3
Reader.WaitAsync(n):         // §4.2
```

## Appendix B — Line ownership table

| Line | Writer | Readers |
|---|---|---|
| 0–1 identity | write once | read once |
| 2 `WriteCursor` | store per Commit | load (polled) |
| 3 `ReserveEnd`, writer id | store per GetBucket | load on slow path only |
| 4 `WriterWaiting` | store on block/unblock | load per Advance, RMW once per writer-sleep |
| 5 masks/generation | load per Commit; RMW on evict/signal | RMW on join/leave/block |
| slot `i` | load on scan (slow path); CAS on evict | store per Advance; RMW on join/leave/block |

---

# REPORT: research:dotnet-specifics

# photone-ipc — .NET 10 / C# 14 Implementation Reference (Windows, double-mapped ring buffer)

Everything below was checked against Microsoft Learn / dotnet/runtime docs, nuget.org, and — where marked **[verified locally]** — actually compiled and run on this dev box (Windows 11 26200, x64, .NET SDK 10.0.112 / runtime 10.0.12) from the scratchpad (`C:\Users\Admin\AppData\Local\Temp\claude\E--GitHub-photone-ipc\1f1c1aea-e7c4-43f4-a5f8-47ebdcd222c5\scratchpad\{exportcheck,tstcheck,refstructcheck}`).

---

## 0. Critical local finding: pin the SDK

`dotnet --version` in an unpinned directory on this machine returns **11.0.100-preview.7.26381.103**, not 10.0.112 (installed SDKs: 8.0.411, 8.0.425, 9.0.205, 10.0.112, 11.0.100-preview.7). Without a `global.json` every build/test/benchmark silently uses the .NET 11 preview SDK. Put this at `E:\GitHub\photone-ipc\global.json`:

```json
{
  "sdk": { "version": "10.0.112", "rollForward": "latestPatch" },
  "test": { "runner": "Microsoft.Testing.Platform" }
}
```

The `"test"` block is what makes `dotnet test` on SDK 10 drive xunit.v3 in Microsoft.Testing.Platform mode (verified below).

---

## 1. Project setup

### 1.1 `net10.0` vs `net10.0-windows`

| | `net10.0` | `net10.0-windows` |
|---|---|---|
| Platform attribute | none — you add `[assembly: SupportedOSPlatform("windows")]` yourself | SDK auto-generates `[assembly: TargetPlatform("Windows7.0")]` + `[assembly: SupportedOSPlatform("Windows7.0")]` (overridable via `<SupportedOSPlatformVersion>`) |
| CA1416 inside the library | Off unless the assembly attribute is present; with it, calls to Windows-only BCL APIs (`EventWaitHandle.OpenExisting`, `Process.StartTime`, etc.) are clean | Clean |
| CA1416 in consumers | A `net10.0` consumer calling your API gets CA1416 warnings unless it guards (`OperatingSystem.IsWindows()`) or is itself Windows-marked | Consumers must target `netX-windows`; a plain `net10.0` project cannot reference the package (NU1202) |
| Recommendation | **Use this.** Windows-only *today*, but a Linux `memfd`/`mmap` backend later would not require a TFM change | Only if you never want non-Windows |

The platform-compat analyzer (`CA1416`) is enabled by default in the SDK (`<AnalysisLevel>` ≥ 5.0). Set `<SupportedOSPlatformVersion>10.0.17134.0</SupportedOSPlatformVersion>` (Win10 1803 = build 17134) only if you use `net10.0-windows`; for plain `net10.0` document the requirement and do a runtime check `OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134)` in the factory method (throw `PlatformNotSupportedException`).

### 1.2 Library `.csproj` (recommended)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <!-- SDK 10 defaults LangVersion to C# 14; do NOT set 'preview' -->
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>          <!-- required by LibraryImport generator and pointer params -->
    <InvariantGlobalization>true</InvariantGlobalization> <!-- harmless for a lib; avoids ICU load in test/bench exes -->
    <IsAotCompatible>true</IsAotCompatible>              <!-- turns on trim + AOT + single-file analyzers (IL2xxx/IL3xxx) -->
    <EnableTrimAnalyzer>true</EnableTrimAnalyzer>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AnalysisLevel>latest</AnalysisLevel>
    <!-- Perf-relevant runtime knobs are NOT set in a library; see 1.3 -->
  </PropertyGroup>
</Project>
```

```csharp
// AssemblyInfo.cs
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
[assembly: SupportedOSPlatform("windows")]
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)] // kernelbase/kernel32 only; blocks DLL hijack via CWD
```

### 1.3 Runtime/JIT settings for benchmarks and the test host (exe projects only)

These are `runtimeconfig.json` knobs; put them in the **benchmark/test exe** csproj, never in the library:

| MSBuild property | Default (.NET 10) | Use |
|---|---|---|
| `<TieredCompilation>` | true | Leave on. Turning it off makes everything full-opt on first call but disables PGO — slower steady-state code. |
| `<TieredPGO>` | true (since .NET 8) | Leave on for realistic numbers; set `false` (or env `DOTNET_TieredPGO=0`) when you need run-to-run reproducibility of codegen. |
| `<TieredCompilationQuickJitForLoops>` | true (OSR handles it) | Leave. |
| `<ServerGarbageCollector>` | false | Irrelevant if hot path allocates nothing; keep workstation. |
| `<ConcurrentGarbageCollection>` | true | Set `false` in latency benchmarks to remove the background GC thread from the picture. |
| `<UseSystemResourceKeys>` | false | Only for AOT size. |
| `<PublishAot>` | – | Set `true` in a *sample* app to prove AOT compatibility; the library itself just needs `IsAotCompatible`. |

Env vars for one-off experiments: `DOTNET_TieredPGO=0`, `DOTNET_TieredCompilation=0`, `DOTNET_ReadyToRun=0`, `DOTNET_JitDisasm=MethodName` (works on release runtime since .NET 7), `DOTNET_gcServer=0`.

AOT compatibility summary: `[LibraryImport]` generates plain C# marshalling → fully AOT/trim safe. `[DllImport]` with `SetLastError=true`, `string`, `bool`, `SafeHandle` or any non-blittable signature requires a runtime-generated IL stub → **not** allowed under NativeAOT (build error / IL3050-class warnings). Blittable-only `DllImport` (`void*`, `nint`, `int`, `uint`, `nuint`) is allowed under AOT even with `DllImport`, but there is no reason not to use `LibraryImport` everywhere.

---

## 2. `[LibraryImport]` source generator

### 2.1 Rules (from the P/Invoke source-generation docs + Compatibility.md)

- Method must be `static partial`, in a `partial` class (nest as deep as you like: every containing type `partial`). No `extern`.
- Project must have `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`; the generated code is `unsafe`. Pointer parameters (`void*`, `byte*`) require the *declaring method* to be in an `unsafe` context (`static unsafe partial class NativeMethods`).
- `EntryPoint` must be the **exact** export name. There is no `ExactSpelling`/`CharSet` A/W probing — write `CreateFileMappingW`, `OpenEventW`, `CreateEventW`, `OpenFileMappingW`. `CharSet` is replaced by `StringMarshalling = StringMarshalling.Utf16` (for W APIs) / `Utf8`; there is no ANSI option.
- `bool`: **explicit marshalling is mandatory** in LibraryImport (Compatibility.md: "all `bool` marshalling must be explicitly specified via `MarshalAs`"). Use `[return: MarshalAs(UnmanagedType.Bool)]` (4-byte Win32 `BOOL`) and `[MarshalAs(UnmanagedType.Bool)] bool` on parameters. Omitting it is a compile error (SYSLIB1051), not a silent 1-byte marshal.
- `char`: only with `StringMarshalling.Utf16` or `UnmanagedType.U2/I2`.
- `SafeHandle`: supported as `in`/by-value parameters (generator does `DangerousAddRef`/`DangerousRelease` around the call), `out`/`ref`, and return values. For `out`/`ref`/return the concrete type must have a **public parameterless constructor** and be non-abstract. `CriticalHandle` and `HandleRef` are **not** supported.
- `SetLastError = true`: generated code calls `Marshal.SetLastSystemError(0)` before the call and `Marshal.SetLastPInvokeError(Marshal.GetLastSystemError())` right after — read it with **`Marshal.GetLastPInvokeError()`** (.NET 6+; `GetLastWin32Error()` is the legacy alias, same value). `Marshal.GetPInvokeErrorMessage(int)` (.NET 7+) formats it; `new Win32Exception(err)` for throwing.
- Calling convention: `[UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]` — unnecessary on x64 (single ABI); omit.
- Delegates/callbacks: prefer `delegate* unmanaged[Stdcall]<...>` function pointers over managed delegates (no allocation, AOT-safe). You need none for this library.
- `StringBuilder`, COM interfaces, `UnmanagedType.Struct`/VARIANT, `SafeArray` are unsupported.

### 2.2 Which DLL exports VirtualAlloc2 / MapViewOfFile3 / UnmapViewOfFile2? **[verified locally]**

`NativeLibrary.TryGetExport` on this Windows 11 26200 box:

| Export | `kernel32.dll` | `kernelbase.dll` | `api-ms-win-core-memory-l1-1-6.dll` |
|---|---|---|---|
| `VirtualAlloc2` | **MISSING** | ok | ok |
| `MapViewOfFile3` | **MISSING** | ok | ok |
| `UnmapViewOfFile2` | **MISSING** | ok | ok |
| `WaitOnAddress` / `WakeByAddress*` | **MISSING** | ok | ok |
| `VirtualFree`, `CreateFileMappingW`, `OpenFileMappingW`, `CreateEventW`, `OpenEventW`, `SetEvent`, `WaitForSingleObject`, `OpenProcess`, `GetProcessTimes` | ok | ok | ok |
| `onecore.dll` | — | — | does not exist as a loadable DLL (`onecore.lib` is an *umbrella import lib*, link-time only) |

Conclusion: the Learn page lists "DLL: Kernel32.dll" but **kernel32.dll does not export these three functions even on the latest Windows 11**. Use **`"kernelbase.dll"`** as the library name for `VirtualAlloc2`, `MapViewOfFile3`, `UnmapViewOfFile2` (and `WaitOnAddress` if ever used). `kernelbase.dll` is loaded in every Win10+ process, so there is no extra load. `api-ms-win-core-memory-l1-1-6.dll` also works (it is the documented API-set contract, min Win10 1803) but is just an alias resolving to kernelbase; `kernelbase.dll` is what dotnet/runtime itself uses. Everything else can stay on `kernel32.dll`.

### 2.3 Declarations **[verified locally — these exact signatures compiled and executed the placeholder ring buffer successfully]**

```csharp
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static unsafe partial class Kernel
{
    // ---- memoryapi (kernelbase-only exports) ----
    [LibraryImport("kernelbase.dll", SetLastError = true)]
    internal static partial void* VirtualAlloc2(
        nint process,            // NULL = current process
        void* baseAddress,       // must be multiple of allocation granularity (64 KiB) when non-null
        nuint size,              // multiple of page size
        uint allocationType,     // MEM_RESERVE | MEM_RESERVE_PLACEHOLDER
        uint pageProtection,     // PAGE_NOACCESS for placeholders
        void* extendedParameters, uint parameterCount);

    [LibraryImport("kernelbase.dll", SetLastError = true)]
    internal static partial void* MapViewOfFile3(
        SafeHandle fileMapping,  // SafeMemoryMappedFileHandle or your own SafeSectionHandle
        nint process,            // NULL worked on 26200; (nint)-1 (GetCurrentProcess pseudo-handle) is the documented-safe value
        void* baseAddress,       // the placeholder address
        ulong offset,            // section offset; page-aligned when replacing a placeholder (64K-aligned otherwise)
        nuint viewSize,          // must EXACTLY equal the placeholder size
        uint allocationType,     // MEM_REPLACE_PLACEHOLDER
        uint pageProtection,     // PAGE_READWRITE
        void* extendedParameters, uint parameterCount);

    [LibraryImport("kernelbase.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnmapViewOfFile2(nint process, void* baseAddress, uint unmapFlags); // MEM_PRESERVE_PLACEHOLDER=2 or 0

    // ---- kernel32 ----
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool VirtualFree(void* address, nuint size, uint freeType); // MEM_RELEASE(|MEM_PRESERVE_PLACEHOLDER to split)

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeSectionHandle CreateFileMappingW(
        nint file,               // (nint)-1 = INVALID_HANDLE_VALUE -> pagefile-backed
        void* securityAttributes, uint protect, uint maxSizeHigh, uint maxSizeLow, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeSectionHandle OpenFileMappingW(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, string name);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeWaitHandle CreateEventW(void* securityAttributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeWaitHandle OpenEventW(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, string name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetEvent(SafeWaitHandle h);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WaitForSingleObject(SafeHandle h, uint milliseconds); // WAIT_OBJECT_0=0, WAIT_TIMEOUT=0x102, WAIT_FAILED=0xFFFFFFFF

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessTimes(SafeProcessHandle h, out long creation, out long exit, out long kernel, out long user); // FILETIMEs as long

    [LibraryImport("kernel32.dll")]
    internal static partial void GetSystemInfo(out SYSTEM_INFO info);   // dwAllocationGranularity, dwPageSize

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint h);                   // only for ReleaseHandle() implementations
}

internal sealed class SafeSectionHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeSectionHandle() : base(ownsHandle: true) { }   // public parameterless ctor REQUIRED for return marshalling
    protected override bool ReleaseHandle() => Kernel.CloseHandle(handle);
}
```

Constants: `MEM_RESERVE=0x2000`, `MEM_RESERVE_PLACEHOLDER=0x00040000`, `MEM_REPLACE_PLACEHOLDER=0x4000`, `MEM_RELEASE=0x8000`, `MEM_PRESERVE_PLACEHOLDER=0x2`, `PAGE_NOACCESS=0x1`, `PAGE_READWRITE=0x4`, `FILE_MAP_ALL_ACCESS=0xF001F`, `EVENT_MODIFY_STATE=0x2`, `SYNCHRONIZE=0x00100000`, `PROCESS_QUERY_LIMITED_INFORMATION=0x1000`, `ERROR_ALREADY_EXISTS=183`, `ERROR_INVALID_ADDRESS=487`, `ERROR_INVALID_PARAMETER=87`.

Tip: you can skip `CreateFileMappingW`/`OpenFileMappingW` entirely and use the BCL: `MemoryMappedFile.CreateNew(name, capacity)` / `CreateOrOpen` / `OpenExisting(name)` → `mmf.SafeMemoryMappedFileHandle` is a `SafeHandle` that `MapViewOfFile3` accepts directly. The BCL handles naming/security descriptors; you only own the view mapping. (Do not call `mmf.CreateViewAccessor` — you map views yourself.)

### 2.4 Mapping sequence that was executed successfully **[verified locally]**

```
VirtualAlloc2(NULL, NULL, 2*N, MEM_RESERVE|MEM_RESERVE_PLACEHOLDER, PAGE_NOACCESS)  -> P
VirtualFree(P, N, MEM_RELEASE|MEM_PRESERVE_PLACEHOLDER)                             // split into [P,N) [P+N,N)
CreateFileMappingW(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, N, "Local\\name")  -> S
MapViewOfFile3(S, NULL, P,   0, N, MEM_REPLACE_PLACEHOLDER, PAGE_READWRITE)         -> P
MapViewOfFile3(S, NULL, P+N, 0, N, MEM_REPLACE_PLACEHOLDER, PAGE_READWRITE)         -> P+N
span[0]=42  => span[N/4]==42   (mirror confirmed)
UnmapViewOfFile2(-1, P+N, MEM_PRESERVE_PLACEHOLDER); UnmapViewOfFile2(-1, P, MEM_PRESERVE_PLACEHOLDER)
VirtualFree(P, 0, MEM_RELEASE); VirtualFree(P+N, 0, MEM_RELEASE)
```

Constraints (from the VirtualAlloc2/MapViewOfFile3 docs): `N` must be a multiple of the **allocation granularity (64 KiB)** (the doc sample rejects anything else); placeholder base must be 64K-aligned; `ViewSize` must exactly equal the placeholder size; `Offset` must be page-aligned when `MEM_REPLACE_PLACEHOLDER` is used (64K-aligned otherwise). For a control block, put a 64 KiB header at section offset 0 and the data at offset 65536; map the header once (plain `MapViewOfFile3` with `baseAddress = NULL`, `allocationType = 0`, size 65536) and the data twice into a 2N placeholder. Second process, best-effort same address: read `CreatorBase` from the header, `VirtualAlloc2(NULL, creatorBase, 2N, MEM_RESERVE|MEM_RESERVE_PLACEHOLDER, PAGE_NOACCESS)`; on failure (`ERROR_INVALID_ADDRESS` 487 — range in use) retry with `NULL`. Store only *offsets* in shared memory anyway; treat the same-address success as an optimization flag (`header.SameAddressMapped` per reader is informational).

---

## 3. `ref struct` design (C# 13/14 rules — from the ref-struct language reference, **behaviour verified locally**)

Rules that matter here:

- A `ref struct` may contain: class references (your owner `RingBuffer<T>`), `Span<T>`/`ReadOnlySpan<T>`, pointers, `ref` fields (C# 11+), other ref structs. It cannot be a field of a class/non-ref struct, an array element, boxed, or captured by a lambda/local function.
- **Pattern-based `using`** (C# 8+): a `ref struct` with an accessible, parameterless, `void Dispose()` works with `using var x = ...;` and `using (var x = ...) {}`. `IDisposable` not required.
- **C# 13**: a `ref struct` *may* implement interfaces, including `IDisposable`; overload resolution still prefers the pattern `Dispose` over `IDisposable.Dispose`. It can only be *used* as an interface through a type parameter declared `where T : IDisposable, allows ref struct` (verified: `static void Use<T>(T t) where T : IDisposable, allows ref struct`). Implementing an interface from another assembly is a source/binary-break risk if that interface later gains default members (the struct must implement *all* instance members, even defaulted ones).
- **C# 13**: `ref struct` locals are allowed in `async` methods as long as they are not in the same *block* as an `await`; verified: `await Task.Yield(); { o.TryRead(out var chunk); use(chunk.Span); } await Task.Yield();` compiles and runs. So the intended pattern `await reader.Wait(100); reader.TryRead(100, out var chunk); ...` must put the `TryRead` + use in a nested block (or a separate synchronous helper) — the ref struct cannot live across the `await`. Same for iterators (`yield`). Document this in XML docs on `Wait`.
- **`using var` locals are read-only but NOT defensively copied for mutating calls [verified]**: `using var b = owner.GetBucket(4); b.Commit(3);` — `Dispose()` observed `committed == 3`. So a mutable `Bucket` with `Commit` setting a field and `Dispose` reading it is correct. (`b = default;` is CS1656.)
- A non-`readonly` ref struct passed by value to a method is copied — never pass `Bucket` around; keep it a local. Mark `Chunk` as `readonly ref struct` (it has nothing to mutate).

Recommendation: implement `Dispose()` via the pattern **and** implement `IDisposable` on `Bucket` (C# 13) — `IDisposable` is BCL, stable, and lets generic helpers `where T : IDisposable, allows ref struct` work. Do not implement any other interface. Shape:

```csharp
public ref struct Bucket<T> where T : unmanaged
{
    private readonly RingBuffer<T> _owner;   // keeps the mapping reachable while the span is alive
    private readonly Span<T> _span;
    private int _committed;                   // -1 = not committed => Dispose commits 0 (abort)
    internal Bucket(RingBuffer<T> owner, Span<T> span) { _owner = owner; _span = span; _committed = -1; }
    public Span<T> Span => _span;
    public void Commit(int count)             // count <= Span.Length; publishes with release semantics
    { ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)count, (uint)_span.Length); _committed = count; }
    public void Dispose() { _owner.EndWrite(_committed < 0 ? 0 : _committed); }
}

public readonly ref struct Chunk<T> where T : unmanaged
{
    public ReadOnlySpan<T> Span { get; }
    internal Chunk(ReadOnlySpan<T> s) => Span = s;
}
```

Optional C# 13 `[OverloadResolutionPriority]` if you expose both `Span<T>`- and array-taking overloads. C# 14 first-class span conversions mean `ReadOnlySpan<T>` params accept arrays/`Span<T>` without explicit `.AsSpan()`.

---

## 4. `Span<T>` over native memory

- `new Span<T>(void* pointer, int length)` — the constructor is `unsafe`; at runtime throws `ArgumentException` if `T` contains references (`RuntimeHelpers.IsReferenceOrContainsReferences<T>()`); no alignment check. Max `length` is `int.MaxValue` elements; a 2 GiB+ *byte* ring is still fine for `T = float` (≤ 2^31 elements), but bucket/chunk lengths must be `int`. Keep cursors as `ulong` monotonic counters in shared memory and expose `int` chunk lengths.
- `MemoryMarshal.CreateSpan(ref Unsafe.AsRef<T>(ptr), length)` and `MemoryMarshal.CreateReadOnlySpan` — same thing without the `unsafe` keyword at the call site (still needs pointer → ref). `MemoryMarshal.Cast<TFrom,TTo>` reinterprets.
- Pointer math: `Unsafe.Add(ref r, (nint)i)` (use `nint` overload to avoid `int` overflow), `Unsafe.AsPointer`, `Unsafe.AsRef<T>(void*)`. `ref T` into unmanaged memory is legal and the GC ignores it (no pinning needed — it is not GC-tracked memory). Prefer `ref`/`Span` over raw pointer arithmetic in hot code; the JIT hoists bounds checks on span loops.
- Element size: `sizeof(T)` for `T : unmanaged` compiles only in an `unsafe` context and is **not** a constant for generic `T` (it becomes a JIT intrinsic, resolved per instantiation — free). `Unsafe.SizeOf<T>()` gives the same value without `unsafe`. Both return the *managed* layout size (`Marshal.SizeOf` returns the marshalled size — never use it here). Verified `sizeof(float)==Unsafe.SizeOf<float>()==4`.
- Alignment: view base is 64K-aligned; keep header fields on their own **64-byte cache lines** (`[StructLayout(LayoutKind.Explicit, Size = 64)]` slots, or `FieldOffset` multiples of 64) so writer cursor, per-reader cursors and the "waiting" flags never share a line (false sharing is the #1 killer of the µs target). Data offset (e.g. 65536) is a multiple of every `sizeof(T)` ≤ 64 K, so element 0 is naturally aligned; require `N % (64 K) == 0` so wraparound preserves alignment. Reject `T` with `sizeof(T)` not a power of two, or round the element region so that `Capacity * sizeof(T) == N`; simplest: `Capacity = N / sizeof(T)` and demand `N % sizeof(T) == 0`.
- `Span.Fill`, `CopyTo`, `Clear` vectorize; `MemoryMarshal.AsBytes` for byte-level I/O.
- For unaligned `T` (explicit-layout structs with odd size), reads through `Span<T>` are fine on x64 but `Interlocked`/`Volatile` on such elements are not — never put synchronization words inside the data region.

---

## 5. `Volatile` / `Interlocked` on .NET 10

- `Volatile.Read(ref T)` / `Volatile.Write(ref T, T)` exist for `bool, byte, sbyte, short, ushort, int, uint, long, ulong, nint, nuint, float, double` and reference `T`. They take `ref`, and a `ref` obtained from native memory is fine: `Volatile.Read(ref Unsafe.AsRef<long>(p))` or `Volatile.Read(ref *(long*)p)` **[verified locally]**. Semantics per the runtime memory-model spec: volatile read = **acquire**, volatile write = **release** (not sequentially consistent — a reader can still see a stale value for a while; poll or wait).
- **New in .NET 10: `Volatile.ReadBarrier()` and `Volatile.WriteBarrier()`** (verified to compile on 10.0.12): a standalone acquire fence / release fence — cheaper than a full `Thread.MemoryBarrier()` (`mfence`/`dmb ish`). Pattern: plain-read a cursor then `ReadBarrier()`, or fill the span then `WriteBarrier()` then plain-store the cursor. On x64 these compile to nothing (compiler-only fence); on ARM64 to `dmb ishld`/`dmb ish`.
- `Interlocked.CompareExchange(ref long, long, long)`, `Exchange`, `Add`, `Increment`, `Or/And` (since .NET 5) all accept `ref` into native memory; `ulong`/`uint`/`nint`/`nuint` overloads exist. All are **full fences**. Memory must be naturally aligned: x64 `lock cmpxchg` on a line-straddling address is a split lock (very slow, and Windows can raise an exception when split-lock detection is on); ARM64 faults. `Interlocked.Read(ref long)` is only for 32-bit processes.
- `Thread.MemoryBarrier()` == `Interlocked.MemoryBarrier()` == full fence (`mfence`-equivalent; JIT uses `lock or [rsp],0` on x64). `Interlocked.MemoryBarrierProcessWide()` = `FlushProcessWriteBuffers` (µs-expensive, only for asymmetric schemes — do not use).
- ARM64: RyuJIT emits `ldar`/`ldapr` for volatile loads and `stlr` for volatile stores (acquire/release instructions, not full barriers), and `casal`/LSE atomics for `Interlocked` — so the same code is correct and cheap on ARM64. The memory-model doc explicitly states that access through *unmanaged pointers* is outside the runtime's guarantees — which just means *you* must guarantee alignment; the instructions emitted are the same.
- Plain (non-volatile) reads/writes of aligned ≤ pointer-size primitives are atomic; the JIT may hoist/elide them inside a loop — so a spin loop must use `Volatile.Read` (or `SpinWait`, which contains a barrier) or it may never observe the update.
- Cross-process: the section is ordinary cacheable memory; acquire/release on the cursors is exactly what you need. Nothing special about it being shared across processes on the same machine.

Recommended protocol: writer publishes with `Volatile.Write(ref hdr.WritePos, newPos)` after filling; reader reads `Volatile.Read(ref hdr.WritePos)` then slices; reader releases with `Volatile.Write(ref slot.ReadPos, pos)`; writer computes free space from `min(slot.ReadPos)` over live slots using `Volatile.Read`. Only slot registration/eviction needs `Interlocked.CompareExchange` (claiming a slot by CAS-ing `Pid` from 0).

---

## 6. Async waiting on Win32 events with zero steady-state allocation

### 6.1 Wrapping raw handles

- `new SafeWaitHandle(nint handle, ownsHandle: true)` wraps a raw `HANDLE`; assign to `new EventWaitHandle(false, EventResetMode.AutoReset) { SafeWaitHandle = h }` — but the constructor *also creates* a kernel event that is immediately replaced (leaked until finalized/disposed). Cleaner: subclass `WaitHandle` (abstract, protected ctor — no kernel object created) and set `SafeWaitHandle`, or avoid `WaitHandle` entirely and call `WaitForSingleObject(SafeWaitHandle, ms)` yourself via LibraryImport (no managed wait machinery, no `SynchronizationContext` checks, no `WaitHandle.WaitOne` overhead of `Thread.CurrentThread` bookkeeping). `WaitHandle.WaitOne` on Windows ultimately calls `WaitForMultipleObjectsEx` and is fine for the slow path.
- Named events across processes: `new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\photone-<id>-r<n>", out bool createdNew)` **creates-or-opens** in one call (no interop needed, returns a managed `EventWaitHandle`; marked `[SupportedOSPlatform("windows")]`). `EventWaitHandle.OpenExisting(name)` throws `WaitHandleCannotBeOpenedException` if absent; `TryOpenExisting` returns `false`. Raw `OpenEventW(SYNCHRONIZE|EVENT_MODIFY_STATE, false, name)` is equivalent minus the exception. Use `Local\` (session namespace) unless you need cross-session (`Global\` requires `SeCreateGlobalPrivilege` for services). Names are UTF-16, ≤ `MAX_PATH`.
- `WaitOnAddress` / `WakeByAddressSingle` is **process-local** (the wait queue is keyed by virtual address inside one process) — it does *not* work across processes even with the same mapped address. Do not use it for the IPC path (fine for the in-process fast path if you ever specialize).

### 6.2 `ThreadPool.RegisterWaitForSingleObject`

Per docs: it registers the `WaitHandle` with a thread-pool *wait thread* (uses `WaitForMultipleObjects`, ≤ 63 handles per wait thread; more handles → more wait threads), invokes the callback on a worker thread, and returns a `RegisteredWaitHandle` that you must `Unregister`. **Each call allocates**: a `RegisteredWaitHandle` object, the `WaitOrTimerCallback` delegate (unless cached), the state box, plus the internal wait-thread registration; the callback also hops to a worker thread (one extra context switch ≈ 5–20 µs). Docs also warn: do not `PulseEvent`, and duplicate the handle if the same native handle is registered more than once. Verdict: acceptable for a one-time "wait for peer to appear", **not** for the per-`Wait(n)` hot path.

### 6.3 Zero-allocation `ValueTask` waits: `ManualResetValueTaskSourceCore<T>`

`System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore<TResult>` is a mutable struct you embed in a *reusable* class implementing `IValueTaskSource`/`IValueTaskSource<T>`. Rules: `Reset()` before each reuse (bumps `Version`), hand out `new ValueTask(this, core.Version)`, the consumer must `await` exactly once (no `.Result` before completion, no double-await), set `RunContinuationsAsynchronously = true` unless you *want* the continuation to run inline on the completing thread (for latency you probably do — then the dedicated wait thread runs user code; make it a documented option). `SetResult`/`SetException` complete it. This is exactly how `Socket`'s `SocketAsyncEventArgs` and `Channel<T>` avoid allocations.

Shape:

```csharp
public sealed class Reader<T> : IValueTaskSource<bool>, IThreadPoolWorkItem where T : unmanaged
{
    private ManualResetValueTaskSourceCore<bool> _vts = new() { RunContinuationsAsynchronously = true };
    private long _wanted;                      // elements requested by the pending Wait
    private readonly SafeWaitHandle _event;    // per-reader auto-reset named event, created by the reader
    private readonly Thread? _waiter;          // dedicated, or use a shared per-process wait thread (see below)

    public ValueTask<bool> Wait(int count, CancellationToken ct = default)
    {
        if (Available() >= count) return ValueTask.FromResult(true);        // fast path, no await, no alloc
        for (int i = 0; i < SpinIterations; i++) { Thread.SpinWait(20); if (Available() >= count) return ValueTask.FromResult(true); }
        _vts.Reset();
        _wanted = count;
        Volatile.Write(ref Slot.Waiting, 1);   // tell the writer a syscall is needed
        if (Available() >= count) { Volatile.Write(ref Slot.Waiting, 0); return ValueTask.FromResult(true); } // re-check after publishing intent (avoids lost wakeup)
        ArmWaitThread();                        // wait thread blocks on _event (+ writer-process handle for death detection)
        return new ValueTask<bool>(this, _vts.Version);
    }
    // wait thread, on WAIT_OBJECT_0: if (Available() >= _wanted) { Volatile.Write(ref Slot.Waiting, 0); _vts.SetResult(true); } else re-wait
    bool IValueTaskSource<bool>.GetResult(short token) => _vts.GetResult(token);
    ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _vts.GetStatus(token);
    void IValueTaskSource<bool>.OnCompleted(Action<object?> c, object? s, short t, ValueTaskSourceOnCompletedFlags f) => _vts.OnCompleted(c, s, t, f);
    void IThreadPoolWorkItem.Execute() { /* optional: ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal:false) to hop off the wait thread with zero alloc */ }
}
```

Writer side after `Commit`: `Volatile.Write(ref hdr.WritePos, ...)`; then `Thread.MemoryBarrier()` (store→load fence: the write of `WritePos` must be visible before reading `Waiting`, the classic Dekker pattern — `Volatile.Write` + `Volatile.Read` alone is *not* enough on x64 either, because a store can be reordered after a later load); then `for each live slot: if (Volatile.Read(ref slot.Waiting) != 0) SetEvent(slot.Event)`. Reader side symmetric: `Volatile.Write(Waiting,1); Thread.MemoryBarrier(); re-read WritePos`. That is what guarantees "writer does no syscall when nobody waits" without lost wakeups. Cache the reader events in the writer process (open by name once when the slot appears; `OpenEventW` costs ~10 µs).

Wait threads: one **dedicated foreground thread per process** doing `WaitForMultipleObjects` over {all local readers' events, writer-death handles, a control event} scales to 63 handles; beyond that, spawn another. Use `new Thread(..., maxStackSize: 256*1024) { IsBackground = true, Name = "photone-wait" }`. Alternatively one thread per `Reader` (simplest, fine for a handful of readers).

### 6.4 Spinning primitives

- `SpinWait` struct: `SpinOnce()` spins with `Thread.SpinWait`, then `Thread.Yield`/`Sleep(0)`, and after ~20 iterations **`Sleep(1)`** (≥ 1 ms, 15.6 ms with default timer resolution) — catastrophic for µs latency. Use `SpinOnce(sleep1Threshold: -1)` (.NET Core 3.0+) to disable `Sleep(1)`, or write your own loop.
- `Thread.SpinWait(int iterations)`: each iteration is a `pause`; the runtime normalizes so ~35–50 iterations ≈ 1 µs on modern x64 (the value is measured at startup: `pause` costs ~140 cycles on Skylake+, ~10 on older). `System.Runtime.Intrinsics.X86.X86Base.Pause()` (.NET 7+) is one raw `pause` when `X86Base.IsSupported`; ARM64: `ArmBase.Yield()` (.NET 8+). Prefer `Thread.SpinWait(n)` — it is portable and already an intrinsic.
- Budget: spin ~2–20 µs (e.g. 200 × `SpinWait(20)`) before falling to the kernel wait; make it configurable (`SpinWaitOptions`). `Thread.Yield()` = `SwitchToThread` (only yields to threads on the same core); `Thread.Sleep(0)` = yield to equal-priority on any core.
- `Stopwatch.GetTimestamp()` (QPC, ~20 ns) to bound spin by time rather than iterations.

---

## 7. Process liveness / dead-reader eviction

- `Process.GetProcessById(pid)`: on Windows it calls `ProcessManager.IsProcessRunning` → `EnumProcesses` (allocates an `int[]` of *all* PIDs, retried with growth) then allocates a `Process` object; `HasExited` then does `OpenProcess`+`WaitForSingleObject` anyway, and `Process` holds finalizable handles. Cost: tens of µs + allocations. Do not use on the hot path.
- Cheap check: `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, false, pid)`. `NULL` with `ERROR_INVALID_PARAMETER` (87) → no such PID; `ERROR_ACCESS_DENIED` (5) → exists but protected (treat as alive). Then `WaitForSingleObject(h, 0)`: `WAIT_OBJECT_0` → exited (process objects become signaled at termination), `WAIT_TIMEOUT` (0x102) → alive. `OpenProcess` ≈ 1–3 µs, the wait ≈ 200 ns. **Open once per reader slot and cache the `SafeProcessHandle` in the writer**; after that liveness is a 0-ms wait. Even better: when the writer must block for space, wait on `WaitForMultipleObjects({space-freed event} ∪ {reader process handles}, waitAll:false, timeout)` — a reader death wakes the writer immediately, no polling.
- **PID reuse**: Windows reuses PIDs quickly (multiples of 4). Disambiguate by storing the process **creation time** in the slot at registration: `GetProcessTimes(GetCurrentProcess(), out creation, ...)` (FILETIME as `long`; managed equivalent `Process.GetCurrentProcess().StartTime` allocates — do it once). Writer validates `GetProcessTimes(cachedHandle).creation == slot.StartTime`; mismatch → the slot's owner is dead and a new process got the PID → evict. `SafeProcessHandle` (Microsoft.Win32.SafeHandles) is the right handle wrapper; `Process.SafeHandle` exists too but drags in `Process`.
- Eviction: `Interlocked.CompareExchange(ref slot.Pid, 0, deadPid)` then recompute `MinReadPos`. Also self-eviction: reader `Dispose` clears its slot; register `AppDomain.CurrentDomain.ProcessExit`/`UnhandledException` best-effort — but design for the crash case only via PID liveness. A dying *writer* is detected by readers the same way (`hdr.WriterPid`, `hdr.WriterStartTime`); `Wait` should then return `false`/throw `WriterExitedException`.
- Never store handles in shared memory (handles are per-process); store PID + start time + a `uint` slot generation.

---

## 8. Testing & benchmarking (versions verified on nuget.org, 2026-09-16)

| Package | Version | Notes |
|---|---|---|
| `xunit.v3` | **4.0.1** (2026-09-12) | 4.0.0 (2026-08-15) dropped MTP v1; requires Microsoft.Testing.Platform **v2** (pulled transitively via `xunit.v3.mtp-v2`); Native AOT test mode; full method-level parallelism by default (opt out per class with `[Collection]`/`DisableParallelization` for the cross-process tests). |
| `xunit.runner.visualstudio` | **4.0.0** (2026-08-15) | Needed for VS Test Explorer / VSTest compatibility; matches v3 4.x. |
| `Microsoft.NET.Test.Sdk` | **18.10.1** (2026-09-15; 18.10.0 on 2026-09-09) | Optional in MTP mode; xunit docs recommend keeping it + runner.visualstudio for backward-compat tooling. Verified 18.10.1 restores/works with the above. |
| `BenchmarkDotNet` | **0.15.8** (stable, 2025-11-30); `0.16.0-preview.1` (2026-06-30) | 0.15.x supports net10.0; use stable. |
| `BenchmarkDotNet.Diagnostics.Windows` | 0.15.8 | `EtwProfiler`, `NativeMemoryProfiler` (needs admin). |

xunit v2 is maintenance-only; on .NET 10 choose **v3**. Test project (**[verified locally]**: restores/builds/runs on SDK 10.0.112 with the `global.json` above; `dotnet test` reported `Passed! total: 1`):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>                      <!-- xunit.v3 test projects are executables -->
    <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
    <Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" Version="4.0.1" />
    <PackageReference Include="xunit.runner.visualstudio" Version="4.0.0" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.10.1" />
    <ProjectReference Include="..\Photone.Ipc\Photone.Ipc.csproj" />
    <ProjectReference Include="..\Photone.Ipc.TestChild\Photone.Ipc.TestChild.csproj" />  <!-- console exe -->
  </ItemGroup>
</Project>
```

Child process strategy (**verified**): a plain `ProjectReference` to a console-exe project copies **`Child.exe` (apphost), `Child.dll`, `Child.runtimeconfig.json`, `Child.deps.json`** into the test output directory (`AppContext.BaseDirectory`). So:

```csharp
var exe = Path.Combine(AppContext.BaseDirectory, "Photone.Ipc.TestChild.exe");
var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true };
psi.ArgumentList.Add("reader"); psi.ArgumentList.Add(sectionName); psi.ArgumentList.Add(expectedBase.ToString("X"));
using var p = Process.Start(psi)!;
```

`ArgumentList` handles quoting; the child does `args[1]` for the section name and `Convert.ToUInt64(args[2], 16)`. Fallback if the apphost is absent (e.g. `UseAppHost=false`): `new ProcessStartInfo("dotnet") { ArgumentList = { dll, ... } }`. Do **not** use `dotnet run --project` (MSBuild evaluation ≈ 1–3 s per spawn). Do not try to re-enter the xunit.v3 test exe as the child — its generated `Main` parses MTP args. Use a unique section name per test (`$"Local\\photone-test-{Environment.ProcessId}-{Guid.NewGuid():N}"`) so parallel tests do not collide, and put cross-process tests in a `[Collection("ipc")]` with `DisableParallelization = true` if they measure timing. Handshake: child signals a named event (or writes its `Ready` flag into the header) — never `Thread.Sleep`.

Running: `dotnet test` (MTP via global.json), filter: `dotnet test --filter-class Photone.Ipc.Tests.CrossProcessTests`, or run the exe directly: `bin\Release\net10.0\Photone.Ipc.Tests.exe --filter-method "*Latency*" --xunit-info`.

BenchmarkDotNet shape:

```csharp
[MemoryDiagnoser]                      // proves 0 B/op on the hot path
[SimpleJob(RuntimeMoniker.Net10_0)]    // or InProcess: [InProcess] / InProcessEmitToolchain for quick iteration
[DisassemblyDiagnoser(maxDepth: 2)]    // inspect ldar/stlr / lock cmpxchg
public class RingBufferBench
{
    Process? _peer; RingBuffer<float> _rb = null!; Reader<float> _reader = null!;

    [GlobalSetup(Target = nameof(CrossProcessRoundTrip))]
    public void SetupCross() { /* create rb + spawn TestChild "echo" that reads from A and writes to B; wait for ready */ }

    [Benchmark(OperationsPerInvoke = 1)]
    public void InProcessRoundTrip() { using (var b = _rb.GetBucket(16)) b.Commit(16); _reader.WaitSync(16); _reader.TryRead(16, out var c); _reader.Advance(16); }

    [Benchmark] public void CrossProcessRoundTrip() { /* write 16 to A, spin-wait on B reader, advance */ }

    [Benchmark] public void Throughput() { /* 64 KiB buckets, OperationsPerInvoke = bytes */ }

    [GlobalCleanup] public void Cleanup() { _peer?.Kill(); _rb.Dispose(); }
}
// Program: BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);  -- exe project, Release, <Optimize>true>
```

Cross-process latency is a *round trip*/2; report p50/p99 via `[StatisticalTestColumn]` or export raw with `--exporters json`. Use `Job.Default.WithGcConcurrent(false).WithAffinity((IntPtr)0b0100)` to pin the benchmark process and pin the child with `SetThreadAffinityMask` or `Process.ProcessorAffinity` to a *different* core (same-core measurements are meaningless). Run as admin with high-resolution timer (`timeBeginPeriod(1)` is unnecessary if nothing sleeps).

---

## 9. Native resource lifetime

- **`SafeHandle`** (derives from `CriticalFinalizerObject`): ref-counted, thread-safe close, integrates with LibraryImport, prevents handle recycling races. Use for: section (`SafeSectionHandle`/`SafeMemoryMappedFileHandle`), events (`SafeWaitHandle`), process handles (`SafeProcessHandle`). Manual `nint` + `CloseHandle` only inside `ReleaseHandle`.
- **The double-mapped region** is *not* a handle; model it as `sealed class MappedRegion : SafeHandle` anyway (handle = placeholder base pointer, `IsInvalid => handle == 0`), storing `size` and the two view pointers, with `ReleaseHandle()` = `UnmapViewOfFile2(view2, 0)`, `UnmapViewOfFile2(view1, 0)`, then (only if any placeholder remains reserved) `VirtualFree`. Unmapping in a finalizer is safe: it touches no managed state, `kernelbase` is always loaded, and the kernel keeps the section alive while views exist, so it does not matter whether the section `SafeHandle` was finalized first. Critical finalizers run **after** all ordinary finalizers in the same finalization pass on CoreCLR, so an ordinary `~RingBuffer()` cannot observe an already-unmapped region. (CER/`PrepareConstrainedRegions` no longer exist on .NET Core; `CriticalFinalizerObject` only affects ordering and is still honored.)
- `RingBuffer<T>` itself: `IDisposable` + finalizer-free (delegate all native ownership to the `SafeHandle`s); `Dispose()` → unregister slot, `_region.Dispose()`, `_section.Dispose()`, `_event.Dispose()`; `GC.SuppressFinalize(this)` only if you *have* a finalizer — you should not.
- Span/pointer lifetime: `Bucket`/`Chunk` hold a reference to the owning class → the region cannot be finalized while a span is in use on the stack (the ref struct is a GC root for the class). Still guard `Dispose` with a volatile `_disposed` and check it in `GetBucket`/`TryRead` (throw `ObjectDisposedException.ThrowIf`). Do **not** hand out `Memory<T>`/`IMemoryOwner<T>` over the region unless you implement a `MemoryManager<T>` with proper lifetime — spans only.
- Threading: `SafeHandle.DangerousAddRef/Release` are what LibraryImport uses; if you cache `handle.DangerousGetHandle()` as `nint` for a hot-path `SetEvent` via a `nint`-taking import, hold an explicit `DangerousAddRef` for the buffer's lifetime (release in `Dispose`).
- Keep `SafeHandle` members `private readonly`; expose `SafeMemoryMappedFileHandle SectionHandle` only if you need `DuplicateHandle` for handle-based (unnamed) sharing.

---

## 10. Pitfalls (one line each)

1. `dotnet` without `global.json` on this box uses SDK **11.0.100-preview.7** — pin 10.0.112.
2. `kernel32.dll` does **not** export `VirtualAlloc2`/`MapViewOfFile3`/`UnmapViewOfFile2` (even on Win11 26200) → `EntryPointNotFoundException`; use `kernelbase.dll`.
3. `onecore.dll` is not a real DLL (`onecore.lib` is link-time umbrella) → `DllNotFoundException`.
4. LibraryImport `bool` without `[MarshalAs(UnmanagedType.Bool)]` is a compile error (SYSLIB1051), not a silent 1-byte marshal.
5. LibraryImport does no A/W probing — `CreateFileMapping` without `W` is `EntryPointNotFoundException`.
6. `StringMarshalling.Utf16` is required for any `string` parameter on W APIs; a `string` with no marshalling setting is a generator error.
7. Forgetting `SetLastError = true` makes `Marshal.GetLastPInvokeError()` return stale garbage from an earlier call.
8. `Marshal.GetLastWin32Error()` after a *managed* call that itself P/Invoked (e.g. `Console.WriteLine`) is already clobbered — read the error immediately.
9. Pointer parameters need the declaring class/method `unsafe`; `nint` does not, so use `nint` for `HANDLE` and `void*` for addresses (never `IntPtr` for addresses you do arithmetic on — use `byte*`/`nuint`).
10. `HANDLE` is `nint`, not `int`; `INVALID_HANDLE_VALUE` is `(nint)-1`, `NULL` is `0` — `CreateFileMappingW` fails with **NULL** while `CreateFileW` fails with **-1** (`SafeHandleZeroOrMinusOneIsInvalid` covers both).
11. `SafeHandle` return types need a **public** parameterless ctor or the generator refuses.
12. `MapViewOfFile3` with `MEM_REPLACE_PLACEHOLDER` fails with `ERROR_INVALID_PARAMETER` unless `ViewSize` equals the placeholder size exactly and the size is a multiple of 64 KiB.
13. After a successful `MapViewOfFile3` the placeholder is consumed — do not `VirtualFree` it; after `UnmapViewOfFile2(..., MEM_PRESERVE_PLACEHOLDER)` you *must* `VirtualFree` it.
14. `UnmapViewOfFile` (classic) on a view that replaced a placeholder also works (returns the range to free state), but mixing styles makes the finalizer logic error-prone — always use `UnmapViewOfFile2`.
15. `sizeof(T)` for a struct with `[StructLayout(Explicit/Sequential, Size=…, Pack=…)]` is the *managed* size; `Marshal.SizeOf<T>` may differ — never use `Marshal.SizeOf` for element stride.
16. `T : unmanaged` allows `bool`/`char`/`decimal` — `bool` is 1 byte managed but 4 bytes when marshalled; irrelevant for spans, just don't `MarshalAs` it.
17. `Span<T>` over `float` at a non-4-aligned offset works on x64 but is slow and faults on ARM64 SIMD paths; keep the data offset a multiple of 64.
18. `Volatile.Write` + `Volatile.Read` does **not** prevent store-load reordering (x64 included) — the "set Waiting then re-check WritePos" handshake needs `Thread.MemoryBarrier()` (or an `Interlocked` op) on both sides.
19. `SpinWait.SpinOnce()` calls `Sleep(1)` after ~20 iterations (≥ 1 ms) — pass `sleep1Threshold: -1`.
20. `ThreadPool.RegisterWaitForSingleObject` allocates per call and adds a worker-thread hop — not for the per-wait hot path.
21. `WaitOnAddress`/`WakeByAddress*` are process-local; useless for cross-process wakeups.
22. `new EventWaitHandle(...) { SafeWaitHandle = raw }` leaks the event the ctor created until the replaced handle is finalized — subclass `WaitHandle` or use the named-event ctor with `out createdNew`.
23. `AutoResetEvent` wakes exactly one waiter — one event per reader, never a shared auto-reset event for broadcast; a shared `ManualResetEvent` needs an explicit reset protocol (races) — avoid.
24. `Process.GetProcessById` enumerates all PIDs and allocates — use `OpenProcess`+`WaitForSingleObject(h,0)` and cache the handle.
25. PID reuse: a reader slot keyed by PID alone can be "resurrected" by an unrelated new process — store creation time (`GetProcessTimes`) and compare.
26. A `ref struct` cannot be in the same block as an `await` (C# 13) — `TryRead` + span use must sit in a nested block or a sync helper after `await Wait()`.
27. `using var bucket` is a readonly local: `bucket = …` is CS1656, but mutating method calls are **not** defensively copied (verified) — the pattern `Commit` then implicit `Dispose` works.
28. Passing a non-readonly `ref struct` by value to a helper copies it — `Commit` on the copy is lost; pass `ref`/`scoped ref` or keep it local.
29. `ref struct` implementing an interface: you cannot cast it to the interface (boxing) — only reachable through `where T : IFoo, allows ref struct`.
30. Named-object namespace: `Local\` is per-session; `Global\` needs `SeCreateGlobalPrivilege` for creation from services; forgetting the prefix defaults to `Local\` for interactive sessions but is ambiguous — always be explicit.
31. Section capacity in `CreateFileMappingW` is split into `maxSizeHigh/maxSizeLow` (`(uint)(size >> 32)`, `(uint)size`); passing `0/0` with `INVALID_HANDLE_VALUE` fails (`ERROR_INVALID_PARAMETER`).
32. `MemoryMappedFile.CreateNew` capacity is rounded to page size, not 64 KiB — request an explicit multiple of 65536.
33. `Interlocked` on a field of an *explicit-layout* header struct at a non-8-aligned `FieldOffset` = split lock / ARM64 fault; keep every sync word at `offset % 8 == 0` (ideally `% 64 == 0`).
34. `[MemoryDiagnoser]` reports 0 B only if the *whole* call is allocation-free — `ValueTask.FromResult` is fine, `Task.FromResult` is not, and `async ValueTask` methods still allocate the state machine box when they actually suspend (use the `IValueTaskSource` design, not `async`).
35. `Environment.ProcessPath` inside an MTP xunit.v3 run is the **test exe** (not `dotnet.exe`) — do not use it to locate a `dotnet` host for children; spawn the copied child apphost.
36. Test parallelism in xunit.v3 4.x is per-method by default — cross-process/latency tests need `[Collection]` with `DisableParallelization = true` and per-test unique section names.
37. `TreatWarningsAsErrors` + `IsAotCompatible` will flag `Process`/`Reflection` usage in the library — keep those out of the library (tests only).

---

### Recommended package/version summary

```
SDK (global.json)                  10.0.112 (rollForward latestPatch); test.runner = Microsoft.Testing.Platform
Library                            net10.0, C# 14 (SDK default), AllowUnsafeBlocks, Nullable, IsAotCompatible, [assembly: SupportedOSPlatform("windows")]
xunit.v3                           4.0.1
xunit.runner.visualstudio          4.0.0
Microsoft.NET.Test.Sdk             18.10.1   (optional under MTP; keep for VS/VSTest compat)
BenchmarkDotNet                    0.15.8    (+ BenchmarkDotNet.Diagnostics.Windows 0.15.8 if ETW profiling)
Native libs                        kernelbase.dll: VirtualAlloc2, MapViewOfFile3, UnmapViewOfFile2 | kernel32.dll: everything else
```

Scratch artifacts backing the "[verified locally]" claims:
`C:\Users\Admin\AppData\Local\Temp\claude\E--GitHub-photone-ipc\1f1c1aea-e7c4-43f4-a5f8-47ebdcd222c5\scratchpad\exportcheck\check.cs` (exports + LibraryImport + placeholder mirror + Volatile/Interlocked on native memory),
`...\scratchpad\tstcheck\` (xunit.v3 4.0.1 MTP test project + child exe spawn),
`...\scratchpad\refstructcheck\rs.cs` (ref struct using/IDisposable/allows ref struct/async-block semantics).

---

# REPORT: research:vmcircbuffer-analysis

# vmcircbuffer (futuresdr) — reference analysis for the photone-ipc .NET port

Source read: `README.md`, `Cargo.toml`, `src/lib.rs`, `src/generic.rs`, `src/sync.rs`, `src/asynchronous.rs`, `src/nonblocking.rs`, `src/lockfree.rs`, `src/double_mapped_buffer/{mod.rs,double_mapped_buffer.rs,windows.rs,unix.rs}`, `tests/{sync,async,lockfree,tags}.rs`, `examples/sdr.rs`. Crate version 0.0.16, edition 2024, deps: `slab`, `spin`, `once_cell`, `thiserror`, optional `futures`; Windows: `winapi` (`sysinfoapi`, `winbase`, `handleapi`, `memoryapi`).

Repository layout:

```
src/lib.rs                                  Notifier + Metadata traits, NoMetadata, feature gates
src/double_mapped_buffer/mod.rs             error enum, pagesize()
src/double_mapped_buffer/double_mapped_buffer.rs  DoubleMappedBuffer<T> (typed wrapper)
src/double_mapped_buffer/windows.rs         DoubleMappedBufferImpl (Windows)
src/double_mapped_buffer/unix.rs            DoubleMappedBufferImpl (Linux/macOS/Android)
src/generic.rs                              Circular/Writer/Reader generic over Notifier + Metadata (Mutex-based)
src/sync.rs, src/asynchronous.rs, src/nonblocking.rs   thin flavors over generic
src/lockfree.rs                             separate SPMC atomic implementation (fixed reader slots)
tests/{sync,async,nonblocking,lockfree,tags}.rs, examples/{sdr.rs,tags.rs,gnuradio/}
```

---

## 1. The `double_mapped_buffer` layer

### 1.1 Granularity ("page size")

On Windows, `pagesize()` deliberately returns the **allocation granularity (64 KiB)**, not the 4 KiB page size, because `MapViewOfFileEx` can only place a view at an address that is a multiple of the allocation granularity:

```rust
#[cfg(windows)]
pub fn pagesize() -> usize {
    *PAGE_SIZE.get_or_init(|| unsafe {
        let mut info: SYSTEM_INFO = std::mem::zeroed();
        GetSystemInfo(&mut info);
        info.dwAllocationGranularity as usize
    })
}
```

On Unix it is `sysconf(_SC_PAGESIZE)` (4 KiB, or 16 KiB on Apple Silicon).

### 1.2 Capacity rounding (exact code, identical on Windows and Unix)

```rust
let ps = pagesize();
let mut size = ps;
while size < min_items * item_size || !size.is_multiple_of(item_size) {
    size += ps;
}
```

Semantics:
- `size` is in **bytes** and is the size of **one** half (the physical buffer). Total virtual reservation is `2 * size`.
- Result = smallest multiple of `ps` that is (a) `>= min_items * item_size` and (b) a multiple of `item_size`. Because any common multiple of `ps` and `item_size` is a multiple of `lcm(ps, item_size)`, this is equivalently "the smallest multiple of `lcm(ps, item_size)` that is `>= max(ps, min_items*item_size)`". The doc comment ("least common multiple of the page size and the size of T") is an approximation of that.
- `min_items == 0` is allowed (`Circular::new()` calls `with_capacity(0)`) and yields exactly one granule (64 KiB on Windows → 16384 `f32`s).
- `capacity() = size_bytes / item_size` (items). Capacity is therefore always exact: no "n-1" slot wasted (the a/b flag, see 2.2, disambiguates full/empty).
- Zero-sized `T` is rejected up front (`DoubleMappedBufferError::ZeroSized`) by the typed wrapper.
- Alignment is checked *after* mapping: `if !(first_tmp as usize).is_multiple_of(alignment) → Err(Alignment)`. It is always satisfied in practice because mapping bases are 64 KiB-aligned.
- The typed wrapper **initializes every slot** of the first mapping with `T::default()` (the second mapping aliases the same storage). Windows pagefile-backed sections are already zero, so for a .NET `T : unmanaged` port this step is redundant unless you want non-zero defaults.

### 1.3 Windows mirror mapping — the OLD racy API, not VirtualAlloc2/MapViewOfFile3

The Rust crate does **not** use `VirtualAlloc2` placeholders or `MapViewOfFile3`. It uses the classic "reserve, free, then race to map twice" trick, wrapped in a 5-attempt retry loop:

```rust
pub fn new(min_items, item_size, alignment) -> Result<Self, DoubleMappedBufferError> {
    for _ in 0..5 {
        let ret = Self::new_try(min_items, item_size, alignment);
        if ret.is_ok() {
            return ret;
        }
    }
    Self::new_try(min_items, item_size, alignment)
}
```

```rust
let handle = CreateFileMappingA(
    INVALID_HANDLE_VALUE,        // pagefile-backed, anonymous
    std::mem::zeroed(),          // no security attributes
    PAGE_READWRITE,
    0,
    size as DWORD,               // NOTE: only the low DWORD -> max 4 GiB half
    std::ptr::null(),            // NO NAME -> not shareable by name
);
if handle == INVALID_HANDLE_VALUE || handle == 0 as LPVOID { return Err(Placeholder); }

// 1. reserve 2*size to find a free address range
let first_tmp = VirtualAlloc(null_mut(), 2 * size, MEM_RESERVE, PAGE_NOACCESS);
if first_tmp.is_null() { CloseHandle(handle); return Err(MapFirst); }

// 2. release it again (this is the race window)
let res = VirtualFree(first_tmp, 0, MEM_RELEASE);
if res == 0 { CloseHandle(handle); return Err(MapSecond); }

// 3. map view #1 at the freed address
let first_cpy = MapViewOfFileEx(handle, FILE_MAP_WRITE, 0, 0, size, first_tmp);
if first_tmp != first_cpy { CloseHandle(handle); return Err(MapFirst); }

if !(first_tmp as usize).is_multiple_of(alignment) { CloseHandle(handle); return Err(Alignment); }

// 4. map view #2 immediately after it
let first_ptr = (first_tmp as *mut u8).add(size) as LPVOID;
let second_cpy = MapViewOfFileEx(handle, FILE_MAP_WRITE, 0, 0, size, first_ptr);
if second_cpy != first_ptr {
    UnmapViewOfFile(first_cpy);
    CloseHandle(handle);
    return Err(MapSecond);
}

Ok(DoubleMappedBufferImpl { addr: first_tmp as usize, handle: handle as usize, size_bytes: size, item_size })
```

Observations:
- Between `VirtualFree` and the two `MapViewOfFileEx` calls another thread can grab the range; that is why there are 6 attempts total. `VirtualAlloc2(MEM_RESERVE | MEM_RESERVE_PLACEHOLDER)` + `VirtualFree(MEM_RELEASE | MEM_PRESERVE_PLACEHOLDER)` split + `MapViewOfFile3(MEM_REPLACE_PLACEHOLDER)` ×2 eliminates the race entirely — this is the approach photone-ipc should use (Win10 1803+), so the retry loop is unnecessary.
- **Validation of the mirror after mapping: none.** The only checks are "the returned view base equals the requested address" for both views and the alignment check. There is no write-through-A/read-through-B probe. (The unit tests `byte_buffer`/`u32_buffer` do that probe in test code: write via `slice_mut()`, read via `slice_with_offset(capacity)`.) A cheap self-test at construction in .NET (write a sentinel to `[0]`, verify `[capacity]`, restore) is worthwhile because a mis-ordered placeholder split silently produces two views that are not adjacent.
- Bug: the `Alignment` error path leaks `first_cpy` (no `UnmapViewOfFile`). Also `MapFirst` error after a failed `VirtualAlloc` is just a plain leak of nothing, fine; `MapSecond` correctly unmaps view 1.
- `size as DWORD` truncates; halves > 4 GiB are silently broken. Use `MaximumSizeHigh/Low` properly (or `CreateFileMappingW` with a 64-bit size) in .NET.
- The section is anonymous and pagefile-backed; the handle is kept only for `CloseHandle` in `Drop`. Nothing exposes the handle or a name, so the buffer is **not** shareable cross-process as written.

Teardown:

```rust
impl Drop for DoubleMappedBufferImpl {
    fn drop(&mut self) {
        unsafe {
            UnmapViewOfFile(self.addr as LPCVOID);
            UnmapViewOfFile((self.addr + self.size_bytes) as LPCVOID);
            CloseHandle(self.handle as HANDLE);
        }
    }
}
```

With `MapViewOfFile3`, each view is likewise released with `UnmapViewOfFile2`/`UnmapViewOfFileEx`; if you keep placeholders around (`MEM_PRESERVE_PLACEHOLDER`) you must additionally `VirtualFree(MEM_RELEASE)` them.

### 1.4 Unix implementation (for completeness)

`mkstemp` in `std::env::temp_dir()` → `unlink` → `ftruncate(fd, 2*size)` → `mmap(NULL, 2*size, MAP_SHARED)` (reserves a contiguous range and maps half #1 — actually maps the whole 2×) → `mmap(buff+size, size, MAP_SHARED|MAP_FIXED, fd, 0)` overlays the second half with offset 0 again → `ftruncate(fd, size)` shrinks the file back → `close(fd)`. Drop: `munmap(addr, 2*size)`. No placeholder race because `MAP_FIXED` over an existing own mapping is atomic. The temp file is unlinked immediately, so again not shareable.

### 1.5 Typed wrapper `DoubleMappedBuffer<T>`

```rust
pub unsafe fn slice_with_offset(&self, offset: usize) -> &[T] {
    debug_assert!(offset <= self.buffer.capacity());
    slice::from_raw_parts((addr as *const T).add(offset), self.buffer.capacity())
}
```

Every view is a full `capacity`-length slice starting at `offset` (0 ≤ offset ≤ capacity), which is valid precisely because the second mapping exists. The circular layer then takes `[0..space]` of that. This is the exact equivalent of the .NET `new Span<T>(basePtr + offset, count)` with `offset + count <= 2*capacity`.

---

## 2. The generic circular buffer layer (`src/generic.rs`)

### 2.1 Types and shared state

```rust
struct State<N, M> {
    writer_offset: usize,
    writer_ab: bool,
    writer_done: bool,
    readers: Slab<ReaderState<N, M>>,
}

struct ReaderState<N, M> {
    ab: bool,
    offset: usize,
    reader_notifier: N,   // wakes THIS reader when data arrives
    writer_notifier: N,   // wakes the writer when THIS reader frees space
    meta: M,
}

pub struct Writer<T, N, M> {
    last_space: usize,
    buffer: Arc<DoubleMappedBuffer<T>>,
    state: Arc<Mutex<State<N, M>>>,     // spin::Mutex
}

pub struct Reader<T, N, M> {
    id: usize,                          // slab key
    last_space: usize,
    buffer: Arc<DoubleMappedBuffer<T>>,
    state: Arc<Mutex<State<N, M>>>,
}
```

- All coordination goes through a single `Arc<spin::Mutex<State>>` shared by the writer and every reader. Readers live in a `Slab` (dense vector with free-list; `id` is the slab key). Everything is in-process heap.
- The buffer itself is an `Arc<DoubleMappedBuffer<T>>`, so it is freed when the last writer/reader drops.
- Single writer only (the `Writer` is a unique owner; `slice()` takes `&mut self`).

### 2.2 Position encoding: offset + a/b flag

Positions are `offset ∈ [0, capacity)` plus a boolean `ab` that flips every time the position wraps. This disambiguates "full" from "empty" when `w_off == r_off` (same offset, same flag = empty; same offset, different flag = full), which is how the crate delivers the full `capacity` rather than `capacity-1`:

```rust
fn writer_space(capacity, w_off, w_ab, r_off, r_ab) -> usize {
    if w_off > r_off        { r_off + capacity - w_off }
    else if w_off < r_off   { r_off - w_off }
    else if r_ab == w_ab    { capacity }   // empty
    else                    { 0 }          // full
}

fn reader_space(capacity, w_off, w_ab, r_off, r_ab) -> usize {
    if r_off > w_off        { w_off + capacity - r_off }
    else if r_off < w_off   { w_off - r_off }
    else if r_ab == w_ab    { 0 }          // empty
    else                    { capacity }   // full
}
```

Advance (both sides use the same pattern):

```rust
if state.writer_offset + n >= self.buffer.capacity() {
    state.writer_ab = !state.writer_ab;
}
state.writer_offset = (state.writer_offset + n) % self.buffer.capacity();
```

The `lockfree.rs` module instead uses monotonically increasing `usize` counters and `w.wrapping_sub(r)` — simpler and better suited to atomics (see 2.9).

### 2.3 Writer: `slice(arm)`

```rust
fn space_and_offset_locked(state, capacity, arm) -> (usize, usize) {
    let w_off = state.writer_offset;
    let w_ab = state.writer_ab;
    let mut space = capacity;
    for (_, reader) in state.readers.iter_mut() {
        let s = Self::writer_space(capacity, w_off, w_ab, reader.offset, reader.ab);
        space = std::cmp::min(space, s);
        if s == 0 && arm {
            reader.writer_notifier.arm();
            break;
        }
        if s == 0 { break; }
    }
    (space, w_off)
}

pub fn slice(&mut self, arm: bool) -> &mut [T] {
    let mut state = self.state.lock();
    let (space, offset) = Self::space_and_offset_locked(&mut state, self.buffer.capacity(), arm);
    self.last_space = space;
    unsafe { &mut self.buffer.slice_with_offset_mut(offset)[0..space] }
}
```

- Free space = **minimum over all live readers** (slowest reader wins). With **zero readers**, space = `capacity` unconditionally, so the writer never blocks and just overwrites (documented: "If there are no Readers, the Writer will not block but continuously overwrite the buffer"; test `no_reader`).
- `arm == true` (blocking flavors) arms the `writer_notifier` of the *first* reader found with 0 space, then stops scanning. When that reader consumes, it notifies; the writer re-runs `slice(true)` and may then arm another full reader. So the writer is woken by exactly one reader at a time; no thundering herd.
- The returned slice may be empty; blocking is done by the flavor wrappers (section 2.7).
- `slice()` never blocks inside `generic`; it is a pure snapshot under the lock.

### 2.4 Writer: `produce(n, meta)`

```rust
pub fn produce(&mut self, n: usize, meta: &[M::Item]) {
    if n == 0 { return; }
    assert!(n <= self.last_space, "vmcircbuffer: produced too much");
    self.last_space -= n;

    let mut state = self.state.lock();
    let capacity = self.buffer.capacity();
    debug_assert!(Self::space_and_offset_locked(&mut state, capacity, false).0 >= n);

    let w_off = state.writer_offset;
    let w_ab = state.writer_ab;
    for (_, r) in state.readers.iter_mut() {
        let space = Reader::<T, N, M>::reader_space(capacity, w_off, w_ab, r.offset, r.ab);
        if !meta.is_empty() { r.meta.add_from_slice(space, meta); }
        r.reader_notifier.notify();
    }
    if state.writer_offset + n >= capacity { state.writer_ab = !state.writer_ab; }
    state.writer_offset = (state.writer_offset + n) % capacity;
}
```

- `n` may be **less than** the last slice length; `last_space` is decremented so you may call `produce` several times after one `slice()` as long as the sum ≤ the slice length. Exceeding it panics (`produce_too_much` test).
- `produce(0)` is a no-op (does not even notify).
- Notifies **every** reader's `reader_notifier` (each notifier only fires if armed → readers that are not waiting cost nothing but a bool check).
- Metadata is fanned out per reader with `offset = that reader's current unread count`, so tag positions are relative to each reader's read cursor.

### 2.5 `add_reader` — late-joining readers

```rust
pub fn add_reader(&self, reader_notifier: N, writer_notifier: N) -> Reader<T, N, M> {
    let mut state = self.state.lock();
    let reader_state = ReaderState {
        ab: state.writer_ab,
        offset: state.writer_offset,
        reader_notifier, writer_notifier,
        meta: M::new(),
    };
    let id = state.readers.insert(reader_state);
    Reader { id, last_space: 0, buffer: self.buffer.clone(), state: self.state.clone() }
}
```

A new reader starts **at the writer's current position** — it sees nothing already produced, only future data (test `late_reader`: produce 100, add reader → `try_slice().len() == 0`; produce 100 more → reader sees exactly those 100). `add_reader` takes `&self`, so readers can be added at any time, from any thread, unbounded in number (slab grows).

### 2.6 Reader: `slice(arm)`, `consume(n)`, drop

```rust
fn space_and_offset_locked(state, id, capacity, arm) -> (usize, usize, bool) {
    let done = state.writer_done;
    let w_off = state.writer_offset;
    let w_ab = state.writer_ab;
    let my = unsafe { state.readers.get_unchecked_mut(id) };
    let space = Self::reader_space(capacity, w_off, w_ab, my.offset, my.ab);
    if space == 0 && arm { my.reader_notifier.arm(); }
    (space, my.offset, done)
}

pub fn slice(&mut self, arm: bool) -> Option<&[T]> {
    let mut state = self.state.lock();
    let (space, offset, done) = Self::space_and_offset_locked(&mut state, self.id, self.buffer.capacity(), arm);
    self.last_space = space;
    if space == 0 && done { return None; }
    unsafe { Some(&self.buffer.slice_with_offset(offset)[0..space]) }
}

pub fn consume(&mut self, n: usize) {
    if n == 0 { return; }
    assert!(n <= self.last_space, "vmcircbuffer: consumed too much!");
    self.last_space -= n;
    let mut state = self.state.lock();
    let my = unsafe { state.readers.get_unchecked_mut(self.id) };
    my.meta.consume(n);
    if my.offset + n >= self.buffer.capacity() { my.ab = !my.ab; }
    my.offset = (my.offset + n) % self.buffer.capacity();
    my.writer_notifier.notify();
}

impl Drop for Reader { fn drop(&mut self) {
    let mut state = self.state.lock();
    let mut s = state.readers.remove(self.id);
    s.writer_notifier.notify();
}}
```

- `slice` returns `None` only when `writer_done && space == 0` — readers can drain everything after the writer is gone, then get `None` forever (`slice_done_semantics` test).
- `consume(n)` may be partial (cumulative ≤ last slice length), `consume(0)` is a no-op; every non-zero consume notifies the writer (cheap if not armed).
- Dropping a reader removes it from the slab and notifies the writer so a writer that was blocked on this reader re-evaluates without it. This is the in-process analogue of "dead reader eviction".

### 2.7 Writer drop

```rust
impl Drop for Writer { fn drop(&mut self) {
    let mut state = self.state.lock();
    state.writer_done = true;
    for (_, r) in state.readers.iter_mut() { r.reader_notifier.notify(); }
}}
```

### 2.8 `Notifier` and `Metadata` abstractions (`src/lib.rs`)

```rust
pub trait Notifier {
    /// Arm the notifier.
    fn arm(&mut self);
    /// The implementation must
    /// - only notify if armed
    /// - notify
    /// - unarm
    fn notify(&mut self);
}

pub trait Metadata {
    type Item: Clone;
    fn new() -> Self;
    fn add_from_slice(&mut self, offset: usize, tags: &[Self::Item]);
    fn get_into(&self, out: &mut Vec<Self::Item>);
    fn consume(&mut self, items: usize);
}
```

`Notifier` is the "armed wakeup" pattern: the waiter arms **while holding the state lock** after observing 0 space; the notifier calls `notify()` **while holding the same lock**, and only pays for a signal if armed. Because arm and notify are serialized by the mutex, there is no lost-wakeup window; the actual wait (`recv()` / `.await`) happens outside the lock. This is exactly the "writer avoids a syscall when nobody is waiting" property the .NET port wants — it just needs to be reimplemented with an atomic flag in shared memory instead of a mutex-protected bool.

`Metadata` = per-reader stream tags (GNU Radio style), with offsets relative to the reader's cursor; `NoMetadata` is the void impl. Not needed for the user's API; it can be an extension point later.

### 2.9 Flavors

**sync** (`std::sync::mpsc` unbounded channel per notifier):

```rust
impl Notifier for BlockingNotifier {
    fn arm(&mut self) { self.armed = true; }
    fn notify(&mut self) {
        if self.armed { let _ = self.chan.send(()); self.armed = false; }
    }
}

pub fn slice(&mut self) -> &mut [T] {
    let (p, s) = loop {
        match self.writer.slice(true) {
            [] => { let _ = self.chan.recv(); }
            s => break (s.as_mut_ptr(), s.len()),
        }
    };
    unsafe { slice::from_raw_parts_mut(p, s) }
}
```

Writer has one `Receiver`, and clones a `Sender` into each reader's `writer_notifier`; each reader has its own `Receiver` with the `Sender` in its `reader_notifier`. Blocking = `recv()` on the channel (kernel wait via the std channel's internal futex/park). Loop tolerates spurious wakeups. `try_slice()` = `slice(false)`.

**async** (`futures::channel::mpsc::channel(1)` + `try_send`): identical structure, waits with `self.chan.next().await`. Capacity-1 bounded channel: if a stale token is already queued, `try_send` fails harmlessly (the waiter will wake anyway). Wakes are therefore coalesced.

**nonblocking** (`NullNotifier`): `arm`/`notify` are no-ops; only `try_slice()` exists; caller polls.

**lockfree** (`src/lockfree.rs`, independent of `generic`): closest in spirit to what the shared-memory port needs, so quoting the core:

```rust
struct Inner<T, M> {
    buffer: DoubleMappedBuffer<T>,
    meta_epoch: AtomicUsize,
    writer_pos: AtomicUsize,          // monotonic, never reduced mod cap
    writer_done: AtomicBool,
    active_readers: AtomicUsize,      // monotonic slot allocator
    readers: Vec<ReaderSlot<M>>,      // fixed size = max_readers
}
struct ReaderSlot<M> {
    state: AtomicUsize,               // READER_INACTIVE / READER_ACTIVE
    pos: AtomicUsize,                 // monotonic
    meta_dirty: AtomicBool,
    meta: spin::Mutex<M>,
}

fn space_and_offset(&self) -> (usize, usize) {   // writer
    let cap = self.inner.buffer.capacity();
    let w = self.inner.writer_pos.load(Ordering::Acquire);
    let mut max_dist = 0usize; let mut any = false;
    let active = self.inner.active_readers.load(Ordering::Acquire);
    for slot in &self.inner.readers[..active] {
        if slot.state.load(Ordering::Acquire) == READER_ACTIVE {
            any = true;
            let r = slot.pos.load(Ordering::Acquire);
            let dist = w.wrapping_sub(r);
            if dist > max_dist { max_dist = dist; }
        }
    }
    let space = if !any { cap } else { cap.saturating_sub(max_dist) };
    (space, w % cap)
}

fn space_and_offset(&self) -> (usize, usize) {   // reader
    let w = self.inner.writer_pos.load(Ordering::Acquire);
    let r = slot.pos.load(Ordering::Acquire);
    let avail = w.wrapping_sub(r);
    let space = if avail >= cap { cap } else { avail };
    (space, r % cap)
}

// produce (no meta):  self.inner.writer_pos.store(w.wrapping_add(n), Ordering::Release);
// consume:            slot.pos.store(r.wrapping_add(n), Ordering::Release);
// add_reader: CAS active_readers n -> n+1 (fail => TooManyReaders); slot.pos = writer_pos; state = ACTIVE
// Reader drop: state = INACTIVE;  Writer drop: writer_done = true
```

Notes on lockfree: no notifier at all — "blocking" is the caller spinning (`fuzz_lockfree` uses `continue`); reader slots are **never recycled** (`active_readers` only increments, so `max_readers` is a lifetime cap, not a concurrent cap); reader `slice()` returns `&[T]` and cannot signal writer-done (only `slice_with_meta_into` returns `Option`). The monotonic-counter distance formula and the fixed slot array, however, are exactly the right shape for a shared-memory control block.

---

## 3. API-level semantics (answers to the specific questions)

| Question | vmcircbuffer behaviour |
|---|---|
| Does writer `slice()` block? | `generic`: never, returns possibly-empty `&mut [T]`. `sync`/`async`: block/await until **any** space > 0 (returned slice never empty). `try_slice()` returns immediately, may be empty. |
| Does reader `slice()` block or return `None`? | `sync`/`async`: block until data > 0 **or** writer dropped; `None` only when writer is gone and everything consumed; otherwise non-empty `Some`. `try_slice()`: `Some(empty)` when no data, `None` when done. |
| Is there a min-items wait? | **No.** Wakes on "space ≥ 1"/"items ≥ 1". Callers wanting N items loop themselves (`examples/sdr.rs` just takes `min(input.len(), output.len())`). |
| Can `produce(n)` use n < slice length? | Yes; cumulative `produce` calls must stay ≤ the last `slice()` length (`last_space` bookkeeping), else panic. `produce(0)` is a no-op. |
| Can reader `consume(n)` be partial? | Yes, same rule; panic if cumulative > last slice; `consume(0)` no-op. |
| Two slices outstanding? | Impossible: `slice()` borrows `&mut self` for the slice lifetime; calling `slice()` again recomputes (and `last_space` is reset to the new snapshot). Only one writer and one outstanding slice per reader. |
| Multiple readers? | Unbounded (`Slab`) in generic/sync/async/nonblocking; fixed `max_readers` in lockfree. Broadcast semantics: every reader sees every item; writer is throttled by the slowest. |
| Late reader initial position? | Writer's current position (sees only future data). |
| No readers? | Writer never blocks, keeps overwriting. |
| Reader dropped while writer blocked? | Reader drop removes it and notifies writer → writer unblocks. |
| Writer dropped? | `writer_done = true`, all readers notified; they drain then get `None`. |
| Memory visibility? | Provided by the `spin::Mutex` acquire/release in generic, or Acquire/Release atomics in lockfree. |
| Item type constraints? | `T: Copy + Default`, non-zero-sized. Equivalent to `T : unmanaged` (plus zero-init). |

---

## 4. Mapping to the desired .NET API

### 4(a) Semantics to mirror exactly

1. **Capacity rounding**: half-size in bytes = smallest multiple of `lcm(64 KiB, sizeof(T))` ≥ `max(64 KiB, minItems*sizeof(T))`; capacity in items = bytes / sizeof(T); reject zero-sized T (impossible with `unmanaged` anyway) and, for safety, `sizeof(T)` that is not a power of two only if you want to keep the 64 KiB arithmetic trivial — otherwise port the loop verbatim. Keep the 64 KiB allocation granularity as the unit even with `MapViewOfFile3` (views of pagefile sections must be placed at allocation-granularity-aligned addresses; the Rust choice is the safe one).
2. **Contiguous slice via double mapping**: `Span<T>(base + (pos % cap), count)` with `count ≤ cap`; never build a span longer than `cap`.
3. **Full capacity usable** (no n-1) — achieve it with monotonic 64-bit counters like `lockfree.rs`, not with the a/b flag.
4. **Slowest-reader throttling**: writer free space = `cap - max_i(writerPos - readerPos_i)` over live readers; `cap` if there are no readers (writer never blocks with zero readers — keep this, it matches "no consumers → free-running producer"; document it).
5. **Late reader starts at the writer's current position** (`readerPos = writerPos` at `CreateReader()`).
6. **Partial commit/advance**: `bucket.Commit(n)` with `n ≤ bucket.Span.Length`, `reader.Advance(n)` with `n ≤ chunk.Span.Length`; zero is a no-op; over-commit throws (`ArgumentOutOfRangeException` instead of panic). Track `last_space` per bucket/chunk exactly as Rust does. Keep the rule "one outstanding bucket per writer, one outstanding chunk per reader" — enforce with a `ref struct` bucket (cannot escape or be stored) or a debug flag.
7. **Armed notifier pattern**: waiter observes insufficient space → sets its "armed/waiting" flag → re-checks → waits. Producer/consumer after publishing checks the flag and only then signals. Notify every reader on `Commit` (each is a cheap flag test), notify the writer on `Advance`.
8. **Writer-done / reader-drain**: on writer dispose set a `writerDone` flag in the control block and wake all readers; readers return the remaining data and then a terminal state (`Wait` completes with `false`, `TryRead` returns false with `IsCompleted == true`) — the .NET analogue of `None`.
9. **Reader removal wakes the writer**: disposing a reader clears its slot and signals the writer.
10. **Spurious wakeups tolerated by loops** (`loop { match slice(true) ... wait }`) — every wait in .NET must be inside a re-check loop.
11. **Zero-init**: rely on the pagefile section being zero-filled instead of the `T::default()` loop.

### 4(b) Places the .NET design should deliberately differ

| Area | vmcircbuffer | photone-ipc | Why |
|---|---|---|---|
| Mapping API | `CreateFileMappingA` (anonymous) + `VirtualAlloc/VirtualFree` + `MapViewOfFileEx` race with 6 retries | `CreateFileMappingW` (**named**, or handle duplicated/inherited) + `VirtualAlloc2(MEM_RESERVE_PLACEHOLDER, 2*size)` → `VirtualFree(MEM_PRESERVE_PLACEHOLDER)` split at `size` → `MapViewOfFile3(MEM_REPLACE_PLACEHOLDER)` twice; cross-process: first try `VirtualAlloc2` placeholder **at the creator's address** (fixed `BaseAddress`), fall back to `NULL` | Race-free; required for "map at the same VA in process B"; a named/duplicable section is the only thing that makes it shared memory at all |
| Section layout | Data only | `[control block (one 64 KiB granule)] [data ×2 views]` in one section — control block gets a *single* view, data region gets the two views at file offset = 64 KiB. Or two sections; a single section is simpler to hand around by name | Reader cursors, writer cursor, flags, PIDs must be in shared memory, not in an `Arc<Mutex<State>>` |
| Control-block synchronization | `spin::Mutex<State>` + `Slab` | Lock-free: `Volatile.Write/Read`, `Interlocked.CompareExchange` on 64-bit fields, each hot field on its own cache line (`writerPos`, per-reader `readerPos`) | A process-shared mutex is either a kernel object (slow) or a spinlock that deadlocks if the holder dies; the `lockfree.rs` counter scheme needs no lock |
| Position encoding | offset + a/b flag | 64-bit monotonic counters; `avail = w - r`, `free = cap - max(w - r_i)`, index = `pos % cap` (or `pos & (cap-1)` if you force power-of-two item capacity) | One atomic load, no wrap ambiguity, still full `cap` usable |
| Reader registry | Unbounded slab, `id` = slab key, removed on drop | **Fixed reader slots** (e.g. 16 or 64) in the control block: `{state (free/active), pos, ownerPid, waitFlag, lastSeenTick}`; `CreateReader()` CASes a free slot; slots are **recycled** (unlike `lockfree.rs`, whose counter never decrements) | Shared memory cannot grow; fixed slots let the writer scan a bounded array with no allocation |
| Reader liveness | Rust `Drop` guarantees removal | `ownerPid` per slot; writer, when it finds `free == 0` for longer than a spin/short timeout, checks `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` / `GetExitCodeProcess` (or a per-reader process handle + `WaitForSingleObject(h, 0)`) on the blocking slot's PID and evicts dead readers (CAS slot state → free, wake writer). Also on `CreateReader()` sweep dead slots to reclaim them. Beware PID reuse: store `ownerPid` + process start time (`GetProcessTimes`) or keep a duplicated process handle | Crash of process B must not wedge process A forever — no Rust equivalent exists |
| Wakeup primitive | mpsc channel (`park`/futex) | Spin (bounded, `SpinWait`) → then kernel wait on a **named auto-reset Event/Semaphore per reader slot** and one for the writer (`WaitOnAddress`/`WakeByAddress` are process-local and cannot be used across processes; likewise `NtAlertThreadByThreadId`). Async reader wait: `RegisterWaitForSingleObject`/`ThreadPool.RegisterWaitForSingleObject` or a dedicated waiter thread completing a `ManualResetValueTaskSourceCore` — allocation-free once the reader is created | Cross-process; keeps "no syscall unless someone is armed" via the shared armed flag |
| Armed flag protocol | `armed: bool` guarded by mutex | Per-slot `int waiting` in shared memory. Waiter: `Volatile.Write(waiting,1)`; full fence; re-check counters; if still insufficient → `WaitForSingleObject(evt)`. Signaler: publish counter with release; full fence (`Interlocked.MemoryBarrier` or `Interlocked.Exchange`); `if waiting != 0 && Interlocked.Exchange(ref waiting, 0) == 1 → SetEvent`. The Dekker-style fence pair replaces the mutex | Lost-wakeup safety without a lock |
| Min-count waiting | none (wakes on ≥1) | `GetBucket(n)` blocks until `free ≥ n` (throw if `n > cap`); `reader.Wait(n)` until `avail ≥ n` or writer done. Store the requested count in the slot (`wantedCount`) so the signaler can skip `SetEvent` when the threshold is not yet met (optional optimisation; correctness only needs "signal if armed") | Matches user API; reduces wakeups |
| Writer arming only first full reader | `break` at first `s == 0` | Same idea: writer arms its single writer-event; every reader's `Advance` checks the writer's flag. Since there is one writer event, no per-reader writer_notifier is needed | Simpler in shared memory |
| Bucket/chunk types | `&mut [T]` / `&[T]` tied to borrow | `ref struct WriteBucket<T> : IDisposable { Span<T> Span; void Commit(int) }` and `ReadChunk<T> { ReadOnlySpan<T> Span }`; `Dispose` without `Commit` commits 0 (mirrors "slice then don't produce") | Zero-alloc, cannot escape |
| Writer lifetime | `Drop` → `writer_done` | `Dispose` → `writerDone`; additionally `writerPid` so readers can detect a crashed writer and return completed instead of waiting forever | Symmetric robustness |
| Address handoff | n/a | Control block stores `creatorBaseAddress` (data view VA) and `capacityBytes`; process B reads them from the single-view mapping of the control block first, then attempts placeholder at that VA | "pointers stored inside the buffer remain valid" best effort |
| Metadata/tags | trait-based per-reader Vec | Omit from v1 (heap-allocated `Vec` per reader has no shared-memory analogue); leave a reserved field | Scope |
| Size field | `size as DWORD` | full 64-bit `MaximumSizeHigh/Low` | >4 GiB buffers |
| Post-map validation | none | write/read sentinel through both views at construction (and verify `capacityBytes % 65536 == 0`) | Cheap insurance for the placeholder split |

### 4(c) Semantics to preserve from the tests (good acceptance tests to port)

- `create_many` (100 buffers), `zero_size` (`with_capacity(0)` still gives a non-empty slice), `no_reader` (writer never blocks), `produce_too_much` / `consume_too_much` (throw), `late_reader`, `several_readers` (independent cursors; `r1.consume(100)` leaves `all-100` for r1 and `all` for r2), `block_writer` / `block_reader` (1 s delayed unblock), `fuzz_sync` (random write sizes ≤ cap/2, `try_slice().len() == w_off - r_off` invariant), `slice_done_semantics` (drain then `None`), lockfree `block_writer` (`slice().is_empty()` when full, non-empty after consume).

---

## 5. Cross-process capability: none (confirmed)

- Windows: `CreateFileMappingA(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, size, NULL)` — pagefile-backed **and unnamed**; the handle is private to the struct and only used in `Drop`. Nothing exposes name/handle/address for another process.
- Unix: `mkstemp` + immediate `unlink` — the backing file is anonymous by the time `new_try` returns; fd is closed after mapping.
- All coordination state (`Arc<spin::Mutex<State>>`, `Slab<ReaderState>`, `Sender<()>`/`Receiver<()>` channels, `Arc<Inner>` with `Vec<ReaderSlot>` in lockfree) lives on the process heap. Readers hold `Arc` clones of the same heap objects — this only works within one address space.
- There is no PID/liveness logic anywhere; reader liveness is entirely Rust ownership (`Drop`).
- `examples/gnuradio/` is an in-process C++ GNU Radio copy-flowgraph used as a throughput comparison against `examples/sdr.rs` (200 chained copy blocks, 20 M floats, `MIN_ITEMS = 16384`), not an IPC example.
- Therefore the crate is **thread-safe, in-process only**; the .NET port must add the shared control block, named section, fixed reader slots, PID liveness and kernel event handles described in 4(b). The parts that transfer directly are the capacity rounding, the two-view addressing scheme, the min-over-readers/late-join/partial-commit/drain-then-done semantics, and the armed-notifier protocol — the `lockfree.rs` counter layout is the closest template for the shared control block.