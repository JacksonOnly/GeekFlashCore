using GeekFlashCore.CLI.Localization;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.CLI;

internal static class StorageCommands
{
    public static async Task ExecuteAsync(IProtocol protocol, string command, string[] args, ConsoleUi ui,
        IProgress<ProgressRecord> progress, CancellationToken ct)
    {
        bool previous = ui.SuppressDiagnosticLogs;
        ui.SuppressDiagnosticLogs = true;
        try { await ExecuteCoreAsync(protocol, command, args, ui, progress, ct).ConfigureAwait(false); }
        finally { ui.SuppressDiagnosticLogs = previous; }
    }

    private static async Task ExecuteCoreAsync(IProtocol protocol, string command, string[] args, ConsoleUi ui,
        IProgress<ProgressRecord> progress, CancellationToken ct)
    {
        if (command == "partitions")
        {
            if (protocol is IQcomProtocol partitionDevice) FirehoseCommands.Require(partitionDevice, "read");
            IReadOnlyList<PartitionInfo> partitions;
            if (args[0].Equals("all", StringComparison.OrdinalIgnoreCase)) partitions = await protocol.GetPartitionsAsync(progress, ct);
            else if (protocol is IQcomProtocol qcom) partitions = await qcom.GetPartitionsAsync(CommandSyntax.Lun(args[0]), progress, ct);
            else
            {
                uint lun = CommandSyntax.Lun(args[0]);
                partitions = (await protocol.GetPartitionsAsync(progress, ct)).Where(x => PartitionLun(x) == lun).ToArray();
            }
            if (partitions.Count == 0)
            {
                Serilog.Log.Warning(Strings.Cli_NoPartitions);
                ui.WriteLine(Strings.Cli_NoPartitions);
            }
            foreach (var item in partitions)
                ui.WriteLine(Strings.FormatCli_PartitionLine(
                    (item.Name ?? string.Empty).PadRight(32),
                    PartitionLun(item),
                    item.Address?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? Strings.Cli_UnknownValue,
                    FormatSize(item.Offset),
                    FormatSize(item.Length)));
            return;
        }

        StorageTarget target = ParseTarget(protocol, command, args);
        if (protocol is IQcomProtocol device)
        {
            FirehoseCommands.Require(device, command == "write" ? "program" : command);
        }
        string? file = command == "erase" ? null : ConsolePath.Normalize(
            args[0].Equals("sector", StringComparison.OrdinalIgnoreCase) ? args[4] : args[1]);
        Serilog.Log.Information(Strings.Cli_LogStorageOperation, command, (target as PartitionTarget)?.Name,
            target is SectorTarget sectors ? sectors.PhysicalPartitionNumber : (target as PartitionTarget)?.PhysicalPartitionNumber,
            (target as SectorTarget)?.StartSector, (target as SectorTarget)?.SectorCount, file);
        if (command == "erase")
        {
            if (!await protocol.EraseAsync(target, progress, ct)) throw new InvalidOperationException(Strings.FormatCli_CommandUnsuccessful(command));
        }
        else
        {
            if (command == "read")
            {
                // Resolve named partitions before creating the output, so an invalid name cannot truncate a file.
                if (target is PartitionTarget named)
                {
                    var matches = (protocol is IQcomProtocol q && named.PhysicalPartitionNumber is { } selected
                        ? await q.GetPartitionsAsync(selected, progress, ct) : await protocol.GetPartitionsAsync(progress, ct))
                        .Where(x => x.Name == named.Name && (named.PhysicalPartitionNumber is null || PartitionLun(x) == named.PhysicalPartitionNumber)).ToArray();
                    if (matches.Length != 1) throw new ArgumentException(Strings.FormatCli_PartitionNotUnique(named.Name));
                    if (protocol is IQcomProtocol)
                    {
                        if (matches[0].Address is not { } start || matches[0].Length is not { } length)
                            throw new InvalidOperationException(Strings.Cli_StorageNotConfigured);
                        target = new SectorTarget { PhysicalPartitionNumber = PartitionLun(matches[0]), StartSector = start,
                            SectorCount = length / SectorSize(protocol), SectorSize = SectorSize(protocol) };
                    }
                }
                await AtomicReadOutput.WriteAsync(file!, stream => protocol.ReadAsync(
                    new ReadDestination { Target = target, OutputStream = stream, OwnsStream = false }, progress, ct), ct);
            }
            else await protocol.WriteAsync(new WriteSource { Source = new FileDataSource(file!), Target = target }, progress, ct);
        }
        ui.WriteLine(Strings.FormatCli_CommandCompleted(command));
    }

    internal static StorageTarget ParseTarget(IProtocol protocol, string command, string[] args)
    {
        if (args[0].Equals("sector", StringComparison.OrdinalIgnoreCase))
        {
            uint size = SectorSize(protocol);
            long start = (long)CommandSyntax.Number(args[2]), count = (long)CommandSyntax.Number(args[3]);
            _ = checked((start + count) * size);
            return new SectorTarget { PhysicalPartitionNumber = CommandSyntax.Lun(args[1]),
                StartSector = start, SectorCount = count, SectorSize = size };
        }
        int mandatory = command == "erase" ? 1 : 2;
        return new PartitionTarget { Name = args[0], PhysicalPartitionNumber = args.Length > mandatory ? CommandSyntax.Lun(args[^1]) : null };
    }

    private static string FormatSize(long? size) =>
        size.HasValue ? ConsoleUi.FormatBytes(size.Value) : Strings.Cli_UnknownValue;

    internal static uint SectorSize(IProtocol protocol)
    {
        if (protocol is not IQcomProtocol qcom) return 512;
        return qcom.TargetInfo?.Firehose?.Configuration?.SectorSizeInBytes is > 0 and var size
            ? size : throw new InvalidOperationException(Strings.Cli_StorageNotConfigured);
    }
    internal static uint PartitionLun(PartitionInfo info) => info.Metadata?.TryGetValue("PhysicalPartitionNumber", out string? lun) == true
        ? uint.Parse(lun, System.Globalization.CultureInfo.InvariantCulture) : 0;
}
