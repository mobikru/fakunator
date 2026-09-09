using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fakunator.Core.Server;

/// <summary>
/// Хранит список настроенных серверов в %APPDATA%\Fakunator\servers.json.
/// Пароли шифруются DPAPI (CurrentUser scope — расшифровать может только тот же винд-юзер).
/// </summary>
public sealed class ServerRegistry
{
    private static readonly string RegistryDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Fakunator");
    private static readonly string RegistryFile = Path.Combine(RegistryDir, "servers.json");

    private readonly List<ServerConfig> _servers;

    public IReadOnlyList<ServerConfig> All => _servers;

    public event Action? Changed;

    public ServerRegistry()
    {
        _servers = LoadFromDisk();
    }

    public ServerConfig? Get(string id) => _servers.FirstOrDefault(s => s.Id == id);

    public void Add(ServerConfig s)
    {
        _servers.RemoveAll(x => x.Id == s.Id); // upsert
        _servers.Add(s);
        SaveToDisk();
        Changed?.Invoke();
    }

    public void Remove(string id)
    {
        var removed = _servers.RemoveAll(x => x.Id == id) > 0;
        if (removed) { SaveToDisk(); Changed?.Invoke(); }
    }

    public void Update(ServerConfig s) => Add(s);

    // ── DPAPI helpers ──────────────────────────────────────
    public static string EncryptPassword(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var enc = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(enc);
    }

    public static string DecryptPassword(string encBase64)
    {
        if (string.IsNullOrEmpty(encBase64)) return "";
        try
        {
            var enc = Convert.FromBase64String(encBase64);
            var bytes = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return ""; }
    }

    // ── Persistence ────────────────────────────────────────
    private List<ServerConfig> LoadFromDisk()
    {
        try
        {
            if (!File.Exists(RegistryFile)) return new List<ServerConfig>();
            var json = File.ReadAllText(RegistryFile);
            var list = JsonSerializer.Deserialize<List<ServerConfig>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return list ?? new List<ServerConfig>();
        }
        catch { return new List<ServerConfig>(); }
    }

    private void SaveToDisk()
    {
        try
        {
            Directory.CreateDirectory(RegistryDir);
            var json = JsonSerializer.Serialize(_servers,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(RegistryFile, json);
        }
        catch { /* best effort */ }
    }
}
