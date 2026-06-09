using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Recall.Server.Rohlik;

/// <summary>
/// Minimal RFC 6238 TOTP implementation for payment confirmation.
/// </summary>
public static class Totp
{
    public static string Generate(string base32Secret, int digits = 6, int timeStep = 30)
    {
        var key = Base32Decode(base32Secret);
        var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / timeStep;
        var counterBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(counter));

        var hmac = HMACSHA1.HashData(key, counterBytes);
        var offset = hmac[^1] & 0x0F;
        var code = ((hmac[offset] & 0x7F) << 24)
                 | (hmac[offset + 1] << 16)
                 | (hmac[offset + 2] << 8)
                 | hmac[offset + 3];

        return (code % (int)Math.Pow(10, digits)).ToString().PadLeft(digits, '0');
    }

    public static bool Verify(string base32Secret, string token, int window = 1)
    {
        var key = Base32Decode(base32Secret);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var tokenBytes = Encoding.UTF8.GetBytes(token);
        var match = false;

        for (var i = -window; i <= window; i++)
        {
            var counter = now + i;
            var counterBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(counter));
            var hmac = HMACSHA1.HashData(key, counterBytes);
            var offset = hmac[^1] & 0x0F;
            var code = ((hmac[offset] & 0x7F) << 24)
                     | (hmac[offset + 1] << 16)
                     | (hmac[offset + 2] << 8)
                     | hmac[offset + 3];
            var expected = Encoding.UTF8.GetBytes(
                (code % 1_000_000).ToString().PadLeft(6, '0'));
            if (CryptographicOperations.FixedTimeEquals(expected, tokenBytes))
                match = true;
        }
        return match;
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        input = input.TrimEnd('=').ToUpperInvariant();

        var bits = 0;
        var value = 0;
        var output = new List<byte>();

        foreach (var c in input)
        {
            var idx = alphabet.IndexOf(c);
            if (idx < 0) continue;
            value = (value << 5) | idx;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(value >> (bits - 8)));
                bits -= 8;
                value &= (1 << bits) - 1;
            }
        }

        return output.ToArray();
    }
}
