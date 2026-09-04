using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GeekFlashCore.Protocol.Qcom.Vendors.Nothing;

public readonly record struct NothingProjectToken(string Token1, string Token2, string Token3);

public static class NothingTokenCodec
{
    public const string DefaultVerificationHash =
        "16386b4035411a770b12507b2e30297c0c5471230b213e6a1e1e701c6a425150";

    public static NothingProjectToken Create(ulong serial, string projectId, string? token1 = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (projectId.Length > 64)
            throw new ArgumentOutOfRangeException(nameof(projectId));
        token1 ??= CreateRandomToken();
        if (token1.Length != 32 || !IsLowerHex(token1))
            throw new ArgumentException("Nothing token1 must contain 32 lowercase hexadecimal characters.", nameof(token1));

        Span<char> serialChars = stackalloc char[16];
        if (!serial.TryFormat(serialChars, out int serialLength, "x", CultureInfo.InvariantCulture))
            throw new InvalidOperationException("Unable to format the Nothing device serial.");
        int charCount = checked(token1.Length + projectId.Length + serialLength + DefaultVerificationHash.Length);
        char[]? rentedChars = null;
        Span<char> input = charCount <= 256
            ? stackalloc char[charCount]
            : (rentedChars = System.Buffers.ArrayPool<char>.Shared.Rent(charCount));
        byte[]? rentedBytes = null;
        try
        {
            int offset = 0;
            token1.AsSpan().CopyTo(input[offset..]);
            offset += token1.Length;
            projectId.AsSpan().CopyTo(input[offset..]);
            offset += projectId.Length;
            serialChars[..serialLength].CopyTo(input[offset..]);
            offset += serialLength;
            DefaultVerificationHash.AsSpan().CopyTo(input[offset..]);

            int byteCount = Encoding.UTF8.GetByteCount(input);
            Span<byte> bytes = byteCount <= 512
                ? stackalloc byte[byteCount]
                : (rentedBytes = System.Buffers.ArrayPool<byte>.Shared.Rent(byteCount));
            Encoding.UTF8.GetBytes(input, bytes);
            Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
            SHA256.HashData(bytes[..byteCount], digest);
            return new NothingProjectToken(token1, ToLowerHex(digest), DefaultVerificationHash);
        }
        finally
        {
            if (rentedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(rentedBytes);
                System.Buffers.ArrayPool<byte>.Shared.Return(rentedBytes);
            }
            if (rentedChars is not null)
            {
                rentedChars.AsSpan().Clear();
                System.Buffers.ArrayPool<char>.Shared.Return(rentedChars);
            }
        }
    }

    private static string CreateRandomToken()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return ToLowerHex(bytes);
    }

    private static string ToLowerHex(ReadOnlySpan<byte> source)
    {
        if (source.Length > SHA256.HashSizeInBytes)
            throw new ArgumentOutOfRangeException(nameof(source));
        Span<char> destination = stackalloc char[SHA256.HashSizeInBytes * 2];
        const string alphabet = "0123456789abcdef";
        for (int index = 0; index < source.Length; index++)
        {
            destination[index * 2] = alphabet[source[index] >> 4];
            destination[index * 2 + 1] = alphabet[source[index] & 0x0F];
        }
        return new string(destination[..checked(source.Length * 2)]);
    }

    private static bool IsLowerHex(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (!char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f'))
                return false;
        }
        return true;
    }
}
