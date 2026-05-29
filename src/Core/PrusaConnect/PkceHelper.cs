using System;
using System.Security.Cryptography;
using System.Text;

namespace PrusaConnect.Core.PrusaConnect;

/// <summary>
/// PKCE (RFC 7636) verifier/challenge generation matching PrusaSlicer's
/// scheme: 40-character [A-Za-z0-9] verifier, base64url-encoded SHA-256 as
/// the challenge.
/// </summary>
public static class PkceHelper
{
    private const string AllowedChars =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    private const int VerifierLength = 40;

    public static (string verifier, string challenge) Generate()
    {
        Span<byte> randomBytes = stackalloc byte[VerifierLength];
        RandomNumberGenerator.Fill(randomBytes);

        var sb = new StringBuilder(VerifierLength);
        for (int i = 0; i < VerifierLength; i++)
        {
            sb.Append(AllowedChars[randomBytes[i] % AllowedChars.Length]);
        }
        string verifier = sb.ToString();

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(verifier));
        string challenge = Convert.ToBase64String(hash)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        return (verifier, challenge);
    }

    public static string GenerateState()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        var sb = new StringBuilder(32);
        foreach (byte b in bytes)
        {
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }
}
