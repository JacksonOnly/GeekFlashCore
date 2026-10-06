using GeekFlashCore.Android.Sparse;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Protocol.Qcom.Firehose;
using GeekFlashCore.Protocol.Qcom.Firehose.Programming;

namespace GeekFlashCore.Protocol.Qcom;

public sealed partial class QcomProtocol
{
    /// <inheritdoc />
    public FirehoseScriptResult ExecuteRawProgram(IDataSource xml, Func<string, IDataSource> imageResolver,
        IProgress<ProgressRecord>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageResolver);
        return ExecuteScript(xml, imageResolver, false, progress, cancellationToken);
    }

    /// <inheritdoc />
    public FirehoseScriptResult ExecutePatchFile(IDataSource xml, IProgress<ProgressRecord>? progress = null,
        CancellationToken cancellationToken = default) => ExecuteScript(xml, null, true, progress, cancellationToken);

    private FirehoseScriptResult ExecuteScript(IDataSource xml, Func<string, IDataSource>? resolver, bool patchOnly,
        IProgress<ProgressRecord>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(xml);
        using var operation = EnterConnected();
        ct.ThrowIfCancellationRequested();
        var entries = FirehoseScriptParser.Read(xml, patchOnly, ct);
        var actions = new List<(string Name, Func<long> Run)>();
        var skipped = new List<FirehoseScriptSkippedEntry>();
        var supported = _targetInfo!.Firehose!.BasicDevCharacteristics?.SupportedFunctions
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested(); index++;
            FirehoseScriptSkipReason? skip = patchOnly && entry.Name != "patch" || !ScriptCommands.Contains(entry.Name)
                ? FirehoseScriptSkipReason.UnsupportedCommand
                : entry.Name == "patch" && !string.Equals(entry.Optional("filename"), "DISK", StringComparison.OrdinalIgnoreCase)
                    ? FirehoseScriptSkipReason.NonDiskPatch
                    : entry.Name == "program" && string.IsNullOrWhiteSpace(entry.Optional("filename"))
                        ? FirehoseScriptSkipReason.EmptyFileName
                        : supported is { Count: > 0 } && !supported.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)
                            ? FirehoseScriptSkipReason.DeviceCommandUnavailable : null;
            if (skip is { } reason) { skipped.Add(DescribeSkippedEntry(index, entry, reason)); continue; }
            if (actions.LastOrDefault().Name == "power") throw new ArgumentException(Strings.Qcom_ScriptPowerMustBeLast);
            try { actions.Add((entry.Name, PrepareScriptCommand(entry, resolver, progress, ct))); }
            catch (Exception error) when (error is FormatException or OverflowException)
            { throw new ArgumentException(Strings.Qcom_ScriptExpressionInvalid, nameof(xml), error); }
        }
        long bytes = 0; int completed = 0;
        foreach (var action in actions)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ProgressRecord(actions.Count, completed, Strings.FormatQcom_ScriptExecuting(completed + 1, actions.Count, action.Name)));
            try { bytes = checked(bytes + action.Run()); completed++; }
            catch { _firehose?.Invalidate(); throw; }
            if (action.Name != "program")
                progress?.Report(new ProgressRecord(actions.Count, completed, Strings.FormatQcom_ScriptExecuting(completed, actions.Count, action.Name)));
        }
        return new(completed, bytes, skipped.AsReadOnly());
    }

    private FirehoseScriptSkippedEntry DescribeSkippedEntry(int index, FirehoseScriptElement entry, FirehoseScriptSkipReason reason)
    {
        string? lunText = entry.Optional(entry.Name == "xblgpt" ? "lun" : "physical_partition_number");
        uint? lun = null;
        try
        {
            if (lunText is not null) lun = checked((uint)FirehoseScriptParser.Evaluate(lunText, null));
            else if (entry.Name is "program" or "patch") lun = 0;
        }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException)
        {
            // Optional diagnostics must not turn a skipped entry into a validation failure.
        }
        ulong? capacity = _targetInfo!.Firehose!.StorageInfos.FirstOrDefault(x => x.PhysicalPartitionNumber == lun)?.BlockCount;
        long? Resolve(string? expression)
        {
            if (expression is null) return null;
            try { return checked((long)FirehoseScriptParser.Evaluate(expression, capacity)); }
            catch (Exception error) when (error is ArgumentException or FormatException or OverflowException) { return null; }
        }
        return new(index, entry.Name, reason)
        {
            Label = entry.Optional("label"), FileName = entry.Optional("filename"),
            PhysicalPartitionExpression = lunText, PhysicalPartitionNumber = lun,
            StartSectorExpression = entry.Optional("start_sector"), StartSector = Resolve(entry.Optional("start_sector")),
            SectorCountExpression = entry.Optional("num_partition_sectors"), SectorCount = Resolve(entry.Optional("num_partition_sectors"))
        };
    }

    private static readonly HashSet<string> ScriptCommands = new(StringComparer.Ordinal)
    {
        "program", "patch", "erase", "nop", "setbootablestoragedrive", "fixgpt", "xblgpt",
        "getstorageinfo", "getsha256digest", "benchmark", "power", "firmwarewrite"
    };

    private Func<long> PrepareScriptCommand(FirehoseScriptElement e, Func<string, IDataSource>? resolver,
        IProgress<ProgressRecord>? progress, CancellationToken ct)
    {
        static long Done(FirehoseCommandResult result) => result.IsSuccess ? 0 :
            throw new QcomProtocolException(Strings.Qcom_ScriptCommandFailed);
        if (e.Name == "nop") { ValidateScriptAttributes(e, ""); return () => Done(_storage!.Nop(ct)); }
        if (e.Name == "power")
        {
            ValidateScriptAttributes(e, "value DelayInSeconds");
            FirehosePowerValue mode = e.Required("value") switch
            {
                "reset" => FirehosePowerValue.Reset, "reset_to_edl" => FirehosePowerValue.ResetToEdl,
                "off" => FirehosePowerValue.Off, _ => throw new ArgumentException(Strings.Qcom_ScriptInvalid)
            };
            ulong delay = e.Number("DelayInSeconds", 0);
            if (delay > 60) throw new ArgumentException(Strings.Qcom_ScriptInvalid);
            return () => { Done(_storage!.Power(mode, delay, ct)); Cleanup(); return 0; };
        }
        if (e.Name == "setbootablestoragedrive")
        {
            ValidateScriptAttributes(e, "value physical_partition_number");
            uint lun = checked((uint)e.Number("value")); ValidateLun(lun);
            return () => Done(_storage!.SetBootableStorageDrive(lun, ct));
        }
        uint partition = checked((uint)e.Number(e.Name == "xblgpt" ? "lun" : "physical_partition_number", 0));
        ValidateLun(partition);
        var info = _targetInfo!.Firehose!.StorageInfos.FirstOrDefault(x => x.PhysicalPartitionNumber == partition);
        uint sectorSize = _storage!.Configuration.SectorSizeInBytes;
        if (e.Number("SECTOR_SIZE_IN_BYTES", sectorSize) != sectorSize || info?.BlockSizeInBytes != sectorSize)
            throw new ArgumentException(Strings.Qcom_TargetSectorSizeMismatch);
        ulong capacity = info.BlockCount ?? throw new ArgumentException(Strings.Qcom_StorageCapacityMissing);
        long Start() => checked((long)FirehoseScriptParser.Evaluate(e.Required("start_sector"), capacity));
        long Count() => checked((long)FirehoseScriptParser.Evaluate(e.Required("num_partition_sectors"), capacity));
        switch (e.Name)
        {
            case "program":
                ValidateScriptAttributes(e, "SECTOR_SIZE_IN_BYTES physical_partition_number start_sector num_partition_sectors filename label file_sector_offset sparse partofsingleimage readbackverify size_in_KB start_byte_hex");
                if (e.Optional("readbackverify") is { } verify && verify is not ("false" or "0"))
                    throw new ArgumentException(Strings.Qcom_ScriptReadbackUnsupported);
                string filename = e.Required("filename");
                IDataSource source = resolver!(filename) ?? throw new ArgumentException(Strings.Qcom_ProgramSourceEmpty);
                long start = Start(), count = Count();
                if (count == 0) count = checked((long)capacity - start);
                ValidateCachedSectorRange(partition, start, count);
                long offset = checked((long)e.Number("file_sector_offset", 0) * sectorSize);
                using (Stream stream = source.OpenStream())
                {
                    ct.ThrowIfCancellationRequested();
                    if (!stream.CanSeek || offset < 0 || offset >= source.Length)
                        throw new ArgumentException(Strings.Qcom_ScriptSourceInvalid);
                    stream.Position = offset;
                    bool sparse = SparseImageParser.IsSparse(stream);
                    var request = new FirehoseProgramRequest
                    {
                        Source = source, PhysicalPartitionNumber = partition, StartSector = start, SectorCount = count,
                        SectorSizeInBytes = sectorSize, SourceOffset = offset,
                        SourceLength = sparse ? source.Length - offset : Math.Min(source.Length - offset, checked(count * sectorSize)),
                        Format = sparse ? FirehoseProgramFormat.AndroidSparse : FirehoseProgramFormat.Raw,
                        Label = e.Optional("label"), FileName = filename, PadToSectorCount = false
                    };
                    request.Validate();
                    using var plan = FirehoseProgramPlanner.Create(request, stream, ct);
                    return () => _storage!.Program(request, ProgramProgress(progress, request), ct);
                }
            case "patch":
                ValidateScriptAttributes(e, "SECTOR_SIZE_IN_BYTES physical_partition_number start_sector byte_offset size_in_bytes value filename what");
                long patchStart = Start(); uint size = checked((uint)e.Number("size_in_bytes")), byteOffset = checked((uint)e.Number("byte_offset"));
                ValidateCachedSectorRange(partition, patchStart, 1);
                if (size is not (1 or 2 or 4 or 8) || checked((ulong)byteOffset + size) > sectorSize)
                    throw new ArgumentException(Strings.Qcom_ScriptInvalid);
                string value = FirehoseScriptParser.PatchValue(e.Required("value"), capacity, sectorSize, size);
                return () => Done(_storage!.Patch(partition, patchStart, byteOffset, size, value, cancellationToken: ct));
            case "erase": case "getsha256digest":
                ValidateScriptAttributes(e, "SECTOR_SIZE_IN_BYTES physical_partition_number start_sector num_partition_sectors label");
                long rangeStart = Start(), rangeCount = Count(); ValidateCachedSectorRange(partition, rangeStart, rangeCount);
                return e.Name == "erase" ? () => Done(_storage!.Erase(partition, rangeStart, rangeCount, cancellationToken: ct)) :
                    () => { _storage!.GetSha256Digest(partition, rangeStart, rangeCount, cancellationToken: ct); return 0; };
            case "getstorageinfo":
                ValidateScriptAttributes(e, "physical_partition_number print_json");
                return () => { QueryStorageInfo(partition); return 0; };
            case "fixgpt":
                ValidateScriptAttributes(e, "physical_partition_number lun grow_last_partition");
                ulong grow = e.Number("grow_last_partition", 1); if (grow > 1) throw new ArgumentException(Strings.Qcom_ScriptInvalid);
                if (e.Optional("lun") is { } lunText && FirehoseScriptParser.Evaluate(lunText, null) != partition)
                    throw new ArgumentException(Strings.Qcom_ScriptInvalid);
                return () => Done(_storage!.FixGpt(partition, partition.ToString(System.Globalization.CultureInfo.InvariantCulture), grow == 1, ct));
            case "xblgpt":
                ValidateScriptAttributes(e, "lun"); return () => Done(_storage!.XblGpt(partition, ct));
            case "benchmark":
                ValidateScriptAttributes(e, "physical_partition_number trials TestDigestPerformance TestWritePerformance TestReadPerformance");
                uint trials = checked((uint)e.Number("trials", 1)); if (trials is 0 or > 1000) throw new ArgumentException(Strings.Qcom_ScriptInvalid);
                bool digest = Flag("TestDigestPerformance"), write = Flag("TestWritePerformance"), read = Flag("TestReadPerformance");
                bool Flag(string key) { ulong flag = e.Number(key, 0); return flag <= 1 ? flag == 1 : throw new ArgumentException(Strings.Qcom_ScriptInvalid); }
                return () => Done(_storage!.Benchmark(partition, trials, digest, write, read, ct));
            case "firmwarewrite":
                ValidateScriptAttributes(e, "physical_partition_number filename");
                IDataSource firmware = resolver!(e.Required("filename"));
                if (firmware.Length is <= 0 or > FirehoseConstants.MaximumRawTransferLength) throw new ArgumentException(Strings.Qcom_ScriptSourceInvalid);
                using (Stream stream = firmware.OpenStream()) { if (!stream.CanRead) throw new ArgumentException(Strings.Qcom_ScriptSourceInvalid); }
                return () => _storage!.FirmwareWrite(partition, firmware, ct).BytesTransferred;
            default: throw new ArgumentException(Strings.Qcom_ScriptInvalid);
        }
    }

    private static void ValidateScriptAttributes(FirehoseScriptElement entry, string allowed)
    {
        var keys = allowed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (entry.Attributes.Keys.Any(x => !keys.Contains(x, StringComparer.Ordinal)))
            throw new ArgumentException(Strings.Qcom_ScriptInvalid);
    }
}
