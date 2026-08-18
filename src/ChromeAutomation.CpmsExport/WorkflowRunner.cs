using System.Net.WebSockets;
using System.Text.Json;
using ChromeAutomation.Bridge;
using ChromeAutomation.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace ChromeAutomation.CpmsExport;

public class WorkflowRunner
{
    public event Action<string>? Log;
    public event Action? IsRunningChanged;

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set { _isRunning = value; IsRunningChanged?.Invoke(); }
    }

    private void WriteLog(string message) => Log?.Invoke(message);

    public async Task RunFullWorkflowAsync(
        string reportUrl,
        string downloadListUrl,
        CancellationToken ct = default)
    {
        if (IsRunning) throw new InvalidOperationException("Already running");
        IsRunning = true;
        try
        {
            await StartBridgeIfNeededAsync(ct);
            await using var chrome = new ChromeController();
            await chrome.ConnectAsync(ct);
            try
            {
                WriteLog("[1/7] 已连接桥接服务器");
                WriteLog("[1/7] 若未连接，请在扩展弹窗点击「重新连接」");
                await ChromeAutomationHelpers.DelayAsync(3000, ct);

                await WaitForExtensionAsync(chrome, ct);
                WriteLog("[1/7] Chrome 扩展已就绪");

                WriteLog($"[2/7] 定位报表页: {reportUrl}");
                var workTabId = await CpmsWorkflow.EnsureReportPageAsync(chrome, reportUrl, WriteLog);
                if (!workTabId.HasValue)
                {
                    await LogPageDebugAsync(chrome, null, "report-page");
                    throw new InvalidOperationException("无法加载报表页，请确认 Chrome 已登录 CPMS");
                }

                WriteLog("[3/7] 点击「导出」并确认弹窗");
                string? serialNumber;
                try
                {
                    serialNumber = await CpmsWorkflow.ClickExportAndConfirmAsync(chrome, reportUrl, workTabId, WriteLog);
                }
                catch (Exception ex)
                {
                    await LogPageDebugAsync(chrome, workTabId, "export-failed");
                    throw new InvalidOperationException($"导出失败: {ex.Message}", ex);
                }

                WriteLog(serialNumber is not null
                    ? $"[3/7] 导出流水号: {serialNumber}"
                    : "[3/7] 未从弹窗解析流水号，将在下载列表页获取");

                WriteLog($"[4/7] 打开附件下载列表: {downloadListUrl}");
                try
                {
                    workTabId = await CpmsWorkflow.EnsureDownloadListPageAsync(
                        chrome, downloadListUrl, workTabId, WriteLog) ?? workTabId;
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
                        WriteLog($"[4/7] 弹窗流水号 {serialNumber}，列表最新 {listSerial}，使用列表最新");
                    else
                        WriteLog($"[4/7] 使用列表最新流水号: {listSerial}");
                    serialNumber = listSerial;
                }
                else if (string.IsNullOrEmpty(serialNumber))
                {
                    WriteLog("[4/7] 列表暂无流水号，将使用第一条可下载任务");
                }

                await CpmsWorkflow.WaitForBackendReadyAsync(chrome, serialNumber, workTabId, WriteLog);

                WriteLog("[6/7] 全自动下载（UIA 保留）并导入数据库");

                var downloadStartedAt = DateTime.UtcNow;
                await CpmsWorkflow.CompleteDownloadAndImportAsync(
                    chrome, serialNumber, workTabId, downloadListUrl, downloadStartedAt, WriteLog);

                WriteLog(string.Empty);
                WriteLog("全部完成。Chrome 保持打开，未关闭任何标签页。");
            }
            finally
            {
                await DisableAutoAcceptSafeAsync(chrome);
            }
        }
        finally
        {
            IsRunning = false;
        }
    }

    public async Task RunDownloadOnlyAsync(
        string downloadListUrl,
        string? presetSerial,
        CancellationToken ct = default)
    {
        if (IsRunning) throw new InvalidOperationException("Already running");
        IsRunning = true;
        try
        {
            await StartBridgeIfNeededAsync(ct);
            await using var chrome = new ChromeController();
            await chrome.ConnectAsync(ct);
            try
            {
                WriteLog("[下载模式] 已连接桥接服务器");
                await ChromeAutomationHelpers.DelayAsync(3000, ct);
                await WaitForExtensionAsync(chrome, ct);

                var workTabId = await GetOrNavigateTabAsync(chrome, downloadListUrl, "[下载模式]");
                workTabId = await CpmsWorkflow.EnsureDownloadListPageAsync(
                    chrome, downloadListUrl, workTabId, WriteLog);

                var serialNumber = presetSerial ?? await CpmsWorkflow.GetLatestSerialAsync(chrome, workTabId);
                WriteLog($"[下载模式] 流水号: {serialNumber ?? "(列表首条)"}");

                if (!string.IsNullOrEmpty(serialNumber))
                {
                    var status = await CpmsWorkflow.GetExportRowStatusAsync(chrome, serialNumber, workTabId);
                    if (status is not { Success: true })
                        await CpmsWorkflow.WaitForBackendReadyAsync(chrome, serialNumber, workTabId);
                    else
                        WriteLog("[下载模式] 后台已处理完成，直接下载");
                }

                var downloadStartedAt = DateTime.UtcNow;
                await CpmsWorkflow.CompleteDownloadAndImportAsync(
                    chrome, serialNumber, workTabId, downloadListUrl, downloadStartedAt, WriteLog);
                WriteLog("全部完成。Chrome 保持打开，未关闭任何标签页。");
            }
            finally
            {
                await DisableAutoAcceptSafeAsync(chrome);
            }
        }
        finally
        {
            IsRunning = false;
        }
    }

    private static async Task DisableAutoAcceptSafeAsync(ChromeController chrome)
    {
        try { await chrome.DisableAutoAcceptDownloadsAsync(); } catch { }
    }

    private async Task WaitForExtensionAsync(ChromeController chrome, CancellationToken ct = default, int timeoutMs = 30000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try { await chrome.GetTabsAsync(ct); return; }
            catch (Exception ex) when (ex.Message.Contains("extension not connected", StringComparison.OrdinalIgnoreCase))
            { WriteLog("[1/7] 等待 Chrome 扩展连接..."); await Task.Delay(3000, ct); }
        }
        throw new InvalidOperationException("Chrome 扩展未连接。请刷新扩展并点击「重新连接」。");
    }

    private async Task<int?> GetOrNavigateTabAsync(ChromeController chrome, string url, string stepLabel)
    {
        var forceNewTab = string.Equals(
            Environment.GetEnvironmentVariable("CPMS_NEW_TAB"), "1", StringComparison.OrdinalIgnoreCase);

        if (!forceNewTab)
        {
            var tabs = await chrome.GetTabsAsync();
            if (tabs.HasValue && tabs.Value.ValueKind == JsonValueKind.Array)
            {
                int? cpmsTabId = null;
                int? activeTabId = null;

                foreach (var tab in tabs.Value.EnumerateArray())
                {
                    if (!tab.TryGetProperty("id", out var idProp)) continue;
                    var tabId = idProp.GetInt32();
                    var tabUrl = tab.TryGetProperty("url", out var urlProp) ? urlProp.GetString() ?? "" : "";
                    if (tabUrl.Contains("cpms.hq.cmcc", StringComparison.OrdinalIgnoreCase)) cpmsTabId = tabId;
                    if (tab.TryGetProperty("active", out var activeProp) && activeProp.GetBoolean()) activeTabId = tabId;
                }

                var reuseId = cpmsTabId ?? activeTabId;
                if (reuseId.HasValue)
                {
                    WriteLog($"{stepLabel} 复用已有标签页 (id={reuseId})，不关闭 Chrome");
                    await chrome.NavigateAsync(url, waitUntil: "spa", tabId: reuseId);
                    await ChromeAutomationHelpers.DelayAsync(8000);
                    return reuseId;
                }
            }
        }

        WriteLog($"{stepLabel} 在当前 Chrome 窗口打开新标签页（不关闭浏览器）");
        var created = await chrome.CommandAsync("createTab", new { url, active = true });
        return created?.TryGetProperty("id", out var newId) == true ? newId.GetInt32() : null;
    }

    private async Task LogPageDebugAsync(ChromeController chrome, int? tabId, string tag)
    {
        try
        {
            var buttons = await chrome.CommandAsync("cpmsListButtons", new { tabId });
            if (buttons?.TryGetProperty("buttons", out var arr) == true)
                WriteLog($"[调试] 页面按钮文字: {string.Join(" | ", arr.EnumerateArray().Select(e => e.GetString()))}");

            var exportBtn = await chrome.CommandAsync("cpmsHasExportButton", new { tabId });
            if (exportBtn?.TryGetProperty("found", out var found) == true)
                WriteLog($"[调试] 导出按钮: found={found.GetBoolean()}, url={exportBtn?.GetProperty("url")}");

            var info = await chrome.CommandAsync("getPageInfo", new { tabId });
            WriteLog($"[调试] 页面 URL: {info?.GetProperty("url")}");
        }
        catch
        {
            WriteLog($"[调试] 无法读取页面信息 ({tag})");
        }
    }

    private async Task StartBridgeIfNeededAsync(CancellationToken ct = default)
    {
        var port = int.TryParse(Environment.GetEnvironmentVariable("BRIDGE_PORT"), out var p) ? p : 9333;
        var testUrl = $"ws://127.0.0.1:{port}/";

        try
        {
            using var testWs = new ClientWebSocket();
            await testWs.ConnectAsync(new Uri(testUrl), ct);
            await testWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test", CancellationToken.None);
            WriteLog($"[Bridge] 外部桥接服务器已在端口 {port} 运行，无需启动");
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

        _ = app.RunAsync();
        WriteLog($"[Bridge] 内嵌桥接服务器已启动: ws://127.0.0.1:{port}");
        await Task.Delay(500, ct);
    }
}
