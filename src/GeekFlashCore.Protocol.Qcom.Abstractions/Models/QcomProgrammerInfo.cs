using System.Collections.Frozen;

namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public sealed record QcomProgrammerInfo
{
    private ReadOnlyMemory<byte>? _rootCaHash;
    private IReadOnlySet<string> _supportedCommands = FrozenSet<string>.Empty;

    public bool IsParsed { get; init; }
    public bool IsProgrammer { get; init; }
    public bool IsSbl { get; init; }
    public string? ImageFormat { get; init; }
    public uint HeaderVersion { get; init; }
    public uint? ImageId { get; init; }
    public uint? OemId { get; init; }
    public uint? ModelId { get; init; }
    public uint? MsmId { get; init; }
    public uint? SocHardwareVersion { get; init; }
    public QcomVendorKind Vendor { get; init; } = QcomVendorKind.Generic;
    public string? OemName { get; init; }
    public string? SocName { get; init; }
    public string? BootMemoryType { get; init; }
    public string? DramGeneration { get; init; }
    public string? QcVersion { get; init; }
    public string? OemVersion { get; init; }
    public string? ImageVariant { get; init; }
    public ulong? MaxPayloadSizeToTargetInBytesSupported { get; init; }
    public string? ErrorMessage { get; init; }

    public ReadOnlyMemory<byte>? RootCaHash
    {
        get => _rootCaHash;
        init => _rootCaHash = value?.ToArray();
    }

    public IReadOnlySet<string> SupportedCommands
    {
        get => _supportedCommands;
        init => _supportedCommands = value?.ToFrozenSet(StringComparer.OrdinalIgnoreCase) ??
                                       FrozenSet<string>.Empty;
    }
}
