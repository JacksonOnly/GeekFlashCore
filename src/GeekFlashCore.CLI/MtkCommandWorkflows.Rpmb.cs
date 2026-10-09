using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using GeekFlashCore.CLI.Localization;
using GeekFlashCore.Protocol.Mtk.Abstractions;
using GeekFlashCore.Protocol.Mtk.Extensions;

namespace GeekFlashCore.CLI;

internal static partial class MtkCommandWorkflows
{
    private sealed class RpmbProfile(long generation)
    {
        internal readonly long Generation = generation;
        internal readonly uint[] Capacities = new uint[4];
        internal MtkDaExtension? Extension;
        internal (MtkExtensionAbi Abi, bool ExplicitAbi, uint Sej, uint Tzcc, uint Ssr)? Configuration;
    }
    private static readonly ConditionalWeakTable<IMtkProtocol, RpmbProfile> RpmbProfiles = new();
    private static async Task<(uint Capacity, MtkDaExtension? Extension)> RpmbProfileAsync(IMtkProtocol protocol, CliOptions options,
        uint region, bool initialize, ConsoleUi ui, CancellationToken ct)
    {
        if (!RpmbProfiles.TryGetValue(protocol, out var profile) || profile.Generation != protocol.Generation)
        {
            RpmbProfiles.Remove(protocol); profile = new(protocol.Generation); RpmbProfiles.Add(protocol, profile);
        }
        var storage = protocol.GetStorageInfo();
        if (storage.Kind == MtkStorageKind.Emmc)
        {
            if (region != 0 || storage.RpmbDataBlocks == 0) throw new MtkCapabilityException("reported eMMC RPMB capacity");
            profile.Capacities[0] = storage.RpmbDataBlocks;
        }
        else if (storage.Kind == MtkStorageKind.Ufs)
        {
            if (region > 3) throw new MtkCapabilityException("UFS RPMB region");
            for (int i = 0; i < options.MtkUfsRpmbBlocks.Count; i++)
                if (options.MtkUfsRpmbBlocks[i] != 0 && profile.Capacities[i] != options.MtkUfsRpmbBlocks[i])
                { profile.Capacities[i] = options.MtkUfsRpmbBlocks[i]; profile.Extension = null; }
            if (profile.Capacities[region] == 0)
            {
                string answer = await ui.AskAsync(Strings.FormatCli_MtkRpmbCapacityRequired(region), ct).ConfigureAwait(false);
                uint capacity = checked((uint)CommandSyntax.Number(answer));
                if (capacity == 0) throw new MtkResourceException("confirmed UFS RPMB capacity");
                profile.Capacities[region] = capacity; profile.Extension = null;
            }
            options = options with { MtkUfsRpmbBlocks = profile.Capacities.ToArray() };
        }
        else throw new MtkCapabilityException("eMMC/UFS RPMB");
        var configuration = (options.MtkExtensionAbi, options.HasExplicitMtkExtensionAbi, options.MtkSejBase, options.MtkTzccBase, options.MtkSsrBase);
        if (initialize && (profile.Configuration != configuration || profile.Extension is null || !profile.Extension.IsReady ||
            !MtkProtocolHostAdapter.IsCurrentExtension(protocol, profile.Extension)))
        {
            profile.Extension = MtkProtocolHostAdapter.CreateExtension(protocol, options, [], ct);
            profile.Configuration = configuration;
        }
        return (profile.Capacities[region], initialize ? profile.Extension : null);
    }
    private static void Authenticate(MtkDaExtension extension, CliOptions options, uint region, string? keyFile, CancellationToken ct)
    {
        if (keyFile is null && extension.IsAuthenticated(region)) return;
        if (keyFile is not null)
        {
            byte[] key = MtkProtocolHostAdapter.ReadBounded(keyFile, 32);
            try
            {
                if (key.Length != 32) throw new MtkResourceException("RPMB key length");
                extension.Authenticate(region, key, ct);
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        else
        {
            if (options.MtkExtensionAbi != MtkExtensionAbi.Penumbra2) throw new MtkCapabilityException("RPMB --key-file or Penumbra2 key derivation");
            using var key = extension.DeriveKey(MtkKeyDeriveId.Rpmb, MtkKeySize.Key256, ct);
            extension.Authenticate(region, key.Memory.Span, ct);
        }
    }
    private static async Task RpmbAsync(IMtkProtocol protocol, CliOptions options, MtkCommandRequest request, ConsoleUi ui, CancellationToken ct)
    {
        string[] a = request.Arguments; string action = a[0]; uint region = request.Number("--region");
        string? file = a.Length == 2 ? ConsolePath.Normalize(a[1]) : null;
        using var input = action == "write" ? new FileStream(file!, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        var profile = await RpmbProfileAsync(protocol, options, region, false, ui, ct);
        if (action == "info") { ui.WriteLine(Strings.FormatCli_MtkRpmbCapacity(region, profile.Capacity, (long)profile.Capacity * 256)); return; }
        uint start = request.Number("--start"), count = request.Number("--count");
        if (action == "write")
        {
            long length = input!.Length;
            if (length <= 0 || length % 256 != 0 || length / 256 > uint.MaxValue) throw new MtkResourceException("RPMB input length/alignment");
            if (count == 0) count = (uint)(length / 256);
            if (length != (long)count * 256) throw new MtkResourceException("RPMB file/count mismatch");
        }
        if (action != "auth")
        {
            if (start >= profile.Capacity) throw new MtkResourceException("RPMB start/capacity");
            if (count == 0) count = profile.Capacity - start;
            if (count > profile.Capacity - start) throw new MtkResourceException("RPMB range/capacity");
        }
        profile = await RpmbProfileAsync(protocol, options, region, true, ui, ct);
        var extension = profile.Extension!;
        Authenticate(extension, options, region, action == "auth" ? file : request.Value("--key-file"), ct);
        if (action == "auth") { ui.WriteLine(Strings.Cli_MtkRpmbAuthenticated); return; }
        if (action == "read")
            await AtomicReadOutput.WriteAsync(file!, stream => { extension.Read(region, start, count, stream, ct); return Task.CompletedTask; }, ct);
        else
        {
            string backupPath = BackupPath(request.Value("--backup"), "rpmb");
            using (var backup = new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { extension.Read(region, start, count, backup, ct); backup.Flush(true); }
            if (!await ConfirmAsync(ui, $"RPMB {action} R{region} {start}+{count}", backupPath, ct)) return;
            if (action == "erase") extension.Erase(region, start, count, ct);
            else extension.Write(region, start, count, input!, ct);
        }
        ui.WriteLine(Strings.FormatCli_CommandCompleted("rpmb " + action));
    }
    private static async Task RpmbLockAsync(IMtkProtocol protocol, CliOptions options, MtkCommandRequest request, ConsoleUi ui, CancellationToken ct)
    {
        if (protocol.DownloadAgent?.Entry.Kind != MtkDaKind.Xml || protocol.GetStorageInfo().Kind != MtkStorageKind.Ufs)
            throw new MtkCapabilityException("XML/UFS RPMB lock metadata");
        var profile = await RpmbProfileAsync(protocol, options, 1, true, ui, ct);
        var extension = profile.Extension!;
        Authenticate(extension, options, 1, request.Value("--key-file"), ct);
        string action = request.Arguments[0];
        if (action == "read")
        { var info = extension.ReadRpmbLockState(ct); ui.WriteLine(Strings.FormatCli_MtkRpmbLockState(info.Version, info.State)); return; }
        string path = BackupPath(request.Value("--backup"), "rpmb-lock");
        using var backup = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        extension.Read(1, 0, 1, backup, ct); backup.Flush(true);
        if (!await ConfirmAsync(ui, "rpmb-lock " + action, path, ct)) return;
        backup.Position = 0; extension.SetRpmbLockState(action == "lock", backup, ct);
        ui.WriteLine(Strings.FormatCli_CommandCompleted("rpmb-lock " + action));
    }
}
