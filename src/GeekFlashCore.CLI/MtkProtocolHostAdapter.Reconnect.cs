using GeekFlashCore.CLI.Localization;
using GeekFlashCore.Protocol.Mtk;
using GeekFlashCore.Protocol.Mtk.Abstractions;
using GeekFlashCore.Protocol.Mtk.Loaders;
using GeekFlashCore.Transport.Abstractions;

namespace GeekFlashCore.CLI;

internal sealed record MtkReconnectSnapshot(MtkTargetInfo Target, MtkDaImage Image,
    MtkStorageInfo Storage, UsbTransportIdentity Identity, bool ExtensionReady);

internal static partial class MtkProtocolHostAdapter
{
    internal static MtkReconnectSnapshot? Capture(GeekFlashCore.Protocol.Abstractions.IProtocol protocol) =>
        protocol is IMtkProtocol { IsConnected: true, TargetInfo: { } target, DownloadAgent: { } image } mtk &&
        protocol.Transport is IUsbTransport usb ? new(target, image, mtk.GetStorageInfo(), usb.Identity,
            Extensions.TryGetValue(mtk, out var extension) && extension.IsReady) : null;

    internal static bool SameDevice(UsbTransportIdentity a, UsbTransportIdentity b) =>
        !string.IsNullOrWhiteSpace(a.SerialNumber) && !string.IsNullOrWhiteSpace(b.SerialNumber)
            ? a.SerialNumber == b.SerialNumber
            : a.BusNumber is not null && a.BusNumber == b.BusNumber && !string.IsNullOrWhiteSpace(a.PortPath) && a.PortPath == b.PortPath;

    internal static async Task ResumeAsync(MtkProtocol protocol, CliOptions options, ConsoleUi ui,
        MtkReconnectSnapshot? snapshot, bool cleanBoundary, string[] arguments, CancellationToken ct)
    {
        var identity = ((IUsbTransport)protocol.Transport).Identity;
        if (snapshot is not null && !SameDevice(snapshot.Identity, identity))
        {
            string answer = await ui.AskAsync(Strings.Cli_MtkReconnectIdentity, ct, "no");
            if (!answer.Equals("yes", StringComparison.OrdinalIgnoreCase)) throw new MtkResourceException("confirmed reconnect identity");
            cleanBoundary = false;
        }
        string stage = arguments.FirstOrDefault()?.ToLowerInvariant() ?? "auto";
        if (stage == "auto")
        {
            var signal = protocol.InspectEntrySignal(ct);
            if (signal == MtkEntrySignal.Da1Sync) stage = "da1";
            else if (snapshot is not null && cleanBoundary && signal == MtkEntrySignal.None) stage = "da2";
            else if (identity.ProductId != 0x2001 && snapshot is null && signal != MtkEntrySignal.FramedDa) stage = "brom";
            else stage = (await ui.AskAsync(Strings.Cli_MtkReconnectStage, ct)).Trim().ToLowerInvariant();
        }
        if (stage == "brom")
        {
            if (options.Loader is null) throw new MtkResourceException("BROM reconnect requires --loader");
            await protocol.ConnectAsync(ct: ct).ConfigureAwait(false);
            InitializeExtension(protocol, options, ct);
            return;
        }
        if (stage is not ("da1" or "da2")) throw new CommandUsageException(CommandSyntax.Usages["reconnect"]);
        if (stage == "da2" && !cleanBoundary)
        {
            string answer = await ui.AskAsync(Strings.Cli_MtkReconnectBoundary, ct, "no");
            if (!answer.Equals("yes", StringComparison.OrdinalIgnoreCase)) throw new MtkResourceException("confirmed DA command boundary");
        }
        string? mode = arguments.ElementAtOrDefault(1) ?? options.MtkDaMode;
        MtkDaKind? kind = mode is not (null or "auto") ? DaKind(mode) : snapshot?.Image.Entry.Kind;
        MtkTargetInfo target;
        if (snapshot is not null) target = snapshot.Target;
        else
        {
            string[] fields = (await ui.AskAsync(Strings.Cli_MtkReconnectHardware, ct)).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 6) throw new MtkResourceException("hardware/subcode/hardware-version/software-version/BROM-version/security");
            target = new(checked((ushort)CommandSyntax.Number(fields[0])), checked((ushort)CommandSyntax.Number(fields[1])),
                checked((ushort)CommandSyntax.Number(fields[2])), checked((ushort)CommandSyntax.Number(fields[3])),
                checked((byte)CommandSyntax.Number(fields[4])), 0, MtkBootStage.Unknown, new(checked((uint)CommandSyntax.Number(fields[5]))));
        }
        MtkDaImage image;
        if (snapshot is not null && snapshot.Image.Entry.Kind == kind && options.Loader is null) image = snapshot.Image;
        else
        {
            options = await SelectLoaderAsync(options, ui, ct);
            var source = new FileDataSource(ConsolePath.Normalize(options.Loader)!);
            try { image = MtkDaParser.Select(source, target, kind); }
            catch (MtkResourceException) when (kind is null && ui.CanPrompt)
            {
                kind = DaKind((await ui.AskAsync(Strings.Cli_MtkReconnectDialect, ct)).Trim()) ?? throw new MtkResourceException("DA dialect");
                image = MtkDaParser.Select(source, target, kind);
            }
        }
        if (stage == "da1" && options.MtkPreloader is null && ui.CanPrompt)
            options = options with { MtkPreloader = await ui.SelectFileAsync(Strings.Cli_MtkPreloaderPrompt, null,
                Strings.Cli_MtkDaMissing, ct, optional: true) };
        var emi = options.MtkPreloader is null ? null : MtkEmiParser.Parse(new FileDataSource(ConsolePath.Normalize(options.MtkPreloader)!));
        protocol.ResumeDownloadAgent(stage == "da1" ? MtkBootStage.Da1 : MtkBootStage.Da2, target, new(image, emi), snapshot?.Storage, ct);
        if (stage == "da1") InitializeExtension(protocol, options, ct);
        else if (snapshot?.ExtensionReady == true)
        {
            var extension = new GeekFlashCore.Protocol.Mtk.Extensions.MtkDaExtension(protocol);
            extension.Initialize(ExtensionContext(protocol, options, []), ct);
            RememberExtension(protocol, extension);
        }
    }
}
