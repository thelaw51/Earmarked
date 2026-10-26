using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace PodcastBookRecs.Tests;

/// <summary>
/// RFC 6238 codes as an authenticator app computes them (SHA-1, 30-second steps, six digits), written out here so
/// the tests check Identity against the standard rather than against itself.
/// </summary>
internal static class Totp
{
    public static string Code(string base32Key, DateTimeOffset at)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, at.ToUnixTimeSeconds() / 30);
#pragma warning disable CA5350 // RFC 6238's default, and what every authenticator app uses.
        var hash = HMACSHA1.HashData(Base32Decode(base32Key), counter);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0F;
        var value = (BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset)) & 0x7FFFFFFF) % 1_000_000;
        return value.ToString("D6", CultureInfo.InvariantCulture);
    }

    private static byte[] Base32Decode(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text.TrimEnd('=').ToUpperInvariant())
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)(buffer >> bits));
            }
        }

        return [.. bytes];
    }
}
