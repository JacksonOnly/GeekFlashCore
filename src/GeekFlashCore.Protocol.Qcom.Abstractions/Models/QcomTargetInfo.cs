using System.Collections.Frozen;

namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public sealed record QcomTargetInfo
{
    private ReadOnlyMemory<byte>? _programmerCaHash;
    private IReadOnlySet<string> _programmerSupportedCommands = FrozenSet<string>.Empty;

    public SaharaTargetInfo? Sahara { get; init; }
    public FirehoseTargetInfo? Firehose { get; init; }
    public QcomVendorKind Vendor { get; init; } = QcomVendorKind.Generic;
    public string? OemName { get; init; }
    public string? SocName { get; init; }
    public SecureBootState SecureBoot { get; init; }
    public ulong? ProgrammerMaxPayloadSizeToTargetInBytes { get; init; }
    public IReadOnlySet<string> ProgrammerSupportedCommands
    {
        get => _programmerSupportedCommands;
        init => _programmerSupportedCommands = value?.ToFrozenSet(StringComparer.OrdinalIgnoreCase) ??
                                               FrozenSet<string>.Empty;
    }

    public ReadOnlyMemory<byte>? ProgrammerCaHash
    {
        get => _programmerCaHash;
        init => _programmerCaHash = value?.ToArray();
    }
}
