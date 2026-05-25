using System.Text.Json;
using ChromeAutomation.Client;
using ChromeAutomation.CpmsExport;

const string ReportUrl = "http://cpms.hq.cmcc/pms/#/mdat/wideTable/BigTableDefind";
var downloadListUrl = Environment.GetEnvironmentVariable("CPMS_EXPORT_TASK_URL")
    ?? "http://cpms.hq.cmcc/pms/#/mops/tools/attachmentDownload/list";

Console.WriteLine("=== CPMS 项目明细导出 + 数据库导入 ===");
Console.WriteLine("请确保：1) 桥接服务器已启动  2) Chrome 扩展已连接  3) 浏览器已登录 CPMS");
Console.WriteLine("重要：请在 chrome://extensions/ 刷新扩展后再运行");
Console.WriteLine();

var excelOnly = Environment.GetEnvironmentVariable("EXCEL_PATH");
if (!string.IsNullOrWhiteSpace(excelOnly))
{
    Console.WriteLine($"[导入模式] 使用已有 Excel: {excelOnly}");
    var path = excelOnly.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
        ? excelOnly
        : CpmsWorkflow.ResolveExcelPath(excelOnly);
    await CpmsWorkflow.RunDatabaseImportAsync(path);
    Console.WriteLine("导入完成。");
    return;
}

var skipExport = string.Equals(
    Environment.GetEnvironmentVariable("CPMS_SKIP_EXPORT"),
    "1",
    StringComparison.OrdinalIgnoreCase);
var presetSerial = Environment.GetEnvironmentVariable("CPMS_SERIAL");

try
{
    if (skipExport)
    {
        await RunDownloadOnlyAsync(downloadListUrl, presetSerial);
    }
    else
    {
        await RunAsync(ReportUrl, downloadListUrl);
    }
}
catch (Exception ex)
{
    Console.WriteLine();
    Console.WriteLine($"错误: {ex.Message}");
    Environment.Exit(1);
}

static async Task DisableAutoAcceptSafeAsync(ChromeController chrome)
{
    try { await chrome.DisableAutoAcceptDownloadsAsync(); } catch { /* ignore */ }
}

static async Task<int?> GetOrNavigateTabAsync(ChromeController chrome, string url, string stepLabel)
{
    var forceNewTab = string.Equals(
        Environment.GetEnvironmentVariable("CPMS_NEW_TAB"),
        "1",
        StringComparison.OrdinalIgnoreCase);

    if (!forceNewTab)
    {
        var tabs = await chrome.GetTabsAsync();
        if (tabs.HasValue && tabs.Value.ValueKind == JsonValueKind.Array)
        {
            int? cpmsTabId = null;
            int? activeTabId = null;

            foreach (var tab in tabs.Value.EnumerateArray())
            {
                if (!tab.TryGetProperty("id", out var idProp))
                {
                    continue;
                }

                var tabId = idProp.GetInt32();
                var tabUrl = tab.TryGetProperty("url", out var urlProp) ? urlProp.GetString() ?? "" : "";

                if (tabUrl.Contains("cpms.hq.cmcc", StringComparison.OrdinalIgnoreCase))
                {
                    cpmsTabId = tabId;
                }

                if (tab.TryGetProperty("active", out var activeProp) && activeProp.GetBoolean())
                {
                    activeTabId = tabId;
                }
            }

            var reuseId = cpmsTabId ?? activeTabId;
            if (reuseId.HasValue)
            {
                Console.WriteLine($"{stepLabel} 复用已有标签页 (id={reuseId})，不关闭 Chrome");
                await chrome.NavigateAsync(url, waitUntil: "spa", tabId: reuseId);
                await ChromeAutomationHelpers.DelayAsync(8000);
                return reuseId;
            }
        }
    }

    Console.WriteLine($"{stepLabel} 在当前 Chrome 窗口打开新标签页（不关闭浏览器）");
    var created = await chrome.CommandAsync("createTab", new { url, active = true });
    return created?.TryGetProperty("id", out var newId) == true ? newId.GetInt32() : null;
}

static async Task RunDownloadOnlyAsync(string downloadListUrl, string? presetSerial)
{
    await using var chrome = new ChromeController();
    await chrome.ConnectAsync();
    try
    {
        Console.WriteLine("[下载模式] 已连接桥接服务器");
        await ChromeAutomationHelpers.DelayAsync(3000);
        await WaitForExtensionAsync(chrome);

        var workTabId = await GetOrNavigateTabAsync(chrome, downloadListUrl, "[下载模式]");
        workTabId = await CpmsWorkflow.EnsureDownloadListPageAsync(
            chrome,
            downloadListUrl,
            workTabId,
            msg => Console.WriteLine(msg));

        var serialNumber = presetSerial ?? await CpmsWorkflow.GetLatestSerialAsync(chrome, workTabId);
        Console.WriteLine($"[下载模式] 流水号: {serialNumber ?? "(列表首条)"}");

        if (!string.IsNullOrEmpty(serialNumber))
        {
            var status = await CpmsWorkflow.GetExportRowStatusAsync(chrome, serialNumber, workTabId);
            if (status is not { Success: true })
            {
                await CpmsWorkflow.WaitForBackendReadyAsync(chrome, serialNumber, workTabId);
            }
            else
            {
                Console.WriteLine("[下载模式] 后台已处理完成，直接下载");
            }
        }

        var downloadStartedAt = DateTime.UtcNow;
        var downloadedPath = await CpmsWorkflow.CompleteDownloadStepAsync(
            chrome,
            serialNumber,
            workTabId,
            downloadListUrl,
            downloadStartedAt);

        var excelPath = downloadedPath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
            ? downloadedPath
            : CpmsWorkflow.ResolveExcelPath(downloadedPath);
        Console.WriteLine($"[下载模式] Excel: {excelPath}");
        await CpmsWorkflow.RunDatabaseImportAsync(excelPath);
        Console.WriteLine("全部完成。Chrome 保持打开，未关闭任何标签页。");
    }
    finally
    {
        await DisableAutoAcceptSafeAsync(chrome);
    }
}

static async Task RunAsync(string reportUrl, string downloadListUrl)
{
    await using var chrome = new ChromeController();
    await chrome.ConnectAsync();
    try
    {
        Console.WriteLine("[1/7] 已连接桥接服务器");
        Console.WriteLine("[1/7] 若未连接，请在扩展弹窗点击「重新连接」");
        await ChromeAutomationHelpers.DelayAsync(3000);

        await WaitForExtensionAsync(chrome);
        Console.WriteLine("[1/7] Chrome 扩展已就绪");

        Console.WriteLine($"[2/7] 定位报表页: {reportUrl}");
        var workTabId = await CpmsWorkflow.EnsureReportPageAsync(chrome, reportUrl, Console.WriteLine);
        if (!workTabId.HasValue)
        {
            await LogPageDebugAsync(chrome, null, "report-page");
            throw new InvalidOperationException("无法加载项目明细查询报表页，请确认 Chrome 已登录 CPMS 内网");
        }

        Console.WriteLine("[3/7] 点击「导出」并确认弹窗");
        string? serialNumber;
        try
        {
            serialNumber = await CpmsWorkflow.ClickExportAndConfirmAsync(
                chrome,
                reportUrl,
                workTabId,
                Console.WriteLine);
        }
        catch (Exception ex)
        {
            await LogPageDebugAsync(chrome, workTabId, "export-failed");
            throw new InvalidOperationException($"导出失败: {ex.Message}", ex);
        }

        Console.WriteLine(serialNumber is not null
            ? $"[3/7] 导出流水号: {serialNumber}"
            : "[3/7] 未从弹窗解析流水号，将在下载列表页获取");

        Console.WriteLine($"[4/7] 打开附件下载列表: {downloadListUrl}");
        try
        {
            workTabId = await CpmsWorkflow.EnsureDownloadListPageAsync(
                chrome,
                downloadListUrl,
                workTabId,
                Console.WriteLine) ?? workTabId;
        }
        catch (TimeoutException)
        {
            await LogPageDebugAsync(chrome, workTabId, "download-list");
            throw;
        }

        if (string.IsNullOrEmpty(serialNumber))
        {
            serialNumber = await CpmsWorkflow.GetLatestSerialAsync(chrome, workTabId);
            Console.WriteLine(serialNumber is not null
                ? $"[4/7] 使用列表最新流水号: {serialNumber}"
                : "[4/7] 将使用列表中第一条可下载任务");
        }

        await CpmsWorkflow.WaitForBackendReadyAsync(chrome, serialNumber, workTabId);

        Console.WriteLine("[6/7] 全自动下载并导入");
        var downloadStartedAt = DateTime.UtcNow;

        var downloadedPath = await CpmsWorkflow.CompleteDownloadStepAsync(
            chrome,
            serialNumber,
            workTabId,
            downloadListUrl,
            downloadStartedAt);

        var excelPath = downloadedPath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
            ? downloadedPath
            : CpmsWorkflow.ResolveExcelPath(downloadedPath);
        Console.WriteLine($"[6/7] Excel 路径: {excelPath}");

        await CpmsWorkflow.RunDatabaseImportAsync(excelPath);

        Console.WriteLine();
        Console.WriteLine("全部完成。Chrome 保持打开，未关闭任何标签页。");
    }
    finally
    {
        await DisableAutoAcceptSafeAsync(chrome);
    }
}

static async Task LogPageDebugAsync(ChromeController chrome, int? tabId, string tag)
{
    try
    {
        var buttons = await chrome.CommandAsync("cpmsListButtons", new { tabId });
        if (buttons?.TryGetProperty("buttons", out var arr) == true)
        {
            Console.WriteLine($"[调试] 页面按钮文字: {string.Join(" | ", arr.EnumerateArray().Select(e => e.GetString()))}");
        }

        var exportBtn = await chrome.CommandAsync("cpmsHasExportButton", new { tabId });
        if (exportBtn?.TryGetProperty("found", out var found) == true)
        {
            Console.WriteLine($"[调试] 导出按钮: found={found.GetBoolean()}, url={exportBtn?.GetProperty("url")}");
        }

        var info = await chrome.CommandAsync("getPageInfo", new { tabId });
        Console.WriteLine($"[调试] 页面 URL: {info?.GetProperty("url")}");
    }
    catch
    {
        Console.WriteLine($"[调试] 无法读取页面信息 ({tag})");
    }
}

static async Task WaitForExtensionAsync(ChromeController chrome, int timeoutMs = 30000)
{
    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
    while (DateTime.UtcNow < deadline)
    {
        try
        {
            await chrome.GetTabsAsync();
            return;
        }
        catch (Exception ex) when (ex.Message.Contains("extension not connected", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("[1/7] 等待 Chrome 扩展连接...");
            await ChromeAutomationHelpers.DelayAsync(3000);
        }
    }

    throw new InvalidOperationException("Chrome 扩展未连接。请刷新扩展并点击「重新连接」。");
}
