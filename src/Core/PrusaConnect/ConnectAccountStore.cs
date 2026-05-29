using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PrusaConnect.Core.PrusaConnect;

/// <summary>
/// Saved catalogue of <see cref="ConnectAccount"/> entries. Sibling to
/// <c>PrinterStore</c> - same file patterns (atomic writes, stale-detect).
/// </summary>
public sealed class ConnectAccountStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _filePath;
    private readonly Dictionary<string, ConnectAccount> _accounts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private DateTime _lastReadUtc = DateTime.MinValue;

    public ConnectAccountStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "connect-accounts.json");
        Load();
    }

    public IReadOnlyList<ConnectAccount> GetAll()
    {
        lock (_gate)
        {
            return _accounts.Values
                .OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public ConnectAccount? TryGet(string id)
    {
        lock (_gate)
        {
            return _accounts.TryGetValue(id, out var a) ? a : null;
        }
    }

    public void Upsert(ConnectAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        lock (_gate)
        {
            _accounts[account.Id] = account;
            Save();
        }
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            bool removed = _accounts.Remove(id);
            if (removed)
            {
                Save();
            }
            return removed;
        }
    }

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
                lock (_gate)
                {
                    Load();
                }
            }
        }
        catch (IOException) { }
    }

    private void Load()
    {
        _accounts.Clear();
        if (!File.Exists(_filePath))
        {
            _lastReadUtc = DateTime.UtcNow;
            return;
        }

        try
        {
            using var stream = File.OpenRead(_filePath);
            var list = JsonSerializer.Deserialize<List<ConnectAccount>>(stream, JsonOptions);
            if (list is not null)
            {
                foreach (var a in list)
                {
                    if (!string.IsNullOrWhiteSpace(a.Id))
                    {
                        _accounts[a.Id] = a;
                    }
                }
            }
            _lastReadUtc = File.GetLastWriteTimeUtc(_filePath);
        }
        catch (JsonException)
        {
            _lastReadUtc = DateTime.UtcNow;
        }
    }

    private void Save()
    {
        var list = _accounts.Values.ToList();
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
