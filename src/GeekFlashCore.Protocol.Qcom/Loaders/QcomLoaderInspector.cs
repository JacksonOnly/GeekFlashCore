using System.Buffers;
using System.Collections.Frozen;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using QcomImageUtils;
using QcomImageUtils.Models;
using QcomImageUtils.Types;

namespace GeekFlashCore.Protocol.Qcom.Loaders;

public sealed class QcomLoaderInspector : IQcomProgrammerInspector
{
    public const int DefaultMaximumImageSize = 512 * 1024 * 1024;

    private readonly int _maximumImageSize;
    private readonly QcomImageParser _parser;
    private readonly object _sync = new();

    public QcomLoaderInspector(int maximumImageSize = DefaultMaximumImageSize)
    {
        if (maximumImageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumImageSize));
        _maximumImageSize = maximumImageSize;
        _parser = new QcomImageParser(new QcomImageParserOptions
        {
            CalculateFileSha256 = false,
            ExportCertificatePem = false,
            AnalyzeFirehoseCommands = true,
            MaximumImageSize = maximumImageSize
        });
    }

    public bool TryInspect(ReadOnlySpan<byte> image, out QcomProgrammerInfo info)
    {
        lock (_sync)
        {
            bool success = _parser.TryParse(image, out QcomImageParseResult result);
            info = ConvertResult(result);
            return success;
        }
    }

    public bool TryInspect(string filePath, out QcomProgrammerInfo info)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        lock (_sync)
        {
            bool success = _parser.TryParse(filePath, out QcomImageParseResult result);
            info = ConvertResult(result);
            return success;
        }
    }

    public bool TryInspect(IDataSource source, out QcomProgrammerInfo info)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length is <= 0 or > int.MaxValue || source.Length > _maximumImageSize)
        {
            info = Failure("The programmer image length is outside the configured inspection limit.");
            return false;
        }

        int length = checked((int)source.Length);
        byte[] rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            using Stream stream = source.OpenStream() ?? throw new InvalidDataException(
                "The programmer data source returned no stream.");
            if (!stream.CanRead)
            {
                info = Failure("The programmer data source is not readable.");
                return false;
            }

            int offset = 0;
            while (offset < length)
            {
                int read = stream.Read(rented, offset, length - offset);
                if (read == 0)
                {
                    info = Failure("The programmer data source ended before its declared length.");
                    return false;
                }
                offset += read;
            }

            return TryInspect(rented.AsSpan(0, length), out info);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            info = Failure("Unable to inspect the programmer image.", exception);
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static QcomProgrammerInfo ConvertResult(QcomImageParseResult result)
    {
        IReadOnlySet<string> commands = result.SupportedCommands
            .Select(static command => command.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        return new QcomProgrammerInfo
        {
            IsParsed = result.IsSuccess,
            IsProgrammer = result.IsProgrammer,
            IsSbl = result.IsSbl,
            ImageFormat = EmptyToNull(result.ImageFormat),
            HeaderVersion = result.HeaderVersion,
            ImageId = result.ImageId,
            OemId = result.HasOemId ? result.OemId : null,
            ModelId = result.HasOemId ? result.ModelId : null,
            MsmId = result.MsmId == 0 ? null : result.MsmId,
            SocHardwareVersion = result.SocHwVersion == 0 ? null : result.SocHwVersion,
            Vendor = MapVendor(result.OemType),
            OemName = result.OemType == QualcommOemType.Unknown ? null : result.OemType.ToString(),
            SocName = result.SocType == QualcommSocType.Unknown ? null : result.SocType.ToString(),
            BootMemoryType = result.BootMemoryType.ToString(),
            DramGeneration = result.DramGeneration.ToString(),
            QcVersion = EmptyToNull(result.QcVersion),
            OemVersion = EmptyToNull(result.OemVersion),
            ImageVariant = EmptyToNull(result.ImageVariant),
            RootCaHash = DecodeHash(result.RootCaHash),
            SupportedCommands = commands,
            MaxPayloadSizeToTargetInBytesSupported = result.MaxPayloadSizeToTargetInBytesSupported,
            ErrorMessage = result.ErrorMessage
        };
    }

    private static QcomVendorKind MapVendor(QualcommOemType vendor) => vendor switch
    {
        QualcommOemType.Qualcomm => QcomVendorKind.Qualcomm,
        QualcommOemType.Xiaomi or QualcommOemType.BlackShark => QcomVendorKind.Xiaomi,
        QualcommOemType.OppoOneplusRealme => QcomVendorKind.Oplus,
        QualcommOemType.Oxygen => QcomVendorKind.OnePlus,
        QualcommOemType.Nothing => QcomVendorKind.Nothing,
        QualcommOemType.Zte => QcomVendorKind.Zte,
        QualcommOemType.Vivo => QcomVendorKind.Vivo,
        QualcommOemType.Motorola => QcomVendorKind.Motorola,
        QualcommOemType.Lenovo => QcomVendorKind.Lenovo,
        QualcommOemType.Asus => QcomVendorKind.Asus,
        QualcommOemType.Lg => QcomVendorKind.Lg,
        QualcommOemType.Huawei or QualcommOemType.Honor => QcomVendorKind.Huawei,
        QualcommOemType.Samsung => QcomVendorKind.Samsung,
        QualcommOemType.Nokia or QualcommOemType.FoxconnNokia => QcomVendorKind.Nokia,
        _ => QcomVendorKind.Generic
    };

    private static ReadOnlyMemory<byte>? DecodeHash(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        try
        {
            return Convert.FromHexString(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static QcomProgrammerInfo Failure(string message, Exception? exception = null) => new()
    {
        IsParsed = false,
        IsProgrammer = false,
        ErrorMessage = exception is null ? message : $"{message} {exception.GetType().Name}."
    };
}
