using System.Text.Json;
using ChromeAutomation.Client;

namespace ChromeAutomation.CpmsExport;

internal static class CpmsDiag
{
    public static async Task RunAsync(string? serial = null)
    {
        const string downloadListUrl = "http://cpms.hq.cmcc/pms/#/mops/tools/attachmentDownload/list";

        await using var chrome = new ChromeController();
        await chrome.ConnectAsync();
        await ChromeAutomationHelpers.DelayAsync(2000);
        try
        {
            await chrome.CommandAsync("reloadExtension", timeoutMs: 5000);
            await ChromeAutomationHelpers.DelayAsync(8000);
        }
        catch
        {
            /* 旧版扩展无 reloadExtension */
        }

        var tabs = await chrome.GetTabsAsync();
        int? tabId = null;
        if (tabs.HasValue && tabs.Value.ValueKind == JsonValueKind.Array)
        {
            foreach (var tab in tabs.Value.EnumerateArray())
            {
                if (!tab.TryGetProperty("id", out var idProp)) continue;
                var url = tab.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                if (url.Contains("attachmentDownload", StringComparison.OrdinalIgnoreCase))
                {
                    tabId = idProp.GetInt32();
                    break;
                }
                if (url.Contains("cpms.hq.cmcc", StringComparison.OrdinalIgnoreCase))
                {
                    tabId = idProp.GetInt32();
                }
            }
        }

        if (!tabId.HasValue)
        {
            var created = await chrome.CommandAsync("createTab", new { url = downloadListUrl, active = true });
            tabId = created?.TryGetProperty("id", out var nid) == true ? nid.GetInt32() : null;
        }
        else
        {
            await chrome.NavigateAsync(downloadListUrl, waitUntil: "spa", tabId: tabId);
        }

        await ChromeAutomationHelpers.DelayAsync(8000);

        if (string.IsNullOrEmpty(serial))
        {
            serial = await CpmsWorkflow.GetLatestSerialAsync(chrome, tabId);
        }

        Console.WriteLine($"流水号: {serial ?? "(null)"}");

        if (!string.IsNullOrEmpty(serial))
        {
            var diag = await chrome.CommandAsync("cpmsDiagRow", new { serialNumber = serial, tabId });
            Console.WriteLine($"cpmsDiagRow: {diag}");

            var resolved = await chrome.CommandAsync("cpmsResolveDownloadUrl", new { serialNumber = serial, tabId });
            Console.WriteLine($"cpmsResolveDownloadUrl: {resolved}");

            try
            {
                var apiProbe = await chrome.CommandAsync("cpmsApiProbe", new { serialNumber = serial, tabId });
                Console.WriteLine($"cpmsApiProbe: {apiProbe}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"cpmsApiProbe 跳过（扩展需重载 ≥1.2.0）: {ex.Message}");
            }

            try
            {
                var apiDownload = await chrome.CommandAsync(
                    "cpmsApiDownloadFile",
                    new { serialNumber = serial, tabId, recreateUrl = downloadListUrl },
                    timeoutMs: 120000);
                Console.WriteLine($"cpmsApiDownloadFile: {apiDownload}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"cpmsApiDownloadFile 跳过: {ex.Message}");
            }

            try
            {
                var planB = await chrome.CommandAsync(
                    "cpmsDownloadByClickPlanB",
                    new
                    {
                        serialNumber = serial,
                        tabId,
                        recreateUrl = downloadListUrl,
                        sinceMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    },
                    timeoutMs: 240000);
                Console.WriteLine($"cpmsDownloadByClickPlanB: {planB}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"cpmsDownloadByClickPlanB 跳过: {ex.Message}");
            }

            try
            {
                var sniff = await chrome.CommandAsync(
                    "cpmsSniffDownloadUrlOnClick",
                    new { serialNumber = serial, tabId },
                    timeoutMs: 120000);
                Console.WriteLine($"cpmsSniffDownloadUrlOnClick: {sniff}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"cpmsSniffDownloadUrlOnClick 跳过: {ex.Message}");
            }

            try
            {
                var probe = await chrome.CommandAsync(
                    "cpmsProbeDownloadAttempts",
                    new { serialNumber = serial, tabId, recreateUrl = downloadListUrl },
                    timeoutMs: 180000);
                Console.WriteLine($"cpmsProbeDownloadAttempts: {probe}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"cpmsProbeDownloadAttempts 跳过: {ex.Message}");
            }

            try
            {
                var dlBySerial = await chrome.CommandAsync(
                    "cpmsDownloadBySerial",
                    new { serialNumber = serial, tabId, recreateUrl = downloadListUrl },
                    timeoutMs: 180000);
                Console.WriteLine($"cpmsDownloadBySerial: {dlBySerial}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"cpmsDownloadBySerial 跳过: {ex.Message}");
            }
        }

        if (!string.IsNullOrEmpty(serial))
        {
            var downloadsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
            var httpPath = await CpmsHttpDownloader.TryDownloadBySerialAsync(
                serial,
                downloadsDir,
                DateTime.UtcNow.AddMinutes(-30));
            Console.WriteLine(httpPath is not null
                ? $"CpmsHttpDownloader: {httpPath}"
                : "CpmsHttpDownloader: 失败");
        }
    }
}
