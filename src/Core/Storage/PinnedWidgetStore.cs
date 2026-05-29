using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PrusaConnect.Core.Storage;

/// <summary>
/// Index of currently-pinned widgets, keyed by widgetId, at
/// <c>{directory}/pinned-widgets.json</c>. The widget COM server writes it from
/// its session hooks; the settings UI reads it to show which tiles exist and
/// what each is bound to. Atomic .tmp-then-replace writes for cross-process safety.
/// </summary>
public sealed class PinnedWidgetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _filePath;
    private readonly ConcurrentDictionary<string, PinnedWidgetSnapshot> _snapshots =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _writeGate = new();
    private DateTime _lastReadUtc = DateTime.MinValue;

    public PinnedWidgetStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "pinned-widgets.json");
        Load();
    }

    public IReadOnlyList<PinnedWidgetSnapshot> GetAll() => _snapshots.Values
        .OrderBy(s => s.DefinitionId, StringComparer.OrdinalIgnoreCase)
        .ThenBy(s => s.WidgetId, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public void Upsert(PinnedWidgetSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(snapshot.WidgetId)) return;
        _snapshots[snapshot.WidgetId] = snapshot;
        SafeSave();
    }

    public bool Remove(string widgetId)
    {
        if (string.IsNullOrWhiteSpace(widgetId)) return false;
        bool removed = _snapshots.TryRemove(widgetId, out _);
        if (removed) { SafeSave(); }
        return removed;
    }

    public void RefreshIfStale()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            DateTime current = File.GetLastWriteTimeUtc(_filePath);
            if (current > _lastReadUtc)
            {
                Load();
            }
        }
        catch (IOException) { }
    }

    private void Load()
    {
        _snapshots.Clear();
        if (!File.Exists(_filePath))
        {
            _lastReadUtc = DateTime.UtcNow;
            return;
        }
        try
        {
            using var stream = File.OpenRead(_filePath);
            var list = JsonSerializer.Deserialize<List<PinnedWidgetSnapshot>>(stream, JsonOptions);
            if (list is not null)
            {
                foreach (var s in list)
                {
                    if (!string.IsNullOrWhiteSpace(s.WidgetId))
                    {
                        _snapshots[s.WidgetId] = s;
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
                var list = _snapshots.Values.ToList();
                string tmp = _filePath + ".tmp";
                using (var stream = File.Create(tmp))
                {
                    JsonSerializer.Serialize(stream, list, JsonOptions);
                }
                if (File.Exists(_filePath)) { File.Replace(tmp, _filePath, null); }
                else { File.Move(tmp, _filePath); }
                _lastReadUtc = File.GetLastWriteTimeUtc(_filePath);
            }
            catch (IOException) { /* settings UI may be reading; next tick retries */ }
        }
    }
}
