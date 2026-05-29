using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PrusaConnect.Core.PrusaConnect;

/// <summary>
/// OAuth/PKCE plumbing for one <see cref="ConnectAccount"/>. Doesn't open
/// browsers or save tokens - that's on the caller. Builds the authorize URL,
/// exchanges codes, refreshes tokens.
/// </summary>
public sealed class PrusaConnectAuth : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public ConnectAccount Account { get; }

    public PrusaConnectAuth(ConnectAccount account, HttpClient? http = null)
    {
        Account = account ?? throw new ArgumentNullException(nameof(account));
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _ownsHttpClient = http is null;
    }

    /// <summary>The browser URL that kicks off the OAuth flow.</summary>
    public Uri BuildAuthorizeUrl(string codeChallenge, string state, string language = "en")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codeChallenge);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);

        var query = new List<KeyValuePair<string, string>>
        {
            new("embed", "1"),
            new("client_id", Account.ClientId),
            new("response_type", "code"),
            new("code_challenge", codeChallenge),
            new("code_challenge_method", "S256"),
            new("scope", ConnectAccountDefaults.Scope),
            new("redirect_uri", ConnectAccountDefaults.RedirectUri),
            new("language", language),
            new("state", state),
        };

        string qs = string.Join('&', query.ConvertAll(
            kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return new Uri($"{Account.AccountBaseUrl}/o/authorize/?{qs}");
    }

    /// <summary>Exchange an authorization <c>code</c> for tokens.</summary>
    public async Task<TokenSet> ExchangeCodeAsync(string code, string codeVerifier, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifier);

        var form = new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = Account.ClientId,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = ConnectAccountDefaults.RedirectUri,
            ["code_verifier"] = codeVerifier,
        };
        return await PostTokenAsync(form, ct).ConfigureAwait(false);
    }

    /// <summary>Trade the long-lived refresh token for a fresh access token.</summary>
    public async Task<TokenSet> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = Account.ClientId,
            ["refresh_token"] = refreshToken,
        };
        return await PostTokenAsync(form, ct).ConfigureAwait(false);
    }

    private async Task<TokenSet> PostTokenAsync(IDictionary<string, string> form, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsync($"{Account.AccountBaseUrl}/o/token/", content, ct)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new PrusaConnectUnreachableException(Account.AccountBaseUrl, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PrusaConnectUnreachableException(Account.AccountBaseUrl, ex);
        }

        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.BadRequest)
            {
                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new PrusaConnectHttpException(resp.StatusCode,
                    $"OAuth token endpoint rejected request ({(int)resp.StatusCode}): {Truncate(body, 200)}");
            }
            if (!resp.IsSuccessStatusCode)
            {
                throw new PrusaConnectHttpException(resp.StatusCode,
                    $"OAuth token endpoint returned HTTP {(int)resp.StatusCode}.");
            }

            var dto = await resp.Content.ReadFromJsonAsync<TokenResponseDto>(cancellationToken: ct)
                .ConfigureAwait(false);
            if (dto is null || string.IsNullOrEmpty(dto.AccessToken))
            {
                throw new PrusaConnectException("OAuth token endpoint returned no access token.");
            }

            // Prefer `expires_in` from the response; fall back to JWT `exp` claim.
            DateTimeOffset expiresAt;
            if (dto.ExpiresIn is { } sec && sec > 0)
            {
                expiresAt = DateTimeOffset.UtcNow.AddSeconds(sec);
            }
            else if (TryReadJwtExp(dto.AccessToken, out var jwtExp))
            {
                expiresAt = jwtExp;
            }
            else
            {
                // neither given - fall back to something conservative
                expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
            }

            return new TokenSet(
                AccessToken: dto.AccessToken,
                RefreshToken: dto.RefreshToken ?? string.Empty,
                SharedSessionKey: dto.SharedSessionKey,
                ExpiresAt: expiresAt);
        }
    }

    private static bool TryReadJwtExp(string jwt, out DateTimeOffset expiresAt)
    {
        expiresAt = default;
        // JWT format: header.payload.signature (base64url-encoded).
        string[] parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return false;
        }
        try
        {
            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            byte[] payloadBytes = Convert.FromBase64String(payload);
            using var doc = System.Text.Json.JsonDocument.Parse(payloadBytes);
            if (doc.RootElement.TryGetProperty("exp", out var expEl)
                && expEl.TryGetInt64(out long expUnix))
            {
                expiresAt = DateTimeOffset.FromUnixTimeSeconds(expUnix);
                return true;
            }
        }
        catch (FormatException) { }
        catch (System.Text.Json.JsonException) { }
        return false;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "…";

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}

public sealed record TokenSet(
    string AccessToken,
    string RefreshToken,
    string? SharedSessionKey,
    DateTimeOffset ExpiresAt)
{
    /// <summary>
    /// True once the token's at/near expiry. Mirrors PrusaSlicer's "renew at 96%
    /// of lifetime" rule.
    /// </summary>
    public bool ShouldRenew(TimeSpan skew = default)
    {
        if (skew == default)
        {
            skew = TimeSpan.FromSeconds(60);
        }
        return DateTimeOffset.UtcNow + skew >= ExpiresAt;
    }
}
