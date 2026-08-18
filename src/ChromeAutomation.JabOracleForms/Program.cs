using ChromeAutomation.Client;
using ChromeAutomation.CpszzNavigate;

Console.WriteLine("=== JAB Oracle Forms: 查看 → 请求（测试） ===");
Console.WriteLine();

using var jab = new JabClient();
await jab.ConnectAsync();
Console.WriteLine("[JAB] 已连接");

var hwnd = await OracleFormsHelper.WaitForFormsHwndAsync(jab, Console.WriteLine, timeoutMs: 60000);
if (!hwnd.HasValue)
{
    Console.WriteLine("未找到 Oracle Forms 窗口");
    return;
}

var ok = await OracleFormsHelper.OpenViewRequestMenuAsync(jab, hwnd.Value, Console.WriteLine);
Console.WriteLine(ok ? "✓ 查看→请求 完成" : "✗ 查看→请求 失败");
