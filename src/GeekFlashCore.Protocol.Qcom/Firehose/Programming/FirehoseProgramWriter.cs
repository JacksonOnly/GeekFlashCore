using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Protocol.Qcom.Firehose.Storage;
using Serilog;

namespace GeekFlashCore.Protocol.Qcom.Firehose.Programming;

/// <summary>One backend decision shared by stream and block-device PROGRAM paths.</summary>
internal sealed class FirehoseProgramWriter(
    FirehoseSession session,
    FirehoseProgramWriteMode mode,
    IFirehoseStoragePolicy? policy,
    Func<(string PublicKey, string Token)?> tokenFactory)
{
    private readonly FirehoseSession _session = session;
    private readonly IFirehoseStoragePolicy? _policy = policy;
    private readonly Func<(string PublicKey, string Token)?> _tokenFactory = tokenFactory;
    internal bool UsesPatch => mode == FirehoseProgramWriteMode.Patch ||
        mode == FirehoseProgramWriteMode.Auto && _session.ProgramUsesPatch;

    internal void ValidateOptions(FirehoseIoOptions options)
    {
        if (UsesPatch && (options.LastSector is not null || options.SkipBadBlock is not null ||
            options.GetSpare is not null || options.EccDisabled is not null))
            throw new NotSupportedException(Strings.Qcom_PatchProgramIoUnsupported);
    }

    private static bool CanPatch(ProgramCommand command) => command.LastSector is null &&
        command.SkipBadBlock is null && command.GetSpare is null && command.EccDisabled is null;

    /// <returns>True for PATCH; false after a successful PROGRAM raw-mode handshake.</returns>
    internal bool Begin(ProgramCommand command, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (UsesPatch)
        {
            if (!CanPatch(command)) throw new NotSupportedException(Strings.Qcom_PatchProgramIoUnsupported);
            return true;
        }
        if (_tokenFactory() is { } credential)
        {
            command.PublicKey = credential.PublicKey;
            command.Token = credential.Token;
        }
        try
        {
            if (_policy is null) _session.Execute(command, expectedRawMode: true, cancellationToken: ct);
            else _policy.ExecuteCommand(_session, command, ct);
            return false;
        }
        catch (FirehoseNakException exception) when (mode == FirehoseProgramWriteMode.Auto &&
            CanPatch(command) && (_session.State is FirehoseSessionState.Started or FirehoseSessionState.Configured) &&
            FirehoseProgramRejection.IsUnsupported(exception))
        {
            _session.ProgramUsesPatch = true;
            Log.ForContext("QcomSummary", true).Warning(Strings.Qcom_LogPatchProgramFallback);
            return true;
        }
    }

    internal long WritePatch(ProgramCommand command, Stream source, long sourceLength, long wireLength,
        byte paddingByte, IProgress<long>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var transfer = new PatchTransfer(this, command, wireLength, progress, ct);
        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(64 * 1024, wireLength));
        try
        {
            while (transfer.Completed < wireLength)
            {
                ct.ThrowIfCancellationRequested();
                int count = (int)Math.Min(64 * 1024, wireLength - transfer.Completed);
                int fromSource = (int)Math.Min(count, Math.Max(0, sourceLength - transfer.Completed));
                int read = 0;
                while (read < fromSource)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = source.Read(buffer.AsSpan(read, fromSource - read));
                    if (n == 0) throw new EndOfStreamException(Strings.Firehose_SourceEndedBeforeDeclaredLength);
                    read += n;
                }
                buffer.AsSpan(fromSource, count - fromSource).Fill(paddingByte);
                transfer.Write(buffer.AsSpan(0, count));
            }
            return transfer.Complete();
        }
        catch { transfer.Fail(); throw; }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal long WritePatch(ProgramCommand command, ReadOnlySpan<byte> source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var transfer = new PatchTransfer(this, command, source.Length, null, ct);
        try { transfer.Write(source); return transfer.Complete(); }
        catch { transfer.Fail(); throw; }
    }

    private sealed class PatchTransfer
    {
        private readonly FirehoseProgramWriter _writer;
        private readonly ProgramCommand _program;
        private readonly IProgress<long>? _progress;
        private readonly CancellationToken _ct;
        private readonly long _length;
        private readonly long _startSector;
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly ILogger _logger = Log.ForContext<FirehoseProgramWriter>();
        private long _commands;
        private bool _attempted;
        internal long Completed { get; private set; }

        internal PatchTransfer(FirehoseProgramWriter writer, ProgramCommand program, long length,
            IProgress<long>? progress, CancellationToken ct)
        {
            _writer = writer; _program = program; _length = length; _progress = progress; _ct = ct;
            _startSector = long.Parse(program.StartSector, CultureInfo.InvariantCulture);
            uint sectorSize = program.SectorSizeInBytes;
            long perSector = sectorSize / 8 + System.Numerics.BitOperations.PopCount(sectorSize % 8);
            long count = checked(length / sectorSize * perSector);
            _logger.ForContext("QcomSummary", true).Information(Strings.Qcom_LogPatchProgramStart,
                program.PhysicalPartitionNumber, _startSector, length, count);
        }

        internal void Write(ReadOnlySpan<byte> source)
        {
            Span<byte> valueBytes = stackalloc byte[8];
            try
            {
                while (!source.IsEmpty)
                {
                    _ct.ThrowIfCancellationRequested();
                    long sector = checked(_startSector + Completed / _program.SectorSizeInBytes);
                    uint offset = (uint)(Completed % _program.SectorSizeInBytes);
                    int available = (int)Math.Min(source.Length, _program.SectorSizeInBytes - offset);
                    int count = available >= 8 ? 8 : available >= 4 ? 4 : available >= 2 ? 2 : 1;
                    valueBytes.Clear();
                    source[..count].CopyTo(valueBytes);
                    var patch = new PatchCommand
                    {
                        PhysicalPartitionNumber = _program.PhysicalPartitionNumber,
                        Storage = _program.Storage, Slot = _program.Slot, FileName = "DISK",
                        SectorSizeInBytes = _program.SectorSizeInBytes,
                        StartSector = sector.ToString(CultureInfo.InvariantCulture), ByteOffset = offset,
                        SizeInBytes = (uint)count,
                        Value = BinaryPrimitives.ReadUInt64LittleEndian(valueBytes).ToString(CultureInfo.InvariantCulture)
                    };
                    if (_writer._tokenFactory() is { } credential)
                    { patch.PublicKey = credential.PublicKey; patch.Token = credential.Token; }
                    _logger.Debug(Strings.Qcom_LogPatchProgramCommand, _program.PhysicalPartitionNumber,
                        sector, offset, count, checked(_commands + 1), Completed);
                    _attempted = true;
                    FirehoseCommandResult result = _writer._policy is null
                        ? _writer._session.Execute(patch, cancellationToken: _ct)
                        : _writer._policy.ExecutePatchCommand(_writer._session, patch, _ct);
                    if (!result.IsSuccess) throw new FirehoseNakException(Strings.Qcom_FirehoseCommandRejected, result);
                    if (result.RawMode) throw new FirehoseProtocolException(Strings.Qcom_FirehoseRawModeUnexpected);
                    Completed = checked(Completed + count);
                    _commands = checked(_commands + 1);
                    _writer._policy?.CommandCompleted();
                    _logger.Debug(Strings.Qcom_LogPatchProgramAck, _commands, Completed, _length);
                    _progress?.Report(Completed);
                    source = source[count..];
                }
            }
            finally { CryptographicOperations.ZeroMemory(valueBytes); }
        }

        internal long Complete()
        {
            _ct.ThrowIfCancellationRequested();
            _logger.ForContext("QcomSummary", true).Information(Strings.Qcom_LogPatchProgramCompleted,
                Completed, _commands, Stopwatch.GetElapsedTime(_started).TotalMilliseconds);
            return Completed;
        }

        internal void Fail()
        {
            if (_attempted) _writer._session.Invalidate();
            _logger.ForContext("QcomSummary", true).Error(Strings.Qcom_LogPatchProgramFailed,
                _program.PhysicalPartitionNumber, checked(_startSector + Completed / _program.SectorSizeInBytes),
                Completed % _program.SectorSizeInBytes, Completed, _length);
        }
    }
}
