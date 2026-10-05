using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.UsbWatcher.Abstractions;
using GeekFlashCore.CLI.Localization;

namespace GeekFlashCore.CLI;

internal sealed record ProtocolHostContext(ConsoleUi Ui, CliOptions Options);

internal interface IProtocolCommandSet
{
    bool Handles(string command);
    CliOptions Normalize(CliOptions options);
    void Validate(CliOptions options);
    bool RequiresConnection(string command);
    void ValidateAvailability(IProtocol protocol, string command);
    void PrintHelp(IProtocol protocol, ConsoleUi ui);
    Task<int> ExecuteAsync(IProtocol protocol, CliOptions options, ConsoleUi ui, IProgress<ProgressRecord> progress, CancellationToken ct);
}

internal interface IProtocolCommandHandler
{
    string Name { get; }
    string HelpText { get; }
    bool Handles(IReadOnlyList<string> arguments);
    bool RequiresConnection(IReadOnlyList<string> arguments);
    Task<int> ExecuteAsync(IProtocol protocol, IReadOnlyList<string> arguments, ConsoleUi ui, IProgress<ProgressRecord> progress, CancellationToken cancellationToken);
}

internal sealed record ProtocolRegistration(
    ProtocolType Type,
    IReadOnlySet<string> Names,
    string DisplayName,
    string WaitingMessage,
    IDeviceIdentify? DeviceIdentifier,
    Func<ProtocolHostContext, GeekFlashCore.Transport.Abstractions.ITransport, IProtocol> Factory,
    IReadOnlyList<IProtocolCommandHandler> CommandHandlers,
    Action<IProtocol, ConsoleUi>? InfoPresenter = null,
    IProtocolCommandSet? CommandSet = null,
    Func<GeekFlashCore.Transport.Abstractions.UsbTransportIdentity, CliOptions, GeekFlashCore.Transport.Abstractions.ITransport>? UsbFactory = null);

internal static class ProtocolRegistry
{
    private static readonly IReadOnlyList<ProtocolRegistration> Registrations =
    [QcomProtocolHostAdapter.Registration, MtkProtocolHostAdapter.Registration];

    public static IReadOnlyList<ProtocolRegistration> All => Registrations;

    public static bool TryResolve(string? name, out ProtocolRegistration registration)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            registration = QcomProtocolHostAdapter.Registration;
            return true;
        }
        registration = Registrations.FirstOrDefault(item => item.Names.Contains(name))!;
        return registration is not null;
    }

    public static bool TryIdentify(UsbDeviceInfo device, out ProtocolRegistration registration)
    {
        foreach (var item in Registrations)
        {
            if (item.DeviceIdentifier?.Identify(device).IsSuccess == true)
            {
                registration = item;
                return true;
            }
        }
        registration = null!;
        return false;
    }

    public static bool TryResolveCommand(string command, out ProtocolRegistration registration)
    {
        registration = Registrations.FirstOrDefault(item => item.CommandSet?.Handles(command) == true ||
            item.CommandHandlers.Any(handler => handler.Name.Equals(command, StringComparison.OrdinalIgnoreCase)))!;
        return registration is not null;
    }

    public static string SupportedNames => string.Join(", ", Registrations.Select(item => item.DisplayName));
}
