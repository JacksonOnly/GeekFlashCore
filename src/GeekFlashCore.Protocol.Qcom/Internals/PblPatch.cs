using System.Buffers.Binary;
using System.Diagnostics;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Transport.Abstractions;
using QcomImageUtils.Constants;
using QcomImageUtils.Types;

namespace GeekFlashCore.Protocol.Qcom.Internals;

internal enum PblPatchKind
{
    None = 0,
    Snapdragon665 = 665,
    Snapdragon710 = 710,
    Snapdragon845 = 845
}

/// <summary>Bounded synchronous PBL sequences from OPPOLoaderTest and the SM6125 capture.</summary>
internal sealed class PblPatch(ITransport transport, int readTimeoutMilliseconds)
{
    private static ReadOnlySpan<byte> Hello =>
        [2, 0, 0, 0, 48, 0, 0, 0, 2, 0, 0, 0, 1, 0, 0, 0,
         0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
         0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    private static ReadOnlySpan<byte> Hello710 =>
        [2, 0, 0, 0, 48, 0, 0, 0, 2, 0, 0, 0, 2, 0, 0, 0,
         0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0,
         3, 0, 0, 0, 4, 0, 0, 0, 5, 0, 0, 0, 6, 0, 0, 0];

    internal static PblPatchKind ResolveKind(SaharaTargetInfo? target)
    {
        SaharaMsmHwInfo? hardware = target?.MsmHwInfo;
        return QualcommMapping.GetSocType(hardware?.SocHwVersion, hardware?.MsmId) switch
        {
            QualcommSocType.Sdm845 => PblPatchKind.Snapdragon845,
            QualcommSocType.Sdm710 => PblPatchKind.Snapdragon710,
            QualcommSocType.Sm6125 => PblPatchKind.Snapdragon665,
            _ => PblPatchKind.None
        };
    }

    /// <returns>The first Loader READ_DATA64 packet for 665; null when a transport reopen is required.</returns>
    internal byte[]? Run(PblPatchKind kind, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (kind == PblPatchKind.Snapdragon665) return Run665(cancellationToken);
        if (kind is not (PblPatchKind.Snapdragon710 or PblPatchKind.Snapdragon845))
            throw new ArgumentOutOfRangeException(nameof(kind));

        bool is710 = kind == PblPatchKind.Snapdragon710;
        string prefix = is710 ? "oppo710_" : "oppo845_";
        // Validate all embedded material before the first patch write.
        byte[] header = LoadResource(prefix + "elf_header", 64);
        byte[] programHeaders = LoadResource(prefix + "program_headers", is710 ? 1232 : 1064);
        byte[] segment1 = LoadResource(prefix + "segment1", 4096);
        byte[] segment2 = LoadResource(prefix + "segment2", is710 ? 1008 : 2968);
        byte[] segment3 = LoadResource(prefix + "segment3", is710 ? 936 : 1016);
        byte[] zero = LoadResource("common_zero_segment", 3064);
        Span<byte> response = stackalloc byte[48];
        ReadHello(response, cancellationToken);
        SendAndRead(is710 ? Hello710 : Hello, response[..32], cancellationToken);
        SendAndRead(header, response[..32], cancellationToken);
        SendAndRead(programHeaders, response[..32], cancellationToken);
        SendAndRead(segment1, response[..32], cancellationToken);
        if (!is710) SendAndRead(segment2, response[..16], cancellationToken);
        SendAndRead(zero, response[..16], cancellationToken);
        ReadOnlySpan<byte> nop = is710 ? [0x13] : [0x13, 0, 0, 0, 8, 0, 0, 0];
        for (int attempt = 0; attempt < 175; attempt++)
            SendAndRead(nop, response, cancellationToken);
        if (is710) Write(segment2, cancellationToken);
        Write(segment3, cancellationToken);
        return null;
    }

    internal void SendLoaderHello(CancellationToken cancellationToken) => Write(Hello, cancellationToken);

    private byte[] Run665(CancellationToken cancellationToken)
    {
        // This window contains only the six ranges captured during bootstrap.
        // The zero-filled gap is not captured material and must never be served.
        byte[] bootstrap = LoadResource("sm6125_pbl_bootstrap", 20480);
        Span<byte> packet = stackalloc byte[48];
        ReadHello(packet, cancellationToken);
        for (int attempt = 0; attempt < 100; attempt++)
        {
            Write([0x13, 0x9A, 0x9A, 0x9A], cancellationToken);
            ReadPacket(packet, cancellationToken);
            if (BinaryPrimitives.ReadUInt32LittleEndian(packet) == (uint)SaharaCommand.ReadData64Bit)
            {
                ValidateBootstrapRequest(packet, 0, 64);
                SendLoaderHello(cancellationToken);
                ReadOnlySpan<int> offsets = [0, 64, 4096, 8192, 12288, 16384];
                for (int block = 0; block < offsets.Length; block++)
                {
                    int offset = offsets[block];
                    int length = block switch { 0 => 64, 1 => 896, _ => 4096 };
                    if (block != 0) ReadPacket(packet, cancellationToken);
                    ValidateBootstrapRequest(packet, offset, length);
                    Write(bootstrap.AsSpan(offset, length), cancellationToken);
                }
                // The captured bootstrap returns directly to Hello, without Done.
                // READ_DATA64 arrives before the second HelloResponse; preserve it
                // for the user's Loader, which must not be used for bootstrap.
                ReadHello(packet, cancellationToken);
                int firstLength = ReadPacket(packet, cancellationToken);
                if (BinaryPrimitives.ReadUInt32LittleEndian(packet) != (uint)SaharaCommand.ReadData64Bit)
                    throw new SaharaProtocolException(Strings.Qcom_PblUnexpectedPacket);
                SendLoaderHello(cancellationToken);
                return packet[..firstLength].ToArray();
            }
        }
        throw new SaharaProtocolException(Strings.Qcom_PblAttemptsExhausted);
    }

    private static void ValidateBootstrapRequest(ReadOnlySpan<byte> packet, int offset, int length)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(packet) != (uint)SaharaCommand.ReadData64Bit ||
            BinaryPrimitives.ReadUInt64LittleEndian(packet[8..]) != 13 ||
            BinaryPrimitives.ReadUInt64LittleEndian(packet[16..]) != (ulong)offset ||
            BinaryPrimitives.ReadUInt64LittleEndian(packet[24..]) != (ulong)length)
            throw new SaharaProtocolException(Strings.Qcom_PblBootstrapRequestInvalid);
    }

    private void ReadHello(Span<byte> packet, CancellationToken cancellationToken)
    {
        int length = ReadPacket(packet, cancellationToken);
        if (length != SaharaHelloRequest.Length ||
            BinaryPrimitives.ReadUInt32LittleEndian(packet) != (uint)SaharaCommand.Hello)
            throw new SaharaProtocolException(Strings.Qcom_PblUnexpectedPacket);
    }

    private int ReadPacket(Span<byte> packet, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        ReadExact(packet[..8], started, cancellationToken);
        uint command = BinaryPrimitives.ReadUInt32LittleEndian(packet);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(packet[4..]);
        if (!((command == (uint)SaharaCommand.Hello && length == SaharaHelloRequest.Length) ||
              (command == (uint)SaharaCommand.ReadData64Bit && length == SaharaReadData64BitRequest.Length)))
            throw new SaharaProtocolException(Strings.Qcom_PblUnexpectedPacket);
        ReadExact(packet.Slice(8, checked((int)length) - 8), started, cancellationToken);
        return checked((int)length);
    }

    private void SendAndRead(ReadOnlySpan<byte> data, Span<byte> response, CancellationToken cancellationToken)
    {
        Write(data, cancellationToken);
        ReadExact(response, Stopwatch.GetTimestamp(), cancellationToken);
    }

    private void Write(ReadOnlySpan<byte> data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        transport.Write(data);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void ReadExact(Span<byte> data, long started, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < data.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (elapsed >= readTimeoutMilliseconds) throw new TimeoutException(Strings.SaharaNak_TimeoutRx);
            int remaining = Math.Max(1, readTimeoutMilliseconds - (int)elapsed);
            int read = transport.Read(data[total..], remaining);
            if (read <= 0 || read > data.Length - total)
                throw new EndOfStreamException(Strings.SaharaNak_TimeoutRx);
            total += read;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static byte[] LoadResource(string name, int length)
    {
        using Stream stream = typeof(PblPatch).Assembly.GetManifestResourceStream(
            "GeekFlashCore.Protocol.Qcom.Internals.PblResources." + name + ".bin")
            ?? throw new QcomResourceException(Strings.Qcom_PblResourceInvalid);
        if (stream.Length != length) throw new QcomResourceException(Strings.Qcom_PblResourceInvalid);
        byte[] data = new byte[length];
        stream.ReadExactly(data);
        return data;
    }
}
