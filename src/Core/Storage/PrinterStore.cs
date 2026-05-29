using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using PrusaConnect.Core.Models;

namespace PrusaConnect.Core.Storage;

/// <summary>
/// JSON catalogue of printers at <c>{directory}/printers.json</c>. The settings
/// UI (writer) and the widget COM server (reader) hit it from separate processes
/// - writes are atomic (.tmp + replace) and the in-memory cache reflects the last
/// good Load.
/// </summary>
public sealed class PrinterStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly string _filePath;
    private readonly ReaderWriterLockSlim _gate = new();
    private readonly Dictionary<string, PrinterInfo> _printers = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastReadUtc = DateTime.MinValue;

    public PrinterStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "printers.json");
        Load();
    }

    public string FilePath => _filePath;

    public IReadOnlyList<PrinterInfo> GetAll()
    {
        _gate.EnterReadLock();
        try
        {
            return _printers.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    public PrinterInfo? TryGet(string id)
    {
        _gate.EnterReadLock();
        try
        {
            return _printers.TryGetValue(id, out var p) ? p : null;
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    public PrinterInfo? FirstOrDefault()
    {
        _gate.EnterReadLock();
        try
        {
            return _printers.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    public void Upsert(PrinterInfo printer)
    {
        ArgumentNullException.ThrowIfNull(printer);
        _gate.EnterWriteLock();
        try
        {
            _printers[printer.Id] = printer;
            Save();
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    public bool Remove(string id)
    {
        _gate.EnterWriteLock();
        try
        {
            bool removed = _printers.Remove(id);
            if (removed)
            {
                Save();
            }
            return removed;
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    /// <summary>
    /// Cheap change detector for the reader: if the file's newer than our last
    /// load, reload. Call before each read to pick up the settings app's writes.
    /// </summary>
    public void RefreshIfStale()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }
            DateTime current = File.GetLastWriteTimeUtc(_filePath);
            if (current > _lastReadUtc)
            {
                _gate.EnterWriteLock();
                try
                {
                    Load();
                }
                finally
                {
                    _gate.ExitWriteLock();
                }
            }
        }
        catch (IOException)
        {
            // Reader-side IO failures are non-fatal - keep using the cache.
        }
    }

    private void Load()
    {
        _printers.Clear();
        if (!File.Exists(_filePath))
        {
            _lastReadUtc = DateTime.UtcNow;
            return;
        }

        try
        {
            using var stream = File.OpenRead(_filePath);
            var list = JsonSerializer.Deserialize<List<PrinterInfo>>(stream, JsonOptions);
            if (list is not null)
            {
                foreach (var p in list)
                {
                    if (!string.IsNullOrWhiteSpace(p.Id))
                    {
                        _printers[p.Id] = p;
                    }
                }
            }
            _lastReadUtc = File.GetLastWriteTimeUtc(_filePath);
        }
        catch (JsonException)
        {
            // corrupt file - start clean instead of crashing. settings rewrites
            // it on next save.
            _lastReadUtc = DateTime.UtcNow;
        }
    }

    private void Save()
    {
        var list = _printers.Values.ToList();
        string tmp = _filePath + ".tmp";
        using (var stream = File.Create(tmp))
        {
            JsonSerializer.Serialize(stream, list, JsonOptions);
        }
        if (File.Exists(_filePath))
        {
            File.Replace(tmp, _filePath, null);
        }
        else
        {
            File.Move(tmp, _filePath);
        }
        _lastReadUtc = File.GetLastWriteTimeUtc(_filePath);
    }
}
