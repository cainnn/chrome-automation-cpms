// Quick diagnostic: get ERP page text and take screenshot
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

Console.WriteLine("=== ERP 页面诊断 ===");

using var ws = new ClientWebSocket();
await ws.ConnectAsync(new Uri("ws://127.0.0.1:9333/"), CancellationToken.None);

// Get tabs
var tabsResp = await SendAsync(ws, "getTabs", new { });
int? erpTabId = null;
if (tabsResp.ValueKind == JsonValueKind.Array)
{
    foreach (var tab in tabsResp.EnumerateArray())
    {
        var id = tab.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;
        var url = tab.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
        var title = tab.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
        var active = tab.TryGetProperty("active", out var a) && a.GetBoolean();
        Console.WriteLine($"  Tab {id}: active={active} title='{title}' url={url[..Math.Min(url.Length, 80)]}");
        if (url.Contains("erp.hq.cmcc")) erpTabId = id;
    }
}

if (erpTabId == null)
{
    Console.WriteLine("未找到 ERP 标签页");
    return;
}

Console.WriteLine($"\nERP tab id={erpTabId}");

// Try clickByText with detailed response
foreach (var text in new[] { "303310PA", "广西全省", "查询项目支出", "CUX", "主页", "导航" })
{
    Console.WriteLine($"\n--- clickByText '{text}' ---");
    try
    {
        var resp = await SendAsync(ws, "clickByText", new { text, exact = false, tabId = erpTabId, timeoutMs = 5000 });
        Console.WriteLine($"  Response: {resp}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Error: {ex.Message}");
    }
}

// Try to get page text via runInTab
Console.WriteLine("\n--- getVisibleText ---");
try
{
    var textResp = await SendAsync(ws, "getVisibleText", new { tabId = erpTabId, timeoutMs = 5000 });
    Console.WriteLine($"  Text response: {textResp}");
}
catch (Exception ex)
{
    Console.WriteLine($"  Error: {ex.Message}");
}

// Try getText
Console.WriteLine("\n--- getText ---");
try
{
    var textResp = await SendAsync(ws, "getText", new { tabId = erpTabId, timeoutMs = 5000 });
    Console.WriteLine($"  Text response: {textResp}");
}
catch (Exception ex)
{
    Console.WriteLine($"  Error: {ex.Message}");
}

// Take screenshot via FlaUI
Console.WriteLine("\n--- 截图 ---");
System.Drawing.Bitmap? bmp = null;
await Task.Run(() =>
{
    using var automation = new FlaUI.UIA3.UIA3Automation();
    var cf = automation.ConditionFactory;
    var desktop = automation.GetDesktop();
    var windows = desktop.FindAllChildren(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Window));
    foreach (var win in windows)
    {
        if (win.ClassName != "Chrome_WidgetWin_1") continue;
        var winName = win.Name ?? "";
        if (winName.Contains("Edge")) continue;
        Console.WriteLine($"  Chrome window: '{winName}'");

        // Check all descendant text
        var allEls = win.FindAllDescendants();
        var texts = new List<string>();
        foreach (var el in allEls)
        {
            var name = el.Name ?? "";
            if (!string.IsNullOrWhiteSpace(name) && name.Length < 200)
                texts.Add($"[{el.ControlType}] {name}");
        }
        Console.WriteLine($"  UIA elements ({texts.Count}):");
        foreach (var t in texts.Take(50))
            Console.WriteLine($"    {t}");
        if (texts.Count > 50)
            Console.WriteLine($"    ... and {texts.Count - 50} more");

        // Screenshot
        bmp = new System.Drawing.Bitmap(1920, 1080);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.CopyFromScreen(0, 0, 0, 0, bmp.Size);
        var screenshotPath = "screenshot_erp_diagnostic.png";
        bmp.Save(screenshotPath);
        Console.WriteLine($"  截图已保存: {screenshotPath}");
        bmp.Dispose();
        break;
    }
});

async Task<JsonElement> SendAsync(ClientWebSocket ws, string action, object @params)
{
    var id = Guid.NewGuid().ToString("N")[..8];
    var msg = JsonSerializer.Serialize(new { id, action, @params });
    var bytes = Encoding.UTF8.GetBytes(msg);
    await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);

    var buffer = new byte[16384];
    var sb = new StringBuilder();
    while (true)
    {
        var recv = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        sb.Append(Encoding.UTF8.GetString(buffer, 0, recv.Count));
        if (recv.EndOfMessage)
        {
            var doc = JsonDocument.Parse(sb.ToString());
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var respId) && respId.GetString() == id)
            {
                // Return full response for diagnostics
                return root.Clone();
            }
            sb.Clear();
        }
    }
}
