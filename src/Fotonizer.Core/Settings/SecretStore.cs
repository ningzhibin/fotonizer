using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fotonizer.Core.Logging;

namespace Fotonizer.Core.Settings;

public sealed class GoogleCredentials
{
    public string? RefreshToken { get; set; }
    public string? ClientSecret { get; set; }
    public string? AccountEmail { get; set; }
}

/// <summary>
/// Stores the Google OAuth refresh token + client secret encrypted with DPAPI
/// (current Windows user), so a copied file cannot be replayed by another user.
/// </summary>
public sealed class SecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Fotonizer.v1.google");
    private readonly object _gate = new();
    private readonly string _path;

    public GoogleCredentials Credentials { get; private set; } = new();

    public SecretStore(string? path = null)
    {
        _path = path ?? AppPaths.DefaultSecretsPath;
        Load();
    }

    public bool HasRefreshToken => !string.IsNullOrWhiteSpace(Credentials.RefreshToken);

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var protectedBytes = File.ReadAllBytes(_path);
            var plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            var creds = JsonSerializer.Deserialize<GoogleCredentials>(plain);
            if (creds != null) Credentials = creds;
        }
        catch (Exception ex)
        {
            AppLog.Shared.Warn($"读取凭据失败（需要重新授权 Google 照片）：{ex.Message}");
            Credentials = new GoogleCredentials();
        }
    }

    public void Update(Action<GoogleCredentials> mutate)
    {
        lock (_gate)
        {
            mutate(Credentials);
            Save();
        }
    }

    public void SetRefreshToken(string? refreshToken)
    {
        lock (_gate)
        {
            Credentials.RefreshToken = refreshToken;
            Save();
        }
    }

    public void SetClientSecret(string? secret)
    {
        lock (_gate)
        {
            Credentials.ClientSecret = secret;
            Save();
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var plain = JsonSerializer.SerializeToUtf8Bytes(Credentials);
            var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, encrypted);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Shared.Error($"保存凭据失败：{ex.Message}", ex);
        }
    }
}
