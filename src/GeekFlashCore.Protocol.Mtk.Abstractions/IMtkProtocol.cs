using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.BlockDevice.Abstractions;

namespace GeekFlashCore.Protocol.Mtk.Abstractions;

/// <summary>Reusable synchronous protocol with asynchronous host resource orchestration.</summary>
public interface IMtkProtocol : IProtocol, IBlockDeviceProvider, IMtkBromSessionAccess
{
    /// <summary>Last observed hardware snapshot; null before probing or after disconnect.</summary>
    MtkTargetInfo? TargetInfo
    {
        get;
    }
    /// <summary>Selected immutable DA metadata; the host owns its source.</summary>
    MtkDaImage? DownloadAgent
    {
        get;
    }
    /// <summary>Current serialized connection state.</summary>
    MtkSessionState SessionState
    {
        get;
    }
    /// <summary>Standard capabilities; extension prerequisites are checked by the extension service.</summary>
    MtkCapabilities Capabilities
    {
        get;
    }
    /// <summary>Monotonic generation invalidating old storage and extension views.</summary>
    long Generation
    {
        get;
    }
    /// <summary>Opens USB, completes the handshake and reads hardware/security information.</summary>
    MtkTargetInfo Probe(CancellationToken cancellationToken = default);
    /// <summary>Connects with resolved resources and a synchronous signer; explicit resources remain caller-owned.</summary>
    void Connect(MtkConnectionResources resources, CancellationToken cancellationToken = default);
    /// <summary>Returns validated ordinary storage geometry from the ready session.</summary>
    MtkStorageInfo GetStorageInfo();
    /// <summary>Reads an aligned ordinary flash range; the borrowed destination remains open.</summary>
    void Read(MtkFlashRange range, Stream destination, CancellationToken cancellationToken = default);
    /// <summary>Writes exactly the aligned range, rejecting early EOF; the borrowed source remains open.</summary>
    void Write(MtkFlashRange range, Stream source, CancellationToken cancellationToken = default);
    /// <summary>Erases an aligned ordinary flash range and requires final device confirmation.</summary>
    void Erase(MtkFlashRange range, CancellationToken cancellationToken = default);
    /// <summary>Ends the session and invalidates all views; a new handshake is required.</summary>
    void Disconnect();
}

/// <summary>An extension-capable serialized session; each action executes within its current generation.</summary>
public interface IMtkSessionAccess
{
    /// <summary>Executes one synchronous action. Retained channels expire immediately after return.</summary>
    T UseSession<T>(Func<IMtkDaChannel, T> action, CancellationToken cancellationToken = default);
}
/// <summary>Scoped DA extension wire; never available to asynchronous resource providers.</summary>
public interface IMtkDaChannel
{
    /// <summary>Confirmed download-agent dialect.</summary>
    MtkDaKind Kind
    {
        get;
    }
    /// <summary>Observed hardware and security configuration.</summary>
    MtkTargetInfo Target
    {
        get;
    }
    /// <summary>Selected immutable DA metadata; the host owns its source.</summary>
    MtkDaImage DownloadAgent
    {
        get;
    }
    /// <summary>Ordinary regions and separately reported RPMB capacity.</summary>
    MtkStorageInfo Storage
    {
        get;
    }
    /// <summary>Monotonic generation invalidating old storage and extension views.</summary>
    long Generation
    {
        get;
    }
    /// <summary>Bounded host write packet size.</summary>
    int WritePacketLength
    {
        get;
    }
    /// <summary>Sends a binary command and validates its initial status.</summary>
    void SendCommand(uint command);
    /// <summary>Sends one framed binary payload.</summary>
    void SendData(ReadOnlySpan<byte> data);
    /// <summary>Receives one bounded FLOW payload, draining bounded MESSAGE packets.</summary>
    int ReceiveData(Span<byte> destination);
    /// <summary>Requires a zero binary status.</summary>
    void CheckStatus();
    /// <summary>Validates an allowlisted XML command, consumes START and checks the command ACK.</summary>
    void BeginXmlCommand(string command, IReadOnlyDictionary<string, string> parameters);
    /// <summary>Requires END with a successful result and acknowledges it.</summary>
    void EndXmlCommand();
    /// <summary>Sends a plain XML acknowledgment.</summary>
    void AcknowledgeXml();
    /// <summary>Sends an XML acknowledgment carrying a decimal value (OK@value).</summary>
    void AcknowledgeXml(long value);
    /// <summary>Receives a bounded virtual file without opening device paths on the host.</summary>
    long ReceiveXmlFile(Stream destination, long? expectedLength, long maximumLength);
    /// <summary>Sends exactly the virtual file length; the borrowed source remains open.</summary>
    void SendXmlFile(Stream source, long length);
    /// <summary>Reads ordinary aligned storage inside this gate.</summary>
    void ReadFlash(MtkFlashRange range, Stream destination);
    /// <summary>Writes ordinary aligned storage inside this gate and invalidates cached GPT metadata.</summary>
    void WriteFlash(MtkFlashRange range, Stream source);
    /// <summary>Closes a potentially changed session even if its cancellation token was triggered.</summary>
    void Invalidate();
}
