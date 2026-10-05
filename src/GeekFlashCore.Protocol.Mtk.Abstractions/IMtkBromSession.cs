using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Protocol.Mtk.Abstractions;

/// <summary>Serialized standard BROM/Preloader access after Probe and before DA execution.</summary>
public interface IMtkBromSessionAccess
{
    /// <summary>Runs one synchronous action. Retained sessions expire on return or an execution transition.</summary>
    T UseBromSession<T>(Func<IMtkBromSession, T> action, CancellationToken cancellationToken = default);
}

/// <summary>Bounded standard BROM methods corresponding to mtkclient Preloader. No security bypass or unchecked-status mode.</summary>
public interface IMtkBromSession
{
    /// <summary>Reads hardware code and the initial hardware version.</summary>
    MtkBromHardwareCode GetHardwareCode();
    /// <summary>Reads subcode, hardware and software versions and validates status.</summary>
    MtkBromHardwareSoftwareVersion GetHardwareSoftwareVersion();
    /// <summary>Reads the security configuration and validates status.</summary>
    MtkSecurityConfiguration GetTargetConfiguration();
    /// <summary>Reads the boot-loader version; 0xFE proves BROM.</summary>
    byte GetBootLoaderVersion();
    /// <summary>Reads the BROM version.</summary>
    byte GetBromVersion();
    /// <summary>Reads both raw Preloader capability words.</summary>
    MtkPreloaderCapabilities GetPreloaderCapabilities();
    /// <summary>Reads a bounded owned MEID; returns null for unsupported early Preloaders. Never logged.</summary>
    MtkSensitiveBuffer? GetMeId();
    /// <summary>Reads a bounded owned SOCID; returns null for unsupported early Preloaders. Never logged.</summary>
    MtkSensitiveBuffer? GetSocId();
    /// <summary>Reads a bounded owned raw debug log. The caller decides whether to disclose it.</summary>
    MtkSensitiveBuffer GetBromLog(bool newCommand = false);
    /// <summary>Reads big-endian 16-bit words with both status checks.</summary>
    ushort[] Read16(uint address, int count = 1);
    /// <summary>Reads big-endian 32-bit words with both status checks.</summary>
    uint[] Read32(uint address, int count = 1);
    /// <summary>Reads one word using the legacy A2 command, which has no status fields.</summary>
    ushort ReadA2(uint address);
    /// <summary>Writes and echoes big-endian 16-bit words, requiring final status.</summary>
    void Write16(uint address, ReadOnlySpan<ushort> values);
    /// <summary>Writes and echoes big-endian 32-bit words, requiring final status.</summary>
    void Write32(uint address, ReadOnlySpan<uint> values);
    /// <summary>Maps little-endian memory bytes to WRITE32. A partial final word requires explicit padding consent.</summary>
    void WriteMemory(uint address, ReadOnlySpan<byte> data, bool padFinalWord = false);
    /// <summary>Reads bounded, aligned register bytes using DA. Both statuses are little-endian and mandatory.</summary>
    void ReadRegisters(uint address, Span<byte> destination);
    /// <summary>Writes bounded, aligned register bytes using DA. Both statuses are little-endian and mandatory.</summary>
    void WriteRegisters(uint address, ReadOnlySpan<byte> data);
    /// <summary>Disables a watchdog using an explicit override or known standard chip metadata.
    /// Unknown chips receive no guessed addresses; acknowledged writes are not repeated in the same session.</summary>
    void DisableWatchdog();
    /// <summary>Configures watchdog-reset download flags at an explicit MISC_LOCK address; does not trigger a reset.</summary>
    void ConfigureBromReset(uint miscLockAddress, bool enabled = true, int timeoutMilliseconds = 0);
    /// <summary>Performs only the reference C8/B1 cache exchange and returns its raw bounded result.</summary>
    MtkBromCacheResult RunCacheDeinitialize();
    /// <summary>Enables UART1 logging and validates status.</summary>
    void EnableUart1Log();
    /// <summary>Sets an explicit positive UART1 baud rate and validates status.</summary>
    void SetUart1BaudRate(uint baudRate);
    /// <summary>Sends a borrowed certificate, padding an odd final byte and validating its checksum.</summary>
    void SendCertificate(ReadOnlySpan<byte> certificate);
    /// <summary>Sends borrowed authentication data, padding an odd final byte and validating its checksum.</summary>
    void SendAuthentication(ReadOnlySpan<byte> authentication);
    /// <summary>Performs one legitimate SLA exchange using the caller's synchronous signer. No key probing.</summary>
    void Authenticate(Func<MtkAuthenticationKind, ReadOnlyMemory<byte>, MtkSensitiveBuffer> signer);
    /// <summary>Sends one DA source without jumping. Its stream is owned by this call; an in-command SLA request uses the signer.</summary>
    void SendDownloadAgent(uint address, uint size, uint signatureLength, IDataSource source,
        Func<MtkAuthenticationKind, ReadOnlyMemory<byte>, MtkSensitiveBuffer>? signer = null);
    /// <summary>Jumps to an uploaded 32-bit DA. Further BROM commands are rejected.</summary>
    void JumpDownloadAgent(uint address);
    /// <summary>Jumps to an uploaded 64-bit DA at a 32-bit wire address. Further BROM commands are rejected.</summary>
    void JumpDownloadAgent64(uint address);
    /// <summary>Jumps to the boot loader with both status checks. Further BROM commands are rejected.</summary>
    void JumpBootLoader();
    /// <summary>Jumps to an explicit UTF-8 partition name of at most 64 bytes. Further BROM commands are rejected.</summary>
    void JumpToPartition(string partitionName);
    /// <summary>Streams Preloader-as-DA partition data and checksum. This reference command has no final device confirmation.</summary>
    void SendPartitionData(string partitionName, IDataSource source);
}
