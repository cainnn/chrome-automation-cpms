using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ChromeAutomation.CpmsExport;

/// <summary>
/// Reads cookies directly from Chrome's SQLite cookie database.
/// Handles Chrome's AES-256-GCM encryption with DPAPI-protected key.
/// </summary>
public static class ChromeCookieReader
{
    public static async Task<string> GetCookiesForDomainAsync(string domain)
    {
        var userDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Google", "Chrome", "User Data");

        var cookieDbPath = Path.Combine(userDataDir, "Default", "Network", "Cookies");
        var localStatePath = Path.Combine(userDataDir, "Local State");

        if (!File.Exists(cookieDbPath)) throw new FileNotFoundException($"Cookie DB not found: {cookieDbPath}");
        if (!File.Exists(localStatePath)) throw new FileNotFoundException($"Local State not found: {localStatePath}");

        // Get encryption key
        var encryptedKey = GetEncryptionKey(localStatePath);

        // Chrome 运行时可能独占 Cookies 文件；先尝试只读直连，再回退到临时副本。
        try
        {
            return await ReadCookiesFromDbAsync(cookieDbPath, encryptedKey, domain);
        }
        catch (Exception ex) when (ex is IOException or SqliteException)
        {
            try
            {
                var tempCopy = Path.Combine(Path.GetTempPath(), $"Cookies_{Guid.NewGuid():N}.db");
                File.Copy(cookieDbPath, tempCopy, overwrite: true);
                try
                {
                    return await ReadCookiesFromDbAsync(tempCopy, encryptedKey, domain);
                }
                finally
                {
                    try { File.Delete(tempCopy); } catch { /* ignore */ }
                }
            }
            catch (Exception copyEx)
            {
                throw new IOException(
                    $"无法读取 Chrome Cookie（直连与副本均失败）: {ex.Message}; copy: {copyEx.Message}",
                    copyEx);
            }
        }
    }

    public static async Task<string> GetCookiesViaExtensionAsync(ChromeAutomation.Client.ChromeController chrome, string url)
    {
        var result = await chrome.CommandAsync("getCookiesForUrl", new { url });
        return result?.TryGetProperty("cookieHeader", out var header) == true
            ? header.GetString() ?? ""
            : "";
    }

    private static byte[] GetEncryptionKey(string localStatePath)
    {
        var json = File.ReadAllText(localStatePath);
        using var doc = JsonDocument.Parse(json);
        var encryptedKeyB64 = doc.RootElement
            .GetProperty("os_crypt")
            .GetProperty("encrypted_key")
            .GetString()!;

        var encryptedKey = Convert.FromBase64String(encryptedKeyB64);

        // Strip "DPAPI" prefix (5 bytes)
        var keyBlob = encryptedKey[5..];

        // Decrypt with DPAPI
        return ProtectedData.Unprotect(keyBlob, null, DataProtectionScope.CurrentUser);
    }

    private static async Task<string> ReadCookiesFromDbAsync(string dbPath, byte[] encryptionKey, string domain)
    {
        var cookies = new List<(string name, string value, string encryptedValue)>();

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT name, encrypted_value, value
            FROM cookies
            WHERE host_key LIKE @domain OR host_key LIKE @domainPrefix";
        cmd.Parameters.AddWithValue("@domain", $"%{domain}");
        cmd.Parameters.AddWithValue("@domainPrefix", $"%.{domain}");

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var name = reader.GetString(0);
            var encValue = reader.GetValue(1) as byte[];
            var plainValue = reader.GetValue(2) as string;

            if (!string.IsNullOrEmpty(plainValue))
            {
                cookies.Add((name, plainValue, ""));
            }
            else if (encValue != null && encValue.Length > 0)
            {
                var decrypted = DecryptCookieValue(encValue, encryptionKey);
                cookies.Add((name, decrypted, ""));
            }
        }

        return string.Join("; ", cookies.Select(c => $"{c.name}={c.value}"));
    }

    private static string DecryptCookieValue(byte[] encryptedValue, byte[] key)
    {
        // Chrome v80+ uses "v10" or "v20" prefix
        var prefix = Encoding.ASCII.GetString(encryptedValue, 0, 3);
        if (prefix != "v10" && prefix != "v20")
        {
            // Older format - DPAPI directly
            try
            {
                return Encoding.UTF8.GetString(
                    ProtectedData.Unprotect(encryptedValue, null, DataProtectionScope.CurrentUser));
            }
            catch
            {
                return "";
            }
        }

        // AES-256-GCM: nonce (12 bytes) + ciphertext + tag (16 bytes)
        var nonce = encryptedValue[3..15];
        var ciphertext = encryptedValue[15..];

        using var aes = new AesGcm(key, 16);
        var plaintext = new byte[ciphertext.Length - 16];
        var tag = ciphertext[^16..];
        var actualCiphertext = ciphertext[..^16];

        aes.Decrypt(nonce, actualCiphertext, tag, plaintext, null);
        return Encoding.UTF8.GetString(plaintext);
    }
}
