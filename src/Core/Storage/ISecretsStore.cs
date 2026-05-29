namespace PrusaConnect.Core.Storage;

/// <summary>
/// Abstraction over the platform's secret storage. Default uses Windows DPAPI;
/// the Widget could swap in PasswordVault for stronger isolation.
/// </summary>
public interface ISecretsStore
{
    string? Get(string key);
    void Set(string key, string value);
    void Remove(string key);
}
