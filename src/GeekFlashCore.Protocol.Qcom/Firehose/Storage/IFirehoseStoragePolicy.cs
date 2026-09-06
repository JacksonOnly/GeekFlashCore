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
        return session.Execute(command, expectedRawMode: true, cancellationToken: cancellationToken);
    }

    void CommandCompleted() { }
}
