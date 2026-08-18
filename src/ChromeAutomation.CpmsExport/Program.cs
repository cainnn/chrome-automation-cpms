using System.Net.WebSockets;
using System.Text.Json;
using ChromeAutomation.Bridge;
using ChromeAutomation.Client;
using ChromeAutomation.CpmsExport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

const string ReportUrl = "http://cpms.hq.cmcc/pms/#/mdat/wideTable/BigTableDefind";
var downloadListUrl = Environment.GetEnvironmentVariable("CPMS_EXPORT_TASK_URL")
    ?? "http://cpms.hq.cmcc/pms/#/mops/tools/attachmentDownload/list";

Console.WriteLine("=== CPMS 项目明细导出 + 数据库导入 ===");
Console.WriteLine("请确保：1) Chrome 扩展已连接  2) 浏览器已登录 CPMS");
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

var runDiag = string.Equals(
    Environment.GetEnvironmentVariable("CPMS_DIAG"),
    "1",
    StringComparison.OrdinalIgnoreCase);
var httpOnly = string.Equals(
    Environment.GetEnvironmentVariable("CPMS_HTTP_ONLY"),
    "1",
    StringComparison.OrdinalIgnoreCase);
var skipExport = string.Equals(
    Environment.GetEnvironmentVariable("CPMS_SKIP_EXPORT"),
    "1",
    StringComparison.OrdinalIgnoreCase);
var presetSerial = Environment.GetEnvironmentVariable("CPMS_SERIAL");
var handleJavaDialog = string.Equals(
    Environment.GetEnvironmentVariable("HANDLE_JAVA_DIALOG"),
    "1",
    StringComparison.OrdinalIgnoreCase);

if (httpOnly)
{
    var serial = presetSerial ?? throw new InvalidOperationException("CPMS_HTTP_ONLY 需要设置 CPMS_SERIAL");
    var downloadsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads");
    await using var chrome = new ChromeController();
    await chrome.ConnectAsync();
    await WaitForExtensionAsync(chrome);
    await EnsureExtensionReloadedAsync(chrome);
    var downloaded = await CpmsHttpDownloader.TryDownloadBySerialAsync(
        serial,
        downloadsDir,
        DateTime.UtcNow.AddHours(-24),
        chrome)
        ?? throw new InvalidOperationException($"HTTP 下载失败，流水号: {serial}");
    var excelPath = downloaded.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
        ? downloaded
        : CpmsWorkflow.ResolveExcelPath(downloaded);
    Console.WriteLine($"[HTTP] Excel: {excelPath}");
    await CpmsWorkflow.RunDatabaseImportAsync(excelPath);
    Console.WriteLine("全部完成。");
    return;
}

// Start embedded bridge server if no external one is running
await StartBridgeIfNeededAsync();

try
{
    if (handleJavaDialog)
    {
        Console.WriteLine("[Java 弹窗处理模式] 等待并处理 Java 安全弹窗...");
        var javaResult = await JavaDialogHelper.HandleSecurityDialogAsync();
        Console.WriteLine(javaResult ? "✓ Java 安全弹窗已处理" : "✗ 未检测到弹窗或处理失败");
        return;
    }

    if (runDiag)
    {
        await CpmsDiag.RunAsync(presetSerial);
        return;
    }

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
    CpmsDownloadDiagnostics.AppendRunLog($"[{DateTime.Now:O}] FATAL {ex}");
    CpmsDownloadDiagnostics.LogDownloadResult(null, ex);
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
        await EnsureExtensionReloadedAsync(chrome);

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
        await CpmsWorkflow.CompleteDownloadAndImportAsync(
            chrome,
            serialNumber,
            workTabId,
            downloadListUrl,
            downloadStartedAt,
            Console.WriteLine);
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
        await EnsureExtensionReloadedAsync(chrome);
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

        await CpmsWorkflow.RefreshDownloadListAsync(chrome, workTabId);
        var listSerial = await CpmsWorkflow.GetLatestSerialAsync(chrome, workTabId);
        if (!string.IsNullOrEmpty(listSerial))
        {
            if (!string.IsNullOrEmpty(serialNumber) && serialNumber != listSerial)
            {
                Console.WriteLine($"[4/7] 弹窗流水号 {serialNumber}，列表最新 {listSerial}，使用列表最新");
            }
            else
            {
                Console.WriteLine($"[4/7] 使用列表最新流水号: {listSerial}");
            }

            serialNumber = listSerial;
        }
        else if (string.IsNullOrEmpty(serialNumber))
        {
            Console.WriteLine("[4/7] 列表暂无流水号，将使用第一条可下载任务");
        }

        await CpmsWorkflow.WaitForBackendReadyAsync(chrome, serialNumber, workTabId);

        Console.WriteLine("[6/7] 全自动下载（UIA 保留）并导入数据库");
        var downloadStartedAt = DateTime.UtcNow;

        await CpmsWorkflow.CompleteDownloadAndImportAsync(
            chrome,
            serialNumber,
            workTabId,
            downloadListUrl,
            downloadStartedAt,
            Console.WriteLine);

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

static async Task EnsureExtensionReloadedAsync(ChromeController chrome, string targetVersion = "1.3.2")
{
    var autoReload = !string.Equals(
        Environment.GetEnvironmentVariable("CPMS_NO_RELOAD_EXT"),
        "1",
        StringComparison.OrdinalIgnoreCase);

    if (!autoReload)
    {
        return;
    }

    string? current = null;
    try
    {
        var ver = await chrome.CommandAsync("getExtensionVersion", timeoutMs: 5000);
        current = ver?.TryGetProperty("version", out var v) == true ? v.GetString() : null;
    }
    catch
    {
        // 旧版扩展无 getExtensionVersion
    }

    if (string.Equals(current, targetVersion, StringComparison.Ordinal))
    {
        Console.WriteLine($"[扩展] 版本 {current} 已就绪");
        return;
    }

    Console.WriteLine($"[扩展] 重载扩展（当前 {current ?? "旧版"} → 目标 {targetVersion}）...");
    var reloaded = false;
    try
    {
        await chrome.CommandAsync("reloadExtension", timeoutMs: 5000);
        reloaded = true;
        Console.WriteLine("[扩展] 已通过 reloadExtension 触发重载");
    }
    catch
    {
        // 旧版扩展无 reloadExtension
    }

    if (!reloaded)
    {
        try
        {
            await chrome.CommandAsync("createTab", new { url = "chrome://extensions/", active = true });
        }
        catch
        {
            // ignore
        }

        await ChromeAutomationHelpers.DelayAsync(3000);
        await ChromeUiaHelper.RefreshExtensionAsync();
    }

    await ChromeAutomationHelpers.DelayAsync(6000);
    await WaitForExtensionAsync(chrome, 60000);

    try
    {
        var ver = await chrome.CommandAsync("getExtensionVersion", timeoutMs: 10000);
        current = ver?.TryGetProperty("version", out var v) == true ? v.GetString() : null;
        Console.WriteLine($"[扩展] 重载后版本: {current ?? "未知"}");
    }
    catch
    {
        Console.WriteLine("[扩展] 重载后仍无法读取版本，请手动在 chrome://extensions/ 点击「重新加载」");
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

static async Task StartBridgeIfNeededAsync()
{
    var port = int.TryParse(Environment.GetEnvironmentVariable("BRIDGE_PORT"), out var p) ? p : 9333;
    var testUrl = $"ws://127.0.0.1:{port}/";

    // Check if an external bridge is already running
    try
    {
        using var testWs = new ClientWebSocket();
        await testWs.ConnectAsync(new Uri(testUrl), CancellationToken.None);
        await testWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test", CancellationToken.None);
        Console.WriteLine($"[Bridge] 外部桥接服务器已在端口 {port} 运行，无需启动");
        return;
    }
    catch (WebSocketException)
    {
        // Port not listening — start embedded bridge
    }

    var bridge = new BridgeHost();
    var builder = WebApplication.CreateSlimBuilder();
    builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
    var app = builder.Build();
    app.UseWebSockets();
    app.Map("/", async context =>
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await bridge.HandleConnectionAsync(socket, context.RequestAborted);
    });

    // Start server in background (lifetime tied to process)
    _ = app.RunAsync();
    Console.WriteLine($"[Bridge] 内嵌桥接服务器已启动: ws://127.0.0.1:{port}");

    // Brief delay to let Kestrel bind the port
    await Task.Delay(500);
}
