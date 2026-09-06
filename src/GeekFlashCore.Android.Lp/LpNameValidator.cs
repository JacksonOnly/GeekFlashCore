using GeekFlashCore.Android.Lp.Abstractions;

namespace GeekFlashCore.Android.Lp;

internal static class LpNameValidator
{
    internal const int MaximumNameLength = 36;

    internal static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaximumNameLength)
        {
            return false;
        }

        foreach (char character in value)
        {
            bool valid = character is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '_';
            if (!valid)
            {
                return false;
            }
        }

        return true;
    }

    internal static void Validate(string value, string parameterName)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(Resources.FormatInvalidName(value), parameterName);
        }
    }

    internal static string GetEffectiveName(
        string rawName,
        bool slotSuffixed,
        int slotNumber) =>
        slotSuffixed ? string.Concat(rawName, "_", checked((char)('a' + slotNumber))) : rawName;

    internal static void ValidatePartitionAttributes(
        LpPartitionAttributes attributes,
        ushort minorVersion,
        string parameterName)
    {
        uint allowed = (uint)(LpPartitionAttributes.ReadOnly | LpPartitionAttributes.SlotSuffixed);
        if (minorVersion >= 1)
        {
            allowed |= (uint)(LpPartitionAttributes.Updated | LpPartitionAttributes.Disabled);
        }

        if (((uint)attributes & ~allowed) != 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                attributes,
                Resources.UnsupportedPartitionAttributes);
        }
    }

    internal static void ValidateGroupFlags(LpGroupFlags flags, string parameterName)
    {
        if (((uint)flags & ~(uint)LpGroupFlags.SlotSuffixed) != 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                flags,
                Resources.UnsupportedGroupFlags);
        }
    }
}
