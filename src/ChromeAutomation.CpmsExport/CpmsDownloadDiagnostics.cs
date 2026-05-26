using System.Text;
using System.Text.Json;

namespace ChromeAutomation.CpmsExport;

/// <summary>
/// 字段运行中观测到的下载失败模式与排查提示（来自 last-run.log / 终端输出）。
/// </summary>
public static class CpmsDownloadDiagnostics
{
    public static string LogFilePath =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "last-run.log");

    private static readonly (string Pattern, string Hint)[] KnownIssues =
    [
        ("click-only", "扩展只完成了页面点击，未解析到可 fetch 的下载 URL。请刷新扩展 ≥1.1.0，并确认列表行状态为「后台下载成功」。"),
        ("acceptDanger", "chrome.downloads.acceptDanger 在 MV3 service worker 中不可用，且只会再弹确认框。应使用 blob-bypass，勿依赖 acceptDanger。"),
        ("operationLog/add", "误捕获了操作日志接口 URL，已在扩展中过滤。若仍出现，请检查 cpmsResolveDownloadUrl。"),
        ("getAttachmentDownloadInfoList", "这是列表查询 API，不是文件下载地址。"),
        ("Request timeout", "扩展命令超时：请在 chrome://extensions/ 重新加载扩展，并确认标签页在「我的下载」列表而非 chrome:// 页。"),
        ("Action failed in all frames", "页面脚本未在正确 frame 执行：请导航到 attachmentDownload/list 并等待表格渲染。"),
        ("clickByText", "回退点击「下载」失败：请改用 cpmsDownloadBySerial（blob 旁路）。"),
        ("no-download-url", "被动解析与网络嗅探均未获得下载 URL。"),
        ("blob-bypass failed", "已拿到 URL 但 fetch 失败：检查内网登录 Cookie 或 URL 是否需 POST。"),
    ];

    public static void AppendRunLog(string line)
    {
        try
        {
            var path = Path.GetFullPath(LogFilePath);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // 日志写入失败不影响主流程
        }
    }

    public static void LogDownloadResult(JsonElement? result, Exception? ex = null)
    {
        if (ex is not null)
        {
            Console.WriteLine($"[6/7] 异常: {ex.Message}");
            AppendRunLog($"[{DateTime.Now:O}] ERROR {ex.Message}");
            PrintHintsForText(ex.Message);
            return;
        }

        if (result is null)
        {
            return;
        }

        if (result.Value.TryGetProperty("method", out var method))
        {
            var m = method.GetString() ?? "";
            Console.WriteLine($"[6/7] 下载策略: {m}");
            AppendRunLog($"[{DateTime.Now:O}] method={m}");
            PrintHintsForText(m);
        }

        if (result.Value.TryGetProperty("filename", out var filename))
        {
            Console.WriteLine($"[6/7] 目标文件: {filename.GetString()}");
        }

        if (result.Value.TryGetProperty("sourceUrl", out var sourceUrl))
        {
            Console.WriteLine($"[6/7] 下载 URL: {sourceUrl.GetString()}");
        }

        if (result.Value.TryGetProperty("error", out var error))
        {
            var err = error.GetString() ?? "";
            Console.WriteLine($"[6/7] 下载错误: {err}");
            AppendRunLog($"[{DateTime.Now:O}] error={err}");
            PrintHintsForText(err);
        }

        if (result.Value.TryGetProperty("captured", out var captured) &&
            captured.ValueKind == JsonValueKind.Array)
        {
            var urls = string.Join(
                ", ",
                captured.EnumerateArray()
                    .Select(e => e.GetString())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Take(8));
            if (!string.IsNullOrEmpty(urls))
            {
                Console.WriteLine($"[6/7] 捕获 URL: {urls}");
                AppendRunLog($"[{DateTime.Now:O}] captured={urls}");
                PrintHintsForText(urls);
            }
        }

        if (result.Value.TryGetProperty("candidates", out var candidates) &&
            candidates.ValueKind == JsonValueKind.Array)
        {
            var urls = string.Join(
                ", ",
                candidates.EnumerateArray()
                    .Select(e => e.GetString())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Take(8));
            if (!string.IsNullOrEmpty(urls))
            {
                Console.WriteLine($"[6/7] 候选 URL: {urls}");
            }
        }
    }

    private static void PrintHintsForText(string text)
    {
        foreach (var (pattern, hint) in KnownIssues)
        {
            if (text.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[6/7] 提示: {hint}");
            }
        }
    }
}
