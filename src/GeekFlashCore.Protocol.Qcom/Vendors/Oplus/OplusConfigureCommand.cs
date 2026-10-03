using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Protocol.Qcom.Internals;

namespace GeekFlashCore.Protocol.Qcom.Vendors.Oplus;

// Preserve captured attribute order through the existing escaping/schema builder.
[FirehoseCmdTag("configure")]
internal sealed record OplusConfigureCommand : BaseCommand
{
    [FirehoseCmdAttribute("ZlpAwareHost")] public byte ZlpAwareHost { get; init; }
    [FirehoseCmdAttribute("SkipWrite")] public byte SkipWrite { get; init; }
    [FirehoseCmdAttribute("SkipStorageInit")] public byte SkipStorageInit { get; init; }
    [FirehoseCmdAttribute("MaxPayloadSizeToTargetInBytes")] public ulong? MaxPayloadSizeToTargetInBytes { get; init; }
    [FirehoseCmdAttribute("MemoryName")] public string MemoryName { get; init; } = "ufs";
    [FirehoseCmdAttribute("Verbose")] public byte? Verbose { get; init; }
    [FirehoseCmdAttribute("AlwaysValidate")] public byte? AlwaysValidate { get; init; }
    [FirehoseCmdAttribute("MaxDigestTableSizeInBytes")] public ulong? MaxDigestTableSizeInBytes { get; init; }
    [FirehoseCmdAttribute("Oem")] public string? Oem { get; init; }

    internal static string BuildCaptured(ConfigureCommand command) => new OplusConfigureCommand
    {
        ZlpAwareHost = command.ZlpAwareHost ?? 1,
        SkipWrite = command.SkipWrite ?? 0,
        SkipStorageInit = command.SkipStorageInit ?? 0,
        MaxPayloadSizeToTargetInBytes = command.MaxPayloadSizeToTargetInBytes,
        MemoryName = (command.MemoryName ?? FirehoseStorage.Ufs).ToWireString().ToLowerInvariant(),
        Verbose = command.Verbose is null or 0 ? null : command.Verbose,
        AlwaysValidate = command.AlwaysValidate is null or 0 ? null : command.AlwaysValidate,
        MaxDigestTableSizeInBytes = command.MaxDigestTableSizeInBytes == FirehoseConstants.DefaultMaxDigestTableSize
            ? null : command.MaxDigestTableSizeInBytes,
        Oem = command.Oem
    }.Build().Replace(" /></data>", "/></data>", StringComparison.Ordinal);
}
