using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Protocol.Qcom.Vendors;

namespace GeekFlashCore.Protocol.Qcom.Firehose.Configuration;

public sealed record FirehoseConfigureResult(
    FirehoseCommandResult CommandResult,
    FirehoseConfigureResponse Configuration)
{
    public int Attempts => Configuration.Attempts;
}

public sealed class ConfigureNegotiator
{
    private static readonly FirehoseStorage[] StorageFallbackOrder =
    [
        FirehoseStorage.Emmc,
        FirehoseStorage.Ufs,
        FirehoseStorage.Spinor,
        FirehoseStorage.Nvme,
        FirehoseStorage.Nand
    ];

    private readonly FirehoseSession _session;

    public ConfigureNegotiator(FirehoseSession session) =>
        _session = session ?? throw new ArgumentNullException(nameof(session));

    public FirehoseConfigureResult Negotiate(
        FirehoseConfiguration configuration,
        QcomVendorKind vendor = QcomVendorKind.Generic,
        Func<FirehoseNakException, bool>? xiaomiAuthentication = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();
        IVendorFirehoseStrategy strategy = VendorStrategyResolver.ForVendor(vendor);

        ConfigureState state = ConfigureState.Create(configuration);
        var attemptedStorage = new HashSet<FirehoseStorage>();
        var seenStates = new HashSet<ConfigureState>();
        FirehoseNakException? lastNak = null;

        for (int attempt = 1; attempt <= configuration.MaxConfigureAttempts; attempt++)
        {
            if (!seenStates.Add(state))
                throw Failure("Configure negotiation repeated an unchanged state.", lastNak);
            attemptedStorage.Add(state.Storage);

            try
            {
                FirehoseCommandResult result = _session.Execute(strategy.PrepareConfigure(state.CreateCommand()));
                state = state.Apply(ConfigureEvidenceParser.Parse(result));
                return new FirehoseConfigureResult(result, state.ToResponse(attempt, result));
            }
            catch (FirehoseNakException exception)
            {
                lastNak = exception;

                if (strategy.Vendor == QcomVendorKind.Xiaomi && !state.AuthenticationCompleted)
                {
                    if (xiaomiAuthentication is null || !xiaomiAuthentication(exception))
                        throw Failure("Xiaomi authentication is required for Configure.", exception);
                    state = state with { AuthenticationCompleted = true };
                    continue;
                }

                if (state.ManualStorage)
                    throw Failure("Firehose rejected the explicitly selected storage configuration.", exception);

                ConfigureEvidence evidence = ConfigureEvidenceParser.Parse(exception.Result);
                ConfigureState next = state.Apply(evidence);
                if (evidence.UnsupportedStorage is not null || next == state)
                {
                    FirehoseStorage? fallback = FindNextStorage(attemptedStorage);
                    if (fallback is null)
                        throw Failure("Firehose rejected every supported storage configuration.", exception);
                    next = next.WithStorage(fallback.Value);
                }

                if (next == state)
                    throw Failure("Firehose Configure returned no actionable negotiation evidence.", exception);
                state = next;
            }
        }

        throw Failure(
            $"Firehose Configure exceeded {configuration.MaxConfigureAttempts} attempts.",
            lastNak);
    }

    private static FirehoseStorage? FindNextStorage(HashSet<FirehoseStorage> attempted)
    {
        foreach (FirehoseStorage storage in StorageFallbackOrder)
        {
            if (!attempted.Contains(storage))
                return storage;
        }
        return null;
    }

    private static FirehoseConfigureException Failure(string message, FirehoseNakException? exception) =>
        new(message, exception, exception?.Result);
}
