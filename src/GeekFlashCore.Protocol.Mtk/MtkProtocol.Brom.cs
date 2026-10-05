using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Protocol.Mtk;

public sealed partial class MtkProtocol : IMtkBromSessionAccess
{
    /// <summary>Runs bounded standard BROM commands after Probe. The session expires on return or a jump.</summary>
    public T UseBromSession<T>(Func<IMtkBromSession, T> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Execute(() =>
        {
            if (_state != MtkSessionState.Probed)
                throw new InvalidOperationException(Strings.SessionUnavailable);
            var session = new BromChannel(this);
            try
            {
                return action(session);
            }
            finally { session.Expire(); }
        }, cancellationToken);
    }
    private sealed class BromChannel(MtkProtocol owner, Action? guard = null) : IMtkBromSession, IMtkBromSessionControl
    {
        private bool _valid = true;
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly long _generation = owner.Generation;
        public void Expire() => _valid = false;
        public void Check()
        {
            guard?.Invoke();
            if (!_valid || owner._state != MtkSessionState.Probed || _thread != Environment.CurrentManagedThreadId || _generation != owner.Generation)
                throw new InvalidOperationException(Strings.SessionUnavailable);
            owner._wire.Check();
        }
        public void Invalidate()
        {
            guard?.Invoke();
            if (!_valid || _thread != Environment.CurrentManagedThreadId || _generation != owner.Generation)
                throw new InvalidOperationException(Strings.SessionUnavailable);
            owner.Fault(); _valid=false;
        }
        private void Transition()
        {
            Expire();
            Interlocked.Increment(ref owner._generation);
            owner.State(MtkSessionState.Da1Ready);
        }
        public MtkBromHardwareCode GetHardwareCode()
        {
            Check();
            return owner._brom.GetHardwareCode();
        }
        public MtkBromHardwareSoftwareVersion GetHardwareSoftwareVersion()
        {
            Check();
            return owner._brom.GetHardwareSoftwareVersion();
        }
        public MtkSecurityConfiguration GetTargetConfiguration()
        {
            Check();
            return owner._brom.GetTargetConfiguration();
        }
        public byte GetBootLoaderVersion()
        {
            Check();
            return owner._brom.GetBootLoaderVersion();
        }
        public byte GetBromVersion()
        {
            Check();
            return owner._brom.GetBromVersion();
        }
        public MtkPreloaderCapabilities GetPreloaderCapabilities()
        {
            Check();
            return owner._brom.GetPreloaderCapabilities();
        }
        public MtkSensitiveBuffer? GetMeId()
        {
            Check();
            return owner._brom.GetMeId();
        }
        public MtkSensitiveBuffer? GetSocId()
        {
            Check();
            return owner._brom.GetSocId();
        }
        public MtkSensitiveBuffer GetBromLog(bool newCommand = false)
        {
            Check();
            return owner._brom.GetBromLog(newCommand);
        }
        public ushort[] Read16(uint address, int count = 1)
        {
            Check();
            return owner._brom.Read16(address, count);
        }
        public uint[] Read32(uint address, int count = 1)
        {
            Check();
            return owner._brom.Read32(address, count);
        }
        public ushort ReadA2(uint address)
        {
            Check();
            return owner._brom.ReadA2(address);
        }
        public void Write16(uint address, ReadOnlySpan<ushort> values)
        {
            Check();
            owner._brom.Write16(address, values);
        }
        public void Write32(uint address, ReadOnlySpan<uint> values)
        {
            Check();
            owner._brom.Write32(address, values);
        }
        public void WriteMemory(uint address, ReadOnlySpan<byte> data, bool padFinalWord = false)
        {
            Check();
            owner._brom.WriteMemory(address, data, padFinalWord);
        }
        public void ReadRegisters(uint address, Span<byte> destination)
        {
            Check();
            owner._brom.ReadRegisters(address, destination);
        }
        public void WriteRegisters(uint address, ReadOnlySpan<byte> data)
        {
            Check();
            owner._brom.WriteRegisters(address, data);
        }
        public void DisableWatchdog()
        {
            Check();
            owner._target = owner._target! with { WatchdogState = owner._brom.DisableWatchdog(owner._target!) };
        }
        public void ConfigureBromReset(uint miscLockAddress, bool enabled = true, int timeoutMilliseconds = 0)
        {
            Check();
            owner._brom.ConfigureBromReset(miscLockAddress, enabled, timeoutMilliseconds);
        }
        public MtkBromCacheResult RunCacheDeinitialize()
        {
            Check();
            return owner._brom.RunCacheDeinitialize();
        }
        public void EnableUart1Log()
        {
            Check();
            owner._brom.EnableUart1Log();
        }
        public void SetUart1BaudRate(uint baudRate)
        {
            Check();
            owner._brom.SetUart1BaudRate(baudRate);
        }
        public void SendCertificate(ReadOnlySpan<byte> certificate)
        {
            Check();
            owner._brom.SendResource(MtkBromCommand.SendCertificate, certificate);
        }
        public void SendAuthentication(ReadOnlySpan<byte> authentication)
        {
            Check();
            owner._brom.SendResource(MtkBromCommand.SendAuthentication, authentication);
        }
        public void Authenticate(Func<MtkAuthenticationKind, ReadOnlyMemory<byte>, MtkSensitiveBuffer> signer)
        {
            Check();
            owner._brom.Authenticate(signer);
        }
        public void SendDownloadAgent(uint address, uint size, uint signatureLength, IDataSource source,
            Func<MtkAuthenticationKind, ReadOnlyMemory<byte>, MtkSensitiveBuffer>? signer = null)
        {
            Check();
            owner._brom.SendDownloadAgent(address, size, signatureLength, source, signer);
        }
        public void JumpDownloadAgent(uint address)
        {
            Check();
            owner._brom.JumpDownloadAgent(address);
            Transition();
        }
        public void JumpDownloadAgent64(uint address)
        {
            Check();
            owner._brom.JumpDownloadAgent(address, true);
            Transition();
        }
        public void JumpBootLoader()
        {
            Check();
            owner._brom.JumpBootLoader();
            Transition();
        }
        public void JumpToPartition(string partitionName)
        {
            Check();
            owner._brom.JumpToPartition(partitionName);
            Transition();
        }
        public void SendPartitionData(string partitionName, IDataSource source)
        {
            Check();
            owner._brom.SendPartitionData(partitionName, source);
        }
    }
}
