using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrusaConnect.Core.PrusaConnect;

// Wire shapes for the Prusa Connect REST API, captured live from
// GET /app/printers/<uuid> against a Mini-Fermi MK4S (2026-05-28).
//
// No published OpenAPI spec, so shapes drift - fields that vary (team_id,
// job_queue) stay raw JsonElement to keep us forward-compatible.

// ----- /api/v1/me/ (on account.prusa3d.com) -----

internal sealed class MeResponseDto
{
    [JsonPropertyName("uuid")] public string? Uuid { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("public_username")] public string? PublicUsername { get; set; }
    [JsonPropertyName("default_team_id")] public int? DefaultTeamId { get; set; }
}

// ----- OAuth /o/token/ -----

internal sealed class TokenResponseDto
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    [JsonPropertyName("shared_session_key")] public string? SharedSessionKey { get; set; }
    [JsonPropertyName("expires_in")] public int? ExpiresIn { get; set; }
    [JsonPropertyName("token_type")] public string? TokenType { get; set; }
    [JsonPropertyName("scope")] public string? Scope { get; set; }
}

// ----- /slicer/status (lightweight bulk poll) -----

public sealed class SlicerStatusItemDto
{
    [JsonPropertyName("printer_uuid")] public string PrinterUuid { get; set; } = string.Empty;
    [JsonPropertyName("printer_state")] public string PrinterState { get; set; } = string.Empty;
}

// ----- /slicer/v1/printers (printer list, light) -----

internal sealed class SlicerPrintersResponseDto
{
    [JsonPropertyName("printers")] public List<SlicerPrinterDto> Printers { get; set; } = new();
}

public sealed class SlicerPrinterDto
{
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = string.Empty;
    [JsonPropertyName("printer_model")] public string? PrinterModel { get; set; }
    [JsonPropertyName("printer_type_name")] public string? PrinterTypeName { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("location")] public string? Location { get; set; }
    // team_id / organization_id are usually numbers, but farm/team accounts have
    // sent arrays/objects/odd strings that broke strict parsing. we don't bind
    // these (TeamName groups the list), so keep them raw for forward-compat.
    [JsonPropertyName("team_id")] public JsonElement? TeamId { get; set; }
    [JsonPropertyName("team_name")] public string? TeamName { get; set; }
}

// ----- /app/printers (paginated fleet list - includes team info) -----

public sealed class AppPrintersResponseDto
{
    [JsonPropertyName("printers")] public List<AppPrinterDto> Printers { get; set; } = new();
    [JsonPropertyName("pager")] public PagerDto? Pager { get; set; }
}

public sealed class PagerDto
{
    [JsonPropertyName("limit")] public int? Limit { get; set; }
    [JsonPropertyName("offset")] public int? Offset { get; set; }
    [JsonPropertyName("total")] public int? Total { get; set; }
}

/// <summary>
/// One row from <c>/app/printers</c>. Lighter than the full detail blob but has
/// team metadata - the reason we use it over /slicer/v1/printers.
/// </summary>
public sealed class AppPrinterDto
{
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("printer_model")] public string? PrinterModel { get; set; }
    [JsonPropertyName("printer_type_name")] public string? PrinterTypeName { get; set; }
    [JsonPropertyName("location")] public string? Location { get; set; }
    [JsonPropertyName("sn")] public string? Serial { get; set; }
    [JsonPropertyName("firmware")] public string? Firmware { get; set; }
    // team_id / organization_id are usually numbers, but farm/team accounts have
    // sent arrays/objects/odd strings that broke strict parsing. we don't bind
    // these (TeamName groups the list), so keep them raw for forward-compat.
    [JsonPropertyName("team_id")] public JsonElement? TeamId { get; set; }
    [JsonPropertyName("team_name")] public string? TeamName { get; set; }
    [JsonPropertyName("organization_id")] public JsonElement? OrganizationId { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("printer_state")] public string? PrinterState { get; set; }
    [JsonPropertyName("connect_state")] public string? ConnectState { get; set; }
    [JsonPropertyName("last_online")] public double? LastOnline { get; set; }
    [JsonPropertyName("is_beta")] public bool? IsBeta { get; set; }
}

// ----- /app/printers/<uuid> (THE goldmine - full detail per printer) -----

public sealed class ConnectPrinterDetailDto
{
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("location")] public string? Location { get; set; }
    [JsonPropertyName("sn")] public string? Serial { get; set; }
    [JsonPropertyName("printer_model")] public string? PrinterModel { get; set; }
    [JsonPropertyName("printer_type_name")] public string? PrinterTypeName { get; set; }
    [JsonPropertyName("firmware")] public string? Firmware { get; set; }
    [JsonPropertyName("nozzle_diameter")] public double? NozzleDiameter { get; set; }
    // team_id / organization_id are usually numbers, but farm/team accounts have
    // sent arrays/objects/odd strings that broke strict parsing. we don't bind
    // these (TeamName groups the list), so keep them raw for forward-compat.
    [JsonPropertyName("team_id")] public JsonElement? TeamId { get; set; }
    [JsonPropertyName("team_name")] public string? TeamName { get; set; }
    [JsonPropertyName("organization_id")] public JsonElement? OrganizationId { get; set; }
    [JsonPropertyName("is_beta")] public bool? IsBeta { get; set; }

    // Three(!) state fields - UI shows `state` as the big badge.
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("state_reason")] public string? StateReason { get; set; }
    [JsonPropertyName("printer_state")] public string? PrinterState { get; set; }
    [JsonPropertyName("connect_state")] public string? ConnectState { get; set; }

    [JsonPropertyName("last_online")] public double? LastOnline { get; set; }
    [JsonPropertyName("time_delta")] public double? TimeDelta { get; set; }
    [JsonPropertyName("inaccurate_estimates")] public bool? InaccurateEstimates { get; set; }

    [JsonPropertyName("axis_x")] public double? AxisX { get; set; }
    [JsonPropertyName("axis_y")] public double? AxisY { get; set; }
    [JsonPropertyName("axis_z")] public double? AxisZ { get; set; }
    [JsonPropertyName("flow")] public int? Flow { get; set; }
    [JsonPropertyName("speed")] public int? Speed { get; set; }

    [JsonPropertyName("temp")] public ConnectTempDto? Temp { get; set; }
    [JsonPropertyName("network_info")] public ConnectNetworkInfoDto? NetworkInfo { get; set; }
    [JsonPropertyName("job_info")] public ConnectJobInfoDto? JobInfo { get; set; }
    [JsonPropertyName("filament")] public ConnectFilamentDto? Filament { get; set; }
    [JsonPropertyName("tools")] public Dictionary<string, ConnectToolDto>? Tools { get; set; }
    [JsonPropertyName("flags")] public ConnectFlagsDto? Flags { get; set; }
    [JsonPropertyName("support")] public ConnectSupportDto? Support { get; set; }
    [JsonPropertyName("allowed_functionalities")] public List<string>? AllowedFunctionalities { get; set; }
    [JsonPropertyName("supported_printer_models")] public List<string>? SupportedPrinterModels { get; set; }

    // the creds we auto-import
    [JsonPropertyName("prusalink_api_key")] public string? PrusaLinkApiKey { get; set; }
    [JsonPropertyName("api_key")] public string? ConnectApiKey { get; set; }
    [JsonPropertyName("prusaconnect_api_key")] public string? PrusaConnectApiKey { get; set; }

    // Permissions for the calling user on this printer.
    [JsonPropertyName("rights_r")] public bool? RightsRead { get; set; }
    [JsonPropertyName("rights_w")] public bool? RightsWrite { get; set; }
    [JsonPropertyName("rights_u")] public bool? RightsUpdate { get; set; }
}

public sealed class ConnectTempDto
{
    [JsonPropertyName("temp_nozzle")] public double? TempNozzle { get; set; }
    [JsonPropertyName("target_nozzle")] public double? TargetNozzle { get; set; }
    [JsonPropertyName("temp_bed")] public double? TempBed { get; set; }
    [JsonPropertyName("target_bed")] public double? TargetBed { get; set; }
    [JsonPropertyName("temp_chamber")] public double? TempChamber { get; set; }
    [JsonPropertyName("target_chamber")] public double? TargetChamber { get; set; }
}

public sealed class ConnectNetworkInfoDto
{
    [JsonPropertyName("hostname")] public string? Hostname { get; set; }
    [JsonPropertyName("lan_ipv4")] public string? LanIpv4 { get; set; }
    [JsonPropertyName("lan_mac")] public string? LanMac { get; set; }
    [JsonPropertyName("wifi_ipv4")] public string? WifiIpv4 { get; set; }
    [JsonPropertyName("wifi_mac")] public string? WifiMac { get; set; }
    [JsonPropertyName("wifi_ssid")] public string? WifiSsid { get; set; }

    /// <summary>Best-guess reachable IP - Wi-Fi takes precedence if present.</summary>
    public string? PreferredIp => !string.IsNullOrWhiteSpace(WifiIpv4) ? WifiIpv4
                                : !string.IsNullOrWhiteSpace(LanIpv4) ? LanIpv4
                                : null;
}

public sealed class ConnectJobInfoDto
{
    [JsonPropertyName("id")] public long? Id { get; set; }
    [JsonPropertyName("origin_id")] public long? OriginId { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("hash")] public string? Hash { get; set; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    [JsonPropertyName("lifetime_id")] public string? LifetimeId { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("progress")] public double? Progress { get; set; }
    [JsonPropertyName("start")] public double? Start { get; set; }
    [JsonPropertyName("end")] public double? End { get; set; }
    [JsonPropertyName("time_remaining")] public double? TimeRemaining { get; set; }
    [JsonPropertyName("time_printing")] public double? TimePrinting { get; set; }
    [JsonPropertyName("model_weight")] public double? ModelWeight { get; set; }
    [JsonPropertyName("weight_remaining")] public double? WeightRemaining { get; set; }
    [JsonPropertyName("print_height")] public double? PrintHeight { get; set; }
    [JsonPropertyName("total_height")] public double? TotalHeight { get; set; }
    [JsonPropertyName("preview_url")] public string? PreviewUrl { get; set; }
}

public sealed class ConnectFilamentDto
{
    [JsonPropertyName("material")] public string? Material { get; set; }
}

public sealed class ConnectToolDto
{
    [JsonPropertyName("temp")] public double? Temp { get; set; }
    [JsonPropertyName("active")] public bool? Active { get; set; }
    [JsonPropertyName("nozzle_diameter")] public double? NozzleDiameter { get; set; }
    [JsonPropertyName("material")] public string? Material { get; set; }
    [JsonPropertyName("high_flow")] public bool? HighFlow { get; set; }
    [JsonPropertyName("hardened")] public bool? Hardened { get; set; }
}

// ----- /app/printers/<uuid>/cameras -----

public sealed class ConnectCamerasResponseDto
{
    [JsonPropertyName("cameras")] public List<ConnectCameraDto> Cameras { get; set; } = new();
}

public sealed class ConnectCameraDto
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("sort_order")] public int? SortOrder { get; set; }
    [JsonPropertyName("registered")] public bool? Registered { get; set; }
    [JsonPropertyName("origin")] public string? Origin { get; set; }
}

public sealed class ConnectFlagsDto
{
    // job_queue is a number on idle printers but real accounts have returned
    // arrays/objects when busy (Apollo: an array of queued jobs). we don't bind
    // it, so keep it raw for forward-compat.
    [JsonPropertyName("job_queue")] public JsonElement? JobQueue { get; set; }
    [JsonPropertyName("last_info_event")] public double? LastInfoEvent { get; set; }
}

public sealed class ConnectSupportDto
{
    [JsonPropertyName("stable")] public string? Stable { get; set; }
    [JsonPropertyName("prerelease")] public string? Prerelease { get; set; }
    [JsonPropertyName("unsupported")] public string? Unsupported { get; set; }
    [JsonPropertyName("current")] public string? Current { get; set; }
    [JsonPropertyName("release")] public string? Release { get; set; }
    [JsonPropertyName("latest")] public string? Latest { get; set; }
    [JsonPropertyName("release_url")] public string? ReleaseUrl { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
}
