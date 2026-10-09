using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Firehose.Storage;

public sealed record FirehoseStorageRange(long StartSector, long SectorCount, string? Label, string? FileName);

/// <summary>Maps and authorizes complete operations before their first command is sent.</summary>
public interface IFirehoseStoragePolicy
{
    IReadOnlyList<FirehoseStorageRange> Map(uint physicalPartitionNumber, long startSector, long sectorCount,
        bool write, string? label = null, string? fileName = null);

    FirehoseCommandResult ExecuteCommand(FirehoseSession session, BaseCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return session.Execute(command, expectedRawMode: command is not PatchCommand, cancellationToken: cancellationToken);
    }

    void CommandCompleted() { }

    /// <summary>Executes an acknowledged non-raw PATCH for a range already authorized by Map.</summary>
    /// <remarks>Routes through ExecuteCommand so existing policy authorization is not bypassed. PATCH must not enter raw mode.</remarks>
    FirehoseCommandResult ExecutePatchCommand(FirehoseSession session, PatchCommand command,
        CancellationToken cancellationToken) => ExecuteCommand(session, command, cancellationToken);
}
