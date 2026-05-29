using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using PrusaConnect.Core.Storage;

namespace PrusaConnect.Core.PrusaConnect;

/// <summary>
/// Per-process access-token cache. The refresh token lives in DPAPI via
/// <see cref="ISecretsStore"/>; this holds the live access token in memory and
/// serializes refreshes so many sessions on one account don't all hit OAuth at
/// once.
/// </summary>
public sealed class ConnectTokenCache
{
    private static readonly ConcurrentDictionary<string, TokenSet> _live = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshLocks = new();

    private readonly ISecretsStore _secrets;

    public ConnectTokenCache(ISecretsStore secrets)
    {
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
    }

    /// <summary>The key under which the refresh token is persisted for an account.</summary>
    public static string RefreshTokenKeyFor(string accountId) => $"connect-{accountId}-refresh";

    /// <summary>
    /// Valid access token for the account, refreshing if the cached one is stale
    /// or missing. Throws <see cref="PrusaConnectAuthRequiredException"/> when
    /// there's no refresh token or the refresh is rejected.
    /// </summary>
    public async Task<string> GetAccessTokenAsync(ConnectAccount account, CancellationToken ct = default)
    {
        if (_live.TryGetValue(account.Id, out var cached) && !cached.ShouldRenew())
        {
            return cached.AccessToken;
        }

        var gate = _refreshLocks.GetOrAdd(account.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // re-check under the lock - someone may have refreshed already
            if (_live.TryGetValue(account.Id, out cached) && !cached.ShouldRenew())
            {
                return cached.AccessToken;
            }

            // main cloud account: re-read PrusaSlicer's current access token from
            // Credential Manager whenever ours goes stale. we do NOT refresh on
            // its behalf - OAuth refresh rotation would kill PrusaSlicer's stored
            // refresh token and log the user out every poll. it keeps its own
            // schedule; we just read what's there.
            if (string.Equals(account.Id, ConnectAccountDefaults.PrusaConnectCloud.Id,
                              StringComparison.Ordinal))
            {
                var fromSlicer = PrusaSlicerTokenReader.TryRead();
                if (fromSlicer is not null && !fromSlicer.ShouldRenew())
                {
                    _live[account.Id] = fromSlicer;
                    return fromSlicer.AccessToken;
                }

                // PrusaSlicer's own token expired - they have to relaunch it to
                // refresh. we don't touch their refresh chain.
                throw new PrusaConnectAuthRequiredException();
            }

            // self-hosted (AFS): use our own OAuth refresh chain
            string? refreshToken = _secrets.Get(RefreshTokenKeyFor(account.Id));
            if (string.IsNullOrEmpty(refreshToken))
            {
                throw new PrusaConnectAuthRequiredException();
            }

            using var auth = new PrusaConnectAuth(account);
            TokenSet fresh;
            try
            {
                fresh = await auth.RefreshAsync(refreshToken, ct).ConfigureAwait(false);
            }
            catch (PrusaConnectHttpException)
            {
                // refresh rejected - the token's dead. drop it so the next call
                // asks for a re-login right away.
                _secrets.Remove(RefreshTokenKeyFor(account.Id));
                _live.TryRemove(account.Id, out _);
                throw new PrusaConnectAuthRequiredException();
            }

            _live[account.Id] = fresh;

            // the endpoint usually rotates the refresh token - save the new one
            // so we don't reuse the spent one next time
            if (!string.IsNullOrEmpty(fresh.RefreshToken)
                && fresh.RefreshToken != refreshToken)
            {
                _secrets.Set(RefreshTokenKeyFor(account.Id), fresh.RefreshToken);
            }

            return fresh.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Seed the cache with a fresh token set, e.g. right after the OAuth code exchange.</summary>
    public void Store(ConnectAccount account, TokenSet tokens)
    {
        _live[account.Id] = tokens;
        if (!string.IsNullOrEmpty(tokens.RefreshToken))
        {
            _secrets.Set(RefreshTokenKeyFor(account.Id), tokens.RefreshToken);
        }
    }

    /// <summary>Sign out - clears in-memory access token and persisted refresh.</summary>
    public void Clear(string accountId)
    {
        _live.TryRemove(accountId, out _);
        _secrets.Remove(RefreshTokenKeyFor(accountId));
    }

    /// <summary>True if we have a refresh token stored - i.e. the user is signed in.</summary>
    public bool IsSignedIn(string accountId)
    {
        return !string.IsNullOrEmpty(_secrets.Get(RefreshTokenKeyFor(accountId)));
    }
}
