using System.Text.Json.Serialization;

namespace PrusaConnect.Core.PrusaLink;

// JSON shapes matching the PrusaLink OpenAPI spec at:
//   https://github.com/prusa3d/Prusa-Link-Web/blob/master/spec/openapi.yaml
// Field names use the wire format (snake_case) so JsonSerializer can bind
// without a naming policy. All non-required fields are nullable.

internal sealed class StatusResponseDto
{
    [JsonPropertyName("printer")]
    public StatusPrinterDto Printer { get; set; } = new();

    [JsonPropertyName("job")]
    public StatusJobDto? Job { get; set; }

    [JsonPropertyName("storage")]
    public StatusStorageDto? Storage { get; set; }
}

internal sealed class StatusPrinterDto
{
    [JsonPropertyName("state")]
    public string State { get; set; } = "UNKNOWN";

    [JsonPropertyName("temp_nozzle")]
    public double? TempNozzle { get; set; }

    [JsonPropertyName("target_nozzle")]
    public double? TargetNozzle { get; set; }

    [JsonPropertyName("temp_bed")]
    public double? TempBed { get; set; }

    [JsonPropertyName("target_bed")]
    public double? TargetBed { get; set; }

    [JsonPropertyName("axis_z")]
    public double? AxisZ { get; set; }

    [JsonPropertyName("flow")]
    public int? Flow { get; set; }

    [JsonPropertyName("speed")]
    public int? Speed { get; set; }
}

internal sealed class StatusJobDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("progress")]
    public double Progress { get; set; }

    [JsonPropertyName("time_remaining")]
    public int? TimeRemaining { get; set; }

    [JsonPropertyName("time_printing")]
    public int? TimePrinting { get; set; }
}

internal sealed class StatusStorageDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }
}

internal sealed class JobResponseDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("progress")]
    public double Progress { get; set; }

    [JsonPropertyName("time_remaining")]
    public int? TimeRemaining { get; set; }

    [JsonPropertyName("time_printing")]
    public int? TimePrinting { get; set; }

    [JsonPropertyName("file")]
    public JobFileDto? File { get; set; }
}

internal sealed class JobFileDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }
}

public sealed class InfoResponseDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("hostname")]
    public string? Hostname { get; set; }

    [JsonPropertyName("serial")]
    public string? Serial { get; set; }

    [JsonPropertyName("nozzle_diameter")]
    public double? NozzleDiameter { get; set; }

    [JsonPropertyName("mmu")]
    public bool? Mmu { get; set; }

    [JsonPropertyName("location")]
    public string? Location { get; set; }

    [JsonPropertyName("farm_mode")]
    public bool? FarmMode { get; set; }
}
