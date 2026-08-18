using System.Net.WebSockets;
using System.Text.Json;
using ChromeAutomation.Bridge;
using ChromeAutomation.Client;
using ChromeAutomation.Import;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace ChromeAutomation.CpszzNavigate;

/// <summary>
/// ERP 门户 → Oracle Forms → 导出支出明细 → 数据库导入。
/// </summary>
public static class ErpWorkflow
{
    public static async Task RunAsync(
        ErpSettings? settings = null,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        settings ??= ErpSettings.FromEnvironment();
        void Log(string msg) => log?.Invoke(msg);

        await using var chrome = new ChromeController();
        await chrome.ConnectAsync(ct);
        Log("[1/9] 已连接桥接服务器");

        await WaitForExtensionAsync(chrome, Log, ct);
        Log("[1/9] Chrome 扩展已就绪");

        Log($"[2/9] 打开门户: {settings.PortalUrl}");
        var workTabId = await GetOrNavigateTabAsync(chrome, settings.PortalUrl, "cpszz.hq.cmcc", Log);
        await ChromeAutomationHelpers.DelayAsync(5000, ct);
        Log("[2/9] 门户页面已加载");

        Log("[3/9] 点击「核心ERP系统」");
        try
        {
            await chrome.ClickByTextAsync("核心ERP系统", exact: true, tabId: workTabId);
        }
        catch (Exception ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            Log("[3/9] 精确匹配失败，尝试模糊匹配...");
            await chrome.ClickByTextAsync("核心ERP", exact: false, tabId: workTabId);
        }

        await ChromeAutomationHelpers.DelayAsync(8000, ct);
        workTabId = await FindErpTabAsync(chrome, workTabId, Log);

        Log($"[4/9] 展开菜单树: {settings.TreeExpandText}");
        try
        {
            await chrome.CommandAsync("activateTab", new { tabId = workTabId });
            await ChromeAutomationHelpers.DelayAsync(2000, ct);
        }
        catch { }

        await ErpUiaHelper.ClickElementByTextInChromeAsync(settings.TreeExpandText, exact: false, timeoutMs: 10000);
        await ChromeAutomationHelpers.DelayAsync(4000, ct);

        await ClickCuxLinkWithRetryAsync(chrome, workTabId, settings, Log, ct);

        Log("[5/9] 已点击 CUX，等待 Oracle Forms 启动");
        await ChromeAutomationHelpers.DelayAsync(5000, ct);

        await LaunchOracleFormsAsync(Log, ct);

        if (settings.StopAtForms)
        {
            using var jab = new JabClient();
            await jab.ConnectAsync();
            var hwnd = await OracleFormsHelper.WaitForFormsHwndAsync(jab, Log, timeoutMs: 120000, ct: ct);
            if (!hwnd.HasValue)
                throw new InvalidOperationException("未找到 Oracle Forms 窗口");
            Log($"[完成] Oracle Forms 已打开 (hwnd={hwnd.Value})");
            return;
        }

        var exportStartedAt = DateTime.UtcNow;
        await ExportFromOracleFormsAsync(settings, Log, ct);

        var excelPath = OracleFormsHelper.WaitForExportedFile(exportStartedAt, Log);
        if (excelPath == null)
            throw new FileNotFoundException("未在 Downloads 目录检测到 ERP 导出文件");

        if (settings.SkipImport)
        {
            Log($"[9/9] 跳过导入（ERP_SKIP_IMPORT=1），文件: {excelPath}");
            return;
        }

        await RunDatabaseImportAsync(excelPath, Log, ct);
        Log("[9/9] ERP 导出并导入完成");
    }

    private static async Task ClickCuxLinkWithRetryAsync(
        ChromeController chrome,
        int? workTabId,
        ErpSettings settings,
        Action<string> log,
        CancellationToken ct,
        int maxAttempts = 5)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            log($"[5/9] 点击「{settings.CuxLinkText}」（第 {attempt}/{maxAttempts} 次）");

            try
            {
                await chrome.CommandAsync("activateTab", new { tabId = workTabId });
                await ChromeAutomationHelpers.DelayAsync(1500, ct);
            }
            catch { }

            var cuxClicked = await ErpUiaHelper.ClickElementByTextInChromeAsync(
                settings.CuxLinkText, exact: false, timeoutMs: 15000);

            if (!cuxClicked)
            {
                log("[5/9] UIA 未找到，回退扩展 clickByText...");
                try
                {
                    await chrome.ClickByTextAsync(settings.CuxLinkText, exact: false, tabId: workTabId);
                    cuxClicked = true;
                }
                catch
                {
                    await chrome.ClickByTextAsync("查询项目支出", exact: false, tabId: workTabId);
                    cuxClicked = true;
                }
            }

            if (!cuxClicked)
                throw new InvalidOperationException("无法点击 CUX 链接，请确认菜单树已展开");

            await ChromeAutomationHelpers.DelayAsync(3000, ct);

            if (!await ErpUiaHelper.HasRequestProcessingErrorAsync(chrome, workTabId, ct: ct))
            {
                log("[5/9] CUX 点击成功，未检测到请求错误");
                return;
            }

            log($"[5/9] 检测到「{ErpUiaHelper.RequestProcessingError}」，将再次点击");
            await ChromeAutomationHelpers.DelayAsync(2000, ct);
        }

        throw new InvalidOperationException(
            $"CUX 点击 {maxAttempts} 次后仍出现「{ErpUiaHelper.RequestProcessingError}」");
    }

    private static async Task LaunchOracleFormsAsync(Action<string> log, CancellationToken ct)
    {
        log("[6/9] 等待 Oracle Forms (JNLP) 启动");

        if (IsJavaRunning(log))
        {
            log("[6/9] Java 进程已运行");
        }
        else
        {
            log("[6/9] 处理 Chrome 不安全下载提示");
            var kept = await ErpUiaHelper.ClickKeepInDownloadPanelAsync(fileHint: ".jnlp", timeoutMs: 60000);
            log(kept ? "[6/9] 已保留 JNLP 文件" : "[6/9] 未检测到下载提示");

            if (!IsJavaRunning(log))
            {
                log("[6/9] 运行 JNLP 文件");
                var ran = await ErpUiaHelper.RunDownloadedJnlpAsync();
                log(ran ? "[6/9] JNLP 已启动" : "[6/9] 未找到 JNLP 文件");
            }
        }

        log("[6/9] 处理 Java 安全弹窗");
        var handled = await ErpUiaHelper.HandleJavaSecurityDialogAsync(timeoutMs: 120000, maxClickAttempts: 20);
        log(handled ? "[6/9] Java 安全弹窗已处理" : "[6/9] 未检测到 Java 安全弹窗");
    }

    private static async Task ExportFromOracleFormsAsync(
        ErpSettings settings,
        Action<string> log,
        CancellationToken ct)
    {
        log("[7/9] 连接 Java Access Bridge");
        using var jab = new JabClient();
        await jab.ConnectAsync();

        log("[7/9] 等待 Oracle Forms 窗口");
        var hwnd = await OracleFormsHelper.WaitForFormsHwndAsync(jab, log, timeoutMs: 120000, ct: ct);
        if (!hwnd.HasValue)
            throw new InvalidOperationException("未找到 Oracle Forms 窗口");

        log("[8/9] 菜单 查看 → 请求");
        var menuOk = await OracleFormsHelper.OpenViewRequestMenuAsync(jab, hwnd.Value, log, ct);
        if (!menuOk)
            throw new InvalidOperationException("无法打开「查看→请求」菜单");

        await Task.Delay(3000, ct);

        log("[8/9] 尝试点击导出按钮");
        var exportOk = await OracleFormsHelper.TryClickExportButtonsAsync(
            jab, hwnd.Value, settings.ExportButtonHints, log, ct);

        if (!exportOk)
            log("[8/9] 未自动点击导出按钮；若 Forms 已打开请求窗口，请手动导出后等待文件落盘");
    }

    public static async Task RunDatabaseImportAsync(
        string excelPath,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        void Log(string msg) => log?.Invoke(msg);
        ct.ThrowIfCancellationRequested();

        Log($"[9/9] 导入 Excel: {excelPath}");

        var result = await ImportRunner.RunAsync(
            excelPath,
            connectionString: Environment.GetEnvironmentVariable("NET_IMPORT_CONNECTION"),
            asposeLicensePath: Environment.GetEnvironmentVariable("ASPOSE_LICENSE_PATH"),
            log: Log);

        if (result.HasError)
            throw new InvalidOperationException($"数据库导入失败: {result.ErrorMessage}");
    }

    private static async Task<int?> GetOrNavigateTabAsync(
        ChromeController chrome,
        string url,
        string urlMatch,
        Action<string>? log)
    {
        var tabs = await chrome.GetTabsAsync();
        int? workTabId = null;

        if (tabs?.ValueKind == JsonValueKind.Array)
        {
            int? activeTabId = null;
            foreach (var tab in tabs.Value.EnumerateArray())
            {
                if (!tab.TryGetProperty("id", out var idProp)) continue;
                var tabId = idProp.GetInt32();
                var tabUrl = tab.TryGetProperty("url", out var tabUrlProp) ? tabUrlProp.GetString() ?? "" : "";

                if (tabUrl.Contains(urlMatch, StringComparison.OrdinalIgnoreCase))
                    workTabId = tabId;

                if (tab.TryGetProperty("active", out var activeProp) && activeProp.GetBoolean())
                    activeTabId = tabId;
            }

            workTabId ??= activeTabId;
        }

        if (workTabId.HasValue)
        {
            log?.Invoke($"  复用标签页 (id={workTabId})");
            await chrome.NavigateAsync(url, waitUntil: "load", tabId: workTabId);
        }
        else
        {
            log?.Invoke("  打开新标签页");
            var created = await chrome.CommandAsync("createTab", new { url, active = true });
            workTabId = created?.TryGetProperty("id", out var newId) == true ? newId.GetInt32() : null;
        }

        return workTabId;
    }

    private static async Task<int?> FindErpTabAsync(
        ChromeController chrome,
        int? previousTabId,
        Action<string>? log)
    {
        await ChromeAutomationHelpers.DelayAsync(2000);
        var tabs = await chrome.GetTabsAsync();
        if (tabs?.ValueKind != JsonValueKind.Array) return previousTabId;

        int? erpTabId = null;
        foreach (var tab in tabs.Value.EnumerateArray())
        {
            if (!tab.TryGetProperty("id", out var idProp)) continue;
            var tabId = idProp.GetInt32();
            var tabUrl = tab.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";

            if (tabUrl.Contains("erp.hq.cmcc", StringComparison.OrdinalIgnoreCase)
                || tabUrl.Contains("OA_HTML", StringComparison.OrdinalIgnoreCase))
            {
                if (erpTabId == null || tabId != previousTabId)
                    erpTabId = tabId;
            }
        }

        if (erpTabId.HasValue && erpTabId != previousTabId)
        {
            log?.Invoke($"  检测到 ERP 标签页 (id={erpTabId})");
            return erpTabId;
        }

        return previousTabId;
    }

    private static async Task WaitForExtensionAsync(
        ChromeController chrome,
        Action<string>? log,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(30000);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await chrome.GetTabsAsync();
                return;
            }
            catch (Exception ex) when (ex.Message.Contains("extension not connected", StringComparison.OrdinalIgnoreCase))
            {
                log?.Invoke("[1/9] 等待 Chrome 扩展连接...");
                await ChromeAutomationHelpers.DelayAsync(3000, ct);
            }
        }

        throw new InvalidOperationException("Chrome 扩展未连接。请刷新扩展并点击「重新连接」。");
    }

    private static bool IsJavaRunning(Action<string>? log)
    {
        try
        {
            var javaws = System.Diagnostics.Process.GetProcessesByName("javaws");
            if (javaws.Length > 0)
            {
                foreach (var p in javaws)
                    log?.Invoke($"  javaws PID={p.Id}");
                return true;
            }

            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='java.exe'");
            foreach (var obj in searcher.Get())
            {
                var cmdLine = obj["CommandLine"]?.ToString() ?? "";
                if (cmdLine.Contains("oracle", StringComparison.OrdinalIgnoreCase)
                    || cmdLine.Contains("forms", StringComparison.OrdinalIgnoreCase)
                    || cmdLine.Contains("frmservlet", StringComparison.OrdinalIgnoreCase))
                {
                    var pid = Convert.ToInt32(obj["ProcessId"]);
                    log?.Invoke($"  Oracle Forms Java PID={pid}");
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}
