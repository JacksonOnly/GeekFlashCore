# Main Merge Notes: Qualcomm Framework, CLI, Android LP and Quality Review

Date: 2026-09-07

Source branch: `codex/develop-20260904`

Target branch: `main`

Pre-merge base: `a03eb99cf4fa0ee0eda409a5210170fe60169bc4`

## 1. Merge decision

The branch passed the second independent review with zero Critical, Important or Minor findings and was fast-forwarded into local `main` at reviewed head `536231f`.

The source branch was a direct descendant of local `main`; `git merge-base --is-ancestor main codex/develop-20260904` exited with code 0. The fast-forward preserved the 67 small, behavior-oriented commits present before the merge-note commit and avoided an artificial merge commit.

The merge is intentionally local and has not been pushed. The source branch is deleted only after the merged result passes the post-merge checks.

## 2. Scope

Before this merge note, the range `main..codex/develop-20260904` contains 67 commits and changes 231 files, with 20,259 insertions and 580 deletions. The reviewed production head is `561b02d483caa989128d274a8614f25b7db916a6`. The change is a framework expansion rather than a narrow bug fix.

### Qualcomm protocol framework

- Adds stable Qcom contracts, options, domain models, exceptions and resource-provider interfaces without exposing MessagePipe types from the core abstractions.
- Implements bounded Sahara discovery, programmer inspection/upload, Firehose configuration negotiation, command execution, raw transfer, storage operations and session generation checks.
- Adds synchronous storage, GPT, read/write/erase, patch, benchmark, peek/poke, firmware write and custom-command capabilities behind the Qcom protocol facade.
- Adds explicit Xiaomi, ZTE, Nothing, OnePlus, Oplus Digest Pt and Oplus Digest Legacy strategies while keeping their command order and state isolated.
- Validates Firehose policy mappings before the first device write, including continuity, total length and overflow. A policy returning `null` is rejected rather than silently replaced with an identity mapping.
- Defines cancellation boundaries: cancellation before the main wire command preserves the previous usable state; cancellation after raw transfer starts invalidates the session and stale leases.
- Moves resource timeout, linked cancellation-source lifetime, late results and sensitive authentication payload disposal into `QcomResourceResolver`.

### Streaming and storage infrastructure

- Introduces the reusable `StreamBlockDevice` with a captured origin, fixed window, synchronized positioned reads, position restoration and explicit Borrow/Transfer ownership.
- Removes the duplicate seekable-stream block-device implementations from Android Sparse and Android LP, and removes the Qcom nested stream adapter.
- Projects validated Android Sparse chunks directly from one `SparseDocument`; Qcom program planning no longer reparses the same sparse metadata.
- Keeps large payload handling streaming and windowed. Sparse and raw images are not expanded into memory by total image size.
- Adds Android logical-partition parsing, editing, planning, allocation, verification, export and commit support with explicit writable-device resolution and durability contracts.

### CLI and host integration

- Adds the .NET 10 `GeekFlashCore.CLI` host with protocol registration, transport selection, device listing, connection/recovery, typed Qcom commands and interactive resource providers.
- Adds bounded progress reporting with transferred amount, average speed, elapsed time and ETA while coordinating progress output with structured logs.
- Moves modeled Firehose operations into typed Core APIs; the CLI is responsible for syntax, file resources and presentation instead of duplicating protocol validation or packet construction.
- Localizes user-visible CLI text through matching Chinese and English resources. Protocol tokens, option names and device-originated fields remain invariant data.

### General quality corrections

- Replaces four duplicate Ext/Erofs sequential-read loops with .NET 8 `Stream.ReadExactly`.
- Adds explicit null guards and correct `ArgumentException.ParamName` behavior on reviewed public contracts.
- Removes valueless generated-code comments, a stale Sparse using and the unused Serilog dependency from `Protocol.Abstractions`.
- Declares Serilog 4.4.0 directly in `Protocol.Qcom`, which is the project that consumes its APIs. This was verified after a full restore so stale NuGet assets cannot mask the dependency.

### Merge-readiness corrections

The first independent review found no Critical issue and five Important issues. All five were reproduced before implementation and corrected before the merge decision:

- VIP capacity is committed only after the main XML reaches `ITransport.Write`; cancellation after a VIP table but before the main command no longer consumes a frame or exhausts the chained-table sequence.
- Firehose stream-transfer buffers are returned to `ArrayPool<byte>` with clearing enabled so Digest and VIP data cannot remain in a shared buffer.
- CLI authentication decoding transfers ownership of the decoded byte array to `SensitiveDataOwner`; failure paths clear temporary storage and successful paths no longer create a second byte-array copy.
- `SparseRegion.OpenRead` now treats the stream's current position as the sparse-image origin, and both strict-document and legacy-parser regions use relative offsets and restore borrowed stream position.
- Storage and partition result lines, including all unknown values, now come from matching Chinese and English resources.

The readiness run also exposed a pre-existing test-fixture race: a Serilog sink used a `List<LogEvent>` while other test cases could emit concurrently through the process-global logger. The ignored local test fixture now uses `ConcurrentQueue<LogEvent>`; the affected 250-test Qcom suite then passed twice consecutively. No production logging behavior was changed for this fixture-only issue.

## 3. Compatibility and integration impact

- Existing public Qualcomm contracts are expanded. External implementations of `IQcomProtocol` may need to implement newly added members; consumers that only call the interface remain source-compatible where existing signatures were preserved.
- Session-backed block devices and leases now enforce generation validity. A handle obtained before disconnect, reconnect or fatal protocol failure must not be reused.
- Cancellation is cooperative at protocol boundaries. The synchronous `ITransport` contract still cannot interrupt a read that is already blocked inside the transport implementation.
- Vendor authentication and Digest routes are mutually exclusive and selected explicitly from options/evidence. Hosts should not infer Xiaomi authentication solely from a vendor label.
- `Protocol.Abstractions` no longer supplies Serilog transitively. Projects that directly use Serilog must declare it themselves; `Protocol.Qcom` and the CLI now do so explicitly.
- The local test projects under `.tests` remain intentionally ignored and are not part of published packages or the solution.

## 4. Verification evidence before merge

The dependency graph was restored from project declarations before the final run:

```powershell
dotnet restore GeekFlashCore.slnx
```

The following Release tests pass:

- `GeekFlashCore.Protocol.Qcom.Tests`: 250/250 (two consecutive runs after fixing the fixture race)
- `GeekFlashCore.CLI.Tests`: 55/55
- `GeekFlashCore.Android.Lp.Tests`: 55/55
- `GeekFlashCore.Core.Tests`: 9/9

`dotnet build GeekFlashCore.slnx -c Release --no-restore` completes with zero errors and zero warnings. `git diff --check` passes, and `git ls-files .tests` produces no output.

The `AnalysisLevel=latest-all` audit records 366 unique diagnostics. No suppression was added just to lower the count. `CA2016` and `CA2025` are both absent; the new resource resolver, stream block-device adapter and sparse planner have no diagnostics. Remaining findings are classified as protocol-layout/ownership/synchronous-boundary design findings, unrelated existing diagnostics, or LibUsb/real-device items requiring measurement.

## 5. Residual risk

- The protocol sequences are covered by scripted transports, not by a complete physical-device matrix. Qualcomm, Xiaomi, OnePlus, Nothing and Oplus loader variants still require hardware regression.
- Real cancellation latency depends on the serial/USB transport returning from an in-progress synchronous operation.
- LibUsb buffer pooling was not changed because there is no trustworthy real-transfer throughput and GC profile yet.
- OnePlus compatibility cryptography intentionally preserves the device protocol's IV behavior; it should not be reused as a general-purpose cryptographic construction.
- Strict GPT and storage geometry validation can reject vendor-nonconforming media. Such cases should be evaluated from sanitized captures rather than handled with an unbounded fallback.

## 6. Post-merge procedure

1. Refresh `origin/main` and confirm the local target has not diverged.
2. Fast-forward local `main` to the reviewed source head.
3. Run all four Release test projects on `main`.
4. Run the Release solution build and `git diff --check`.
5. Confirm `main` points at the reviewed head, the tracked worktree is clean, and `.tests`, `bin/obj` and temporary analysis output remain ignored.
6. Keep the source branch until the merged result is green. Delete it only after verification; do not push without a separate request.

If post-merge verification fails, preserve both refs and investigate the exact merged tree. Do not force-reset or delete the source branch.

## 7. Actual merge result

- Second independent review: Critical 0, Important 0, Minor 0; verdict `Ready to fast-forward merge: Yes`.
- Remote check: `origin/main` remained at `5b4d6c5`; local pre-merge `main` was `a03eb99`, four commits ahead of the remote and with no remote-only commit.
- Merge command: `git merge --ff-only codex/develop-20260904` advanced local `main` from `a03eb99` to `536231f` without conflicts or a merge commit.
- Post-merge restore: `dotnet restore GeekFlashCore.slnx` completed successfully from project declarations.
- Post-merge tests: Qcom 250/250, CLI 55/55, Android LP 55/55 and Core 9/9 passed as separately checked commands.
- Post-merge build: `dotnet build GeekFlashCore.slnx -c Release --no-restore` completed with zero warnings and zero errors.
- Hygiene: `git diff --check` passed and `.tests` remained ignored and untracked by Git.

During the first post-merge Qcom test attempt, two concurrent uncommitted working-tree edits temporarily removed explicit `Protocol.Abstractions` entries from the solution and CLI project, and the Qcom abstraction project reference was also absent at the instant of compilation. The committed `536231f` tree already contained all three required entries. Restoring those exact committed lines produced a clean worktree and the complete post-merge gate above passed; no source commit was needed for this transient workspace anomaly.

Local `main` remains intentionally unpushed. Hardware verification remains a release risk rather than a merge blocker because no complete physical Qualcomm/Xiaomi/OnePlus/Nothing/Oplus device matrix was available.

## 8. Follow-up verification (2026-09-07)

- `git fetch origin main` completed successfully. `origin/main` remains an ancestor of local `main` (`5b4d6c5` -> `9e2b4f6`); the remote has no commit absent from the local target, so no merge or rebase is required.
- Re-ran the four post-merge Release test projects on the actual local `main`: Qcom 250/250, CLI 55/55, Android LP 55/55 and Core 9/9 passed.
- Re-ran `dotnet build GeekFlashCore.slnx -c Release --no-restore`: 0 warnings and 0 errors.
- `git diff --check` passed; `git ls-files .tests` produced no output. The tracked worktree remains clean and generated `.tests`, `bin/obj`, IDE and `temp` paths remain ignored.
- No remote push or remote-branch deletion was performed. After containment and post-merge verification, the local `codex/develop-20260904` branch was deleted; its remote counterpart remains available for audit. The verified `main` at `9e2b4f6` was ahead of `origin/main` by 73 commits before this documentation-only follow-up.
