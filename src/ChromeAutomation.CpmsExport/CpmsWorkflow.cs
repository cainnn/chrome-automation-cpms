using System.Diagnostics;
using System.IO.Compression;
using ChromeAutomation.Client;

namespace ChromeAutomation.CpmsExport;

internal static class CpmsWorkflow
{
    public static async Task<bool> WaitForReportPageAsync(ChromeController chrome, int? tabId = null)
    {
        var markers = new[] { "项目明细查询报表", "项目明细", "显示列配置", "字段说明" };
        var deadline = DateTime.UtcNow.AddMilliseconds(120000);

        while (DateTime.UtcNow < deadline)
        {
            var body = await chrome.GetBodyTextAsync(tabId);
            if (bodyContainsLogin(body))
            {
                throw new InvalidOperationException("当前为登录页，请先在 Chrome 中登录 CPMS");
            }

            var info = await chrome.CommandAsync("getPageInfo", new { tabId });
            if (info?.TryGetProperty("url", out var urlProp) == true)
            {
                var url = urlProp.GetString() ?? "";
                if (url.Contains("BigTableDefind", StringComparison.OrdinalIgnoreCase) ||
                    url.Contains("wideTable", StringComparison.OrdinalIgnoreCase))
                {
                    if (body is not null && markers.Any(m => body.Contains(m, StringComparison.Ordinal)))
                    {
                        return true;
                    }
                }
            }
            else if (body is not null && markers.Any(m => body.Contains(m, StringComparison.Ordinal)))
            {
                return true;
            }

            await ChromeAutomationHelpers.DelayAsync(2000);
        }

        return false;
    }

    private static bool bodyContainsLogin(string? body) =>
        !string.IsNullOrEmpty(body) &&
        (body.Contains("登录", StringComparison.Ordinal) || body.Contains("用户名", StringComparison.Ordinal)) &&
        !body.Contains("项目明细", StringComparison.Ordinal);

    public static async Task ClickExportButtonAsync(ChromeController chrome, int? tabId = null)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            try
            {
                await chrome.CommandAsync("clickByText", new { text = "导出", exact = true, tabId });
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            try
            {
                await chrome.CommandAsync("cpmsClickExport", new { tabId });
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            Console.WriteLine($"[3/7] 未找到导出按钮，重试 {attempt}/8...");
            await ChromeAutomationHelpers.DelayAsync(3000);
        }

        throw lastError ?? new InvalidOperationException("export-button-not-found");
    }

    public static async Task<string?> WaitAndConfirmExportDialogAsync(
        ChromeController chrome,
        int? tabId = null,
        int timeoutMs = 90000)
    {
        var hints = new[] { "导出任务已提交", "流水号", "导出任务" };
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var found = false;

        while (DateTime.UtcNow < deadline)
        {
            var body = await chrome.GetBodyTextAsync(tabId);
            if (!string.IsNullOrEmpty(body) && hints.Any(h => body.Contains(h, StringComparison.Ordinal)))
            {
                found = true;
                break;
            }

            await ChromeAutomationHelpers.DelayAsync(1000);
        }

        if (!found)
        {
            Console.WriteLine("[3/7] 未检测到弹窗文案，仍尝试点击「确定」");
        }

        await ChromeAutomationHelpers.DelayAsync(500);
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                await chrome.CommandAsync("cpmsClickDialogConfirm", new { tabId });
                break;
            }
            catch
            {
                try
                {
                    await chrome.CommandAsync("clickByText", new { text = "确定", exact = true, tabId });
                    break;
                }
                catch when (attempt < 5)
                {
                    await ChromeAutomationHelpers.DelayAsync(800);
                }
            }
        }

        var serial = await chrome.ExtractSerialNumberAsync(tabId);
        await ChromeAutomationHelpers.DelayAsync(1500);
        return serial;
    }

    public static async Task<string?> GetLatestSerialAsync(ChromeController chrome, int? tabId = null)
    {
        var result = await chrome.CommandAsync("cpmsGetLatestSerial", new { tabId });
        if (result?.TryGetProperty("serial", out var serialProp) == true)
        {
            return serialProp.GetString();
        }

        return null;
    }

    public static async Task RefreshExportTaskListAsync(ChromeController chrome, int? tabId = null)
    {
        try
        {
            await chrome.ClickByTextAsync("查询", exact: true, tabId: tabId);
            await ChromeAutomationHelpers.DelayAsync(2000);
        }
        catch
        {
            // 部分页面可能自动刷新，忽略查询按钮缺失
        }
    }

    public static async Task WaitForBackendReadyAsync(
        ChromeController chrome,
        string? serialNumber,
        int? tabId = null,
        int pollIntervalMs = 10000,
        int timeoutMs = 1800000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var label = serialNumber ?? "最新任务";
        Console.WriteLine($"[5/7] 等待后台处理完成 ({label})...");

        while (DateTime.UtcNow < deadline)
        {
            ExportRowStatus? status;
            if (string.IsNullOrEmpty(serialNumber))
            {
                status = await GetFirstRowStatusAsync(chrome, tabId);
            }
            else
            {
                status = await GetExportRowStatusAsync(chrome, serialNumber, tabId);
            }

            if (status is null)
            {
                Console.WriteLine("[5/7] 未找到任务行，刷新列表...");
            }
            else if (status.Processing)
            {
                Console.WriteLine("[5/7] 状态: 正在后台下载...");
            }
            else if (status.Success)
            {
                Console.WriteLine("[5/7] 状态: 后台下载成功，可以下载");
                return;
            }
            else
            {
                Console.WriteLine($"[5/7] 状态: {status.RawStatus}");
            }

            await RefreshExportTaskListAsync(chrome, tabId);
            await ChromeAutomationHelpers.DelayAsync(pollIntervalMs);
        }

        throw new TimeoutException($"等待后台下载超时 ({timeoutMs / 1000}s)");
    }

    /// <summary>
    /// 全自动下载：磁盘监控优先，并行触发点击与 acceptDanger，文件落盘即返回（不依赖 tab 存活）。
    /// </summary>
    public static async Task<string> CompleteDownloadStepAsync(
        ChromeController chrome,
        string? serialNumber,
        int? tabId,
        string downloadListUrl,
        DateTime downloadStartedAt,
        int timeoutMs = 600000)
    {
        Console.WriteLine("[6/7] 全自动下载（blob-bypass + 会话内 acceptDanger + 磁盘监控）");

        try
        {
            await chrome.EnableAutoAcceptDownloadsAsync(durationMs: timeoutMs + 60000);
        }
        catch
        {
            // 旧版扩展
        }

        try
        {
            var downloadsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");

            var existing = FindRecentCpmsArchive(downloadsDir, downloadStartedAt);
            if (existing is not null && File.Exists(existing))
            {
                Console.WriteLine($"[6/7] 下载文件已存在: {existing}");
                return existing;
            }

            using var cts = new CancellationTokenSource(timeoutMs);

            var diskTask = WaitForDownloadOnDiskAsync(
                serialNumber ?? "",
                timeoutMs,
                downloadStartedAt);

            var acceptLoop = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await chrome.AcceptPendingDownloadsAsync(cts.Token);
                    }
                    catch
                    {
                        // ignore
                    }

                    await Task.Delay(3000, cts.Token);
                }
            }, cts.Token);

            var clickTask = Task.Run(async () =>
            {
                await Task.Delay(500, cts.Token);
                await TriggerDownloadClickAsync(chrome, serialNumber, tabId, downloadListUrl, cts.Token);
            }, cts.Token);

            try
            {
                var path = await diskTask;
                Console.WriteLine($"[6/7] 下载完成: {path}");
                return path;
            }
            finally
            {
                cts.Cancel();
                try { await acceptLoop; } catch { /* cancelled */ }
                try { await clickTask; } catch { /* cancelled */ }
            }
        }
        finally
        {
            try { await chrome.DisableAutoAcceptDownloadsAsync(); } catch { /* ignore */ }
        }
    }

    private static async Task TriggerDownloadClickAsync(
        ChromeController chrome,
        string? serialNumber,
        int? tabId,
        string downloadListUrl,
        CancellationToken cancellationToken)
    {
        var clickParams = new
        {
            serialNumber,
            tabId,
            recreateUrl = downloadListUrl,
        };

        try
        {
            if (!string.IsNullOrEmpty(serialNumber))
            {
                await chrome.CommandAsync(
                    "cpmsDownloadBySerial",
                    clickParams,
                    cancellationToken,
                    timeoutMs: 180000);
            }
            else
            {
                await chrome.CommandAsync(
                    "cpmsClickFirstReadyDownload",
                    clickParams,
                    cancellationToken,
                    timeoutMs: 60000);
            }

            return;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[6/7] cpmsDownloadBySerial: {ex.Message}");
        }

        try
        {
            if (!string.IsNullOrEmpty(serialNumber))
            {
                var click = await chrome.CommandAsync(
                    "cpmsClickDownload",
                    clickParams,
                    cancellationToken,
                    timeoutMs: 60000);
                if (click?.TryGetProperty("url", out var urlProp) == true)
                {
                    var url = urlProp.GetString();
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        await chrome.CommandAsync(
                            "startDownload",
                            new { url, tabId, recreateUrl = downloadListUrl },
                            cancellationToken,
                            timeoutMs: 180000);
                        return;
                    }
                }
            }

            await chrome.CommandAsync(
                "clickByText",
                new { text = "下载", exact = true, tabId, recreateUrl = downloadListUrl },
                cancellationToken,
                timeoutMs: 60000);
            await chrome.AcceptPendingDownloadsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[6/7] 下载点击回退: {ex.Message}");
        }
    }

    public static async Task ClickDownloadInRowAsync(ChromeController chrome, string serialNumber, int? tabId = null)
    {
        try
        {
            var result = await chrome.CommandAsync("cpmsDownloadBySerial", new { serialNumber, tabId });
            if (result?.TryGetProperty("method", out var methodProp) == true)
            {
                Console.WriteLine($"[6/7] 下载策略: {methodProp.GetString()}");
            }
            if (result?.TryGetProperty("filename", out var fn) == true)
            {
                Console.WriteLine($"[6/7] 目标文件: {fn.GetString()}");
            }
            return;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[6/7] cpmsDownloadBySerial 失败: {ex.Message}");
        }

        try
        {
            var click = await chrome.CommandAsync("cpmsClickDownload", new { serialNumber, tabId });
            if (click?.TryGetProperty("url", out var urlProp) == true)
            {
                var url = urlProp.GetString();
                if (!string.IsNullOrWhiteSpace(url))
                {
                    await chrome.CommandAsync("startDownload", new { url, tabId });
                    return;
                }
            }
        }
        catch
        {
            // fallback
        }

        await chrome.ClickByTextAsync("下载", exact: true, tabId: tabId);
        try { await chrome.AcceptPendingDownloadsAsync(); } catch { /* ignore */ }
    }

    public static async Task ClickFirstReadyDownloadAsync(ChromeController chrome, int? tabId = null)
    {
        try
        {
            await chrome.CommandAsync("cpmsClickFirstReadyDownload", new { tabId });
            return;
        }
        catch
        {
            // fallback
        }

        await chrome.ClickByTextAsync("下载", exact: true, tabId: tabId);
    }

    public static async Task<ExportRowStatus?> GetFirstRowStatusAsync(ChromeController chrome, int? tabId = null)
    {
        var result = await chrome.CommandAsync("cpmsFirstReadyRow", new { tabId });
        if (result is null || !result.Value.TryGetProperty("found", out var found) || !found.GetBoolean())
        {
            return null;
        }

        return new ExportRowStatus
        {
            Processing = result.Value.TryGetProperty("processing", out var p) && p.GetBoolean(),
            Success = result.Value.TryGetProperty("success", out var s) && s.GetBoolean(),
            RawStatus = result.Value.TryGetProperty("rawStatus", out var r) ? r.GetString() ?? "" : "",
        };
    }

    public static async Task<ExportRowStatus?> GetExportRowStatusAsync(
        ChromeController chrome,
        string serialNumber,
        int? tabId = null)
    {
        var result = await chrome.CommandAsync("cpmsExportRowStatus", new { serialNumber, tabId });
        if (result is null || !result.Value.TryGetProperty("found", out var found) || !found.GetBoolean())
        {
            return null;
        }

        return new ExportRowStatus
        {
            Processing = result.Value.TryGetProperty("processing", out var p) && p.GetBoolean(),
            Success = result.Value.TryGetProperty("success", out var s) && s.GetBoolean(),
            RawStatus = result.Value.TryGetProperty("rawStatus", out var r) ? r.GetString() ?? "" : "",
        };
    }

    public static async Task<string> WaitForDownloadedFileAsync(
        ChromeController chrome,
        string serialNumber,
        DateTime? notBefore = null,
        int timeoutMs = 300000)
    {
        try
        {
            await chrome.EnableAutoAcceptDownloadsAsync();
            Console.WriteLine("[6/7] 已启用自动保留危险下载");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[6/7] 扩展未支持自动下载确认，请在 Chrome 中手动点击「保留」: {ex.Message}");
        }

        var acceptDeadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < acceptDeadline)
        {
            try
            {
                var accepted = await chrome.AcceptPendingDownloadsAsync();
                if (accepted?.TryGetProperty("count", out var countProp) == true && countProp.GetInt32() > 0)
                {
                    Console.WriteLine($"[6/7] 已自动保留 {countProp.GetInt32()} 个被拦截的下载");
                }
            }
            catch
            {
                // 旧版扩展无 acceptPendingDownloads
            }

            try
            {
                var download = await chrome.WaitForDownloadAsync(
                    filenameContains: null,
                    timeoutMs: 5000,
                    since: notBefore);
                if (download?.TryGetProperty("filename", out var filenameProp) == true)
                {
                    var path = filenameProp.GetString();
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    {
                        Console.WriteLine($"[6/7] 下载完成: {path}");
                        return path;
                    }
                }
            }
            catch
            {
                // 继续轮询 accept + 磁盘
            }

            var disk = FindRecentCpmsArchive(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                notBefore ?? DateTime.UtcNow.AddSeconds(-10));
            if (disk is not null && File.Exists(disk))
            {
                Console.WriteLine($"[6/7] 在下载目录找到文件: {disk}");
                return disk;
            }

            await Task.Delay(2000);
        }

        return await WaitForDownloadOnDiskAsync(serialNumber, 30000, notBefore);
    }

    public static async Task<string> WaitForDownloadOnDiskAsync(string serialNumber, int timeoutMs, DateTime? notBefore = null)
    {
        var downloadsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var minTime = notBefore ?? DateTime.UtcNow.AddSeconds(-10);
        Console.WriteLine($"[6/7] 监控下载目录: {downloadsDir}");

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (!string.IsNullOrEmpty(serialNumber))
            {
                var excel = FindExcelBySerial(downloadsDir, serialNumber, minTime);
                if (excel is not null)
                {
                    Console.WriteLine($"[6/7] 找到 Excel: {excel}");
                    return excel;
                }

                var zip = Directory.GetFiles(downloadsDir, $"*{serialNumber}*.zip", SearchOption.TopDirectoryOnly)
                    .Where(f => !f.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase))
                    .Where(f => File.GetLastWriteTimeUtc(f) >= minTime)
                    .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                    .FirstOrDefault();
                if (zip is not null && File.Exists(zip))
                {
                    Console.WriteLine($"[6/7] 找到 ZIP: {zip}");
                    return zip;
                }
            }

            var recent = FindRecentCpmsArchive(downloadsDir, minTime);
            if (recent is not null)
            {
                Console.WriteLine($"[6/7] 找到近期 CPMS 压缩包: {recent}");
                return recent;
            }

            await Task.Delay(3000);
        }

        throw new TimeoutException($"等待下载文件超时，流水号: {serialNumber}");
    }

    private static string? FindRecentCpmsArchive(string downloadsDir, DateTime minTime)
    {
        var patterns = new[] { "*.zip", "*.xlsx" };
        foreach (var pattern in patterns)
        {
            var file = Directory.GetFiles(downloadsDir, pattern, SearchOption.TopDirectoryOnly)
                .Where(f => !f.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase))
                .Where(f => File.GetLastWriteTimeUtc(f) >= minTime)
                .Where(f => LooksLikeCpmsExport(f))
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .FirstOrDefault();
            if (file is not null)
            {
                return file;
            }
        }

        return null;
    }

    private static bool LooksLikeCpmsExport(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Contains("项目明细", StringComparison.Ordinal) ||
            name.Contains("加工完成", StringComparison.Ordinal) ||
            name.Contains("pms", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("cpms", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // CPMS 导出 zip 常见命名: ...-2026-05-25-01-531779681740387.zip
        return name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
               System.Text.RegularExpressions.Regex.IsMatch(name, @"\d{4}-\d{2}-\d{2}-\d{2}-\d+");
    }

    private static string? FindExcelBySerial(string downloadsDir, string serialNumber, DateTime minTime)
    {
        foreach (var dir in Directory.GetDirectories(downloadsDir, $"*{serialNumber}*", SearchOption.TopDirectoryOnly)
                     .Where(d => Directory.GetLastWriteTimeUtc(d) >= minTime))
        {
            var xlsx = Directory.GetFiles(dir, "*.xlsx", SearchOption.AllDirectories)
                .Where(f => File.GetLastWriteTimeUtc(f) >= minTime)
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .FirstOrDefault();
            if (xlsx is not null)
            {
                return xlsx;
            }
        }

        return Directory.GetFiles(downloadsDir, $"*{serialNumber}*.xlsx", SearchOption.AllDirectories)
            .Where(f => File.GetLastWriteTimeUtc(f) >= minTime)
            .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
            .FirstOrDefault();
    }

    public static string ResolveExcelPath(string downloadedPath)
    {
        if (downloadedPath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) && File.Exists(downloadedPath))
        {
            return downloadedPath;
        }

        if (downloadedPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var extractDir = Path.Combine(
                Path.GetDirectoryName(downloadedPath)!,
                Path.GetFileNameWithoutExtension(downloadedPath));

            if (!Directory.Exists(extractDir))
            {
                Directory.CreateDirectory(extractDir);
                ZipFile.ExtractToDirectory(downloadedPath, extractDir, overwriteFiles: true);
            }

            var xlsx = Directory.GetFiles(extractDir, "*.xlsx", SearchOption.AllDirectories).FirstOrDefault();
            if (xlsx is not null)
            {
                return xlsx;
            }
        }

        var siblingDir = Path.Combine(Path.GetDirectoryName(downloadedPath)!, Path.GetFileNameWithoutExtension(downloadedPath));
        if (Directory.Exists(siblingDir))
        {
            var xlsx = Directory.GetFiles(siblingDir, "*.xlsx", SearchOption.AllDirectories).FirstOrDefault();
            if (xlsx is not null)
            {
                return xlsx;
            }
        }

        throw new FileNotFoundException($"未找到 Excel 文件，下载路径: {downloadedPath}");
    }

    public static async Task RunDatabaseImportAsync(string excelPath)
    {
        var netProjectDir = Environment.GetEnvironmentVariable("NET_IMPORT_PROJECT") ?? @"D:\NET";
        if (!Directory.Exists(netProjectDir))
        {
            throw new DirectoryNotFoundException($"导入项目不存在: {netProjectDir}");
        }

        Console.WriteLine($"[7/7] 调用导入项目: {netProjectDir}");
        Console.WriteLine($"[7/7] Excel 文件: {excelPath}");

        var csproj = Path.Combine(netProjectDir, "PersonalPMS.ProjectReport.csproj");
        if (!File.Exists(csproj))
        {
            csproj = netProjectDir;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{csproj}\" -- --excel \"{excelPath}\"",
            WorkingDirectory = netProjectDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动导入进程");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        Console.WriteLine(stdout);
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            Console.Error.WriteLine(stderr);
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"数据库导入失败，退出码: {process.ExitCode}");
        }

        Console.WriteLine("[7/7] 数据库导入完成");
    }
}

internal sealed class ExportRowStatus
{
    public bool Processing { get; init; }
    public bool Success { get; init; }
    public string RawStatus { get; init; } = "";
}
