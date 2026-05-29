using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PrusaConnect.Core.Storage;

/// <summary>
/// DPAPI secrets store - each key is a per-user-encrypted file under the given
/// directory. Whether it survives an MSIX uninstall depends on the directory you
/// pass (package-scoped: no; persistent: yes).
/// </summary>
public sealed class DpapiSecretsStore : ISecretsStore
{
    private readonly string _directory;

    public DpapiSecretsStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public string? Get(string key)
    {
        string path = PathFor(key);
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] encrypted = File.ReadAllBytes(path);
        byte[] decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(decrypted);
    }

    public void Set(string key, string value)
    {
        byte[] data = Encoding.UTF8.GetBytes(value);
        byte[] encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);

        string path = PathFor(key);
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, encrypted);
        if (File.Exists(path))
        {
            File.Replace(tmp, path, null);
        }
        else
        {
            File.Move(tmp, path);
        }
    }

    public void Remove(string key)
    {
        string path = PathFor(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string PathFor(string key)
    {
        // Sanitise the key to make it safe as a filename component.
        Span<char> safe = stackalloc char[key.Length];
        for (int i = 0; i < key.Length; i++)
        {
            char c = key[i];
            safe[i] = char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_';
        }
        return Path.Combine(_directory, new string(safe) + ".secret");
    }
}
