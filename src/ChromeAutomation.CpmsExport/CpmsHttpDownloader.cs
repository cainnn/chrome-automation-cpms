using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ChromeAutomation.Client;

namespace ChromeAutomation.CpmsExport;

/// <summary>
/// C# 直连 CPMS 下载 API（使用 Chrome Cookie），作为扩展解析失败时的兜底。
/// </summary>
internal static class CpmsHttpDownloader
{
    private const string BaseUrl = "http://cpms.hq.cmcc";

    private static readonly string[] ListPaths =
    [
        "/cpms/mops/mops/attachmentDownload/v1/getAttachmentDownloadInfoList",
        "/cpms/mops/mops/v1/getAttachmentDownloadInfoList",
        "/pms/mops/mops/v1/getAttachmentDownloadInfoList",
    ];

    private static readonly string[] DownloadPaths =
    [
        "/cpms/mops/mops/attachmentDownload/v1/downloadAttachment",
        "/cpms/mops/mops/v1/downloadAttachment",
        "/pms/mops/mops/v1/downloadAttachment",
        "/cpms/mops/mops/v1/download",
        "/cpms/mops/mops/v1/file/download",
    ];

    public static async Task<string?> TryDownloadBySerialAsync(
        string serialNumber,
        string downloadsDir,
        DateTime notBeforeUtc,
        ChromeController? chrome = null)
    {
        if (string.IsNullOrWhiteSpace(serialNumber))
        {
            return null;
        }

        Console.WriteLine("[6/7] C# HTTP 直连下载（Cookie 兜底）...");
        string cookieHeader = "";
        if (chrome is not null)
        {
            try
            {
                cookieHeader = await ChromeCookieReader.GetCookiesViaExtensionAsync(chrome, BaseUrl + "/");
                Console.WriteLine($"[6/7] 扩展 Cookie 长度: {cookieHeader.Length}");
            }
            catch (Exception extEx)
            {
                Console.WriteLine($"[6/7] 扩展 Cookie 失败: {extEx.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            try
            {
                cookieHeader = await ChromeCookieReader.GetCookiesForDomainAsync("cpms.hq.cmcc");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[6/7] Cookie 文件读取失败: {ex.Message}");
                return null;
            }
        }

        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            Console.WriteLine("[6/7] 无法获取 CPMS Cookie，跳过 HTTP 兜底");
            return null;
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.Add("Cookie", cookieHeader);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

        JsonElement? matchedRow = null;
        foreach (var listPath in ListPaths)
        {
            foreach (var body in BuildListBodies(serialNumber))
            {
                try
                {
                    using var content = new StringContent(
                        JsonSerializer.Serialize(body),
                        Encoding.UTF8,
                        "application/json");
                    var response = await client.PostAsync(BaseUrl + listPath, content);
                    var text = await response.Content.ReadAsStringAsync();
                    Console.WriteLine(
                        $"[6/7] 列表 API {listPath} status={(int)response.StatusCode} preview={text[..Math.Min(text.Length, 200)]}");
                    if (!response.IsSuccessStatusCode)
                    {
                        continue;
                    }

                    using var doc = JsonDocument.Parse(text);
                    matchedRow = FindRowBySerial(doc.RootElement, serialNumber);
                    if (matchedRow.HasValue)
                    {
                        Console.WriteLine($"[6/7] 匹配行: {matchedRow.Value.GetRawText()[..Math.Min(matchedRow.Value.GetRawText().Length, 300)]}");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[6/7] 列表 API 失败 {listPath}: {ex.Message}");
                }
            }

            if (matchedRow.HasValue)
            {
                break;
            }
        }

        var attempts = BuildDownloadAttempts(serialNumber, matchedRow);
        foreach (var attempt in attempts)
        {
            try
            {
                var path = await ExecuteDownloadAttemptAsync(client, attempt, downloadsDir, serialNumber, notBeforeUtc);
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    Console.WriteLine($"[6/7] HTTP 下载成功: {path}");
                    return path;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[6/7] HTTP 尝试失败 {attempt.Method} {attempt.Url}: {ex.Message}");
            }
        }

        return null;
    }

    private static IEnumerable<object> BuildListBodies(string serialNumber)
    {
        yield return new { pageNum = 1, pageSize = 50 };
        yield return new { pageNum = 1, pageSize = 20 };
        yield return new { businessSerialNumber = serialNumber };
        yield return new { serialNumber };
        yield return new { businessFlowCode = serialNumber };
        yield return new { pageNum = 1, pageSize = 20, businessSerialNumber = serialNumber };
    }

    private static JsonElement? FindRowBySerial(JsonElement root, string serialNumber)
    {
        foreach (var array in CollectObjectArrays(root))
        {
            foreach (var item in array.EnumerateArray())
            {
                if (ElementContainsSerial(item, serialNumber))
                {
                    return item;
                }
            }
        }

        return null;
    }

    private static bool ElementContainsSerial(JsonElement element, string serialNumber)
    {
        try
        {
            return element.GetRawText().Contains(serialNumber, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<JsonElement> CollectObjectArrays(JsonElement root)
    {
        var stack = new Stack<JsonElement>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current.ValueKind == JsonValueKind.Array)
            {
                var allObjects = true;
                foreach (var item in current.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        allObjects = false;
                        break;
                    }
                }

                if (current.GetArrayLength() > 0 && allObjects)
                {
                    yield return current;
                }

                foreach (var item in current.EnumerateArray())
                {
                    stack.Push(item);
                }
            }
            else if (current.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in current.EnumerateObject())
                {
                    stack.Push(prop.Value);
                }
            }
        }
    }

    private sealed record DownloadAttempt(string Url, string Method, object? Body);

    private static List<DownloadAttempt> BuildDownloadAttempts(string serialNumber, JsonElement? row)
    {
        var attempts = new List<DownloadAttempt>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string url, string method, object? body = null)
        {
            var key = $"{method}:{url}";
            if (!seen.Add(key))
            {
                return;
            }

            attempts.Add(new DownloadAttempt(url, method, body));
        }

        if (row.HasValue)
        {
            foreach (var field in new[]
                     {
                         "fileUrl", "filePath", "downloadUrl", "attachmentUrl", "annexUrl", "url", "path", "fullPath",
                     })
            {
                if (TryGetString(row.Value, field, out var value) && value.Length > 3)
                {
                    var abs = value.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? value
                        : BaseUrl + (value.StartsWith('/') ? value : "/" + value);
                    Add(abs, "GET");
                }
            }

            foreach (var idField in new[]
                     {
                         "id", "attachmentId", "fileId", "downloadId", "recordId", "businessId", "attachmentDownloadId",
                     })
            {
                if (!TryGetString(row.Value, idField, out var idValue))
                {
                    continue;
                }

                foreach (var path in DownloadPaths)
                {
                    var url = BaseUrl + path;
                    Add(url, "POST", new Dictionary<string, string> { [idField] = idValue });
                    Add(url, "POST", new Dictionary<string, string> { ["id"] = idValue });
                    Add($"{url}?id={Uri.EscapeDataString(idValue)}", "GET");
                    Add($"{url}?{idField}={Uri.EscapeDataString(idValue)}", "GET");
                }
            }
        }

        foreach (var path in DownloadPaths)
        {
            var url = BaseUrl + path;
            Add(url, "POST", new { businessSerialNumber = serialNumber });
            Add(url, "POST", new { serialNumber });
            Add(url, "POST", new { businessFlowCode = serialNumber });
        }

        return attempts;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = "";
        if (!element.TryGetProperty(propertyName, out var prop))
        {
            return false;
        }

        value = prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString() ?? "",
            JsonValueKind.Number => prop.GetRawText(),
            _ => "",
        };
        return !string.IsNullOrWhiteSpace(value);
    }

    private static async Task<string?> ExecuteDownloadAttemptAsync(
        HttpClient client,
        DownloadAttempt attempt,
        string downloadsDir,
        string serialNumber,
        DateTime notBeforeUtc)
    {
        using var request = new HttpRequestMessage(
            attempt.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) ? HttpMethod.Post : HttpMethod.Get,
            attempt.Url);

        if (attempt.Body is not null &&
            attempt.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(attempt.Body),
                Encoding.UTF8,
                "application/json");
        }

        using var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"{(int)response.StatusCode} {err[..Math.Min(err.Length, 120)]}");
        }

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
            contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"非文件响应: {contentType} {err[..Math.Min(err.Length, 120)]}");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (bytes.Length == 0)
        {
            throw new InvalidOperationException("空文件响应");
        }

        var fileName = ResolveFileName(response, serialNumber);
        var targetPath = Path.Combine(downloadsDir, fileName);
        await File.WriteAllBytesAsync(targetPath, bytes);

        if (File.GetLastWriteTimeUtc(targetPath) < notBeforeUtc.AddSeconds(-5))
        {
            File.SetLastWriteTimeUtc(targetPath, DateTime.UtcNow);
        }

        return targetPath;
    }

    private static string ResolveFileName(HttpResponseMessage response, string serialNumber)
    {
        if (response.Content.Headers.ContentDisposition?.FileName is { } rawName)
        {
            var cleaned = rawName.Trim('"');
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                return cleaned;
            }
        }

        return $"cpms-export-{serialNumber}-{DateTime.Now:yyyyMMddHHmmss}.zip";
    }
}
