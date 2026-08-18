using System.Text.Json;
using ChromeAutomation.Client;
using ChromeAutomation.CpmsExport;

const string ReportUrl = "http://cpms.hq.cmcc/pms/#/mdat/wideTable/BigTableDefind";
const string DownloadListUrl = "http://cpms.hq.cmcc/pms/#/mops/tools/attachmentDownload/list";
var downloadsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

Console.WriteLine("=== CPMS 全流程 (导出 + acceptDanger 自动下载) ===");
Console.WriteLine($"监控目录: {downloadsDir}");
Console.WriteLine();

var existingFiles = Directory.GetFiles(downloadsDir, "*.zip")
    .Concat(Directory.GetFiles(downloadsDir, "*.xlsx"))
    .ToHashSet();

await using var chrome = new ChromeController();
await chrome.ConnectAsync();
Console.WriteLine("[1/7] 已连接桥接服务器");
await ChromeAutomationHelpers.DelayAsync(3000);

var deadline = DateTime.UtcNow.AddSeconds(30);
while (DateTime.UtcNow < deadline)
{
    try { await chrome.GetTabsAsync(); break; }
    catch (Exception ex) when (ex.Message.Contains("extension not connected", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("[1/7] 等待 Chrome 扩展连接...");
        await ChromeAutomationHelpers.DelayAsync(3000);
    }
}
Console.WriteLine("[1/7] Chrome 扩展已就绪");

// 开启自动确认危险下载
try
{
    await chrome.CommandAsync("enableAutoAcceptDownloads", new { durationMs = 600000 });
    Console.WriteLine("[1/7] 已启用自动确认危险下载 (10分钟)");
}
catch (Exception ex)
{
    Console.WriteLine($"[WARN] 启用自动确认失败: {ex.Message}");
}

Console.WriteLine($"[2/7] 定位报表页: {ReportUrl}");
var reportTabId = await CpmsWorkflow.EnsureReportPageAsync(chrome, ReportUrl, Console.WriteLine);
if (!reportTabId.HasValue)
    throw new InvalidOperationException("无法加载报表页，请确认已登录 CPMS");
Console.WriteLine($"[2/7] 报表页 (tab id={reportTabId})");

Console.WriteLine("[3/7] 点击「导出」并确认弹窗");
string? serialNumber;
try
{
    serialNumber = await CpmsWorkflow.ClickExportAndConfirmAsync(chrome, ReportUrl, reportTabId, Console.WriteLine);
}
catch (Exception ex)
{
    throw new InvalidOperationException($"导出失败: {ex.Message}", ex);
}
Console.WriteLine(serialNumber is not null
    ? $"[3/7] 导出流水号: {serialNumber}"
    : "[3/7] 未解析到流水号，将在下载列表页获取");

Console.WriteLine($"[4/7] 打开附件下载列表: {DownloadListUrl}");
int? downloadTabId;
try
{
    downloadTabId = await CpmsWorkflow.EnsureDownloadListPageAsync(
        chrome, DownloadListUrl, reportTabId, Console.WriteLine);
}
catch (TimeoutException)
{
    throw new TimeoutException("下载列表页加载超时");
}
downloadTabId ??= reportTabId;

if (string.IsNullOrEmpty(serialNumber))
{
    serialNumber = await CpmsWorkflow.GetLatestSerialAsync(chrome, downloadTabId);
    Console.WriteLine(serialNumber is not null
        ? $"[4/7] 使用列表最新流水号: {serialNumber}"
        : "[4/7] 将使用列表中第一条可下载任务");
}

await CpmsWorkflow.WaitForBackendReadyAsync(chrome, serialNumber, downloadTabId);

Console.WriteLine("[6/7] 开始下载");

var downloadStartedAt = DateTime.UtcNow;

// 策略 1: 扩展 blob-bypass
bool downloaded = false;
string? downloadedPath = null;
try
{
    Console.WriteLine("[6/7] 尝试 cpmsDownloadBySerial...");
    var result = await chrome.CommandAsync(
        "cpmsDownloadBySerial",
        new { serialNumber, tabId = downloadTabId, recreateUrl = DownloadListUrl },
        timeoutMs: 300000);
    CpmsDownloadDiagnostics.LogDownloadResult(result);
    if (result.HasValue)
    {
        var method = result?.TryGetProperty("method", out var m) == true ? m.GetString() ?? "" : "";
        Console.WriteLine($"[6/7] 下载策略: {method}");

        // 接受任何有效下载策略
        var path = ResolveDownloadPath(result.Value, downloadsDir);
        if (path is not null && File.Exists(path))
        {
            Console.WriteLine($"[6/7] 下载完成: {path}");
            downloadedPath = path;
            downloaded = true;
        }
        else
        {
            // 扩展下载成功但文件名可能不匹配，扫描整个 Downloads 目录
            Console.WriteLine("[6/7] 扩展返回成功但文件路径不匹配，扫描目录...");
            var recent = Directory.GetFiles(downloadsDir, "*.zip")
                .Concat(Directory.GetFiles(downloadsDir, "*.xlsx"))
                .Where(f => !existingFiles.Contains(f) && !f.EndsWith(".crdownload"))
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .FirstOrDefault();
            if (recent is not null)
            {
                Console.WriteLine($"[6/7] 找到新文件: {recent}");
                downloadedPath = recent;
                downloaded = true;
            }
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"[6/7] cpmsDownloadBySerial 失败: {ex.Message}");
}

// 策略 2: 原生点击 + acceptDanger 自动确认
if (!downloaded)
{
    Console.WriteLine("[6/7] 尝试原生点击 + 自动 acceptDanger...");

    // 确保自动确认已开启
    try
    {
        await chrome.CommandAsync("enableAutoAcceptDownloads", new { durationMs = 600000 });
    }
    catch { }

    try
    {
        var clickResult = await chrome.CommandAsync("cpmsClickDownload",
            new { serialNumber, tabId = downloadTabId }, timeoutMs: 30000);
        Console.WriteLine($"[6/7] 点击结果: {JsonSerializer.Serialize(clickResult)}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[6/7] 点击失败: {ex.Message}");
    }

    Console.WriteLine("[6/7] 等待文件落盘...");
    var waitDeadline = DateTime.UtcNow.AddMinutes(3);
    while (DateTime.UtcNow < waitDeadline)
    {
        // 检查 .crdownload（下载中）
        var crdownloads = Directory.GetFiles(downloadsDir, "*.crdownload");
        if (crdownloads.Length > 0)
        {
            Console.WriteLine($"  下载中: {Path.GetFileName(crdownloads[0])}");
        }

        var found = Directory.GetFiles(downloadsDir, "*.zip", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(downloadsDir, "*.xlsx", SearchOption.TopDirectoryOnly))
            .Where(f => !f.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase))
            .Where(f => !existingFiles.Contains(f))
            .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
            .FirstOrDefault();

        if (found is not null)
        {
            var fi = new FileInfo(found);
            Console.WriteLine($"[6/7] 文件已下载: {found}");
            Console.WriteLine($"     大小: {fi.Length / 1024.0:F1} KB");
            downloadedPath = found;
            downloaded = true;
            break;
        }

        Console.Write(".");
        await Task.Delay(3000);
    }
}

// 关闭自动确认
try { await chrome.CommandAsync("disableAutoAcceptDownloads"); } catch { }

if (!downloaded || string.IsNullOrEmpty(downloadedPath))
{
    Console.WriteLine("[6/7] 下载超时");
    Environment.Exit(1);
    return;
}

// --- 步骤 7: 解压 + 导入数据库 ---
Console.WriteLine();
Console.WriteLine("[7/7] 导入数据库");

var excelPath = CpmsWorkflow.ResolveExcelPath(downloadedPath);
Console.WriteLine($"[7/7] Excel: {excelPath}");

await CpmsWorkflow.RunDatabaseImportAsync(excelPath);

Console.WriteLine();
Console.WriteLine("=== 全流程完成 ===");
Console.WriteLine("Chrome 保持打开，未关闭任何标签页。");

void ctsMaybeCancel(object? _) { }

static string? ResolveDownloadPath(JsonElement result, string downloadsDir)
{
    if (result.TryGetProperty("filename", out var fn))
    {
        var name = fn.GetString();
        if (!string.IsNullOrWhiteSpace(name))
        {
            if (Path.IsPathRooted(name) && File.Exists(name)) return name;
            var combined = Path.Combine(downloadsDir, name);
            if (File.Exists(combined)) return combined;
        }
    }
    return null;
}
