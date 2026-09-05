using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.UsbWatcher.Abstractions;

namespace GeekFlashCore.CLI;

internal sealed record ProtocolHostContext(ConsoleUi Ui, CliOptions Options);

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
    Action<IProtocol, ConsoleUi>? InfoPresenter = null);

internal static class ProtocolRegistry
{
    private static readonly IReadOnlyList<ProtocolRegistration> Registrations =
    [QcomProtocolHostAdapter.Registration];

    public static IReadOnlyList<ProtocolRegistration> All => Registrations;

    public static bool TryResolve(string? name, out ProtocolRegistration registration)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            registration = Registrations.Count == 1
                ? Registrations[0]
                : throw new ArgumentException("协议数量超过一个时必须使用 --protocol 指定协议");
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
        registration = Registrations.FirstOrDefault(item => item.CommandHandlers.Any(handler => handler.Name.Equals(command, StringComparison.OrdinalIgnoreCase)))!;
        return registration is not null;
    }

    public static string SupportedNames => string.Join(", ", Registrations.Select(item => item.DisplayName));
}
