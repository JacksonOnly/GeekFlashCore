using System.Globalization;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Protocol.Qcom.Firehose;

namespace GeekFlashCore.Protocol.Qcom.Vendors.OnePlus;

public sealed class OnePlusProjectVerifier(FirehoseSession session)
{
    private readonly FirehoseSession _session = session ?? throw new ArgumentNullException(nameof(session));

    public FirehoseCommandResult VerifyDemacia(string publicKey, string token)
    {
        OnePlusToken credential = OnePlusTokenCodec.Validate(token, publicKey);
        FirehoseCommandResult result = _session.Execute(new OnePlusDemaciaCommand
        {
            PublicKey = credential.PublicKey,
            Token = credential.Value
        });
        RequireZero(result, "verify_res", "OnePlus Demacia verification failed.");
        return result;
    }

    public FirehoseCommandResult VerifyProject(string publicKey, string token)
    {
        OnePlusToken credential = OnePlusTokenCodec.Validate(token, publicKey);
        FirehoseCommandResult result = _session.Execute(new OnePlusSetProjectModelCommand
        {
            PublicKey = credential.PublicKey,
            Token = credential.Value
        });
        RequireModelVerification(result, "OnePlus project verification failed.");
        return result;
    }

    public long BeginSoftwareVerification()
    {
        FirehoseCommandResult result = _session.Execute(new OnePlusSetProcessStartCommand());
        if (TryGetValue(result, "device_timestamp", out string timestamp) &&
            long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) && parsed > 0)
        {
            return parsed;
        }
            throw new QcomAuthenticationException(Strings.Qcom_OnePlusTimestampMissing);
    }

    public FirehoseCommandResult VerifySoftwareProject(string publicKey, string token)
    {
        OnePlusToken credential = OnePlusTokenCodec.Validate(token, publicKey, softwareGeneration: true);
        FirehoseCommandResult result = _session.Execute(new OnePlusSetSoftwareProjectModelCommand
        {
            PublicKey = credential.PublicKey,
            Token = credential.Value
        });
        RequireModelVerification(result, "OnePlus software project verification failed.");
        return result;
    }

    public FirehoseCommandResult EndSoftwareVerification() =>
        _session.Execute(new OnePlusSetProcessEndCommand());

    public FirehoseCommandResult SetNetworkType() =>
        _session.Execute(new OnePlusSetNetTypeCommand());

    internal OnePlusAuthenticationContext VerifyBuiltIn(
        ulong serial,
        IEnumerable<string> projectIds,
        IReadOnlySet<string> supportedFunctions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectIds);
        ArgumentNullException.ThrowIfNull(supportedFunctions);
        if (serial == 0)
            throw new QcomAuthenticationException(Strings.Qcom_InvalidAuthentication);

        bool software = supportedFunctions.Contains("setswprojmodel");
        bool demacia = supportedFunctions.Contains("demacia");
        bool setProject = supportedFunctions.Contains("setprojmodel");
        bool setNetwork = supportedFunctions.Contains("SetNetType");
        QcomAuthenticationException? lastFailure = null;
        foreach (string candidate in projectIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!OnePlusDeviceProfiles.TryGet(candidate, out OnePlusDeviceProfile profile))
                continue;
            if ((software && profile.Version != 3) || (!software && profile.Version == 3))
                continue;
            string publicKey = OnePlusTokenGenerator.CreatePublicKey();
            try
            {
                if (software)
                {
                    long timestamp = BeginSoftwareVerification();
                    (string key, string token) = OnePlusTokenGenerator.CreateSoftwareToken(
                        profile, serial, publicKey, timestamp, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    VerifySoftwareProject(key, token);
                    return new OnePlusAuthenticationContext(profile, serial, key, timestamp, true);
                }

                if (demacia)
                {
                    (string key, string token) = OnePlusTokenGenerator.CreateLegacyToken(
                        profile, serial, publicKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), demacia: true);
                    VerifyDemacia(key, token);
                    if (setNetwork)
                    {
                        _session.Execute(new OnePlusSetNetTypeCommand(), cancellationToken: cancellationToken);
                        return new OnePlusAuthenticationContext(profile, serial, key, 0, false);
                    }
                }

                if (setProject)
                {
                    (string key, string token) = OnePlusTokenGenerator.CreateLegacyToken(
                        profile, serial, publicKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    VerifyProject(key, token);
                    return new OnePlusAuthenticationContext(profile, serial, key, 0, false);
                }

                if (demacia)
                    return new OnePlusAuthenticationContext(profile, serial, publicKey, 0, false);
                throw new QcomAuthenticationException(Strings.Qcom_InvalidAuthentication);
            }
            catch (FirehoseNakException exception)
            {
                lastFailure = new QcomAuthenticationException(
                    Strings.Qcom_OnePlusProjectVerificationFailed, exception);
            }
            catch (QcomAuthenticationException exception)
            {
                lastFailure = exception;
            }
        }

        throw lastFailure ?? new QcomAuthenticationException(Strings.Qcom_OnePlusProjectVerificationFailed);
    }

    private static void RequireModelVerification(FirehoseCommandResult result, string message)
    {
        RequireZero(result, "model_check", message);
        RequireZero(result, "auth_token_verify", message);
    }

    private static void RequireZero(FirehoseCommandResult result, string name, string message)
    {
        if (!TryGetValue(result, name, out string value) || !value.Equals("0", StringComparison.Ordinal))
            throw new QcomAuthenticationException(message);
    }

    private static bool TryGetValue(FirehoseCommandResult result, string name, out string value)
    {
        if (result.Attributes.TryGetValue(name, out string? attribute) && attribute is not null)
        {
            value = attribute;
            return true;
        }
        foreach (IReadOnlyDictionary<string, string> element in result.PayloadElements.Values)
        {
            if (element.TryGetValue(name, out attribute) && attribute is not null)
            {
                value = attribute;
                return true;
            }
        }
        value = string.Empty;
        return false;
    }
}
