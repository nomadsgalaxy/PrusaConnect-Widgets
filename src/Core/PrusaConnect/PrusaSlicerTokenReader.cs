using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using PrusaConnect.Core.Storage;

namespace PrusaConnect.Core.PrusaConnect;

/// <summary>
/// Pulls Prusa Connect tokens out of PrusaSlicer's Credential Manager entry.
/// PrusaSlicer keeps them at <c>PrusaSlicer/PrusaAccount/tokens</c>: username =
/// shared_session_key, blob = pipe-joined
/// <c>access_token|refresh_token|next_timeout|master_pid</c>
/// (from PrusaSlicer's UserAccountCommunication.cpp).
/// </summary>
public static class PrusaSlicerTokenReader
{
    public const string TargetName = "PrusaSlicer/PrusaAccount/tokens";

    /// <summary>
    /// Read + parse PrusaSlicer's current tokens. null if it has none stored
    /// (never signed in there).
    /// </summary>
    public static TokenSet? TryRead()
    {
        WindowsCredentialManager.StoredCredential? cred;
        try
        {
            cred = WindowsCredentialManager.Read(TargetName);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PrusaSlicerTokenReader] CredRead threw: {ex.Message}");
            return null;
        }

        if (cred is null)
        {
            Debug.WriteLine("[PrusaSlicerTokenReader] no credential entry found");
            return null;
        }
        if (cred.Blob.Length == 0)
        {
            Debug.WriteLine("[PrusaSlicerTokenReader] credential found but blob is empty");
            return null;
        }
        Debug.WriteLine($"[PrusaSlicerTokenReader] credential found, user={cred.UserName?.Length ?? 0} chars, blobLen={cred.Blob.Length}");

        // wx stores the blob UTF-16LE by default; DecodeBlob sorts out the rest
        string? payload = DecodeBlob(cred.Blob);
        if (string.IsNullOrEmpty(payload))
        {
            return null;
        }

        string[] parts = payload.Split('|');
        if (parts.Length < 2 || string.IsNullOrEmpty(parts[0]))
        {
            return null;
        }

        string accessToken = parts[0];
        string refreshToken = parts[1];
        // parts[2] = next_timeout, parts[3] = master_pid - don't need either,
        // expiry comes from the JWT

        DateTimeOffset expiresAt;
        if (TryReadJwtExp(accessToken, out var jwtExp))
        {
            expiresAt = jwtExp;
        }
        else if (parts.Length >= 3 && long.TryParse(parts[2], out long nextTimeoutRaw)
                 && nextTimeoutRaw > 0)
        {
            // next_timeout is seconds on current PrusaSlicer, but some builds
            // write ms. guess by magnitude: < 1e12 -> seconds, else ms.
            try
            {
                expiresAt = nextTimeoutRaw >= 1_000_000_000_000L
                    ? DateTimeOffset.FromUnixTimeMilliseconds(nextTimeoutRaw)
                    : DateTimeOffset.FromUnixTimeSeconds(nextTimeoutRaw);
            }
            catch (ArgumentOutOfRangeException)
            {
                expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
            }
        }
        else
        {
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        }

        // if expiry parsed to something absurd, assume the parse was junk and
        // give it 5 min so we at least try the token before giving up
        if (expiresAt < DateTimeOffset.UtcNow.AddYears(-5))
        {
            Debug.WriteLine($"[PrusaSlicerTokenReader] parsed expiry {expiresAt:O} is implausible; defaulting to +5min");
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        }

        return new TokenSet(
            AccessToken: accessToken,
            RefreshToken: refreshToken,
            SharedSessionKey: cred.UserName,
            ExpiresAt: expiresAt);
    }

    /// <summary>True if PrusaSlicer has stored tokens (regardless of validity).</summary>
    public static bool IsPrusaSlicerSignedIn()
    {
        try
        {
            var cred = WindowsCredentialManager.Read(TargetName);
            return cred is not null && cred.Blob.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? DecodeBlob(byte[] blob)
    {
        // wxSecretStore wraps the payload with DPAPI (CurrentUser) before
        // writing it. try unwrapping first, then decode.
        byte[]? unwrapped = TryDpapiUnprotect(blob);
        if (unwrapped is not null)
        {
            string? decoded = TryDecodeAsText(unwrapped);
            if (decoded is not null)
            {
                Debug.WriteLine($"[PrusaSlicerTokenReader] decoded via DPAPI unwrap, len={unwrapped.Length}");
                return decoded;
            }
        }

        // older/non-Windows builds store it raw - try UTF-16LE then UTF-8
        string? plain = TryDecodeAsText(blob);
        if (plain is not null)
        {
            Debug.WriteLine($"[PrusaSlicerTokenReader] decoded as plaintext, blobLen={blob.Length}");
            return plain;
        }

        Debug.WriteLine($"[PrusaSlicerTokenReader] could not decode blob (len={blob.Length}, dpapiUnwrap={unwrapped != null})");
        return null;
    }

    private static byte[]? TryDpapiUnprotect(byte[] blob)
    {
        try
        {
            return ProtectedData.Unprotect(blob, optionalEntropy: null, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static string? TryDecodeAsText(byte[] data)
    {
        try
        {
            string utf16 = Encoding.Unicode.GetString(data).TrimEnd('\0');
            if (LooksLikeTokenPayload(utf16)) return utf16;
        }
        catch { }
        try
        {
            string utf8 = Encoding.UTF8.GetString(data).TrimEnd('\0');
            if (LooksLikeTokenPayload(utf8)) return utf8;
        }
        catch { }
        return null;
    }

    private static bool LooksLikeTokenPayload(string s)
    {
        // looks-like-a-token check: pipe-joined, first chunk is a 3-part JWT
        if (string.IsNullOrEmpty(s) || !s.Contains('|')) return false;
        string firstSeg = s.Split('|', 2)[0];
        return firstSeg.Split('.').Length >= 3;
    }

    private static bool TryReadJwtExp(string jwt, out DateTimeOffset expiresAt)
    {
        expiresAt = default;
        string[] parts = jwt.Split('.');
        if (parts.Length < 2) return false;
        try
        {
            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            byte[] bytes = Convert.FromBase64String(payload);
            using var doc = System.Text.Json.JsonDocument.Parse(bytes);
            if (!doc.RootElement.TryGetProperty("exp", out var expEl))
            {
                return false;
            }

            // exp arrives as int or float (Prusa emits a JS-style number) -
            // try int, else double + truncate
            long expUnix;
            if (expEl.TryGetInt64(out expUnix))
            {
                // ok
            }
            else if (expEl.TryGetDouble(out double expDouble) && expDouble > 0)
            {
                expUnix = (long)expDouble;
            }
            else
            {
                return false;
            }

            // spec says seconds; some use ms - magnitude check (~1e10 = yr 2286)
            try
            {
                expiresAt = expUnix >= 10_000_000_000L
                    ? DateTimeOffset.FromUnixTimeMilliseconds(expUnix)
                    : DateTimeOffset.FromUnixTimeSeconds(expUnix);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }
        catch { }
        return false;
    }
}
