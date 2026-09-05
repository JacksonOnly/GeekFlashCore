using System.Security.Cryptography;
using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Loaders;

public static class SecureBootEvaluator
{
    public static SecureBootState Evaluate(
        ReadOnlySpan<byte> devicePkHash,
        ReadOnlySpan<byte> programmerCaHash,
        bool programmerStarted)
    {
        if (!programmerStarted || !IsUsableHash(devicePkHash) || !IsUsableHash(programmerCaHash) ||
            devicePkHash.Length != programmerCaHash.Length)
        {
            return SecureBootState.Unknown;
        }

        if (CryptographicOperations.FixedTimeEquals(devicePkHash, programmerCaHash))
            return SecureBootState.Enabled;
        return programmerStarted ? SecureBootState.Disabled : SecureBootState.Unknown;
    }

    private static bool IsUsableHash(ReadOnlySpan<byte> hash)
    {
        if (hash.IsEmpty)
            return false;
        bool allZero = true;
        bool allOnes = true;
        foreach (byte value in hash)
        {
            allZero &= value == 0;
            allOnes &= value == byte.MaxValue;
        }
        return !allZero && !allOnes;
    }
}
