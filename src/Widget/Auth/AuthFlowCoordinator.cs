using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PrusaConnect.Widget.Diagnostics;

namespace PrusaConnect.Widget.Auth;

/// <summary>
/// Process-wide state for an in-flight OAuth sign-in. The UI calls
/// <see cref="BeginFlow"/> before opening the browser; the protocol-activation
/// handler calls <see cref="CompleteFlow"/> on redirect back. One flow at a
/// time - all the UI needs.
/// </summary>
internal static class AuthFlowCoordinator
{
    private static readonly object _gate = new();
    private static PendingFlow? _current;

    /// <summary>State the UI needs to know about a flow it just kicked off.</summary>
    public sealed record PendingFlow(
        string AccountId,
        string CodeVerifier,
        string State,
        TaskCompletionSource<AuthResult> Tcs);

    /// <summary>What the activation handler hands back to the UI.</summary>
    public sealed record AuthResult(string Code, string State);

    public static PendingFlow BeginFlow(string accountId, string codeVerifier, string state)
    {
        var pending = new PendingFlow(
            accountId, codeVerifier, state,
            new TaskCompletionSource<AuthResult>(TaskCreationOptions.RunContinuationsAsynchronously));

        lock (_gate)
        {
            // cancel any earlier flow - the user restarted sign-in
            _current?.Tcs.TrySetCanceled();
            _current = pending;
        }
        Log.Write($"AuthFlow: started for accountId={accountId} state={state}");
        return pending;
    }

    public static void CompleteFlow(string code, string state)
    {
        PendingFlow? pending;
        lock (_gate)
        {
            pending = _current;
            _current = null;
        }
        if (pending is null)
        {
            Log.Write("AuthFlow: redirect received but no flow is pending - ignoring.");
            return;
        }
        if (!string.Equals(pending.State, state, StringComparison.Ordinal))
        {
            Log.Write($"AuthFlow: state mismatch (expected={pending.State} got={state}) - rejecting.");
            pending.Tcs.TrySetException(
                new InvalidOperationException("OAuth state parameter mismatch."));
            return;
        }
        Log.Write("AuthFlow: completed with code");
        pending.Tcs.TrySetResult(new AuthResult(code, state));
    }

    public static void CancelFlow()
    {
        lock (_gate)
        {
            _current?.Tcs.TrySetCanceled();
            _current = null;
        }
    }

    /// <summary>
    /// Parse the redirect URI (<c>prusaslicer://login?code=xxx&amp;state=yyy</c>)
    /// and complete the pending flow. True if it was a redirect we acted on.
    /// </summary>
    public static bool TryHandleRedirect(Uri uri)
    {
        if (uri is null) return false;
        if (!string.Equals(uri.Scheme, "prusaslicer", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        string? code = query["code"];
        string? state = query["state"];
        string? error = query["error"];

        if (!string.IsNullOrEmpty(error))
        {
            Log.Write($"AuthFlow: redirect contained error={error}");
            lock (_gate)
            {
                _current?.Tcs.TrySetException(
                    new InvalidOperationException($"OAuth error: {error}"));
                _current = null;
            }
            return true;
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            Log.Write("AuthFlow: redirect missing code or state");
            return false;
        }

        CompleteFlow(code, state);
        return true;
    }
}
