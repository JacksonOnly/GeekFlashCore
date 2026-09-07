# GeekFlashCore Repository Quality Review Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在保持已确认 Qualcomm 线路行为的前提下，修复 Firehose 范围、取消和资源生命周期缺陷，统一重复基础设施，消除 Sparse 双解析，并完成 CLI 本地化与低风险冗余清理。

**Architecture:** 先用模拟传输锁定 Firehose 的设备写入边界，再把范围验证和资源解析分别收敛为单一职责的内部协作者。通用 seekable Stream 适配能力下沉至 BlockDevice，Android Sparse、Android LP 和 Qcom 仅组合该能力；Sparse 严格文档直接投影写入区域，不再走旧解析器。最后以资源文件承载所有用户可见文本，并用严格分析器复核剩余诊断。

**Tech Stack:** C#、.NET 8、CLI .NET 10、xUnit、同步 `ITransport`、`Span<T>`/`Memory<T>`、`ArrayPool<T>`、Serilog、`.resx`。

**Spec:** `docs/plans/2026-09-06-project-quality-review-design.md`

## Global Constraints

- Sahara、Firehose、Raw、XML ACK/NAK、Sparse 展开和厂商线路保持同步 I/O；同步协议层不得阻塞等待异步资源。
- 普通 Digest、VIP、Oplus Digest、Xiaomi、OnePlus 和 Nothing 的已确认线路顺序不得改变。
- 外部长度、偏移、扇区数、映射总和和整数转换必须在分配或设备写入前使用 `checked` 或减法式边界检查。
- 大文件路径不得按镜像展开长度物化；新增内存必须按缓冲区或 Sparse chunk 数增长。
- Stream 只按显式 `DeviceOwnership.Borrow` 或 `DeviceOwnership.Transfer` 释放；迟到认证载荷必须清零并释放。
- Token、签名、Challenge、完整 Digest、私密 Hash、认证响应和完整自定义 XML 不得写入日志。
- 用户可见异常和 CLI 文本必须同时进入对应 `Strings.resx` 与 `Strings.en.resx`，两边键集合一致。
- `.tests` 只做本地验证，保持 Git ignored，不加入解决方案、不执行 `git add -f`、不进入提交。
- 每项生产修改执行目标测试、Release 构建和 `git diff --check`；每个提交只包含该项生产代码和必要文档。
- 真实设备未验证的行为必须记录为风险，不把模拟传输结果写成硬件结论。

## File Map

- `src/GeekFlashCore.Protocol.Qcom/Firehose/Storage/FirehoseStorageRangeValidator.cs`：唯一的策略映射不变量验证器。
- `src/GeekFlashCore.Protocol.Qcom/Resources/QcomResourceResolver.cs`：资源请求、超时、取消、迟到结果和 CTS 生命周期所有者。
- `src/GeekFlashCore.BlockDevice/StreamBlockDevice.cs`：唯一的 seekable Stream 到 `IReadableBlockDevice` 适配器。
- `src/GeekFlashCore.Android.Sparse/SparseDocument.cs`：从严格验证后的 chunk 投影 `SparseRegion`。
- `src/GeekFlashCore.Protocol.Qcom/Firehose/Programming/SparseProgramPlanner.cs`：消费单个 `SparseDocument` 生成 Firehose 段。
- `src/GeekFlashCore.CLI/Localization/Strings*.resx`：CLI 用户可见文本的中英文事实来源。
- `.tests/GeekFlashCore.Core.Tests`：BlockDevice、Sparse、文件系统公共契约的本地测试工程；整个目录保持 ignored。

---

### Task 1: Validate Firehose policy mappings before wire I/O

**Files:**

- Create: `src/GeekFlashCore.Protocol.Qcom/Firehose/Storage/FirehoseStorageRangeValidator.cs`
- Modify: `src/GeekFlashCore.Protocol.Qcom/Firehose/Storage/FirehoseStorageService.cs`
- Modify: `src/GeekFlashCore.Protocol.Qcom/Firehose/Programming/FirehoseProgramExecutor.cs`
- Modify: `src/GeekFlashCore.Protocol.Qcom/Localization/Strings.resx`
- Modify: `src/GeekFlashCore.Protocol.Qcom/Localization/Strings.en.resx`
- Test: `.tests/GeekFlashCore.Protocol.Qcom.Tests/Firehose/FirehoseStorageServiceTests.cs`
- Test: `.tests/GeekFlashCore.Protocol.Qcom.Tests/Programming/FirehoseProgramExecutorTests.cs`

**Interfaces:**

- Consumes: `IFirehoseStoragePolicy.Map(uint, long, long, bool, string?, string?)` and `FirehoseStorageRange`.
- Produces: `internal static IReadOnlyList<FirehoseStorageRange> FirehoseStorageRangeValidator.Validate(IReadOnlyList<FirehoseStorageRange>? ranges, long requestedStartSector, long requestedSectorCount)`.

- [x] **Step 1: Add failing zero-wire and single-evaluation tests**

Add a table-driven policy test covering null, empty, zero/negative count, overlap, gap, wrong start, overflow and total mismatch. The assertion must verify the failure happens before `ScriptedTransport.Written` changes. Add a stateful policy proving Program calls `Map` once per planned segment:

```csharp
[Theory]
[MemberData(nameof(InvalidMappings))]
public void Read_InvalidPolicyMappingFailsBeforeWire(IReadOnlyList<FirehoseStorageRange>? ranges)
{
    using var transport = new ScriptedTransport([]);
    using var session = StartedSession(transport);
    var service = Service(session, 512, policy: new StubPolicy(ranges));

    Assert.Throws<InvalidOperationException>(() => service.Read(
        new FirehoseReadRequest
        {
            PhysicalPartitionNumber = 0,
            StartSector = 10,
            SectorCount = 4,
            SectorSizeInBytes = 512
        },
        new MemoryStream()));
    Assert.Empty(transport.Written);
}

[Fact]
public void Program_EvaluatesEachPolicyMappingOnce()
{
    var policy = new CountingPolicy();
    using var transport = new ScriptedTransport(Concat(RawAck(), Ack()));
    using var session = StartedSession(transport);

    Service(session, 512, policy: policy).Program(0, 20, new byte[512]);

    Assert.Equal(1, policy.MapCount);
}
```

`InvalidMappings` must return these exact invalid shapes for request `[10, 14)`:

```csharp
public static TheoryData<IReadOnlyList<FirehoseStorageRange>?> InvalidMappings
{
    get
    {
        var data = new TheoryData<IReadOnlyList<FirehoseStorageRange>?>();
        data.Add(null);
        data.Add([]);
        data.Add([new(10, 0, null, null)]);
        data.Add([new(10, -1, null, null)]);
        data.Add([new(9, 4, null, null)]);
        data.Add([new(10, 2, null, null), new(11, 2, null, null)]);
        data.Add([new(10, 2, null, null), new(13, 1, null, null)]);
        data.Add([new(10, 5, null, null)]);
        return data;
    }
}
```

Add a direct overflow case so the endpoint addition is exercised rather than rejected first for a wrong start:

```csharp
[Fact]
public void Validate_OverflowingMappedEndpointFailsBeforeWire()
{
    Assert.Throws<OverflowException>(() => FirehoseStorageRangeValidator.Validate(
        [new FirehoseStorageRange(long.MaxValue, 2, null, null)],
        long.MaxValue,
        2));
}
```

Extend the local `Service` helper with an optional policy and pass it to the production constructor:

```csharp
private static FirehoseStorageService Service(
    FirehoseSession session,
    uint sectorSize,
    ulong payload = 1024 * 1024,
    IFirehoseStoragePolicy? policy = null) => new(session, new FirehoseConfigureResponse
{
    Storage = sectorSize == 4096 ? FirehoseStorage.Ufs : FirehoseStorage.Emmc,
    MemoryName = sectorSize == 4096 ? "UFS" : "eMMC",
    SectorSizeInBytes = sectorSize,
    MaxPayloadSizeToTargetInBytes = payload,
    MaxPayloadSizeToTargetInBytesSupported = payload,
    MaxPayloadSizeFromTargetInBytes = payload,
    MaxXmlSizeInBytes = 4096
}, policy);
```

- [x] **Step 2: Run the focused tests and confirm RED**

Run:

```powershell
dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~FirehoseStorageServiceTests|FullyQualifiedName~FirehoseProgramExecutorTests"
```

Expected: invalid mappings are accepted too late or fail with a different exception, and `Program_EvaluatesEachPolicyMappingOnce` reports `MapCount == 2`.

- [x] **Step 3: Implement the centralized validator**

Use a remaining-count algorithm so neither endpoint subtraction nor accumulated totals can silently overflow:

```csharp
internal static class FirehoseStorageRangeValidator
{
    internal static IReadOnlyList<FirehoseStorageRange> Validate(
        IReadOnlyList<FirehoseStorageRange>? ranges,
        long requestedStartSector,
        long requestedSectorCount)
    {
        if (ranges is null || ranges.Count == 0)
            throw new InvalidOperationException(Strings.Qcom_StoragePolicyMappingInvalid);
        if (requestedStartSector < 0 || requestedSectorCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(requestedSectorCount));

        long expectedStart = requestedStartSector;
        long remaining = requestedSectorCount;
        foreach (FirehoseStorageRange? range in ranges)
        {
            if (range is null || range.SectorCount <= 0 ||
                range.StartSector != expectedStart || range.SectorCount > remaining)
                throw new InvalidOperationException(Strings.Qcom_StoragePolicyMappingInvalid);

            expectedStart = checked(expectedStart + range.SectorCount);
            remaining -= range.SectorCount;
        }

        if (remaining != 0)
            throw new InvalidOperationException(Strings.Qcom_StoragePolicyMappingInvalid);
        return ranges;
    }
}
```

Add resource key `Qcom_StoragePolicyMappingInvalid` with Chinese value `存储策略返回了无效或不完整的扇区映射。` and English value `The storage policy returned an invalid or incomplete sector mapping.`

- [x] **Step 4: Route Read and Program through the validator once**

In `FirehoseStorageService.Map`, validate both policy and identity mappings. In `FirehoseProgramExecutor`, build one validated mapping array before the first XML write and reuse it during transfer:

```csharp
private IReadOnlyList<FirehoseStorageRange> Map(
    uint partition, long start, long count, bool write,
    string? label = null, string? fileName = null)
{
    IReadOnlyList<FirehoseStorageRange>? ranges = _policy is null
        ? [new FirehoseStorageRange(start, count, label, fileName)]
        : _policy.Map(partition, start, count, write, label, fileName);
    return FirehoseStorageRangeValidator.Validate(ranges, start, count);
}
```

```csharp
var mappedSegments = new IReadOnlyList<FirehoseStorageRange>[plan.Segments.Count];
for (int index = 0; index < plan.Segments.Count; index++)
{
    cancellationToken.ThrowIfCancellationRequested();
    FirehoseProgramSegment segment = plan.Segments[index];
    IReadOnlyList<FirehoseStorageRange>? ranges = _policy is null
        ? [new FirehoseStorageRange(segment.StartSector, segment.SectorCount,
            request.Label, request.FileName)]
        : _policy.Map(request.PhysicalPartitionNumber, segment.StartSector,
            segment.SectorCount, true, request.Label, request.FileName);
    mappedSegments[index] = FirehoseStorageRangeValidator.Validate(
        ranges, segment.StartSector, segment.SectorCount);
}
```

- [x] **Step 5: Verify GREEN and regression coverage**

Run the focused test command from Step 2, then:

```powershell
dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore
dotnet build GeekFlashCore.slnx -c Release --no-restore
git diff --check
```

Expected: Qcom tests all pass, Release build has 0 errors, and diff check has no output.

- [x] **Step 6: Commit production changes only**

```powershell
git add src/GeekFlashCore.Protocol.Qcom/Firehose/Storage/FirehoseStorageRangeValidator.cs src/GeekFlashCore.Protocol.Qcom/Firehose/Storage/FirehoseStorageService.cs src/GeekFlashCore.Protocol.Qcom/Firehose/Programming/FirehoseProgramExecutor.cs src/GeekFlashCore.Protocol.Qcom/Localization/Strings.resx src/GeekFlashCore.Protocol.Qcom/Localization/Strings.en.resx
git commit -m "fix(qcom): validate mapped firehose ranges"
```

### Task 2: Make Firehose cancellation boundaries explicit

**Files:**

- Modify: `src/GeekFlashCore.Protocol.Qcom/Firehose/FirehoseSession.cs`
- Modify: `src/GeekFlashCore.Protocol.Qcom/Firehose/Storage/IFirehoseStoragePolicy.cs`
- Modify: `src/GeekFlashCore.Protocol.Qcom/Firehose/Storage/FirehoseStorageService.cs`
- Modify: `src/GeekFlashCore.Protocol.Qcom/Firehose/Programming/FirehoseProgramExecutor.cs`
- Modify: `src/GeekFlashCore.Protocol.Qcom/Firehose/Configuration/ConfigureNegotiator.cs`
- Modify: `src/GeekFlashCore.Protocol.Qcom/Vendors/Oplus/OplusDigestLegacyPolicy.cs`
- Modify: `src/GeekFlashCore.Protocol.Qcom/Vendors/Oplus/OplusGptCompatibility.cs`
- Modify: `src/GeekFlashCore.Protocol.Qcom/Localization/Strings.resx`
- Modify: `src/GeekFlashCore.Protocol.Qcom/Localization/Strings.en.resx`
- Test: `.tests/GeekFlashCore.Protocol.Qcom.Tests/Firehose/FirehoseCommandSessionTests.cs`
- Test: `.tests/GeekFlashCore.Protocol.Qcom.Tests/Firehose/FirehoseStorageServiceTests.cs`
- Test: `.tests/GeekFlashCore.Protocol.Qcom.Tests/Vendors/OplusDigestLegacyTests.cs`

**Interfaces:**

- Consumes: `FirehoseSession.Execute(BaseCommand, bool, CancellationToken)` and synchronous transport behavior.
- Produces: pre-wire cancellation leaves the original session state usable; any cancellation after Raw starts leaves `FirehoseSessionState.Faulted`.

- [x] **Step 1: Add cancellation state tests**

Add a test proving pre-cancel writes nothing and does not poison the session. The second NOP consumes the only scripted ACK:

```csharp
[Fact]
public void Execute_PreCancelledDoesNotWriteOrFaultSession()
{
    using var transport = new ScriptedTransport(Ack());
    using var session = StartedSession(transport);
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();

    Assert.Throws<OperationCanceledException>(() =>
        session.Execute(new NopCommand(), cancellationToken: cancellation.Token));
    Assert.Empty(transport.Written);
    Assert.Equal(FirehoseSessionState.Started, session.State);

    Assert.True(session.Execute(new NopCommand()).IsSuccess);
    Assert.Equal(FirehoseSessionState.Started, session.State);
}
```

Add a `_beforeCommand` boundary test where the callback returns in XML state, cancels the token, and verifies the main command is omitted while the state remains `Configured`. Retain the existing `RawCancellation_InvalidatesSessionAndLease` test as the Raw-stage contract.

- [x] **Step 2: Confirm RED**

Run:

```powershell
dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~FirehoseCommandSessionTests|FullyQualifiedName~RawCancellation_InvalidatesSessionAndLease"
```

Expected: the pre-cancel test observes `Faulted` under the current catch-all path.

- [x] **Step 3: Preserve state only before the main wire command**

Apply the same sent-boundary structure to `Execute` and `ExecuteXml`:

```csharp
FirehoseSessionState initialState = State;
bool mainCommandMayHaveChangedWire = false;
try
{
    cancellationToken.ThrowIfCancellationRequested();
    _beforeCommand?.Invoke(this, cancellationToken);
    cancellationToken.ThrowIfCancellationRequested();
    mainCommandMayHaveChangedWire = true;
    FirehoseCommandResult result = _executor.Execute(
        command, expectedRawMode, _xmlDeclarationAttribute);
    CompleteCommand(command is ConfigureCommand, result, initialState);
    return result;
}
catch (OperationCanceledException) when (!mainCommandMayHaveChangedWire)
{
    SetState(initialState);
    throw;
}
```

Do not add cancellation polling inside a blocking `_receiver` read: the transport contract cannot interrupt it. Existing Raw send/receive catch paths continue setting `Faulted`.

Replace the remaining hardcoded expected-state exception in `Enter` with resource key `Qcom_FirehoseExpectedState`, Chinese value `Firehose 操作要求状态为 {0}，当前状态为 {1}。` and English value `The Firehose operation requires state {0}, but the session is {1}.`:

```csharp
throw new InvalidOperationException(
    Strings.FormatQcom_FirehoseExpectedState(expectedState, state));
```

- [x] **Step 4: Propagate tokens at every already-tokenized call site**

Make the default storage policy call exact:

```csharp
return session.Execute(
    command,
    expectedRawMode: true,
    cancellationToken: cancellationToken);
```

Pass the current method token in `ConfigureNegotiator`, `FirehoseProgramExecutor`, `FirehoseStorageService` firmware/erase/read-related paths, `OplusDigestLegacyPolicy` replay/NOP paths and `OplusGptCompatibility`. Do not add cancellation parameters to public methods that currently have no token; this task only closes dropped-token gaps where the caller already supplies one.

- [x] **Step 5: Verify Qcom behavior and analyzer signal**

Run:

```powershell
dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore
dotnet build src/GeekFlashCore.Protocol.Qcom/GeekFlashCore.Protocol.Qcom.csproj -t:Rebuild -c Release --no-restore -p:AnalysisLevel=latest-all -p:EnableNETAnalyzers=true -p:TreatWarningsAsErrors=false
git diff --check
```

Expected: tests pass; no CA2016 remains at a call site whose containing method already receives the same cancellation token. Remaining synchronous APIs without tokens are listed in the final progress record rather than silently changed.

- [x] **Step 6: Commit**

```powershell
git add src/GeekFlashCore.Protocol.Qcom/Firehose/FirehoseSession.cs src/GeekFlashCore.Protocol.Qcom/Firehose/Storage/IFirehoseStoragePolicy.cs src/GeekFlashCore.Protocol.Qcom/Firehose/Storage/FirehoseStorageService.cs src/GeekFlashCore.Protocol.Qcom/Firehose/Programming/FirehoseProgramExecutor.cs src/GeekFlashCore.Protocol.Qcom/Firehose/Configuration/ConfigureNegotiator.cs src/GeekFlashCore.Protocol.Qcom/Vendors/Oplus/OplusDigestLegacyPolicy.cs src/GeekFlashCore.Protocol.Qcom/Vendors/Oplus/OplusGptCompatibility.cs src/GeekFlashCore.Protocol.Qcom/Localization/Strings.resx src/GeekFlashCore.Protocol.Qcom/Localization/Strings.en.resx
git commit -m "fix(qcom): align cancellation boundaries"
```

### Task 3: Isolate Qcom resource resolution and retain cancellation sources safely

**Files:**

- Create: `src/GeekFlashCore.Protocol.Qcom/Resources/QcomResourceResolver.cs`
- Modify: `src/GeekFlashCore.Protocol.Qcom/QcomProtocol.cs`
- Test: `.tests/GeekFlashCore.Protocol.Qcom.Tests/Resources/QcomResourceResolverTests.cs`
- Test: `.tests/GeekFlashCore.Protocol.Qcom.Tests/QcomProtocolWorkflowTests.cs`

**Interfaces:**

- Consumes: lifetime token, `QcomProtocolOptions.ResourceRequestTimeoutMilliseconds`, provider `ValueTask<T>`, and optional late-result disposer.
- Produces: `ValueTask<T> ResolveAsync<T>(Func<CancellationToken, ValueTask<T>>, CancellationToken, Action<T>?)` and `T Resolve<T>(Func<CancellationToken, ValueTask<T>>, Action<T>?)`, both constrained with `where T : class`.

- [x] **Step 1: Add failing lifetime and sensitive-payload tests**

Create a provider that waits for cancellation, deliberately reads its token after the caller has already timed out, then returns a `VendorAuthenticationResourceResponse`. Assert that token access is safe and the late payload is disposed:

```csharp
[Fact]
public async Task ResolveAsync_KeepsLinkedSourceAliveUntilLateProviderCompletes()
{
    using var lifetime = new CancellationTokenSource();
    var resolver = new QcomResourceResolver(20, lifetime.Token);
    var payload = SensitiveDataOwner.CopyFrom([1, 2, 3, 4]);
    var providerFinished = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);

    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        await resolver.ResolveAsync(async token =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { }
            _ = token.WaitHandle;
            providerFinished.SetResult();
            return new VendorAuthenticationResourceResponse(payload);
        }, CancellationToken.None, static response => response.Payload.Dispose()));

    await providerFinished.Task.WaitAsync(TimeSpan.FromSeconds(1));
    Assert.Throws<ObjectDisposedException>(() => payload.Memory.ToArray());
}
```

Add cases for synchronous success, null result, provider exception mapping, caller cancellation and lifetime cancellation. Provider exceptions other than `QcomProtocolException` must surface as `QcomResourceException` with the original exception as `InnerException`.

- [x] **Step 2: Confirm RED against the current private implementation**

Run:

```powershell
dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~QcomResourceResolverTests|FullyQualifiedName~IgnoringCancellation_ResourceTimeoutDisposesLateSignature"
```

Expected: compilation fails because `QcomResourceResolver` does not exist; the existing workflow test remains as integration coverage.

- [x] **Step 3: Implement resolver ownership transfer**

The resolver owns the linked CTS. On cancellation it transfers CTS disposal to the observer task together with late-result cleanup:

```csharp
internal sealed class QcomResourceResolver
{
    private readonly CancellationToken _lifetimeToken;
    private readonly int _timeoutMilliseconds;

    internal QcomResourceResolver(
        int timeoutMilliseconds,
        CancellationToken lifetimeToken)
    {
        if (timeoutMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        _lifetimeToken = lifetimeToken;
        _timeoutMilliseconds = timeoutMilliseconds;
    }

    internal async ValueTask<T> ResolveAsync<T>(
        Func<CancellationToken, ValueTask<T>> request,
        CancellationToken cancellationToken,
        Action<T>? disposeLateResult = null) where T : class
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetimeToken);
        linked.CancelAfter(_timeoutMilliseconds);
        Task<T>? pending = null;
        bool ownershipTransferred = false;
        try
        {
            pending = request(linked.Token).AsTask();
            T result = await pending.WaitAsync(linked.Token).ConfigureAwait(false);
            return result ?? throw new QcomResourceException(Strings.Qcom_InvalidResource);
        }
        catch (OperationCanceledException)
        {
            if (pending is not null)
            {
                ownershipTransferred = true;
                _ = ObserveLateAsync(pending, linked, disposeLateResult);
            }
            throw;
        }
        catch (Exception exception) when (exception is not QcomProtocolException)
        {
            throw new QcomResourceException(Strings.Qcom_InvalidResource, exception);
        }
        finally
        {
            if (!ownershipTransferred) linked.Dispose();
        }
    }

    internal T Resolve<T>(
        Func<CancellationToken, ValueTask<T>> request,
        Action<T>? disposeLateResult = null) where T : class
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
        linked.CancelAfter(_timeoutMilliseconds);
        Task<T>? pending = null;
        bool ownershipTransferred = false;
        try
        {
            pending = request(linked.Token).AsTask();
            T result = pending.WaitAsync(linked.Token).GetAwaiter().GetResult();
            return result ?? throw new QcomResourceException(Strings.Qcom_InvalidResource);
        }
        catch (OperationCanceledException)
        {
            if (pending is not null)
            {
                ownershipTransferred = true;
                _ = ObserveLateAsync(pending, linked, disposeLateResult);
            }
            throw;
        }
        catch (Exception exception) when (exception is not QcomProtocolException)
        {
            throw new QcomResourceException(Strings.Qcom_InvalidResource, exception);
        }
        finally
        {
            if (!ownershipTransferred) linked.Dispose();
        }
    }

    private static async Task ObserveLateAsync<T>(
        Task<T> pending,
        CancellationTokenSource linked,
        Action<T>? disposeLateResult)
    {
        try { disposeLateResult?.Invoke(await pending.ConfigureAwait(false)); }
        catch { /* Observe provider failure; never retain a late sensitive result. */ }
        finally { linked.Dispose(); }
    }
}
```

The synchronous method uses `.GetAwaiter().GetResult()` only at the existing synchronous host boundary; no protocol-layer async wait is introduced.

- [x] **Step 4: Delegate from QcomProtocol**

Create `_resourceResolver` after `_options.Validate()`. Replace every private `ResolveResourceAsync/Sync` call with the collaborator. Authentication calls pass the exact disposer below; image, configuration, Digest and VIP calls pass no disposer because their `IDataSource` ownership remains with their response contract:

```csharp
static void DisposeLateAuthentication(VendorAuthenticationResourceResponse response) =>
    response.Payload.Dispose();
```

Delete `ResolveResourceAsync`, `ResolveResourceSync` and `DisposeLateAuthenticationAsync` from `QcomProtocol.cs`.

- [x] **Step 5: Verify lifecycle behavior**

Run:

```powershell
dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~QcomResourceResolverTests|FullyQualifiedName~QcomProtocolWorkflowTests"
dotnet build GeekFlashCore.slnx -c Release --no-restore
git diff --check
```

Expected: all focused tests pass; cancellation does not produce `ObjectDisposedException` in the provider; late authentication payload is disposed; solution builds cleanly.

- [x] **Step 6: Commit**

```powershell
git add src/GeekFlashCore.Protocol.Qcom/Resources/QcomResourceResolver.cs src/GeekFlashCore.Protocol.Qcom/QcomProtocol.cs
git commit -m "refactor(qcom): isolate resource resolution"
```

### Task 4: Replace duplicate seekable Stream adapters

**Files:**

- Create: `src/GeekFlashCore.BlockDevice/StreamBlockDevice.cs`
- Modify: `src/GeekFlashCore.BlockDevice/Localization/Strings.resx`
- Modify: `src/GeekFlashCore.BlockDevice/Localization/Strings.en.resx`
- Modify: `src/GeekFlashCore.Android.Sparse/SparseImageWriter.cs`
- Delete: `src/GeekFlashCore.Android.Sparse/BlockDevice/SeekableStreamBlockDevice.cs`
- Modify: `src/GeekFlashCore.Android.Lp/LpPartitionImageSource.cs`
- Delete: `src/GeekFlashCore.Android.Lp/SeekableStreamBlockDevice.cs`
- Modify: `src/GeekFlashCore.Protocol.Qcom/GeekFlashCore.Protocol.Qcom.csproj`
- Modify: `src/GeekFlashCore.Protocol.Qcom/Firehose/Programming/SparseProgramPlanner.cs`
- Test create: `.tests/GeekFlashCore.Core.Tests/GeekFlashCore.Core.Tests.csproj`
- Test create: `.tests/GeekFlashCore.Core.Tests/BlockDevice/StreamBlockDeviceTests.cs`

**Interfaces:**

- Consumes: `IReadableBlockDevice`, `BlockDeviceId`, `BlockDeviceIO`, `DeviceOwnership`.
- Produces: `public sealed class StreamBlockDevice` with constructor `StreamBlockDevice(Stream source, long length, DeviceOwnership ownership, int logicalBlockSize = 512, BlockDeviceId? id = null)`.

- [x] **Step 1: Create the ignored local core test project**

Use this exact project definition and confirm `git check-ignore` identifies it as ignored:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/GeekFlashCore.BlockDevice/GeekFlashCore.BlockDevice.csproj" />
    <ProjectReference Include="../../src/GeekFlashCore.Android.Sparse/GeekFlashCore.Android.Sparse.csproj" />
    <ProjectReference Include="../../src/GeekFlashCore.FileSystem.Abstractions/GeekFlashCore.FileSystem.Abstractions.csproj" />
  </ItemGroup>
</Project>
```

Run `git check-ignore -v .tests/GeekFlashCore.Core.Tests/GeekFlashCore.Core.Tests.csproj`; expected: an ignore rule and the test path are printed.

- [x] **Step 2: Add failing adapter contract tests**

Cover non-zero origin, fixed length, EOF, empty read, negative offset, position restoration, Borrow, Transfer, invalid ownership, non-seekable source and concurrent reads. The central behavior test is:

```csharp
[Fact]
public void ReadAt_UsesCapturedOriginAndRestoresSourcePosition()
{
    using var source = new MemoryStream(Enumerable.Range(0, 32)
        .Select(static value => (byte)value).ToArray());
    source.Position = 8;
    using var device = new StreamBlockDevice(
        source, 12, DeviceOwnership.Borrow, logicalBlockSize: 4,
        id: new BlockDeviceId("test:stream"));
    source.Position = 3;
    Span<byte> destination = stackalloc byte[6];

    int read = device.ReadAt(2, destination);

    Assert.Equal(6, read);
    Assert.True(destination.SequenceEqual(new byte[] { 10, 11, 12, 13, 14, 15 }));
    Assert.Equal(3, source.Position);
    Assert.Equal(12, device.Length);
    Assert.Equal(4, device.LogicalBlockSize);
}
```

- [x] **Step 3: Confirm RED**

Run:

```powershell
dotnet restore .tests/GeekFlashCore.Core.Tests/GeekFlashCore.Core.Tests.csproj
dotnet test .tests/GeekFlashCore.Core.Tests/GeekFlashCore.Core.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~StreamBlockDeviceTests
```

Expected: compilation fails because public `StreamBlockDevice` does not exist.

- [x] **Step 4: Implement the shared adapter**

Implement construction and reads with captured origin, instance locking and `finally` restoration:

```csharp
public sealed class StreamBlockDevice : IReadableBlockDevice
{
    private readonly Stream _source;
    private readonly object _sync = new();
    private readonly long _origin;
    private readonly bool _ownsSource;
    private bool _disposed;

    public StreamBlockDevice(Stream source, long length, DeviceOwnership ownership,
        int logicalBlockSize = 512, BlockDeviceId? id = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
            throw new ArgumentException(Strings.StreamMustBeReadableAndSeekable, nameof(source));
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfLessThan(logicalBlockSize, 1);
        if (!Enum.IsDefined(ownership)) throw new ArgumentOutOfRangeException(nameof(ownership));
        _origin = source.Position;
        if (_origin > source.Length - length) throw new ArgumentOutOfRangeException(nameof(length));
        _source = source;
        _ownsSource = ownership == DeviceOwnership.Transfer;
        Length = length;
        LogicalBlockSize = logicalBlockSize;
        Id = id ?? new BlockDeviceId($"stream:{Guid.NewGuid():N}");
    }

    public int ReadAt(long offset, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int length = BlockDeviceIO.GetReadLength(Length, offset, destination.Length);
        if (length == 0) return 0;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long previous = _source.Position;
            try
            {
                _source.Position = checked(_origin + offset);
                return BlockDeviceIO.ValidateReadResult(
                    _source.Read(destination[..length]), length);
            }
            finally { _source.Position = previous; }
        }
    }
}
```

Add idempotent locked disposal. Add `StreamMustBeReadableAndSeekable` to BlockDevice Chinese/English resources.

- [x] **Step 5: Replace all three private adapters**

Use explicit ownership and logical block sizes:

```csharp
new StreamBlockDevice(source, checked(source.Length - source.Position),
    DeviceOwnership.Borrow, logicalBlockSize: 1,
    id: new BlockDeviceId("stream:sparse-writer"))
```

LP passes its existing `length` and `ownership`. Qcom adds a direct `GeekFlashCore.BlockDevice` project reference and uses id `qcom-sparse-source`. Delete both duplicate class files and the nested `SparseProgramPlanner.StreamSource`.

- [x] **Step 6: Verify adapter and consumers**

Run:

```powershell
dotnet test .tests/GeekFlashCore.Core.Tests/GeekFlashCore.Core.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~StreamBlockDeviceTests
dotnet test .tests/GeekFlashCore.Android.Lp.Tests/GeekFlashCore.Android.Lp.Tests.csproj -c Release --no-restore
dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~FirehoseProgramExecutorTests
dotnet build GeekFlashCore.slnx -c Release --no-restore
git diff --check
```

Expected: all tests pass, no source file named `SeekableStreamBlockDevice.cs` remains, and `rg "class StreamSource|class SeekableStreamBlockDevice" src` has no output.

- [x] **Step 7: Commit production changes only**

```powershell
git add src/GeekFlashCore.BlockDevice src/GeekFlashCore.Android.Sparse/SparseImageWriter.cs src/GeekFlashCore.Android.Sparse/BlockDevice/SeekableStreamBlockDevice.cs src/GeekFlashCore.Android.Lp/LpPartitionImageSource.cs src/GeekFlashCore.Android.Lp/SeekableStreamBlockDevice.cs src/GeekFlashCore.Protocol.Qcom/GeekFlashCore.Protocol.Qcom.csproj src/GeekFlashCore.Protocol.Qcom/Firehose/Programming/SparseProgramPlanner.cs
git commit -m "refactor(block): share seekable stream adapter"
```

### Task 5: Build Qcom Sparse plans from one strict parse

**Files:**

- Modify: `src/GeekFlashCore.Android.Sparse/SparseDocument.cs`
- Modify: `src/GeekFlashCore.Protocol.Qcom/Firehose/Programming/SparseProgramPlanner.cs`
- Test: `.tests/GeekFlashCore.Core.Tests/Sparse/SparseDocumentRegionTests.cs`
- Test create: `.tests/GeekFlashCore.Core.Tests/Sparse/SparseFixture.cs`
- Test: `.tests/GeekFlashCore.Protocol.Qcom.Tests/Programming/FirehoseProgramExecutorTests.cs`

**Interfaces:**

- Consumes: `SparseDocument.Chunks`, `SparseChunk.OutputOffset`, `SparseChunk.PayloadOffset`, `SparseRegion`, and the shared `StreamBlockDevice` from Task 4.
- Produces: `public IReadOnlyList<SparseRegion> SparseDocument.CreateDataRegions()`; Qcom consumes these regions without calling `SparseImageParser.Parse(Stream)`.

- [x] **Step 1: Add region projection and single-scan tests**

Build a sparse fixture with Raw, adjacent Raw, Don't Care, Fill and CRC chunks. Assert exact region starts and lengths:

```csharp
[Fact]
public void CreateDataRegions_MergesRawAndPreservesSparseGaps()
{
    byte[] image = SparseFixture.Create(512,
        SparseFixture.Raw(Enumerable.Repeat((byte)0x11, 512).ToArray()),
        SparseFixture.Raw(Enumerable.Repeat((byte)0x22, 512).ToArray()),
        SparseFixture.DontCare(2),
        SparseFixture.Fill(1, 0xAABBCCDD),
        SparseFixture.Crc32(0));
    using var source = new MemoryStream(image);
    using var device = new StreamBlockDevice(source, image.Length,
        DeviceOwnership.Borrow, 1, new BlockDeviceId("test:sparse"));
    using SparseDocument document = SparseImageParser.Open(device, DeviceOwnership.Borrow);

    IReadOnlyList<SparseRegion> regions = document.CreateDataRegions();

    Assert.Collection(regions,
        raw => { Assert.Equal(0u, raw.StartBlock); Assert.Equal(1024, raw.Length); },
        fill => { Assert.Equal(4u, fill.StartBlock); Assert.Equal(512, fill.Length); });
}
```

Add a `CountingStream` integration test around `FirehoseStorageService.Program` with `Format = FirehoseProgramFormat.AndroidSparse`. Count reads that begin at the captured source origin and include the 28-byte sparse header; assert the header is read once. The current planner executes both `SparseImageParser.Open` and `SparseImageParser.Parse`, so it reads the header twice.

Create the core-test fixture with explicit little-endian encoding and these signatures; each chunk factory returns its type, block count and payload, while `Create` computes total blocks and writes Android Sparse headers:

```csharp
internal static class SparseFixture
{
    internal readonly record struct Chunk(ushort Type, uint Blocks, byte[] Payload);

    internal static Chunk Raw(byte[] payload) =>
        new(0xCAC1, checked((uint)(payload.Length / 512)), payload);
    internal static Chunk Fill(uint blocks, uint value)
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, value);
        return new Chunk(0xCAC2, blocks, payload);
    }
    internal static Chunk DontCare(uint blocks) => new(0xCAC3, blocks, []);
    internal static Chunk Crc32(uint value)
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, value);
        return new Chunk(0xCAC4, 0, payload);
    }

    internal static byte[] Create(uint blockSize, params Chunk[] chunks)
    {
        using var output = new MemoryStream();
        Span<byte> header = stackalloc byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0xED26FF3A);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..], 28);
        BinaryPrimitives.WriteUInt16LittleEndian(header[10..], 12);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], blockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..],
            checked((uint)chunks.Sum(static chunk => (long)chunk.Blocks)));
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], checked((uint)chunks.Length));
        output.Write(header);
        foreach (Chunk chunk in chunks)
        {
            Span<byte> chunkHeader = stackalloc byte[12];
            BinaryPrimitives.WriteUInt16LittleEndian(chunkHeader, chunk.Type);
            BinaryPrimitives.WriteUInt32LittleEndian(chunkHeader[4..], chunk.Blocks);
            BinaryPrimitives.WriteUInt32LittleEndian(chunkHeader[8..],
                checked((uint)(12 + chunk.Payload.Length)));
            output.Write(chunkHeader);
            output.Write(chunk.Payload);
        }
        return output.ToArray();
    }
}
```

In the Qcom test, `CountingStream.HeaderReadCount` increments only when `Read(Span<byte>)` begins at the captured source origin and the requested span includes the 28-byte file header. Assert `HeaderReadCount == 1`; the current two-parser path produces 2.

- [x] **Step 2: Confirm RED**

Run:

```powershell
dotnet test .tests/GeekFlashCore.Core.Tests/GeekFlashCore.Core.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~SparseDocumentRegionTests
dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~FirehoseProgramExecutorTests
```

Expected: core test does not compile because `CreateDataRegions` is absent; the Qcom counting test detects the reparse.

- [x] **Step 3: Project validated chunks into streaming regions**

Add `CreateDataRegions` to `SparseDocument`. Consecutive Raw chunks remain one region even when payloads are separated by chunk headers; `SparseRegionStream` already reads a list of source offsets. Fill is a one-chunk region; Don't Care and CRC flush Raw and emit no region:

```csharp
public IReadOnlyList<SparseRegion> CreateDataRegions()
{
    ThrowIfDisposed();
    var regions = new List<SparseRegion>();
    var rawChunks = new List<SparseDataChunk>();
    uint rawStartBlock = 0;
    long rawLength = 0;

    foreach (SparseChunk chunk in _chunks)
    {
        uint startBlock = checked((uint)(chunk.OutputOffset / Header.BlockSize));
        if (chunk.Type == SparseChunkType.Raw)
        {
            if (rawChunks.Count == 0) rawStartBlock = startBlock;
            rawChunks.Add(new SparseDataChunk(
                SparseDataChunkType.Raw, chunk.PayloadOffset, chunk.OutputLength, 0));
            rawLength = checked(rawLength + chunk.OutputLength);
            continue;
        }

        FlushRawRegion(regions, rawChunks, rawStartBlock, ref rawLength);
        if (chunk.Type == SparseChunkType.Fill)
            regions.Add(new SparseRegion(startBlock, chunk.OutputLength,
                [new SparseDataChunk(SparseDataChunkType.Fill, 0,
                    chunk.OutputLength, chunk.FillValue)]));
    }

    FlushRawRegion(regions, rawChunks, rawStartBlock, ref rawLength);
    return regions;
}
```

The private `FlushRawRegion` added to `SparseDocument` must snapshot `rawChunks.ToArray()`, clear the list and reset `rawLength` exactly as the legacy parser does.

- [x] **Step 4: Make SparseProgramPlanner consume one document**

Open one shared `StreamBlockDevice`, one `SparseDocument`, verify checksum when `NotVerified`, check block-size alignment and expanded length, then iterate `document.CreateDataRegions()`. Delete the `try/finally` source rewind and the call to `SparseImageParser.Parse(source)`. Keep `SparseImageParser.Parse` public for compatibility, but no production Qcom path may call it.

- [x] **Step 5: Verify bytes, ordering and bounded planning memory**

Run:

```powershell
dotnet test .tests/GeekFlashCore.Core.Tests/GeekFlashCore.Core.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~SparseDocumentRegionTests
dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~FirehoseProgramExecutorTests|FullyQualifiedName~SparseProgressCountsTransmittedRegionsInsteadOfPartitionCapacity"
rg -n "SparseImageParser\.Parse" src/GeekFlashCore.Protocol.Qcom
dotnet build GeekFlashCore.slnx -c Release --no-restore
git diff --check
```

Expected: tests pass; `rg` has no output; mixed Raw/Fill/Don't Care bytes and XML sector starts match existing test expectations; planning structures scale with chunk count.

- [x] **Step 6: Commit**

```powershell
git add src/GeekFlashCore.Android.Sparse/SparseDocument.cs src/GeekFlashCore.Protocol.Qcom/Firehose/Programming/SparseProgramPlanner.cs
git commit -m "perf(qcom): parse sparse programs once"
```

### Task 6: Localize all CLI user-visible text

**Files:**

- Modify: `src/GeekFlashCore.CLI/Localization/Strings.resx`
- Modify: `src/GeekFlashCore.CLI/Localization/Strings.en.resx`
- Modify: `src/GeekFlashCore.CLI/CommandLine.cs`
- Modify: `src/GeekFlashCore.CLI/CliApplication.cs`
- Modify: `src/GeekFlashCore.CLI/TransportResolver.cs`
- Modify: `src/GeekFlashCore.CLI/ProtocolRegistry.cs`
- Modify: `src/GeekFlashCore.CLI/ConsoleUi.cs`
- Modify: `src/GeekFlashCore.CLI/QcomProtocolHostAdapter.cs`
- Modify: `src/GeekFlashCore.CLI/ConsoleProviders.cs`
- Modify: `src/GeekFlashCore.CLI/Program.cs`
- Test: `.tests/GeekFlashCore.CLI.Tests/ConsoleRegressionTests.cs`
- Test: `.tests/GeekFlashCore.CLI.Tests/CommandDispatchTests.cs`
- Test create: `.tests/GeekFlashCore.CLI.Tests/LocalizationResourceTests.cs`
- Test modify: `.tests/GeekFlashCore.CLI.Tests/GeekFlashCore.CLI.Tests.csproj`

**Interfaces:**

- Consumes: generated `GeekFlashCore.CLI.Strings` properties and `Format<Key>` helpers.
- Produces: identical resource key sets in neutral Chinese and English resources; no Chinese literal remains in CLI `.cs` files.

- [x] **Step 1: Add culture and resource parity tests**

Capture help output under `zh-CN` and `en-US`, and compare resource key sets:

```csharp
[Theory]
[InlineData("zh-CN", "用法")]
[InlineData("en-US", "Usage")]
public void PrintHelp_UsesCurrentUICulture(string cultureName, string expected)
{
    CultureInfo previous = CultureInfo.CurrentUICulture;
    TextWriter previousOutput = Console.Out;
    try
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);
        using var output = new StringWriter();
        Console.SetOut(output);
        CommandLine.PrintHelp();
        Assert.Contains(expected, output.ToString());
    }
    finally
    {
        Console.SetOut(previousOutput);
        CultureInfo.CurrentUICulture = previous;
    }
}
```

Link the production resources into the ignored CLI test output:

```xml
<ItemGroup>
  <None Include="../../src/GeekFlashCore.CLI/Localization/Strings.resx"
        Link="Fixtures/Strings.resx" CopyToOutputDirectory="PreserveNewest" />
  <None Include="../../src/GeekFlashCore.CLI/Localization/Strings.en.resx"
        Link="Fixtures/Strings.en.resx" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

`LocalizationResourceTests` loads both files with `XDocument`, selects `root/data`, compares the sorted `name` arrays, and asserts every `value` element is nonblank:

```csharp
private static IReadOnlyDictionary<string, string> Load(string path) =>
    XDocument.Load(path).Root!.Elements("data").ToDictionary(
        element => (string)element.Attribute("name")!,
        element => (string?)element.Element("value") ?? string.Empty,
        StringComparer.Ordinal);

[Fact]
public void NeutralAndEnglishResourcesHaveIdenticalNonblankKeys()
{
    string fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    IReadOnlyDictionary<string, string> neutral = Load(Path.Combine(fixtures, "Strings.resx"));
    IReadOnlyDictionary<string, string> english = Load(Path.Combine(fixtures, "Strings.en.resx"));
    Assert.Equal(neutral.Keys.Order(), english.Keys.Order());
    Assert.DoesNotContain(neutral.Values, string.IsNullOrWhiteSpace);
    Assert.DoesNotContain(english.Values, string.IsNullOrWhiteSpace);
}
```

- [x] **Step 2: Confirm RED**

Run:

```powershell
dotnet test .tests/GeekFlashCore.CLI.Tests/GeekFlashCore.CLI.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~PrintHelp_UsesCurrentUICulture|FullyQualifiedName~LocalizationResourceTests"
```

Expected: English help still contains hardcoded Chinese text or the key sets differ after the new assertions are introduced.

- [x] **Step 3: Add exact resource contracts**

Add matching Chinese and English values for these keys, preserving command names and format arguments:

```text
Cli_Title
Cli_HelpUsage
Cli_HelpCommands
Cli_HelpOptionsPrimary
Cli_HelpOptionsSecondary
Cli_UnknownOption
Cli_MissingOptionValue
Cli_UnknownCommand
Cli_UnclosedQuote
Cli_ProtocolNotRegistered
Cli_UsbFormatInvalid
Cli_WaitingForDevice
Cli_DeviceMissingComPort
Cli_ProtocolSelectionRequired
Cli_InfoProtocol
Cli_InfoVendor
Cli_InfoHardware
Cli_InfoSahara
Cli_InfoSaharaIdentity
Cli_InfoHardwareIds
Cli_InfoFirehose
Cli_InfoFirehoseDevice
Cli_InfoStorage
Cli_UnknownValue
Cli_ArgumentError
Cli_OperationCancelled
Cli_CommandFailed
Cli_QcomWaitingForDevice
Cli_SaharaProgrammerPrompt
Cli_SaharaProgrammerMissing
Cli_OplusDigestPrompt
Cli_OplusDigestMissing
Cli_FirehoseDigestPrompt
Cli_FirehoseDigestMissing
Cli_VipSignedPrompt
Cli_VipSignedMissing
Cli_VipChainedPrompt
Cli_VipChainedMissing
Cli_AuthenticationRequired
Cli_AuthenticationPayloadPrompt
Cli_AuthenticationCancelled
Cli_AuthenticationPayloadInvalid
Cli_SaharaProbeResult
Cli_XmlResult
```

For example, `Cli_UnknownOption` is `未知选项 {0}。` / `Unknown option {0}.`; `Cli_ProtocolNotRegistered` is `协议“{0}”当前未注册；可用协议：{1}。` / `Protocol '{0}' is not registered. Available protocols: {1}.`

- [x] **Step 4: Replace literals without changing structure or exit codes**

Use generated format helpers for parameters:

```csharp
throw new ArgumentException(Strings.FormatCli_UnknownOption(name));
```

```csharp
Console.WriteLine(Strings.Cli_Title);
Console.WriteLine(Strings.Cli_HelpUsage);
Console.WriteLine(Strings.Cli_HelpCommands);
```

Keep protocol tokens (`QualcommEdl`), option names (`--usb`), VID/PID, paths and device-returned strings as format arguments. Do not localize log level abbreviations or raw protocol field names.

- [x] **Step 5: Verify both cultures and scan production literals**

Run:

```powershell
dotnet test .tests/GeekFlashCore.CLI.Tests/GeekFlashCore.CLI.Tests.csproj -c Release --no-restore
rg -n "[一-龥]" src/GeekFlashCore.CLI --glob "*.cs"
dotnet build src/GeekFlashCore.CLI/GeekFlashCore.CLI.csproj -c Release --no-restore
git diff --check
```

Expected: CLI tests pass; the Chinese scan has no output; CLI builds with 0 errors; resource keys are identical.

- [x] **Step 6: Commit**

```powershell
git add src/GeekFlashCore.CLI/Localization/Strings.resx src/GeekFlashCore.CLI/Localization/Strings.en.resx src/GeekFlashCore.CLI/CommandLine.cs src/GeekFlashCore.CLI/CliApplication.cs src/GeekFlashCore.CLI/TransportResolver.cs src/GeekFlashCore.CLI/ProtocolRegistry.cs src/GeekFlashCore.CLI/ConsoleUi.cs src/GeekFlashCore.CLI/QcomProtocolHostAdapter.cs src/GeekFlashCore.CLI/ConsoleProviders.cs src/GeekFlashCore.CLI/Program.cs
git commit -m "fix(cli): localize remaining user messages"
```

### Task 7: Remove proven low-risk redundancy and fix public argument contracts

**Files:**

- Modify: `src/GeekFlashCore.Protocol.Abstractions/GeekFlashCore.Protocol.Abstractions.csproj`
- Modify: `src/GeekFlashCore.FileSystem.Ext/ExtVolume.cs`
- Modify: `src/GeekFlashCore.FileSystem.Ext/ExtDirectoryReader.cs`
- Modify: `src/GeekFlashCore.FileSystem.Erofs/ErofsVolume.cs`
- Modify: `src/GeekFlashCore.FileSystem.Erofs/ErofsDirectoryReader.cs`
- Modify: `src/GeekFlashCore.FileSystem.Abstractions/Models/FileSystemReadLimits.cs`
- Create: `src/GeekFlashCore.FileSystem.Abstractions/Localization/Strings.resx`
- Create: `src/GeekFlashCore.FileSystem.Abstractions/Localization/Strings.en.resx`
- Modify: `src/GeekFlashCore.Protocol.Qcom/QcomDeviceIdentify.cs`
- Modify: `src/GeekFlashCore.UsbWatcher/Extensions/UsbDeviceMonitorExtensions.cs`
- Modify: `src/GeekFlashCore.Protocol.Qcom.Abstractions/Models/FirehoseResponses.cs`
- Modify: `src/GeekFlashCore.BlockDevice.Abstractions/GlobalUsings.cs`
- Modify: `src/GeekFlashCore.FileSystem.Erofs/GlobalUsings.cs`
- Modify: `src/GeekFlashCore.Android.Sparse/GlobalUsings.cs`
- Modify: `src/GeekFlashCore.FileSystem.Ext/GlobalUsings.cs`
- Modify: `src/GeekFlashCore.Protocol.Qcom.Abstractions/Packets/SaharaPackets.cs`
- Test: `.tests/GeekFlashCore.Core.Tests/FileSystem/FileSystemReadLimitsTests.cs`
- Test: `.tests/GeekFlashCore.Protocol.Qcom.Tests/PublicContractTests.cs`

**Interfaces:**

- Consumes: .NET 8 `Stream.ReadExactly(Span<byte>)` and existing public constructors/extensions.
- Produces: correct `ArgumentException.ParamName`, explicit null guards, no unused Serilog dependency in Protocol.Abstractions, and no duplicate private sequential read loops.

- [x] **Step 1: Add failing public contract tests**

```csharp
[Fact]
public void Constructor_WorkingBudgetTooSmallNamesWorkingBudget()
{
    ArgumentException exception = Assert.Throws<ArgumentException>(() =>
        new FileSystemReadLimits(
            maximumWorkingBytes: 4096,
            maximumCompressedInputBytes: 4096,
            maximumDecodedBytes: 4096));

    Assert.Equal("maximumWorkingBytes", exception.ParamName);
    Assert.NotEqual("maximumWorkingBytes", exception.Message);
}

[Fact]
public void PublicReferenceParametersRejectNull()
{
    Assert.Throws<ArgumentNullException>(() => new QcomDeviceIdentify().Identify(null!));
    Assert.Throws<ArgumentNullException>(() => UsbDeviceMonitorExtensions.ExtractPortName(null!));
    Assert.Throws<ArgumentNullException>(() => new FirehoseResponse<int>(null!, 1));
}
```

- [x] **Step 2: Confirm RED**

Run:

```powershell
dotnet test .tests/GeekFlashCore.Core.Tests/GeekFlashCore.Core.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~FileSystemReadLimitsTests
dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~PublicContractTests
```

Expected: `FileSystemReadLimits` has null `ParamName`, and one or more public null cases throw `NullReferenceException` or fail later.

- [x] **Step 3: Correct exception and null contracts**

Add resource `WorkingBudgetTooSmall` as `工作缓冲预算必须至少容纳压缩输入与解码输出。` / `The working buffer budget must contain both compressed input and decoded output.` and throw:

```csharp
throw new ArgumentException(
    Strings.WorkingBudgetTooSmall,
    nameof(maximumWorkingBytes));
```

Because FileSystem.Abstractions did not previously have resources, create both files with the standard resx headers and this exact data entry (English file uses the English value):

```xml
<?xml version="1.0" encoding="utf-8"?>
<root>
  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
  <resheader name="version"><value>2.0</value></resheader>
  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms</value></resheader>
  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms</value></resheader>
  <data name="WorkingBudgetTooSmall" xml:space="preserve">
    <value>工作缓冲预算必须至少容纳压缩输入与解码输出。</value>
  </data>
</root>
```

At the first line of `Identify` and `ExtractPortName`, add `ArgumentNullException.ThrowIfNull(...)`. The generic Firehose response must reject null in its base-constructor initializer before any member access:

```csharp
public FirehoseResponse(FirehoseResponse response, T? data)
    : base(
        (response ?? throw new ArgumentNullException(nameof(response))).Logs,
        response.Attributes,
        response.Status,
        response.RawMode,
        response.PayloadElements)
{
    Data = data;
}
```

Simplify `QcomDeviceIdentify` to return the conditional result directly while retaining its protocol rationale comment.

- [x] **Step 4: Replace duplicate sequential read loops**

At each Ext/Erofs call site replace the private helper call with the framework method:

```csharp
stream.ReadExactly(target);
```

Delete only the four private helpers whose entire behavior is an offset loop over `Stream.Read`. Keep `ErofsXattrReader.ReadExactly(Stream, long, Span<byte>)` because it also performs positioned access and therefore is not duplicate behavior.

- [x] **Step 5: Remove the unused dependency and valueless comments**

Delete this item from `GeekFlashCore.Protocol.Abstractions.csproj`:

```xml
<PackageReference Include="Serilog" Version="4.4.0" />
```

Remove only comments that literally describe generated global-using syntax or IDE code generation. Preserve comments explaining Qualcomm PID selection, protocol compatibility, ownership, retries and safety boundaries.

- [x] **Step 6: Verify targeted contracts and all consumers**

Run:

```powershell
dotnet test .tests/GeekFlashCore.Core.Tests/GeekFlashCore.Core.Tests.csproj -c Release --no-restore
dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore
dotnet build GeekFlashCore.slnx -c Release --no-restore
rg -n "private static void ReadExactly\(Stream (source|stream), Span<byte> destination\)" src/GeekFlashCore.FileSystem.Ext src/GeekFlashCore.FileSystem.Erofs
git diff --check
```

Expected: tests and solution build pass; `rg` has no output; each project that uses Serilog still has its own explicit package reference or an intentional direct reference.

- [x] **Step 7: Commit**

Review `git diff --name-only` before staging, then stage only the files listed in this task:

```powershell
git add src/GeekFlashCore.Protocol.Abstractions/GeekFlashCore.Protocol.Abstractions.csproj src/GeekFlashCore.FileSystem.Ext/ExtVolume.cs src/GeekFlashCore.FileSystem.Ext/ExtDirectoryReader.cs src/GeekFlashCore.FileSystem.Ext/GlobalUsings.cs src/GeekFlashCore.FileSystem.Erofs/ErofsVolume.cs src/GeekFlashCore.FileSystem.Erofs/ErofsDirectoryReader.cs src/GeekFlashCore.FileSystem.Erofs/GlobalUsings.cs src/GeekFlashCore.FileSystem.Abstractions/Models/FileSystemReadLimits.cs src/GeekFlashCore.FileSystem.Abstractions/Localization/Strings.resx src/GeekFlashCore.FileSystem.Abstractions/Localization/Strings.en.resx src/GeekFlashCore.Protocol.Qcom/QcomDeviceIdentify.cs src/GeekFlashCore.UsbWatcher/Extensions/UsbDeviceMonitorExtensions.cs src/GeekFlashCore.Protocol.Qcom.Abstractions/Models/FirehoseResponses.cs src/GeekFlashCore.BlockDevice.Abstractions/GlobalUsings.cs src/GeekFlashCore.Android.Sparse/GlobalUsings.cs src/GeekFlashCore.Protocol.Qcom.Abstractions/Packets/SaharaPackets.cs
git commit -m "refactor(core): remove redundant infrastructure"
```

### Task 8: Full verification, analyzer disposition and progress record

**Files:**

- Modify: `docs/plans/2026-09-04-qcom-protocol-implementation.md`
- Modify: `docs/plans/2026-09-06-project-quality-review-design.md`
- Modify: `docs/plans/2026-09-06-project-quality-review-implementation.md`

**Interfaces:**

- Consumes: commits and verification evidence from Tasks 1–7.
- Produces: dated progress entries containing behavior, commands, exact pass counts, commit hashes, workspace state and unresolved hardware risks.

- [x] **Step 1: Run every local test suite**

```powershell
dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore
dotnet test .tests/GeekFlashCore.CLI.Tests/GeekFlashCore.CLI.Tests.csproj -c Release --no-restore
dotnet test .tests/GeekFlashCore.Android.Lp.Tests/GeekFlashCore.Android.Lp.Tests.csproj -c Release --no-restore
dotnet test .tests/GeekFlashCore.Core.Tests/GeekFlashCore.Core.Tests.csproj -c Release --no-restore
```

Expected: all four commands exit 0. Record each exact total; do not summarize a failing or skipped suite as passed.

- [x] **Step 2: Run Release build and repository hygiene checks**

```powershell
dotnet build GeekFlashCore.slnx -c Release --no-restore
git diff --check
git status --short --ignored
git ls-files .tests
```

Expected: build exits 0 with no errors; diff check has no output; `git ls-files .tests` has no output; status contains only the three intended documentation edits plus ignored test/build artifacts.

- [x] **Step 3: Re-run strict analysis and classify remaining warnings**

```powershell
New-Item -ItemType Directory -Force temp | Out-Null
dotnet build GeekFlashCore.slnx -t:Rebuild -c Release --no-restore -p:AnalysisLevel=latest-all -p:EnableNETAnalyzers=true -p:TreatWarningsAsErrors=false 2>&1 | Tee-Object -FilePath temp/quality-review-analyzers.txt
```

Classify unique warnings into these exact groups in the progress record:

```text
fixed: mapped-range validation, cancellation propagation, resource CTS lifetime, public null/ParamName contracts
retained-by-design: synchronous protocol boundary, ownership-transfer Dispose paths, fixed wire layouts, localized logging isolation
hardware-or-measurement-required: LibUsb transfer allocations and real-device cancellation latency
unrelated: diagnostics outside files changed by this plan
```

Do not suppress a warning merely to lower the count. If a new high-risk warning points into changed code, return to that task and fix it before continuing.

- [x] **Step 4: Update design status and implementation progress**

Set the design status to `已实施（待真实设备验证）`. Mark completed task checkboxes in this file. Add a 2026-09-06 quality-review entry to the Qualcomm implementation document with:

```text
任务：全仓质量治理
行为结论：Firehose 映射在零线路写入前验证；安全预取消不使会话失效；Raw 取消仍使会话失效；Qcom Sparse 使用单次严格解析。
验证证据：四个测试命令的精确通过数、Release 构建结果、严格分析分类、git diff --check。
提交：Tasks 1–7 的短哈希与提交标题。
工作区：受跟踪文件状态和 ignored 测试状态。
风险：未在真实 Qualcomm/Oplus/OnePlus/Nothing 设备验证；同步 Transport 无法中断正在阻塞的读；LibUsb 分配优化缺少真实传输测量，未修改。
```

- [x] **Step 5: Review the complete diff and commit documentation**

```powershell
git diff -- docs/plans/2026-09-04-qcom-protocol-implementation.md docs/plans/2026-09-06-project-quality-review-design.md docs/plans/2026-09-06-project-quality-review-implementation.md
git diff --check
git add docs/plans/2026-09-04-qcom-protocol-implementation.md docs/plans/2026-09-06-project-quality-review-design.md docs/plans/2026-09-06-project-quality-review-implementation.md
git commit -m "docs: record quality review results"
git status --short --ignored
```

Expected: documentation contains only observed facts and remaining risks; final tracked status is clean; `.tests`, `bin/obj` and `temp` remain ignored.

## Execution Results

### 2026-09-06 implementation / 2026-09-07 final verification

- 行为结论：Firehose 策略映射现在会在首次线路写入前统一校验起点、连续性、长度与溢出；策略返回 `null` 不再被误解为 identity 映射。安全的线路前取消保留会话状态，Raw 开始后取消仍使会话失效。
- 架构结论：资源请求、超时、迟到结果与敏感载荷释放收敛到 `QcomResourceResolver`；seekable `Stream` 适配收敛到公共 `StreamBlockDevice`；Qcom Sparse 从严格 `SparseDocument` 直接投影区域，同一输入的元数据解析从两次降为一次。
- 可维护性结论：CLI 剩余用户可见文本已进入中英文资源；四处 Ext/Erofs 重复读取循环改用 .NET 8 `Stream.ReadExactly`；移除未使用 Serilog 依赖、无价值注释和遗留 using；公共空参数与文件系统预算异常契约已明确。
- 验证证据：Qcom 246/246、CLI 51/51、Android LP 55/55、Core 7/7 通过；`dotnet build GeekFlashCore.slnx -c Release --no-restore` 为 0 警告/0 错误；`git diff --check` 和 `git ls-files .tests` 无错误/无输出。
- 严格分析：`latest-all` 重建成功，共 366 条唯一诊断（构建输出分两阶段重复打印为 732 行）。`CA2016=0`、`CA2025=0`；新的 `QcomResourceResolver`、`StreamBlockDevice` 和 `SparseProgramPlanner` 无诊断，`FirehoseStorageRangeValidator` 仅有 `CA1512` 语法风格建议。
- 分类处置：`fixed` = mapped-range validation、cancellation propagation、resource CTS lifetime、public null/ParamName contracts；`retained-by-design` = 同步协议边界、所有权转移 Dispose 路径、固定线路布局、本地化日志隔离和 OnePlus 协议兼容 IV；`hardware-or-measurement-required` = LibUsb 传输分配与真机取消延迟；`unrelated` = 本计划修改文件之外的存量诊断。未为降低数量新增抑制。
- 生产提交：`5292540 fix(qcom): validate mapped firehose ranges`；`b1c7215 fix(qcom): align cancellation boundaries`；`d60342d refactor(qcom): isolate resource resolution`；`75c8f66 refactor(block): share seekable stream adapter`；`f3de74c perf(qcom): parse sparse programs once`；`00bd533 fix(cli): localize remaining user messages`；`86303f2 refactor(core): remove redundant infrastructure`。
- 工作区：最终文档提交前仅本计划要求的三份文档为受跟踪改动；`.tests`、`bin/obj`、IDE 文件与 `temp/quality-review-analyzers.txt` 均保持 ignored，`.tests` 未被 Git 跟踪。
- 风险：未在真实 Qualcomm/Oplus/OnePlus/Nothing 设备执行本轮回归；同步 `ITransport` 无法在协议层中断已阻塞的读；LibUsb 分配优化缺少真实传输吞吐与 GC 测量，因此本轮未修改。

## Execution Notes

- 执行顺序固定为 Task 1 → 2 → 3 → 4 → 5 → 6 → 7 → 8；后续任务消费前一任务形成的契约。
- 每个任务在目标测试首次失败后才写最小生产实现；若失败原因不是预期缺陷，先修正测试假设，不扩大生产修改。
- 每个任务提交前检查完整 diff，不使用破坏性 Git 命令覆盖用户修改。
- 本计划不实施 LibUsb 池化或 LP 日志生成器改写：两者缺少足够测量或无法同时保持本地化、EventId 与日志失败隔离，Task 8 记录为有理由保留的风险。

### 2026-09-07 main merge readiness review

- 首轮独立复审：无 Critical；发现 5 个 Important，分别是 VIP 在主 XML 写入前提前计数、Firehose 流式发送池化缓冲未清零、CLI 认证十六进制数组被重复复制、Sparse region 忽略非零流起点、两处 CLI 结果行仍硬编码英文。
- 修复结论：VIP 通过实际 XML 写入后的回调提交帧计数；Raw 池化缓冲归还时清零；`SensitiveDataOwner.TakeOwnership` 明确转移并清零认证数组；Sparse strict/legacy 偏移统一相对当前流起点；存储与分区输出进入中英文资源。
- 测试先行证据：取消主命令会复现 chained table 提前耗尽；数组池复用可读取完整 `0x5A` 载荷；新增所有权 API 在实现前编译失败；带 4 字节前缀的 strict/legacy Sparse region 分别读取错误和未恢复位置；中英文 CLI 结果测试在实现前 4/4 失败。实现后目标测试全部通过。
- 依赖证据：完全还原后曾暴露 `Protocol.Qcom` 直接使用 Serilog 却依赖旧资产中的传递引用；提交 `ac8dbac fix(qcom): declare serilog dependency` 后，新鲜还原、Qcom 构建与测试恢复通过。
- 生产提交：`1db191d fix(qcom): commit vip frames after wire send`；`bfdc728 fix(qcom): clear pooled raw buffers`；`d28b7ca fix(security): transfer authentication buffer ownership`；`931a8e3 fix(sparse): honor nonzero source origins`；`561b02d fix(cli): localize storage command output`。
- 门禁证据：`dotnet restore GeekFlashCore.slnx` 成功；Qcom 250/250 连续两次通过，CLI 55/55、Android LP 55/55、Core 9/9 通过；Release 解决方案构建 0 警告/0 错误。
- 测试夹具：首次全量 Qcom 运行发现进程级 Serilog 的测试 sink 用 `List<LogEvent>` 并发枚举竞争；ignored `.tests` 中改为 `ConcurrentQueue<LogEvent>` 后连续两次通过，不涉及生产代码。
- 工作区：生产修改均已按独立行为提交；`.tests` 和 `bin/obj` 仍 ignored 且不提交。真实设备验证风险保持不变。
- 合并结果：第二轮独立复审为 Critical 0、Important 0、Minor 0；本地 `main` 通过 `--ff-only` 从 `a03eb99` 前进到 `536231f`。合并后分别运行 Qcom 250/250、CLI 55/55、Android LP 55/55、Core 9/9，Release 解决方案构建 0 警告/0 错误；未推送远端。
