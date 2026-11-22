using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace MattsRecords.Services;

internal static class AccountLinkToken
{
    public static (string Code, byte[] Hash) Create()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return (WebEncoders.Base64UrlEncode(bytes), SHA256.HashData(bytes));
    }

    public static bool TryHash(string token, out byte[] hash)
    {
        hash = [];
        if (token.Length != 43 || token.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            return false;
        var bytes = WebEncoders.Base64UrlDecode(token);
        if (bytes.Length != 32 || WebEncoders.Base64UrlEncode(bytes) != token)
            return false;
        hash = SHA256.HashData(bytes);
        return true;
    }
}
