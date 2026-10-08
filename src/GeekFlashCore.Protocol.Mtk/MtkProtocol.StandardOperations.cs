using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Mtk.Da;
using System.Security.Cryptography;

namespace GeekFlashCore.Protocol.Mtk;

public sealed partial class MtkProtocol : IMtkDaStandardOperations
{
    /// <inheritdoc />
    public void SetRscInfo(string partition, IDataSource source, IProgress<ProgressRecord>? progress = null, CancellationToken cancellationToken = default)
    {
        PartitionName(partition);
        ArgumentNullException.ThrowIfNull(source);
        if (partition.Length > 63)
            throw new ArgumentOutOfRangeException(nameof(partition));
        Execute(() =>
        {
            Ready();
            if (_da is not XFlashSession x)
                throw new MtkCapabilityException("RSC info/dialect");
            long length = source.Length;
            if (length is <= 0 or > 16777216)
                throw new MtkResourceException("RSC info length");
            using Stream input = source.OpenStream();
            if (!input.CanRead || !input.CanSeek || input.Position != 0 || input.Length != length)
                throw new MtkResourceException("RSC info source");
            byte[] payload = new byte[328];
            System.Text.Encoding.ASCII.GetBytes(partition, payload.AsSpan(8, 64));
            try
            {
                progress?.Report(new(length, 0, Strings.RscProgress) { Unit = ProgressUnit.Bytes, Phase = ProgressPhase.Started });
                for (long done = 0; done < length;)
                {
                    _wire.Check();
                    payload.AsSpan(72).Clear();
                    int count = (int)Math.Min(256, length - done);
                    input.ReadExactly(payload.AsSpan(72, count));
                    // Reference payload keeps byte zero reserved; the seven-byte record index starts at byte one.
                    BinaryPrimitives.WriteUInt64LittleEndian(payload, checked((ulong)(done / 256) << 8));
                    x.Control(MtkXFlashCommand.SetRscInfo, payload);
                    done += count;
                    progress?.Report(new(length, done, Strings.RscProgress) { Unit = ProgressUnit.Bytes, Phase = ProgressPhase.Running });
                }

                _wire.Check();
                progress?.Report(new(length, length, Strings.RscProgress) { Unit = ProgressUnit.Bytes, Phase = ProgressPhase.Completed });
                return 0;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }, cancellationToken);
    }

    /// <inheritdoc />
    public MtkSensitiveBuffer ReadEfuses(CancellationToken cancellationToken = default) => ExecuteSensitive(() =>
    {
        Ready();
        return _da switch
        {
            XFlashSession x => x.ReadEfuses(),
            XmlSession xml => xml.QueryFile(MtkXmlCommand.ReadEfuse, 0x5000),
            _ => throw new MtkCapabilityException("eFuse/dialect")};
    }, cancellationToken);
    /// <inheritdoc />
    public void WriteEfuses(IDataSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        Execute(() =>
        {
            Ready();
            if (_da is not (XFlashSession or XmlSession))
                throw new MtkCapabilityException("eFuse/dialect");
            long length = source.Length;
            if (length <= 0 || length > Math.Min(_options.MaximumFrameSize, 0x5000) || _da is XFlashSession && length != 0x42d4)
                throw new MtkResourceException("eFuse size");
            byte[] image = new byte[(int)length];
            try
            {
                using (Stream stream = source.OpenStream())
                {
                    if (!stream.CanRead)
                        throw new MtkResourceException("eFuse source");
                    stream.ReadExactly(image);
                }

                try
                {
                    if (_da is XFlashSession x)
                        x.WriteEfuses(image);
                    else
                    {
                        using var stream = new MemoryStream(image, false);
                        ((XmlSession)_da).WriteFile(MtkXmlCommand.WriteEfuse, stream, length);
                    }

                    _wire.Check();
                }
                catch (Exception ex)
                {
                    if (_wire.HasWritten)
                        throw new MtkPermanentWriteException(ex);
                    throw;
                }

                return 0;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(image);
            }
        }, cancellationToken);
    }

    /// <inheritdoc />
    public void SetSecurityResource(MtkDaSecurityResource kind, IDataSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        Execute(() =>
        {
            Ready();
            if (!Enum.IsDefined(kind))
                throw new ArgumentOutOfRangeException(nameof(kind));
            if (_da is not (XFlashSession or XmlSession))
                throw new MtkCapabilityException("DA security resource/dialect");
            long length = source.Length;
            if (length <= 0 || length > _options.MaximumFrameSize)
                throw new MtkResourceException("DA security resource");
            byte[] data = new byte[(int)length];
            try
            {
                using (Stream sourceStream = source.OpenStream())
                    sourceStream.ReadExactly(data);
                if (_da is XFlashSession x)
                    x.Control(kind == MtkDaSecurityResource.FlashPolicy ? MtkXFlashCommand.SetRemoteSecPolicy : MtkXFlashCommand.SetAllInOneSig, data);
                else
                {
                    using var stream = new MemoryStream(data, false);
                    ((XmlSession)_da).WriteFile(kind == MtkDaSecurityResource.FlashPolicy ? MtkXmlCommand.SecuritySetFlashPolicy : MtkXmlCommand.SecuritySetAllinoneSignature, stream, length);
                }

                return 0;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(data);
            }
        }, cancellationToken);
    }
}
