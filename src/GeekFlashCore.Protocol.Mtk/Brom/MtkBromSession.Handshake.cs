// SPDX-License-Identifier: AGPL-3.0-or-later
// Preloader wakeup: penumbra (Shomy, 2025-2026, AGPL-3.0-or-later).
using GeekFlashCore.Protocol.Mtk.Internals;

namespace GeekFlashCore.Protocol.Mtk.Brom;

internal sealed partial class MtkBromSession
{
    private const byte HandshakeStart = 0xa0;
    private const byte HandshakeStartResponse = HandshakeStart ^ 0xff;
    private const int StartupPacketBufferSize = 1024;

    private MtkBromHardwareCode HandshakeAndIdentify(Action? identified)
    {
        bool preloaderCandidate = wire.IsPreloaderCandidate;
        Span<byte> packet = stackalloc byte[StartupPacketBufferSize];
        var reader = new MtkHandshakeReader(wire, preloaderCandidate ? packet : Span<byte>.Empty,
            options.ReadTimeoutMilliseconds);
        try
        {
            if (preloaderCandidate)
            {
                MtkDiagnostics.Summary(wire.Logger, Strings.PreloaderWake);
                reader.Check();
                wire.WriteByte(HandshakeStart); // Host detection; never repeated after this one wakeup.
            }

            ReadOnlySpan<byte> sequence = [HandshakeStart, 0x0a, 0x50, 0x05];
            bool alreadyHandshaken = false;
            for (int step = 0; step < sequence.Length; step++)
            {
                wire.Logger.Debug(Strings.HandshakeStep, step + 1, sequence.Length);
                reader.Check();
                wire.WriteByte(sequence[step]);
                byte response = reader.ReadByte();
                int prefixes = 0;
                while (step == 0 && response != HandshakeStartResponse && response != HandshakeStart)
                {
                    if (++prefixes > options.MaximumHandshakePrefix)
                        throw wire.Failure();
                    response = reader.ReadByte();
                }
                if (prefixes != 0)
                    wire.Logger.Debug(Strings.HandshakePrefix, prefixes);
                // Some preloaders acknowledge both the wakeup and the sequence's initial A0.
                // Consume at most one duplicate 5F, without resending 0A or restarting the sequence.
                if (step == 1 && preloaderCandidate && response == HandshakeStartResponse)
                {
                    wire.Logger.Debug(Strings.HandshakeWakeDuplicate);
                    response = reader.ReadByte();
                }
                wire.Logger.Debug(Strings.HandshakeResponse, step + 1, response, (byte)~sequence[step]);
                if (step == 0 && response == HandshakeStart)
                {
                    alreadyHandshaken = true; // Echo is only a candidate, not an identity or stage proof.
                    break;
                }
                if (response != (byte)~sequence[step])
                    throw wire.Failure();
            }

            wire.TraceCommand((byte)MtkBromCommand.GetHardwareCode, nameof(MtkBromCommand.GetHardwareCode));
            reader.Check();
            wire.WriteByte((byte)MtkBromCommand.GetHardwareCode);
            byte echo = reader.ReadByte();
            if (preloaderCandidate && alreadyHandshaken && echo == HandshakeStart)
            {
                wire.Logger.Debug(Strings.HandshakeWakeDuplicate);
                echo = reader.ReadByte();
            }
            if (echo != (byte)MtkBromCommand.GetHardwareCode)
                throw wire.Failure();
            var hardware = new MtkBromHardwareCode(reader.Read16(), reader.Read16());
            if (hardware.Code == 0)
                throw wire.Failure();
            // Stop admission recovery before any chip-specific write, even if later queries fail.
            identified?.Invoke();
            reader.RequireEmpty(); // Never silently discard unsolicited data at the FD boundary.
            wire.Logger.Debug(Strings.HandshakeIdentified, alreadyHandshaken);
            return hardware;
        }
        finally { packet.Clear(); }
    }
}
