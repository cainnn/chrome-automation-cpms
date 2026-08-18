using ChromeAutomation.Client;

namespace ChromeAutomation.CpszzNavigate;

/// <summary>
/// Oracle Forms 内 JAB 自动化：查看→请求、导出按钮、等待落盘文件。
/// </summary>
public static class OracleFormsHelper
{
    public static async Task<long?> WaitForFormsHwndAsync(
        JabClient jab,
        Action<string>? log = null,
        int timeoutMs = 120000,
        CancellationToken ct = default)
    {
        void Log(string msg) => log?.Invoke(msg);

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var hwnd = await FindFormsHwndAsync(jab);
            if (hwnd.HasValue)
            {
                Log($"[ERP/JAB] Oracle Forms 窗口 hwnd={hwnd.Value}");
                return hwnd;
            }

            await Task.Delay(2000, ct);
        }

        Log("[ERP/JAB] 超时：未找到 Oracle Forms 窗口");
        return null;
    }

    public static async Task<long?> FindFormsHwndAsync(JabClient jab)
    {
        var jvms = await jab.EnumJvmsAsync();
        foreach (var jvm in jvms)
        {
            foreach (var win in jvm.windows)
            {
                if (win.name.Contains("Oracle Applications", StringComparison.OrdinalIgnoreCase)
                    || win.name.Contains("ERPPRD", StringComparison.OrdinalIgnoreCase)
                    || win.name.Contains("查询项目支出", StringComparison.OrdinalIgnoreCase))
                {
                    return win.hwnd;
                }
            }
        }

        var any = jvms.SelectMany(j => j.windows).FirstOrDefault();
        return any?.hwnd;
    }

    /// <summary>菜单 查看 → 请求（JAB 优先）。</summary>
    public static async Task<bool> OpenViewRequestMenuAsync(
        JabClient jab,
        long hwnd,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        void Log(string msg) => log?.Invoke(msg);

        var viewMenu = await jab.FindNodeAsync(hwnd, nameContains: "查看");
        if (viewMenu == null)
        {
            Log("[ERP/JAB] 未找到「查看」菜单");
            return false;
        }

        Log($"[ERP/JAB] 点击「查看」: {viewMenu.name}");
        if (!await ClickNodeAsync(jab, hwnd, viewMenu))
            return false;

        await Task.Delay(1200, ct);

        var requestItem = await jab.FindNodeAsync(hwnd, role: "menu item", nameContains: "请求")
            ?? await jab.FindNodeAsync(hwnd, nameContains: "请求(R)")
            ?? await jab.FindNodeAsync(hwnd, nameContains: "请求");

        if (requestItem == null)
        {
            Log("[ERP/JAB] 未找到「请求」菜单项");
            return false;
        }

        Log($"[ERP/JAB] 点击「请求」: {requestItem.name}");
        return await ClickNodeAsync(jab, hwnd, requestItem);
    }

    /// <summary>在 Forms 中尝试点击导出相关按钮。</summary>
    public static async Task<bool> TryClickExportButtonsAsync(
        JabClient jab,
        long hwnd,
        IEnumerable<string> buttonHints,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        void Log(string msg) => log?.Invoke(msg);

        foreach (var hint in buttonHints)
        {
            ct.ThrowIfCancellationRequested();

            var node = await jab.FindNodeAsync(hwnd, role: "push button", nameContains: hint)
                ?? await jab.FindNodeAsync(hwnd, nameContains: hint);

            if (node == null)
                continue;

            Log($"[ERP/JAB] 点击导出相关按钮: {node.name}");
            if (await ClickNodeAsync(jab, hwnd, node))
            {
                await Task.Delay(2000, ct);
                await TryClickConfirmAsync(jab, hwnd, log, ct);
                return true;
            }
        }

        Log("[ERP/JAB] 未找到可点击的导出按钮（可在 ERP_EXPORT_BUTTONS 中配置）");
        return false;
    }

    public static async Task<bool> TryClickConfirmAsync(
        JabClient jab,
        long hwnd,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        void Log(string msg) => log?.Invoke(msg);

        string[] confirms = ["确定", "OK", "是", "Yes", "保存", "Save", "运行", "Run"];
        foreach (var hint in confirms)
        {
            ct.ThrowIfCancellationRequested();
            var node = await jab.FindNodeAsync(hwnd, role: "push button", nameContains: hint)
                ?? await jab.FindNodeAsync(hwnd, nameContains: hint);
            if (node == null)
                continue;

            Log($"[ERP/JAB] 点击确认: {node.name}");
            if (await ClickNodeAsync(jab, hwnd, node))
            {
                await Task.Delay(1500, ct);
                return true;
            }
        }

        return false;
    }

    public static string? WaitForExportedFile(
        DateTime notBeforeUtc,
        Action<string>? log = null,
        int timeoutMs = 300000)
    {
        void Log(string msg) => log?.Invoke(msg);

        var downloadsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var patterns = new[] { "*.xlsx", "*.xls", "*.csv" };

        while (DateTime.UtcNow < deadline)
        {
            foreach (var pattern in patterns)
            {
                var file = Directory.GetFiles(downloadsDir, pattern, SearchOption.TopDirectoryOnly)
                    .Where(f => File.GetLastWriteTimeUtc(f) >= notBeforeUtc.AddSeconds(-5))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();

                if (file != null && new FileInfo(file).Length > 1024)
                {
                    Log($"[ERP] 检测到导出文件: {file}");
                    return file;
                }
            }

            Thread.Sleep(3000);
        }

        Log("[ERP] 等待导出文件超时");
        return null;
    }

    private static async Task<bool> ClickNodeAsync(JabClient jab, long hwnd, JabNode node)
    {
        if (await jab.DoActionAsync(node.vmId, node.ac))
            return true;

        return await jab.ClickNodeAsync(hwnd, name: node.name);
    }
}
