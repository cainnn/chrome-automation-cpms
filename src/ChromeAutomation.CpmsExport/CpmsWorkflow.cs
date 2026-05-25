using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using ChromeAutomation.Client;

namespace ChromeAutomation.CpmsExport;

internal static class CpmsWorkflow
{
    private static bool IsReportPageUrl(string url) =>
        url.Contains("BigTableDefind", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("wideTable", StringComparison.OrdinalIgnoreCase);

    private static bool IsExcludedCpmsUrl(string url) =>
        url.Contains("attachmentDownload", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("mops/tools", StringComparison.OrdinalIgnoreCase);

    public static async Task<bool> HasExportButtonAsync(ChromeController chrome, int? tabId = null)
    {
        try
        {
            var result = await chrome.CommandAsync("cpmsHasExportButton", new { tabId });
            if (result?.TryGetProperty("found", out var found) == true)
            {
                return found.GetBoolean();
            }
        }
        catch
        {
            // 旧版扩展无 cpmsHasExportButton，回退到按钮列表检测
        }

        try
        {
            var buttons = await chrome.CommandAsync("cpmsListButtons", new { tabId });
            if (buttons?.TryGetProperty("buttons", out var arr) == true)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var text = item.GetString() ?? "";
                    if (text is "导出" or "导 出" || (text.Contains('导') && text.Contains('出') && text.Length <= 8))
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    public static async Task<bool> IsOnReportPageAsync(ChromeController chrome, int? tabId = null)
    {
        var info = await chrome.CommandAsync("getPageInfo", new { tabId });
        var url = info?.TryGetProperty("url", out var urlProp) == true ? urlProp.GetString() ?? "" : "";
        if (IsExcludedCpmsUrl(url) || !IsReportPageUrl(url))
        {
            return false;
        }

        var body = await chrome.GetBodyTextAsync(tabId);
        if (bodyContainsLogin(body))
        {
            return false;
        }

        if (await HasExportButtonAsync(chrome, tabId))
        {
            return true;
        }

        var strongMarkers = new[] { "项目明细查询报表", "显示列配置", "字段说明" };
        return body is not null && strongMarkers.Any(m => body.Contains(m, StringComparison.Ordinal));
    }

    public static async Task<int?> EnsureReportPageAsync(
        ChromeController chrome,
        string reportUrl,
        Action<string>? log = null)
    {
        void Log(string message) => log?.Invoke(message);

        int? cpmsTabId = null;
        var tabs = await chrome.GetTabsAsync();
        if (tabs.HasValue && tabs.Value.ValueKind == JsonValueKind.Array)
        {
            foreach (var tab in tabs.Value.EnumerateArray())
            {
                if (!tab.TryGetProperty("id", out var idProp))
                {
                    continue;
                }

                var tabId = idProp.GetInt32();
                var tabUrl = tab.TryGetProperty("url", out var urlProp) ? urlProp.GetString() ?? "" : "";
                if (!tabUrl.Contains("cpms.hq.cmcc", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                cpmsTabId ??= tabId;

                if (IsReportPageUrl(tabUrl) && await IsOnReportPageAsync(chrome, tabId))
                {
                    Log($"[2/7] 找到已打开的报表页 (tab id={tabId})");
                    await chrome.CommandAsync("activateTab", new { tabId });
                    return tabId;
                }
            }
        }

        var targetTabId = cpmsTabId;
        if (!targetTabId.HasValue)
        {
            Log("[2/7] 未找到 CPMS 标签页，新建标签页打开报表");
            var created = await chrome.CommandAsync("createTab", new { url = reportUrl, active = true });
            if (created?.TryGetProperty("id", out var newId) != true)
            {
                return null;
            }

            targetTabId = newId.GetInt32();
        }
        else
        {
            Log($"[2/7] 导航到报表页 (tab id={targetTabId})");
            await chrome.CommandAsync("activateTab", new { tabId = targetTabId });
        }

        await chrome.NavigateAsync(reportUrl, waitUntil: "spa", tabId: targetTabId, recreateUrl: reportUrl);
        await ChromeAutomationHelpers.DelayAsync(10000);

        if (await WaitForReportPageAsync(chrome, targetTabId))
        {
            return targetTabId;
        }

        Log("[2/7] 报表页未就绪，再次导航...");
        await chrome.NavigateAsync(reportUrl, waitUntil: "spa", tabId: targetTabId, recreateUrl: reportUrl);
        await ChromeAutomationHelpers.DelayAsync(10000);
        return await WaitForReportPageAsync(chrome, targetTabId) ? targetTabId : null;
    }

    public static async Task<bool> WaitForReportPageAsync(ChromeController chrome, int? tabId = null)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(120000);

        while (DateTime.UtcNow < deadline)
        {
            var info = await chrome.CommandAsync("getPageInfo", new { tabId });
            var url = info?.TryGetProperty("url", out var urlProp) == true ? urlProp.GetString() ?? "" : "";

            var body = await chrome.GetBodyTextAsync(tabId);
            if (bodyContainsLogin(body))
            {
                throw new InvalidOperationException("当前为登录页，请先在 Chrome 中登录 CPMS");
            }

            if (IsReportPageUrl(url) && !IsExcludedCpmsUrl(url))
            {
                if (await HasExportButtonAsync(chrome, tabId))
                {
                    return true;
                }

                var strongMarkers = new[] { "项目明细查询报表", "显示列配置", "字段说明" };
                if (body is not null && strongMarkers.Any(m => body.Contains(m, StringComparison.Ordinal)))
                {
                    await ChromeAutomationHelpers.DelayAsync(2000);
                    if (await HasExportButtonAsync(chrome, tabId))
                    {
                        return true;
                    }
                }
            }

            await ChromeAutomationHelpers.DelayAsync(2000);
        }

        return false;
    }

    private static bool bodyContainsLogin(string? body) =>
        !string.IsNullOrEmpty(body) &&
        (body.Contains("登录", StringComparison.Ordinal) || body.Contains("用户名", StringComparison.Ordinal)) &&
        !body.Contains("项目明细", StringComparison.Ordinal);

    public static async Task ClickExportButtonAsync(
        ChromeController chrome,
        string reportUrl,
        int? tabId,
        Action<string>? log = null)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            if (!await IsOnReportPageAsync(chrome, tabId) || !await HasExportButtonAsync(chrome, tabId))
            {
                log?.Invoke($"[3/7] 报表页或导出按钮未就绪，重新导航 (尝试 {attempt}/8)...");
                tabId = await EnsureReportPageAsync(chrome, reportUrl, log);
                if (!tabId.HasValue)
                {
                    throw new InvalidOperationException("无法打开项目明细查询报表页");
                }

                await ChromeAutomationHelpers.DelayAsync(3000);
            }

            try
            {
                var probe = await chrome.CommandAsync("cpmsHasExportButton", new { tabId });
                if (probe?.TryGetProperty("found", out var found) == true && found.GetBoolean())
                {
                    await chrome.CommandAsync("cpmsClickExport", new { tabId });
                    return;
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            try
            {
                await chrome.CommandAsync("clickByText", new { text = "导出", exact = true, tabId });
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

    public static async Task<string?> ClickExportAndConfirmAsync(
        ChromeController chrome,
        string reportUrl,
        int? tabId,
        Action<string>? log = null)
    {
        Exception? lastError = null;

        for (var round = 1; round <= 2; round++)
        {
            if (!await IsOnReportPageAsync(chrome, tabId))
            {
                Log("[3/7] 当前不在报表页，重新定位...");
                tabId = await EnsureReportPageAsync(chrome, reportUrl, log);
                if (!tabId.HasValue)
                {
                    throw new InvalidOperationException("无法打开项目明细查询报表页");
                }
            }

            try
            {
                await ChromeAutomationHelpers.DelayAsync(2000);
                await ClickExportButtonAsync(chrome, reportUrl, tabId, log);
                var serial = await WaitAndConfirmExportDialogAsync(chrome, tabId);
                return serial;
            }
            catch (Exception ex) when (round < 2)
            {
                lastError = ex;
                Log($"[3/7] 导出或确认失败 ({ex.Message})，重新定位报表页并重试...");
                tabId = await EnsureReportPageAsync(chrome, reportUrl, log);
                if (!tabId.HasValue)
                {
                    throw new InvalidOperationException("无法打开项目明细查询报表页", ex);
                }
            }
        }

        throw lastError ?? new InvalidOperationException("导出失败");
        
        void Log(string message) => log?.Invoke(message);
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
            Console.WriteLine("[3/7] 未检测到弹窗文案，仍尝试点击确认按钮");
        }

        await ChromeAutomationHelpers.DelayAsync(500);
        if (!await TryConfirmExportDialogAsync(chrome, tabId))
        {
            throw new InvalidOperationException("未找到导出确认弹窗的「确定」按钮，请确认当前在报表页且导出已提交");
        }

        var serial = await chrome.ExtractSerialNumberAsync(tabId);
        await ChromeAutomationHelpers.DelayAsync(1500);
        return serial;
    }

    private static async Task<bool> TryConfirmExportDialogAsync(ChromeController chrome, int? tabId)
    {
        var labels = new[] { "确定", "确认", "OK", "知道了" };
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            try
            {
                var result = await chrome.CommandAsync("cpmsClickDialogConfirm", new { tabId });
                if (result?.TryGetProperty("ok", out var okProp) == true && okProp.GetBoolean())
                {
                    return true;
                }
            }
            catch
            {
                // try text labels
            }

            foreach (var label in labels)
            {
                try
                {
                    await chrome.CommandAsync("clickByText", new { text = label, exact = true, tabId });
                    return true;
                }
                catch
                {
                    // next label
                }
            }

            await ChromeAutomationHelpers.DelayAsync(1000);
        }

        return false;
    }

    public static async Task<int?> EnsureDownloadListPageAsync(
        ChromeController chrome,
        string downloadListUrl,
        int? tabId,
        Action<string>? log = null)
    {
        void Log(string message) => log?.Invoke(message);

        var tabs = await chrome.GetTabsAsync();
        if (tabs.HasValue && tabs.Value.ValueKind == JsonValueKind.Array)
        {
            foreach (var tab in tabs.Value.EnumerateArray())
            {
                if (!tab.TryGetProperty("id", out var idProp))
                {
                    continue;
                }

                var existingId = idProp.GetInt32();
                var tabUrl = tab.TryGetProperty("url", out var urlProp) ? urlProp.GetString() ?? "" : "";
                if (!tabUrl.Contains("attachmentDownload", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Log($"[4/7] 找到已有下载列表标签页 (tab id={existingId})");
                await chrome.CommandAsync("activateTab", new { tabId = existingId });
                if (await TryLoadDownloadListTableAsync(chrome, existingId, downloadListUrl, Log))
                {
                    return existingId;
                }
            }
        }

        Log("[4/7] 在新标签页打开下载列表（避免 SPA 路由切换失败）...");
        var created = await chrome.CommandAsync("createTab", new { url = downloadListUrl, active = true });
        if (created?.TryGetProperty("id", out var newId) != true)
        {
            throw new InvalidOperationException("无法打开下载列表标签页");
        }

        var newTabId = newId.GetInt32();
        await ChromeAutomationHelpers.DelayAsync(10000);

        var deadline = DateTime.UtcNow.AddMilliseconds(120000);
        while (DateTime.UtcNow < deadline)
        {
            if (await TryLoadDownloadListTableAsync(chrome, newTabId, downloadListUrl, Log))
            {
                return newTabId;
            }

            await ChromeAutomationHelpers.DelayAsync(3000);
        }

        throw new TimeoutException("下载列表页加载超时，未找到「后台下载状态」或「业务流水号」");
    }

    private static async Task<bool> TryLoadDownloadListTableAsync(
        ChromeController chrome,
        int tabId,
        string downloadListUrl,
        Action<string>? log)
    {
        var info = await chrome.CommandAsync("getPageInfo", new { tabId, recreateUrl = downloadListUrl });
        var url = info?.TryGetProperty("url", out var urlProp) == true ? urlProp.GetString() ?? "" : "";

        if (!url.Contains("attachmentDownload", StringComparison.OrdinalIgnoreCase))
        {
            log?.Invoke($"[4/7] 标签页 URL 不正确: {url}");
            await chrome.NavigateAsync(downloadListUrl, waitUntil: "spa", tabId: tabId, recreateUrl: downloadListUrl);
            await ChromeAutomationHelpers.DelayAsync(8000);
        }

        await RefreshDownloadListAsync(chrome, tabId);

        var body = await chrome.GetBodyTextAsync(tabId);
        if (body is not null &&
            (body.Contains("后台下载状态", StringComparison.Ordinal) ||
             body.Contains("业务流水号", StringComparison.Ordinal)))
        {
            return true;
        }

        return !string.IsNullOrEmpty(await GetLatestSerialAsync(chrome, tabId));
    }

    public static async Task WaitForDownloadListPageAsync(
        ChromeController chrome,
        int? tabId = null,
        int timeoutMs = 120000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            var info = await chrome.CommandAsync("getPageInfo", new { tabId });
            var url = info?.TryGetProperty("url", out var urlProp) == true ? urlProp.GetString() ?? "" : "";

            if (url.Contains("attachmentDownload", StringComparison.OrdinalIgnoreCase))
            {
                await RefreshDownloadListAsync(chrome, tabId);
                var body = await chrome.GetBodyTextAsync(tabId);
                if (body is not null &&
                    (body.Contains("后台下载状态", StringComparison.Ordinal) ||
                     body.Contains("业务流水号", StringComparison.Ordinal)))
                {
                    return;
                }

                if (!string.IsNullOrEmpty(await GetLatestSerialAsync(chrome, tabId)))
                {
                    return;
                }
            }

            await ChromeAutomationHelpers.DelayAsync(2000);
        }

        throw new TimeoutException("下载列表页加载超时，未找到「后台下载状态」或「业务流水号」");
    }

    public static async Task RefreshDownloadListAsync(ChromeController chrome, int? tabId = null)
    {
        try
        {
            await chrome.CommandAsync("cpmsRefreshDownloadList", new { tabId });
            return;
        }
        catch
        {
            // 旧版扩展
        }

        await RefreshExportTaskListAsync(chrome, tabId);
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

            var extensionDownloadTask = PollExtensionDownloadAsync(
                chrome,
                downloadStartedAt,
                cts.Token);

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
                var winner = await Task.WhenAny(diskTask, extensionDownloadTask);
                var path = await winner;
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

    private static async Task<string> PollExtensionDownloadAsync(
        ChromeController chrome,
        DateTime downloadStartedAt,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var download = await chrome.WaitForDownloadAsync(
                    filenameContains: null,
                    timeoutMs: 5000,
                    since: downloadStartedAt);
                if (download?.TryGetProperty("filename", out var filenameProp) == true)
                {
                    var path = filenameProp.GetString();
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    {
                        Console.WriteLine($"[6/7] Chrome 下载 API 检测到文件: {path}");
                        return path;
                    }
                }
            }
            catch
            {
                // keep polling
            }

            await Task.Delay(2000, cancellationToken);
        }

        throw new OperationCanceledException(cancellationToken);
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

        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            attempt++;
            try
            {
                await RefreshDownloadListAsync(chrome, tabId);
                await ChromeAutomationHelpers.DelayAsync(1500, cancellationToken);

                if (!string.IsNullOrEmpty(serialNumber))
                {
                    var status = await GetExportRowStatusAsync(chrome, serialNumber, tabId);
                    if (status is { Success: false, Processing: true })
                    {
                        Console.WriteLine($"[6/7] 后台仍在处理，等待后重试 (第 {attempt} 次)...");
                        await Task.Delay(10000, cancellationToken);
                        continue;
                    }

                    if (status is not { Success: true })
                    {
                        Console.WriteLine($"[6/7] 任务行状态: {status?.RawStatus ?? "未找到"}，刷新后重试 (第 {attempt} 次)...");
                    }
                }

                Console.WriteLine($"[6/7] 触发下载点击 (第 {attempt} 次)...");
                if (!string.IsNullOrEmpty(serialNumber))
                {
                    var result = await chrome.CommandAsync(
                        "cpmsDownloadBySerial",
                        clickParams,
                        cancellationToken,
                        timeoutMs: 120000);
                    LogDownloadResult(result);
                }
                else
                {
                    await chrome.CommandAsync(
                        "cpmsClickFirstReadyDownload",
                        clickParams,
                        cancellationToken,
                        timeoutMs: 60000);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
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
                    LogDownloadResult(click);
                    if (click?.TryGetProperty("url", out var urlProp) == true)
                    {
                        var url = urlProp.GetString();
                        if (!string.IsNullOrWhiteSpace(url) &&
                            url.Contains("download", StringComparison.OrdinalIgnoreCase))
                        {
                            await chrome.CommandAsync(
                                "startDownload",
                                new { url, tabId, recreateUrl = downloadListUrl },
                                cancellationToken,
                                timeoutMs: 120000);
                        }
                    }
                }

                await chrome.CommandAsync(
                    "clickByText",
                    new { text = "下载", exact = true, tabId, recreateUrl = downloadListUrl },
                    cancellationToken,
                    timeoutMs: 30000);
                await chrome.AcceptPendingDownloadsAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"[6/7] 下载点击回退: {ex.Message}");
            }

            await Task.Delay(15000, cancellationToken);
        }
    }

    private static void LogDownloadResult(JsonElement? result)
    {
        if (result is null)
        {
            return;
        }

        if (result.Value.TryGetProperty("method", out var method))
        {
            Console.WriteLine($"[6/7] 下载策略: {method.GetString()}");
        }

        if (result.Value.TryGetProperty("filename", out var filename))
        {
            Console.WriteLine($"[6/7] 目标文件: {filename.GetString()}");
        }

        if (result.Value.TryGetProperty("sourceUrl", out var sourceUrl))
        {
            Console.WriteLine($"[6/7] 下载 URL: {sourceUrl.GetString()}");
        }

        if (result.Value.TryGetProperty("captured", out var captured) && captured.ValueKind == JsonValueKind.Array)
        {
            var urls = string.Join(", ", captured.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrEmpty(s)).Take(5));
            if (!string.IsNullOrEmpty(urls))
            {
                Console.WriteLine($"[6/7] 捕获 URL: {urls}");
            }
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
