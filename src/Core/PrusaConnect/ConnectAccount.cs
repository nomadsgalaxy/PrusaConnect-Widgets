using System;

namespace PrusaConnect.Core.PrusaConnect;

/// <summary>
/// One Prusa Connect *instance* the user signed into. Cloud Connect and
/// self-hosted farms (AFS) are separate OAuth realms, so each is its own entity
/// - several can coexist, and a PrinterInfo remembers which it came from.
/// </summary>
public sealed record ConnectAccount
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>e.g. <c>https://connect.prusa3d.com</c> - no trailing slash.</summary>
    public required string ConnectBaseUrl { get; init; }

    /// <summary>e.g. <c>https://account.prusa3d.com</c> - the OAuth provider.</summary>
    public required string AccountBaseUrl { get; init; }

    /// <summary>Optional path prefix some self-hosted farms use (e.g. AFS uses <c>/afs</c>).</summary>
    public string? FrontendPathPrefix { get; init; }

    /// <summary>
    /// OAuth client ID. Defaults to PrusaSlicer's published client; overridable
    /// if we ever register our own with Prusa.
    /// </summary>
    public string ClientId { get; init; } = ConnectAccountDefaults.PrusaSlicerClientId;

    /// <summary>The end user's account UUID (from <c>/api/v1/me/</c>).</summary>
    public string? UserUuid { get; init; }

    public string? UserEmail { get; init; }

    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
}

public static class ConnectAccountDefaults
{
    /// <summary>
    /// PrusaSlicer's OAuth client ID. We ride on it for now - inheriting its
    /// redirect URI (<c>prusaslicer://login</c>) and granted permissions - until
    /// we register our own.
    /// </summary>
    public const string PrusaSlicerClientId = "oamhmhZez7opFosnwzElIgE2oGgI2iJORSkw587O";

    public const string RedirectUri = "prusaslicer://login";

    public const string Scope = "basic_info";

    public static readonly ConnectAccount PrusaConnectCloud = new()
    {
        Id = "prusa-connect-cloud",
        DisplayName = "Prusa Connect",
        ConnectBaseUrl = "https://connect.prusa3d.com",
        AccountBaseUrl = "https://account.prusa3d.com",
    };
}
