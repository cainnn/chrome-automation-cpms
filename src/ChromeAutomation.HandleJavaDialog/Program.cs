using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.IO;

// Mode 1: click-erp   — click "核心ERP系统" on the portal page, then wait for Oracle Forms menu
// Mode 2: click-cux   — click "CUX:查询项目支出" via UIA
// Mode 3: handle-java — handle Java security dialog (checkbox + Run)
// Mode 4: full-flow   — do all of the above in sequence
// Mode 5: diagnose    — dump ERP page content for analysis

var mode = args.Length > 0 ? args[0] : "full-flow";
Console.WriteLine($"=== ERP Navigate Tool (mode={mode}) ===");

switch (mode)
{
    case "click-erp":
        await ClickErpLinkAsync();
        break;
    case "expand-tree":
        await ExpandTreeNodeAsync();
        break;
    case "click-cux":
        await ClickCuxMenuAsync();
        break;
    case "handle-java":
        await HandleJavaSecurityDialogViaFlaUIAsync();
        break;
    case "click-java-dialog":
        HandleJavaDialog.ClickJavaDialog.Run();
        break;
    case "playwright":
        await PlaywrightFlowAsync();
        break;
    case "diagnose":
        await DiagnoseErpPageAsync();
        break;
    case "full-flow":
    default:
        // Step 1: Try launching existing JNLP file first (bypass tree navigation)
        Console.WriteLine("[1] 检查现有 JNLP 文件...");
        var jnlpLaunchedDirect = await FindAndLaunchJnlpAsync();

        if (jnlpLaunchedDirect)
        {
            // Step 2: Handle Java security dialog
            Console.WriteLine("[2] 处理 Java 安全弹窗...");
            await HandleJavaSecurityDialogViaFlaUIAsync();

            // Step 3: Verify Oracle Forms launched
            Console.WriteLine("[3] 等待 Oracle Forms 启动...");
            await Task.Delay(15000);
            var javaProcs = Process.GetProcessesByName("java")
                .Concat(Process.GetProcessesByName("javaw"))
                .ToArray();
            if (javaProcs.Length > 0)
            {
                foreach (var jp in javaProcs)
                    Console.WriteLine($"  ✓ Java 进程: '{jp.MainWindowTitle}' (pid={jp.Id})");
                Console.WriteLine("=== JNLP 启动流程完成 ===");
            }
            else
            {
                Console.WriteLine("  未检测到 Java 进程（可能仍在启动中）");
            }
            break;
        }

        // No JNLP file found — fall back to full ERP navigation
        Console.WriteLine("[1b] 未找到 JNLP 文件，执行完整 ERP 导航流程...");
        await ClickErpLinkAsync();

        // Step 2: Wait for ERP page to load, then activate the ERP tab + focus Chrome
        Console.WriteLine("[2] 等待 ERP 页面加载...");
        await Task.Delay(8000);
        await ActivateErpTabAsync();
        FocusChromeWindow();

        // Step 3: Expand tree node — try bridge CSS selector first, then UIA
        Console.WriteLine("[3] 展开树节点...");
        var expanded = await ExpandTreeViaBridgeAsync();
        if (!expanded)
        {
            Console.WriteLine("[3b] Bridge 展开 failed, 尝试 UIA...");
            await ExpandTreeNodeAsync();
            await Task.Delay(3000);
        }
        else
        {
            Console.WriteLine("[3b] Bridge 展开成功，等待加载...");
            await Task.Delay(3000);
        }

        // Step 3c: Verify expansion — check if CUX items appeared
        Console.WriteLine("[3c] 验证展开结果...");
        await VerifyTreeExpansionAsync();

        // Step 4: Click CUX menu item — try bridge first, then UIA fallback
        Console.WriteLine("[4] 点击 CUX 菜单...");
        var cuxClicked = await ClickCuxViaBridgeAsync();
        if (!cuxClicked)
        {
            Console.WriteLine("[4b] Bridge CUX click failed, 尝试 UIA...");
            await ClickCuxMenuAsync();
        }

        // Step 5: Wait for JNLP download, then launch it
        Console.WriteLine("[5] 等待 JNLP 下载...");
        var jnlpLaunched = await FindAndLaunchJnlpAsync();

        // Step 6: Handle Java security dialog
        Console.WriteLine("[6] 处理 Java 安全弹窗...");
        await HandleJavaSecurityDialogViaFlaUIAsync();

        // Step 7: Verify Oracle Forms launched
        if (jnlpLaunched)
        {
            Console.WriteLine("[7] 等待 Oracle Forms 启动...");
            await Task.Delay(10000);
            var javaProcs = Process.GetProcessesByName("java")
                .Concat(Process.GetProcessesByName("javaw"))
                .Where(p => !string.IsNullOrEmpty(p.MainWindowTitle))
                .ToArray();
            if (javaProcs.Length > 0)
            {
                foreach (var jp in javaProcs)
                    Console.WriteLine($"  ✓ Java 窗口: '{jp.MainWindowTitle}' (pid={jp.Id})");
            }
            else
            {
                Console.WriteLine("  未检测到 Oracle Forms 窗口（可能仍在启动中）");
            }
        }

        Console.WriteLine("=== 全流程完成 ===");
        break;
}

async Task ClickErpLinkAsync()
{
    Console.WriteLine("[1] 点击「核心ERP系统」");
    using var ws = new ClientWebSocket();
    await ws.ConnectAsync(new Uri("ws://127.0.0.1:9333/"), CancellationToken.None);

    // Get tabs
    var tabsResp = await SendAsync(ws, "getTabs", new { });
    Console.WriteLine($"  Tabs: {tabsResp}");

    // Find the portal page tab (has "核心ERP系统" link)
    int? portalTabId = null;
    int? activeTabId = null;
    if (tabsResp.ValueKind == JsonValueKind.Array)
    {
        foreach (var tab in tabsResp.EnumerateArray())
        {
            var id = tab.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;
            var url = tab.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            var active = tab.TryGetProperty("active", out var a) && a.GetBoolean();

            // Prefer cpszz portal page (it has "核心ERP系统" link)
            if (url.Contains("cpszz", StringComparison.OrdinalIgnoreCase))
                portalTabId = id;
            if (active) activeTabId = id;
        }
    }
    // Fallback to any tab that's not erp already
    portalTabId ??= activeTabId;

    if (portalTabId == null)
    {
        Console.WriteLine("  未找到 ERP 门户标签页");
        return;
    }

    Console.WriteLine($"  使用标签页 id={portalTabId}");

    // Click "核心ERP系统"
    try
    {
        var clickResp = await SendAsync(ws, "clickByText", new { text = "核心ERP系统", exact = false, tabId = portalTabId, timeoutMs = 10000 });
        Console.WriteLine($"  点击结果: {clickResp}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  点击失败: {ex.Message}");
    }
}

async Task ExpandTreeNodeAsync()
{
    Console.WriteLine("[展开] 通过 UIA 查找并点击 '展开 303310PA_广西全省_项目查询岗' Hyperlink");
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

            // Only search in Chrome windows showing ERP content
            bool isErpWindow = winName.StartsWith("主页") || winName.Contains("Oracle")
                || winName.Contains("电子商务套件") || winName.Contains("ERP");
            if (!isErpWindow)
            {
                Console.WriteLine($"  跳过非 ERP 窗口: '{winName}'");
                continue;
            }

            Console.WriteLine($"  检查 ERP 窗口: '{winName}'");

            var allEls = win.FindAllDescendants();

            // Priority 1: Find the Hyperlink element with "展开 303310PA_广西全省_项目查询岗"
            // This is the expand link we need to click
            var expandLinks = allEls.Where(el =>
            {
                var name = el.Name ?? "";
                return el.ControlType == FlaUI.Core.Definitions.ControlType.Hyperlink
                    && name.Contains("展开")
                    && name.Contains("303310PA_广西全省_项目查询岗")
                    && !name.Contains("(TD)");
            }).ToList();

            if (expandLinks.Count > 0)
            {
                var target = expandLinks[0];
                var r = target.BoundingRectangle;
                Console.WriteLine($"  找到展开链接: '{target.Name}' [{target.ControlType}] at ({(int)r.Left},{(int)r.Top}) size({(int)r.Width}x{(int)r.Height})");

                // Strategy 1: Win32 PostMessage click (generates trusted events via Chrome's input pipeline!)
                Console.WriteLine("  [策略1] Win32 PostMessage 点击...");
                try
                {
                    var hwnd = win.Properties.NativeWindowHandle.Value;
                    if (hwnd != IntPtr.Zero)
                    {
                        var x = (int)(r.Left + 12);  // left edge = expand icon
                        var y = (int)(r.Top + r.Height / 2);

                        var point = new POINT { X = x, Y = y };
                        ScreenToClient(hwnd, ref point);
                        IntPtr lParam = (IntPtr)((point.Y << 16) | (point.X & 0xFFFF));

                        Console.WriteLine($"  PostMessage hwnd={hwnd} screen=({x},{y}) client=({point.X},{point.Y})");
                        PostMessage(hwnd, 0x0201, IntPtr.Zero, lParam); // WM_LBUTTONDOWN
                        Thread.Sleep(50);
                        PostMessage(hwnd, 0x0202, IntPtr.Zero, lParam); // WM_LBUTTONUP
                        Thread.Sleep(1500);

                        // Check if expansion happened
                        var allEls2 = win.FindAllDescendants();
                        var collapsed = allEls2.Any(el =>
                        {
                            try { var name = el.Name ?? ""; return name.Contains("收起") && name.Contains("303310PA_广西全省_项目查询岗"); }
                            catch { return false; }
                        });
                        if (collapsed)
                        {
                            Console.WriteLine("  ✓ PostMessage 成功展开 (检测到 '收起')");
                            return;
                        }
                        Console.WriteLine("  PostMessage 未展开，尝试下一策略...");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  PostMessage 失败: {ex.Message}");
                }

                // Strategy 2: Minimize console + force Chrome foreground + real mouse click
                Console.WriteLine("  [策略2] 最小化控制台 + 强制前台点击...");
                try
                {
                    var chromeHwnd = win.Properties.NativeWindowHandle.Value;
                    var consoleHwnd = GetConsoleWindow();

                    // Pre-compute coordinates (before any output)
                    var x = (int)(r.Left + 12);
                    var y = (int)(r.Top + r.Height / 2);

                    // Minimize console to prevent focus stealing
                    if (consoleHwnd != IntPtr.Zero) ShowWindow(consoleHwnd, 6); // SW_MINIMIZE
                    Thread.Sleep(200);

                    // Force Chrome to foreground
                    if (chromeHwnd != IntPtr.Zero) ForceForegroundWindow(chromeHwnd);
                    Thread.Sleep(500);

                    // Real mouse click via Win32
                    SetCursorPos(x, y);
                    Thread.Sleep(200);
                    mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); // LEFTDOWN
                    Thread.Sleep(30);
                    mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); // LEFTUP
                    Thread.Sleep(1500);

                    // Restore console
                    if (consoleHwnd != IntPtr.Zero) ShowWindow(consoleHwnd, 9); // SW_RESTORE
                    Console.WriteLine($"  ✓ 真实点击 at ({x}, {y})");
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  点击失败: {ex.Message}");
                }

                // Strategy 3: UIA Invoke pattern (last resort)
                try
                {
                    var invokePattern = target.Patterns.Invoke.PatternOrDefault;
                    if (invokePattern != null)
                    {
                        Console.WriteLine("  [策略3] UIA Invoke Pattern...");
                        invokePattern.Invoke();
                        Thread.Sleep(1500);
                        Console.WriteLine("  ✓ Invoke 完成");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  Invoke 失败: {ex.Message}");
                }
                return;
            }

            // Priority 2: Fall back to any element matching the tree node text
            var candidates = allEls.Where(el =>
            {
                var name = el.Name ?? "";
                var rect = el.BoundingRectangle;
                return name.Contains("303310PA_广西全省_项目查询岗")
                    && !name.Contains("(TD)")
                    && rect.Width > 0 && rect.Height > 0;
            }).Select(el => (el, el.Name ?? "", el.BoundingRectangle.Width * el.BoundingRectangle.Height))
            .OrderBy(c => c.Item3)
            .ToList();

            if (candidates.Count > 0)
            {
                var target = candidates[0];
                var r = target.el.BoundingRectangle;
                Console.WriteLine($"  回退: 点击 '{target.Item2[..Math.Min(target.Item2.Length, 60)]}' [{target.el.ControlType}]");

                if (win is FlaUI.Core.AutomationElements.Window w) w.Focus();
                Thread.Sleep(500);

                var x = (int)(r.Left + r.Width / 2);
                var y = (int)(r.Top + r.Height / 2);
                FlaUI.Core.Input.Mouse.MoveTo(new System.Drawing.Point(x, y));
                Thread.Sleep(300);
                FlaUI.Core.Input.Mouse.Click(FlaUI.Core.Input.MouseButton.Left);
                Console.WriteLine($"  ✓ 已点击 at ({x}, {y})");
                return;
            }
        }

        Console.WriteLine("  未找到展开链接（可能已展开或 ERP 页面未激活）");
    });
}

async Task ClickCuxMenuAsync()
{
    Console.WriteLine("[CUX] 通过 UIA 点击 CUX 菜单项 (OS-level click)");
    await Task.Run(() =>
    {
        using var automation = new FlaUI.UIA3.UIA3Automation();
        var cf = automation.ConditionFactory;
        var desktop = automation.GetDesktop();

        // Look for Chrome windows with ERP content
        var windows = desktop.FindAllChildren(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Window));
        foreach (var win in windows)
        {
            if (win.ClassName != "Chrome_WidgetWin_1") continue;
            var winName = win.Name ?? "";

            // Only search in ERP windows
            bool isErpWindow = winName.StartsWith("主页") || winName.Contains("Oracle")
                || winName.Contains("电子商务套件") || winName.Contains("ERP");
            if (!isErpWindow) continue;

            // First pass: search for small elements that individually contain "CUX" and "查询项目支出"
            var allEls = win.FindAllDescendants();

            // Collect all elements with CUX-related text
            var cuxElements = new List<(FlaUI.Core.AutomationElements.AutomationElement el, string name, double area)>();
            foreach (var el in allEls)
            {
                var name = el.Name ?? "";
                var rect = el.BoundingRectangle;
                if (!name.Contains("查询项目支出")) continue;
                if (rect.Width <= 0 || rect.Height <= 0) continue;

                var area = rect.Width * rect.Height;
                cuxElements.Add((el, name, area));
                Console.WriteLine($"  候选: '{(name.Length > 80 ? name[..80] : name)}' [{el.ControlType}] area={area:F0} at ({(int)rect.Left},{(int)rect.Top}) size=({(int)rect.Width}x{(int)rect.Height})");
            }

            // Pick the smallest element containing "查询项目支出" (most specific match)
            if (cuxElements.Count > 0)
            {
                // Sort by area - smallest first for most specific match
                var target = cuxElements.OrderBy(e => e.area).First();
                Console.WriteLine($"  选中 (最小面积): '{(target.name.Length > 80 ? target.name[..80] : target.name)}' [{target.el.ControlType}]");

                if (win is FlaUI.Core.AutomationElements.Window w) w.Focus();
                Thread.Sleep(500);

                var rect = target.el.BoundingRectangle;
                var x = (int)(rect.Left + rect.Width / 2);
                var y = (int)(rect.Top + rect.Height / 2);
                FlaUI.Core.Input.Mouse.MoveTo(new System.Drawing.Point(x, y));
                Thread.Sleep(300);
                FlaUI.Core.Input.Mouse.Click(FlaUI.Core.Input.MouseButton.Left);
                Console.WriteLine($"  已 OS 级点击 at ({x}, {y})");
                return;
            }

            // Second pass: if no small element found, look for the text position in the large group
            // Oracle EBS renders menu as one big group - we need to estimate position from text
            foreach (var el in allEls)
            {
                var name = el.Name ?? "";
                if (!name.Contains("查询项目支出") || name.Length < 200) continue;
                // This is the big group with all text - estimate the position
                var rect = el.BoundingRectangle;
                Console.WriteLine($"  大组元素: area={rect.Width * rect.Height:F0}");

                // Count menu items to estimate vertical position
                // The group contains all items as one text block
                // We need to find where "CUX:查询项目支出" is in the sequence
                var lines = name.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                int targetLine = -1;
                int totalLines = 0;
                foreach (var line in lines)
                {
                    if (line.Contains("CUX") && line.Contains("查询项目支出"))
                    {
                        targetLine = totalLines;
                    }
                    // Count actual menu items (lines that look like menu items)
                    if (line.StartsWith("CUX") || line.Contains("查询") || line.Contains("项目"))
                    {
                        totalLines++;
                    }
                }

                if (targetLine >= 0 && totalLines > 0)
                {
                    Console.WriteLine($"  目标在第 {targetLine}/{totalLines} 行");

                    if (win is FlaUI.Core.AutomationElements.Window w) w.Focus();
                    Thread.Sleep(500);

                    // Calculate position: header takes some space, then items are evenly spaced
                    var headerHeight = rect.Height * 0.15; // approximate header
                    var itemHeight = (rect.Height - headerHeight) / Math.Max(totalLines, 1);
                    var targetY = (int)(rect.Top + headerHeight + (targetLine * itemHeight) + itemHeight / 2);
                    var targetX = (int)(rect.Left + rect.Width * 0.4); // slightly right of left edge

                    Console.WriteLine($"  估算位置: ({targetX}, {targetY})");
                    FlaUI.Core.Input.Mouse.MoveTo(new System.Drawing.Point(targetX, targetY));
                    Thread.Sleep(300);
                    FlaUI.Core.Input.Mouse.Click(FlaUI.Core.Input.MouseButton.Left);
                    Console.WriteLine($"  已点击 at ({targetX}, {targetY})");
                    return;
                }
            }
        }

        Console.WriteLine("  未找到 CUX 菜单项");
    });
}

async Task PlaywrightFlowAsync()
{
    Console.WriteLine("[Playwright] 启动完整 ERP → Oracle Forms 流程 (复用 Chrome Profile)");

    using var pw = await Microsoft.Playwright.Playwright.CreateAsync();

    // Use the user's Chrome profile to reuse cookies/sessions
    var chromeProfile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "Google", "Chrome", "User Data");
    Console.WriteLine($"[Playwright] Chrome Profile: {chromeProfile}");

    await using var context = await pw.Chromium.LaunchPersistentContextAsync(chromeProfile, new()
    {
        Headless = false,
        Channel = "chrome", // Use installed Chrome, not Playwright Chromium
        Args = ["--profile-directory=Default", "--no-first-run", "--no-default-browser-check"],
        ViewportSize = new() { Width = 1920, Height = 1080 },
    });

    var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();

    // Step 1: Navigate to ERP portal
    Console.WriteLine("[Playwright] 1. 导航到 ERP 门户...");
    await page.GotoAsync("http://cpszz.hq.cmcc/oldHome", new() { WaitUntil = Microsoft.Playwright.WaitUntilState.NetworkIdle, Timeout = 30000 });
    await page.WaitForTimeoutAsync(3000);

    // Step 2: Click 核心ERP系统
    Console.WriteLine("[Playwright] 2. 点击「核心ERP系统」...");
    try
    {
        await page.ClickAsync("text=核心ERP系统", new() { Timeout = 15000 });
    }
    catch
    {
        // Try in all frames
        var frames = page.Frames;
        foreach (var f in frames)
        {
            try { await f.ClickAsync("text=核心ERP系统", new() { Timeout = 5000 }); break; }
            catch { }
        }
    }
    await page.WaitForTimeoutAsync(8000);

    // Step 3: Find the ERP tab/page (might open new tab)
    var erpPage = page.Context.Pages.LastOrDefault() ?? page;
    Console.WriteLine($"[Playwright] 3. ERP 页面: {erpPage.Url}");

    // Step 4: Expand tree node "303310PA_广西全省_项目查询岗"
    Console.WriteLine("[Playwright] 4. 展开「303310PA_广西全省_项目查询岗」...");
    bool expanded = false;
    foreach (var frame in erpPage.Frames)
    {
        try
        {
            // Look for "展开" link near the tree node
            var expandLink = await frame.QuerySelectorAsync("a:has-text('303310PA_广西全省_项目查询岗')");
            if (expandLink != null)
            {
                var text = await expandLink.TextContentAsync();
                Console.WriteLine($"  找到树节点: '{text?.Trim()}'");
                await expandLink.ClickAsync(new() { Timeout = 5000 });
                expanded = true;
                break;
            }
        }
        catch { }
    }

    if (!expanded)
    {
        // Fallback: click text matching "303310PA" in any frame
        foreach (var frame in erpPage.Frames)
        {
            try
            {
                await frame.ClickAsync("text=303310PA_广西全省_项目查询岗", new() { Timeout = 5000 });
                expanded = true;
                break;
            }
            catch { }
        }
    }

    await page.WaitForTimeoutAsync(5000);
    Console.WriteLine(expanded ? "  树节点已展开" : "  未找到树节点（可能已展开）");

    // Step 5: Click CUX:查询项目支出
    Console.WriteLine("[Playwright] 5. 点击「CUX:查询项目支出」...");
    bool cuxClicked = false;
    foreach (var frame in erpPage.Frames)
    {
        try
        {
            var cuxLink = await frame.QuerySelectorAsync("a:has-text('CUX:查询项目支出')");
            if (cuxLink != null)
            {
                Console.WriteLine($"  找到 CUX 链接");
                // Use expect() to wait and click
                await cuxLink.ClickAsync(new() { Timeout = 10000 });
                cuxClicked = true;
                Console.WriteLine("  已点击 CUX:查询项目支出 (isTrusted!)");
                break;
            }
        }
        catch { }
    }

    if (!cuxClicked)
    {
        // Try broader text match
        foreach (var frame in erpPage.Frames)
        {
            try
            {
                await frame.ClickAsync("text=查询项目支出", new() { Timeout = 5000 });
                cuxClicked = true;
                break;
            }
            catch { }
        }
    }

    Console.WriteLine(cuxClicked ? "  CUX 已点击" : "  CUX 未找到");

    // Step 6: Wait for JNLP download
    Console.WriteLine("[Playwright] 6. 等待 JNLP 下载...");
    await page.WaitForTimeoutAsync(10000);

    // Step 7: Check for downloaded JNLP file
    var downloads = Directory.GetFiles(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        "frmservlet*.jnlp");
    var crdownloads = Directory.GetFiles(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        "frmservlet*.jnlp.crdownload");

    Console.WriteLine($"  JNLP files: {downloads.Length}, crdownload: {crdownloads.Length}");

    string? jnlpPath = null;
    if (downloads.Length > 0)
        jnlpPath = downloads.OrderByDescending(File.GetLastWriteTimeUtc).First();
    else if (crdownloads.Length > 0)
    {
        // Copy crdownload as jnlp
        var src = crdownloads.OrderByDescending(File.GetLastWriteTimeUtc).First();
        jnlpPath = Path.Combine(Path.GetDirectoryName(src)!, "playwright_launch.jnlp");
        File.Copy(src, jnlpPath, true);
        Console.WriteLine($"  复制 crdownload → {jnlpPath}");
    }

    if (jnlpPath != null)
    {
        Console.WriteLine($"[Playwright] 7. 启动 JNLP: {jnlpPath}");
        Process.Start(new ProcessStartInfo { FileName = jnlpPath, UseShellExecute = true });
        await page.WaitForTimeoutAsync(8000);
    }

    // Step 8: Handle Java security dialog with Alt+I + Alt+R
    Console.WriteLine("[Playwright] 8. 处理 Java 安全弹窗...");
    await HandleJavaSecurityDialogViaFlaUIAsync();

    // Step 9: Wait and verify
    await page.WaitForTimeoutAsync(10000);
    var javaProcs = Process.GetProcessesByName("java");
    Console.WriteLine($"[Playwright] 9. Java 进程数: {javaProcs.Length}");

    // Keep browser open
    Console.WriteLine("[Playwright] 完成。浏览器保持打开。按 Enter 关闭...");
}

async Task HandleJavaSecurityDialogViaFlaUIAsync()
{
    await Task.Run(() =>
    {
        using var automation = new FlaUI.UIA3.UIA3Automation();
        var cf = automation.ConditionFactory;
        var desktop = automation.GetDesktop();
        var deadline = DateTime.UtcNow.AddSeconds(90);
        var handled = false;

        while (DateTime.UtcNow < deadline && !handled)
        {
            var allWindows = desktop.FindAllChildren(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Window));

            foreach (var win in allWindows)
            {
                try
                {
                    var winClass = win.ClassName ?? "";
                    var winName = win.Name ?? "";
                    if (winClass != "SunAwtDialog") continue;
                    if (winName.Contains("启动") || winName.Contains("Starting") || winName.Contains("加载")) continue;

                    Console.WriteLine($"  Java 弹窗: '{winName}'");

                    if (win is FlaUI.Core.AutomationElements.Window w) w.Focus();
                    Thread.Sleep(800);

                    // Alt+I (checkbox) then Alt+R (Run)
                    FlaUI.Core.Input.Keyboard.Pressing(FlaUI.Core.WindowsAPI.VirtualKeyShort.LMENU);
                    Thread.Sleep(100);
                    FlaUI.Core.Input.Keyboard.Pressing(FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_I);
                    Thread.Sleep(100);
                    FlaUI.Core.Input.Keyboard.Release(FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_I);
                    Thread.Sleep(100);
                    FlaUI.Core.Input.Keyboard.Release(FlaUI.Core.WindowsAPI.VirtualKeyShort.LMENU);
                    Thread.Sleep(800);

                    FlaUI.Core.Input.Keyboard.Pressing(FlaUI.Core.WindowsAPI.VirtualKeyShort.LMENU);
                    Thread.Sleep(100);
                    FlaUI.Core.Input.Keyboard.Pressing(FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_R);
                    Thread.Sleep(100);
                    FlaUI.Core.Input.Keyboard.Release(FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_R);
                    Thread.Sleep(100);
                    FlaUI.Core.Input.Keyboard.Release(FlaUI.Core.WindowsAPI.VirtualKeyShort.LMENU);
                    Thread.Sleep(2000);

                    handled = true;
                    Console.WriteLine("  ✓ Java 弹窗已处理 (Alt+I + Alt+R)");
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // Element no longer available, skip
                }
            }

            if (!handled) Thread.Sleep(2000);
        }

        if (!handled) Console.WriteLine("  未检测到 Java 安全弹窗");
    });
}
{
    Console.WriteLine("[3] 处理 Java 安全弹窗 (UIA 查找按钮)");
    await Task.Run(() =>
    {
        using var automation = new FlaUI.UIA3.UIA3Automation();
        var cf = automation.ConditionFactory;
        var desktop = automation.GetDesktop();
        var deadline = DateTime.UtcNow.AddSeconds(60);
        var handled = false;

        while (DateTime.UtcNow < deadline && !handled)
        {
            var allWindows = desktop.FindAllChildren(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Window));

            foreach (var win in allWindows)
            {
                try
                {
                    var winClass = win.ClassName ?? "";
                    var winName = win.Name ?? "";
                    if (winClass != "SunAwtDialog") continue;

                    Console.WriteLine($"  Java 弹窗: '{winName}' size={win.BoundingRectangle.Width}x{win.BoundingRectangle.Height}");

                    // Activate the window
                    if (win is FlaUI.Core.AutomationElements.Window w) w.Focus();
                    Thread.Sleep(500);

                    // First, close "详细信息" sub-dialog if present
                    if (winName.Contains("详细"))
                    {
                        Console.WriteLine("  关闭详细信息子窗口");
                        FlaUI.Core.Input.Keyboard.Pressing(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESC);
                        Thread.Sleep(1000);
                        continue;
                    }

                    // Skip "正在启动应用程序..." (loading dialog)
                    if (winName.Contains("启动") || winName.Contains("Starting"))
                    {
                        Console.WriteLine("  跳过加载对话框");
                        continue;
                    }

                    // Scan all descendants for checkbox and buttons
                    var allEls = win.FindAllDescendants();
                    Console.WriteLine($"  共 {allEls.Length} 个子元素");

                    // Find checkbox element
                    var checkbox = allEls.FirstOrDefault(el =>
                    {
                        var name = el.Name ?? "";
                        return name.Contains("接受") || name.Contains("accept") || name.Contains("risk")
                            || (el.ControlType == FlaUI.Core.Definitions.ControlType.CheckBox);
                    });

                    if (checkbox != null)
                    {
                        var r = checkbox.BoundingRectangle;
                        Console.WriteLine($"  找到复选框: '{checkbox.Name}' [{checkbox.ControlType}] at ({(int)r.Left},{(int)r.Top}) size({(int)r.Width}x{(int)r.Height})");
                        if (r.Width > 0 && r.Height > 0)
                        {
                            var cx = (int)(r.Left + r.Width / 2);
                            var cy = (int)(r.Top + r.Height / 2);
                            FlaUI.Core.Input.Mouse.MoveTo(new System.Drawing.Point(cx, cy));
                            Thread.Sleep(200);
                            FlaUI.Core.Input.Mouse.Click(FlaUI.Core.Input.MouseButton.Left);
                            Console.WriteLine($"  已点击复选框 at ({cx}, {cy})");
                            Thread.Sleep(800);
                        }
                        else
                        {
                            Console.WriteLine("  复选框不可见，用 Space 键");
                            FlaUI.Core.Input.Keyboard.Pressing(FlaUI.Core.WindowsAPI.VirtualKeyShort.SPACE);
                            Thread.Sleep(800);
                        }
                    }
                    else
                    {
                        var rect = win.BoundingRectangle;
                        int cbX = (int)(rect.Left + rect.Width * 0.12);
                        int cbY = (int)(rect.Top + rect.Height * 0.75);
                        Console.WriteLine($"  未找到复选框元素，用估算位置 ({cbX}, {cbY})");
                        FlaUI.Core.Input.Mouse.MoveTo(new System.Drawing.Point(cbX, cbY));
                        Thread.Sleep(200);
                        FlaUI.Core.Input.Mouse.Click(FlaUI.Core.Input.MouseButton.Left);
                        Thread.Sleep(800);
                    }

                    // Find "运行" button
                    var runBtn = allEls.FirstOrDefault(el =>
                    {
                        var name = el.Name ?? "";
                        return name.Contains("运行") || name.Contains("Run")
                            || (el.ControlType == FlaUI.Core.Definitions.ControlType.Button && name.Contains("R"));
                    });

                    if (runBtn != null)
                    {
                        var r = runBtn.BoundingRectangle;
                        Console.WriteLine($"  找到运行按钮: '{runBtn.Name}' [{runBtn.ControlType}] at ({(int)r.Left},{(int)r.Top}) size({(int)r.Width}x{(int)r.Height})");
                        if (r.Width > 0 && r.Height > 0)
                        {
                            var bx = (int)(r.Left + r.Width / 2);
                            var by = (int)(r.Top + r.Height / 2);
                            FlaUI.Core.Input.Mouse.MoveTo(new System.Drawing.Point(bx, by));
                            Thread.Sleep(200);
                            FlaUI.Core.Input.Mouse.Click(FlaUI.Core.Input.MouseButton.Left);
                            Console.WriteLine($"  已点击运行按钮 at ({bx}, {by})");
                        }
                        else
                        {
                            FlaUI.Core.Input.Keyboard.Pressing(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
                            Console.WriteLine("  运行按钮不可见，用 Enter 键");
                        }
                    }
                    else
                    {
                        Console.WriteLine("  未找到运行按钮，列出所有子元素:");
                        foreach (var el in allEls.Take(30))
                        {
                            try
                            {
                                var r = el.BoundingRectangle;
                                var elName = el.Name ?? "";
                                if (elName.Length > 60) elName = elName[..60];
                                Console.WriteLine($"    [{el.ControlType}] '{elName}' at ({(int)r.Left},{(int)r.Top}) size({(int)r.Width}x{(int)r.Height})");
                            }
                            catch { }
                        }
                    }

                    handled = true;
                    Thread.Sleep(1500);
                    Console.WriteLine("  ✓ 处理完毕");
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // Element no longer available, skip
                }
            }

            if (!handled) Thread.Sleep(2000);
        }

        if (!handled) Console.WriteLine("  ✗ 未检测到 Java 安全弹窗");
    });
}

async Task DiagnoseErpPageAsync()
{
    Console.WriteLine("=== ERP 页面诊断 ===");
    using var ws = new ClientWebSocket();
    await ws.ConnectAsync(new Uri("ws://127.0.0.1:9333/"), CancellationToken.None);

    // Find ERP tab
    int? erpTabId = await FindTabIdAsync(ws, "erp.hq.cmcc");
    if (erpTabId == null)
    {
        Console.WriteLine("  未找到 ERP 标签页");
        return;
    }
    Console.WriteLine($"  ERP tab id={erpTabId}");

    // Activate it first
    await SendAsync(ws, "activateTab", new { tabId = erpTabId, timeoutMs = 5000 });
    await Task.Delay(2000);
    FocusChromeWindow();
    await Task.Delay(2000);

    // Try clickByText with RAW response logging
    Console.WriteLine("\n--- Bridge clickByText 测试 ---");
    foreach (var text in new[] { "303310PA", "广西全省", "查询项目支出", "CUX", "主页", "导航", "ERP" })
    {
        Console.WriteLine($"\n  尝试 '{text}':");
        try
        {
            var resp = await SendAsync(ws, "clickByText", new { text, exact = false, tabId = erpTabId, timeoutMs = 5000 });
            Console.WriteLine($"    RAW: {resp}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    Error: {ex.Message}");
        }
    }

    // Try other bridge actions to get page text
    Console.WriteLine("\n--- Bridge 其他 action ---");
    foreach (var action in new[] { "getPageText", "getText", "getBodyText", "extractText" })
    {
        try
        {
            var resp = await SendAsync(ws, action, new { tabId = erpTabId, timeoutMs = 5000 });
            Console.WriteLine($"  {action}: {resp}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  {action}: {ex.Message}");
        }
    }

    // Dump UIA elements from Chrome window
    Console.WriteLine("\n--- UIA 元素扫描 ---");
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

            // Only check ERP window
            bool isErpWindow = winName.StartsWith("主页") || winName.Contains("Oracle")
                || winName.Contains("电子商务套件") || winName.Contains("ERP");
            if (!isErpWindow) continue;

            Console.WriteLine($"  Chrome 窗口: '{winName}'");
            var allEls = win.FindAllDescendants();
            Console.WriteLine($"  共 {allEls.Length} 个后代元素");

            var interesting = allEls
                .Where(el => !string.IsNullOrWhiteSpace(el.Name) && el.Name!.Length < 200)
                .Select(el => $"[{el.ControlType}] '{el.Name}'")
                .ToList();
            Console.WriteLine($"  有文本的元素 ({interesting.Count}):");
            foreach (var t in interesting.Take(80))
                Console.WriteLine($"    {t}");
            if (interesting.Count > 80)
                Console.WriteLine($"    ... 还有 {interesting.Count - 80} 个");
            break;
        }
    });

    // Take screenshot
    var screenshotPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads", "diagnose_erp.png");
    TakeScreenShot(screenshotPath);
    Console.WriteLine($"\n截图已保存: {screenshotPath}");
}

void TakeScreenShot(string path)
{
    Task.Run(() =>
    {
        using var bmp = new System.Drawing.Bitmap(
            System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width,
            System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.CopyFromScreen(0, 0, 0, 0, bmp.Size);
        bmp.Save(path);
    }).Wait();
}

async Task VerifyTreeExpansionAsync()
{
    // Take a screenshot to see current state
    var screenshotPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads", "after_expand.png");
    TakeScreenShot(screenshotPath);
    Console.WriteLine($"  截图: {screenshotPath}");

    // Check UIA for expanded items
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

            var allEls = win.FindAllDescendants();
            // Look for CUX-related elements
            var cuxEls = allEls.Where(el =>
            {
                var name = el.Name ?? "";
                return name.Contains("CUX") || name.Contains("查询项目支出");
            }).ToList();

            if (cuxEls.Count > 0)
            {
                Console.WriteLine($"  ✓ 找到 {cuxEls.Count} 个 CUX 元素:");
                foreach (var el in cuxEls.Take(10))
                {
                    var r = el.BoundingRectangle;
                    Console.WriteLine($"    [{el.ControlType}] '{el.Name}' at ({(int)r.Left},{(int)r.Top})");
                }
            }
            else
            {
                Console.WriteLine("  ✗ 未找到 CUX 元素，展开可能未成功");
                // Show all Hyperlink elements to see what's available
                var links = allEls.Where(el =>
                    el.ControlType == FlaUI.Core.Definitions.ControlType.Hyperlink
                    && !string.IsNullOrWhiteSpace(el.Name))
                    .Select(el => el.Name)
                    .ToList();
                Console.WriteLine($"  当前页面的 Hyperlink 元素 ({links.Count}):");
                foreach (var l in links.Take(20))
                    Console.WriteLine($"    '{l}'");
            }
            break;
        }
    });
}

void FocusChromeWindow()
{
    Console.WriteLine("  将 Chrome 窗口带到前台...");
    Task.Run(() =>
    {
        using var automation = new FlaUI.UIA3.UIA3Automation();
        var cf = automation.ConditionFactory;
        var desktop = automation.GetDesktop();
        var windows = desktop.FindAllChildren(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Window));

        // Find Chrome windows (not Edge)
        foreach (var win in windows)
        {
            if (win.ClassName != "Chrome_WidgetWin_1") continue;
            var winName = win.Name ?? "";
            if (winName.Contains("Edge", StringComparison.OrdinalIgnoreCase)) continue;

            Console.WriteLine($"  聚焦窗口: '{winName}'");
            if (win is FlaUI.Core.AutomationElements.Window w) w.Focus();
            return;
        }
        Console.WriteLine("  未找到 Chrome 窗口");
    }).Wait();
}

async Task<bool> ExpandTreeViaBridgeAsync()
{
    try
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri("ws://127.0.0.1:9333/"), CancellationToken.None);

        int? erpTabId = await FindTabIdAsync(ws, "erp.hq.cmcc");
        if (erpTabId == null)
        {
            Console.WriteLine("  未找到 ERP 标签页");
            return false;
        }

        // Strategy 1: Use CSS selector to click the expand link by title attribute
        // Oracle EBS expand links have title="展开 XXXXX"
        var selectors = new[]
        {
            "a[title*='展开 303310PA_广西全省_项目查询岗']",
            "a[title*='303310PA_广西全省_项目查询岗'][title*='展开']",
        };
        foreach (var selector in selectors)
        {
            Console.WriteLine($"  尝试 bridge click (CSS): {selector}");
            try
            {
                var resp = await SendAsync(ws, "click", new { selector, tabId = erpTabId, timeoutMs = 10000 });
                Console.WriteLine($"  结果: {resp}");
                if (resp.ValueKind == JsonValueKind.Object && resp.TryGetProperty("data", out var data))
                {
                    if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("clicked", out _))
                    {
                        Console.WriteLine($"  ✓ CSS 选择器点击成功");
                        await Task.Delay(3000);
                        // Verify expansion
                        return await VerifyExpansionViaBridgeAsync(ws, erpTabId.Value);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  失败: {ex.Message}");
            }
        }

        // Strategy 2: Try clickByText with the responsibility name
        var textPatterns = new[] { "303310PA_广西全省_项目查询岗" };
        foreach (var pattern in textPatterns)
        {
            Console.WriteLine($"  尝试 bridge clickByText '{pattern}'...");
            try
            {
                var resp = await SendAsync(ws, "clickByText", new { text = pattern, exact = false, tabId = erpTabId, timeoutMs = 10000 });
                Console.WriteLine($"  结果: {resp}");
                if (resp.ValueKind == JsonValueKind.Object && resp.TryGetProperty("clicked", out _))
                {
                    Console.WriteLine($"  ✓ clickByText 点击成功");
                    await Task.Delay(3000);
                    return await VerifyExpansionViaBridgeAsync(ws, erpTabId.Value);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  失败: {ex.Message}");
            }
        }

        return false;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Bridge 展开 failed: {ex.Message}");
        return false;
    }
}

async Task<bool> VerifyExpansionViaBridgeAsync(ClientWebSocket ws, int tabId)
{
    // Check if the page text now contains CUX items (sign of successful expansion)
    try
    {
        var bodyResp = await SendAsync(ws, "getBodyText", new { tabId, timeoutMs = 5000 });
        if (bodyResp.ValueKind == JsonValueKind.Object && bodyResp.TryGetProperty("text", out var textEl))
        {
            var bodyText = textEl.GetString() ?? "";
            if (bodyText.Contains("CUX") || bodyText.Contains("查询项目支出"))
            {
                Console.WriteLine("  ✓ 验证通过: 页面包含 CUX 内容");
                return true;
            }
            // Check if "收起" appeared (means tree expanded)
            if (bodyText.Contains("收起"))
            {
                Console.WriteLine("  ✓ 验证通过: 检测到 '收起' (已展开)");
                return true;
            }
            Console.WriteLine("  ⚠ 页面未显示 CUX 内容，展开可能未成功");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  验证异常: {ex.Message}");
    }
    return false;
}

async Task<bool> ClickCuxViaBridgeAsync()
{
    try
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri("ws://127.0.0.1:9333/"), CancellationToken.None);

        int? erpTabId = await FindTabIdAsync(ws, "erp.hq.cmcc");
        if (erpTabId == null)
        {
            Console.WriteLine("  未找到 ERP 标签页");
            return false;
        }

        // First, check if CUX items exist in the page text
        Console.WriteLine("  检查页面文本中是否有 CUX...");
        var bodyResp = await SendAsync(ws, "getBodyText", new { tabId = erpTabId, timeoutMs = 5000 });
        string bodyText = "";
        if (bodyResp.ValueKind == JsonValueKind.Object && bodyResp.TryGetProperty("text", out var textEl))
            bodyText = textEl.GetString() ?? "";
        Console.WriteLine($"  页面文本长度: {bodyText.Length}");

        bool hasCux = bodyText.Contains("CUX") || bodyText.Contains("查询项目支出");
        Console.WriteLine($"  CUX 在文本中: {hasCux}");

        if (!hasCux)
        {
            // CUX items might be below the visible area — scroll the tree panel
            Console.WriteLine("  CUX 不在可见区域，尝试滚动导航树...");
            try
            {
                // Scroll down in the ERP tab to find CUX items
                await SendAsync(ws, "scroll", new { selector = "body", direction = "down", tabId = erpTabId, timeoutMs = 5000 });
                await Task.Delay(1000);

                // Re-check
                bodyResp = await SendAsync(ws, "getBodyText", new { tabId = erpTabId, timeoutMs = 5000 });
                if (bodyResp.ValueKind == JsonValueKind.Object && bodyResp.TryGetProperty("text", out textEl))
                    bodyText = textEl.GetString() ?? "";
                hasCux = bodyText.Contains("CUX") || bodyText.Contains("查询项目支出");
                Console.WriteLine($"  滚动后 CUX 在文本中: {hasCux}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  滚动失败: {ex.Message}");
            }
        }

        // Try clicking CUX via bridge
        var patterns = new[] { "CUX:查询项目支出", "查询项目支出", "CUX" };
        foreach (var pattern in patterns)
        {
            Console.WriteLine($"  尝试 bridge clickByText '{pattern}'...");
            try
            {
                var resp = await SendAsync(ws, "clickByText", new { text = pattern, exact = false, tabId = erpTabId, timeoutMs = 10000 });
                Console.WriteLine($"  结果: {resp}");
                if (resp.ValueKind == JsonValueKind.Object && resp.TryGetProperty("clicked", out _))
                {
                    Console.WriteLine($"  ✓ CUX 点击成功: '{pattern}'");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  失败: {ex.Message}");
            }
        }
        return false;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Bridge CUX click failed: {ex.Message}");
        return false;
    }
}

async Task<int?> FindTabIdAsync(ClientWebSocket ws, string urlContains)
{
    var tabsResp = await SendAsync(ws, "getTabs", new { });
    if (tabsResp.ValueKind != JsonValueKind.Array) return null;

    foreach (var tab in tabsResp.EnumerateArray())
    {
        var id = tab.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;
        var url = tab.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
        if (url.Contains(urlContains, StringComparison.OrdinalIgnoreCase))
            return id;
    }
    return null;
}

async Task ActivateErpTabAsync()
{
    Console.WriteLine("  激活 ERP 标签页...");
    using var ws = new ClientWebSocket();
    await ws.ConnectAsync(new Uri("ws://127.0.0.1:9333/"), CancellationToken.None);

    var tabsResp = await SendAsync(ws, "getTabs", new { });
    if (tabsResp.ValueKind != JsonValueKind.Array) return;

    // Find the ERP tab (url contains "erp.hq.cmcc")
    int? erpTabId = null;
    foreach (var tab in tabsResp.EnumerateArray())
    {
        var id = tab.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;
        var url = tab.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
        if (url.Contains("erp.hq.cmcc", StringComparison.OrdinalIgnoreCase))
        {
            erpTabId = id;
            break;
        }
    }

    if (erpTabId == null)
    {
        Console.WriteLine("  未找到 ERP 标签页");
        return;
    }

    Console.WriteLine($"  激活标签页 id={erpTabId}");
    try
    {
        await SendAsync(ws, "activateTab", new { tabId = erpTabId, timeoutMs = 5000 });
        Console.WriteLine("  ✓ ERP 标签页已激活");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  激活失败: {ex.Message}");
    }
    await Task.Delay(2000); // Wait for tab content to render
}

async Task<bool> FindAndLaunchJnlpAsync()
{
    var downloadsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    // Record the newest JNLP file before we start (to detect NEW downloads)
    var beforeFiles = Directory.GetFiles(downloadsDir, "frmservlet*.jnlp")
        .OrderByDescending(File.GetLastWriteTimeUtc)
        .ToArray();
    var beforeNewest = beforeFiles.Length > 0 ? File.GetLastWriteTimeUtc(beforeFiles[0]) : DateTime.MinValue;
    var beforeNewestPath = beforeFiles.Length > 0 ? beforeFiles[0] : "";

    Console.WriteLine($"  当前最新 JNLP: {(beforeFiles.Length > 0 ? Path.GetFileName(beforeNewestPath) : "无")} (modified={beforeNewest:HH:mm:ss})");

    // If we have existing JNLP files, launch the newest one
    if (beforeFiles.Length > 0)
    {
        var jnlpPath = beforeNewestPath;
        var fileInfo = new FileInfo(jnlpPath);
        var age = DateTime.UtcNow - fileInfo.LastWriteTimeUtc;
        Console.WriteLine($"  找到现有 JNLP: {Path.GetFileName(jnlpPath)} (age={age.TotalSeconds:F0}s, size={fileInfo.Length})");

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = jnlpPath,
                UseShellExecute = true
            });
            Console.WriteLine("  ✓ JNLP 已启动");
            await Task.Delay(5000);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  启动 JNLP 失败: {ex.Message}");
            return false;
        }
    }

    // No existing JNLP — wait for a new download (up to 45s)
    string? jnlpPath2 = null;
    var deadline = DateTime.UtcNow.AddSeconds(45);
    while (DateTime.UtcNow < deadline)
    {
        var files = Directory.GetFiles(downloadsDir, "frmservlet*.jnlp")
            .Concat(Directory.GetFiles(downloadsDir, "frmservlet*.jnlp.crdownload"))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();

        if (files.Length > 0)
        {
            var latest = files[0];
            if (!latest.EndsWith(".crdownload"))
            {
                jnlpPath2 = latest;
                Console.WriteLine($"  ✓ 检测到新下载: {Path.GetFileName(latest)}");
                break;
            }
            Console.WriteLine($"  下载中: {Path.GetFileName(latest)}...");
        }
        await Task.Delay(2000);
    }

    if (jnlpPath2 == null)
    {
        Console.WriteLine("  未找到 JNLP 文件");
        return false;
    }

    try
    {
        Process.Start(new ProcessStartInfo { FileName = jnlpPath2, UseShellExecute = true });
        Console.WriteLine("  ✓ JNLP 已启动");
        await Task.Delay(5000);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  启动 JNLP 失败: {ex.Message}");
        return false;
    }
}

async Task<JsonElement> SendAsync(ClientWebSocket ws, string action, object @params)
{
    var id = Guid.NewGuid().ToString("N")[..8];
    var msg = JsonSerializer.Serialize(new { id, action, @params });
    var bytes = Encoding.UTF8.GetBytes(msg);
    await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);

    var buffer = new byte[16384];
    var sb = new StringBuilder();
    JsonElement result;
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
                if (root.TryGetProperty("data", out var data))
                    return data.Clone();
                return root.Clone();
            }
            sb.Clear();
        }
    }
}

// Win32 P/Invoke
[DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
[DllImport("user32.dll", CharSet = CharSet.Auto)] static extern int GetWindowText(IntPtr hWnd, StringBuilder lpWindowText, int nMaxCount);
[DllImport("user32.dll", CharSet = CharSet.Auto)] static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
[DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
[DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
[DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);
[DllImport("user32.dll")] static extern IntPtr PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
[DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
[DllImport("user32.dll")] static extern bool SetCursorPos(int X, int Y);
[DllImport("user32.dll")] static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);
[DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
[DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
[DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

static void ForceForegroundWindow(IntPtr hWnd)
{
    var fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
    var myThread = GetCurrentThreadId();
    if (fgThread != myThread) AttachThreadInput(myThread, fgThread, true);
    ShowWindow(hWnd, 9); // SW_RESTORE
    SetForegroundWindow(hWnd);
    Thread.Sleep(300);
    if (fgThread != myThread) AttachThreadInput(myThread, fgThread, false);
}

delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

[StructLayout(LayoutKind.Sequential)]
struct POINT { public int X, Y; }

[StructLayout(LayoutKind.Sequential)]
struct RECT { public int Left, Top, Right, Bottom; }
