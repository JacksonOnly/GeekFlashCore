using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Protocol.Mtk.Abstractions;

/// <summary>Optional standard DA operations, independent of extension payloads and security bypasses.</summary>
public interface IMtkDaStandardOperations
{
    /// <summary>Returns the bounded raw eFuse image; disposal clears the buffer.</summary>
    MtkSensitiveBuffer ReadEfuses(CancellationToken cancellationToken=default);
    /// <summary>Explicit irreversible programming request. Source is borrowed; failure after sending is not retried.</summary>
    void WriteEfuses(IDataSource source,CancellationToken cancellationToken=default);
    /// <summary>Applies a caller-supplied legitimate policy or all-in-one signature.</summary>
    void SetSecurityResource(MtkDaSecurityResource kind,IDataSource source,CancellationToken cancellationToken=default);
    /// <summary>Standard XFlash RSC metadata update in 256-byte records. Source is borrowed; no auto-retry after writes.</summary>
    void SetRscInfo(string partition,IDataSource source,IProgress<ProgressRecord>? progress=null,CancellationToken cancellationToken=default)=>
        throw new MtkCapabilityException("RSC info/dialect");
}
public enum MtkDaSecurityResource { FlashPolicy,AllInOneSignature }
/// <summary>Programming may have reached permanent hardware. Reconnect and inspect; never retry automatically.</summary>
public sealed class MtkPermanentWriteException(Exception inner) : ProtocolException(Localization.Strings.PermanentWriteUnknown,inner)
{
    public bool MayHaveWritten=>true;
}
