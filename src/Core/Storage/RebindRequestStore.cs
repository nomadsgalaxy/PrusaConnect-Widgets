using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PrusaConnect.Core.Storage;

/// <summary>
/// Cross-process queue of pending widget rebinds. Settings UI writes;
/// widget COM server reads + clears. Same atomic-write pattern as
/// PinnedWidgetStore. Latest request per widgetId wins.
/// </summary>
public sealed class RebindRequestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _filePath;
    private readonly ConcurrentDictionary<string, RebindRequest> _requests =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _writeGate = new();
    private DateTime _lastReadUtc = DateTime.MinValue;

    public RebindRequestStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "rebind-requests.json");
        Load();
    }

    public void Enqueue(string widgetId, string newPrinterId, string? newKind = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(widgetId);

        // newPrinterId can be empty for a kind-only change (e.g. switching a slot
        // to Farm Status, which isn't printer-scoped). the session applies
        // whichever fields are set.
        _requests[widgetId] = new RebindRequest
        {
            WidgetId = widgetId,
            NewPrinterId = newPrinterId ?? string.Empty,
            NewKind = newKind,
            RequestedAt = DateTimeOffset.UtcNow,
        };
        SafeSave();
    }

    public RebindRequest? TryTake(string widgetId)
    {
        RefreshIfStale();
        if (_requests.TryRemove(widgetId, out var req))
        {
            SafeSave();
            return req;
        }
        return null;
    }

    public IReadOnlyList<RebindRequest> GetAll()
    {
        RefreshIfStale();
        return _requests.Values.ToList();
    }

    public void RefreshIfStale()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            DateTime current = File.GetLastWriteTimeUtc(_filePath);
            if (current > _lastReadUtc) { Load(); }
        }
        catch (IOException) { }
    }

    private void Load()
    {
        _requests.Clear();
        if (!File.Exists(_filePath))
        {
            _lastReadUtc = DateTime.UtcNow;
            return;
        }
        try
        {
            using var stream = File.OpenRead(_filePath);
            var list = JsonSerializer.Deserialize<List<RebindRequest>>(stream, JsonOptions);
            if (list is not null)
            {
                foreach (var r in list)
                {
                    if (!string.IsNullOrWhiteSpace(r.WidgetId) && !string.IsNullOrWhiteSpace(r.NewPrinterId))
                    {
                        _requests[r.WidgetId] = r;
                    }
                }
            }
            _lastReadUtc = File.GetLastWriteTimeUtc(_filePath);
        }
        catch (JsonException) { _lastReadUtc = DateTime.UtcNow; }
    }

    private void SafeSave()
    {
        lock (_writeGate)
        {
            try
            {
                var list = _requests.Values.ToList();
                string tmp = _filePath + ".tmp";
                using (var stream = File.Create(tmp))
                {
                    JsonSerializer.Serialize(stream, list, JsonOptions);
                }
                if (File.Exists(_filePath)) { File.Replace(tmp, _filePath, null); }
                else { File.Move(tmp, _filePath); }
                _lastReadUtc = File.GetLastWriteTimeUtc(_filePath);
            }
            catch (IOException) { /* another process reading - retry next tick */ }
        }
    }
}
