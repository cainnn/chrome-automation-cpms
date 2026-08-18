using FlaUI.UIA3;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;

namespace ChromeAutomation.CpmsExport;

/// <summary>
/// Uses FlaUI (UIA3) to interact with Chrome UI:
/// - Click download buttons in CPMS download list
/// - Click "Keep dangerous file" on download confirmation
/// </summary>
public static class ChromeUiaHelper
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    private const int SW_RESTORE = 9;

    private static readonly string[] CpmsTitleHints =
    [
        "中国移动",
        "计划建设",
        "cpms",
        "CMCC",
        "PMS",
        "我的下载",
        "attachmentDownload",
    ];

    private static readonly string[] ExcludeTitleHints =
    [
        "Cursor",
        "Visual Studio",
        "Code -",
    ];

    /// <summary>
    /// 将 CPMS Chrome 窗口置于前台（用户可能在操作其他程序时使用 AttachThreadInput 提高成功率）。
    /// </summary>
    public static async Task<bool> ActivateCpmsChromeAsync(string? titleHint = null, int timeoutMs = 8000)
    {
        try
        {
            return await Task.Run(() =>
            {
            using var automation = new UIA3Automation();
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                var windows = FindChromeWindows(automation)
                    .OrderByDescending(w => ScoreChromeWindow(w, titleHint))
                    .ToList();

                foreach (var win in windows)
                {
                    if (ScoreChromeWindow(win, titleHint) < 0)
                        continue;

                    try
                    {
                        var hwnd = win.Properties.NativeWindowHandle.Value;
                        if (hwnd == IntPtr.Zero)
                            continue;

                        ForceForeground(hwnd);
                        Console.WriteLine($"[UIA] 已激活 Chrome: {win.Name}");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[UIA] 激活 Chrome 失败 ({win.Name}): {ex.Message}");
                    }
                }

                Thread.Sleep(400);
            }

            Console.WriteLine("[UIA] 未找到可激活的 CPMS Chrome 窗口");
            return false;
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UIA] ActivateCpmsChrome 异常: {ex.Message}");
            return false;
        }
    }

    private static void ForceForeground(IntPtr hWnd)
    {
        var fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var myThread = GetCurrentThreadId();
        if (fgThread != myThread)
            AttachThreadInput(myThread, fgThread, true);
        ShowWindow(hWnd, SW_RESTORE);
        SetForegroundWindow(hWnd);
        Thread.Sleep(400);
        if (fgThread != myThread)
            AttachThreadInput(myThread, fgThread, false);
    }

    private static int ScoreChromeWindow(AutomationElement win, string? titleHint)
    {
        var name = win.Name ?? "";
        foreach (var bad in ExcludeTitleHints)
        {
            if (name.Contains(bad, StringComparison.OrdinalIgnoreCase))
                return -100;
        }

        var score = 0;
        foreach (var hint in CpmsTitleHints)
        {
            if (name.Contains(hint, StringComparison.OrdinalIgnoreCase))
                score += 40;
        }

        if (!string.IsNullOrEmpty(titleHint) && name.Contains(titleHint, StringComparison.OrdinalIgnoreCase))
            score += 80;

        if (name.Contains("Google Chrome", StringComparison.OrdinalIgnoreCase))
            score += 10;

        return score;
    }

    /// <summary>
    /// Opens chrome://extensions and clicks the refresh button for the Chrome Automation Bridge extension.
    /// </summary>
    public static async Task RefreshExtensionAsync(int timeoutMs = 15000)
    {
        await Task.Run(() =>
        {
            using var automation = new UIA3Automation();
            var chromeWindows = FindChromeWindows(automation);

            // Find chrome://extensions tab
            Window? extensionsWindow = null;
            foreach (var win in chromeWindows)
            {
                if (win.Name.Contains("扩展程序") || win.Name.Contains("Extensions"))
                {
                    extensionsWindow = win;
                    break;
                }
            }

            if (extensionsWindow == null && chromeWindows.Count > 0)
            {
                Console.WriteLine("[UIA] 导航到 chrome://extensions...");
                var win = chromeWindows[0];
                FlaUI.Core.Input.Keyboard.Pressing(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL, FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_L);
                System.Threading.Thread.Sleep(500);
                foreach (var c in "chrome://extensions")
                    FlaUI.Core.Input.Keyboard.Type(c);
                System.Threading.Thread.Sleep(300);
                FlaUI.Core.Input.Keyboard.Pressing(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
                System.Threading.Thread.Sleep(3000);

                chromeWindows = FindChromeWindows(automation);
                foreach (var w in chromeWindows)
                {
                    if (w.Name.Contains("扩展程序") || w.Name.Contains("Extensions"))
                    { extensionsWindow = w; break; }
                }
            }

            if (extensionsWindow == null)
            {
                Console.WriteLine("[UIA] 未找到 chrome://extensions 窗口");
                return;
            }

            Console.WriteLine($"[UIA] 找到扩展窗口: {extensionsWindow.Name}");

            // Find the extension card and its refresh button
            // Chrome extensions page uses a custom UI with buttons like "重新加载" / "Reload"
            var allButtons = extensionsWindow.FindAllDescendants(cf =>
                cf.ByControlType(ControlType.Button));

            foreach (var btn in allButtons)
            {
                var name = btn.Name ?? "";
                if (name.Contains("重新加载") || name.Contains("Reload") ||
                    name.Contains("刷新") || name.Contains("Refresh"))
                {
                    // Check if it's near "Chrome Automation Bridge" text
                    var parent = btn.Parent;
                    if (parent != null)
                    {
                        var parentText = parent.Name ?? "";
                        var siblings = parent.FindAllChildren();
                        var nearBridgeText = siblings.Any(s =>
                            (s.Name ?? "").Contains("Chrome Automation Bridge") ||
                            (s.Name ?? "").Contains("Automation Bridge"));

                        if (nearBridgeText || parentText.Contains("Chrome Automation"))
                        {
                            btn.Click();
                            Console.WriteLine($"[UIA] 已点击刷新按钮: {name}");
                            return;
                        }
                    }
                }
            }

            // Fallback: find any reload button near "Bridge" text
            var allElements = extensionsWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Text));
            string? bridgeElementName = null;
            foreach (var el in allElements)
            {
                if ((el.Name ?? "").Contains("Chrome Automation Bridge"))
                {
                    bridgeElementName = el.Name;
                    // Look for sibling buttons
                    var container = el.Parent?.Parent; // usually card container
                    if (container != null)
                    {
                        var containerButtons = container.FindAllChildren(cf =>
                            cf.ByControlType(ControlType.Button));
                        foreach (var btn in containerButtons)
                        {
                            Console.WriteLine($"[UIA] Container button: {btn.Name}");
                            if ((btn.Name ?? "").Contains("重新加载") || (btn.Name ?? "").Contains("Reload"))
                            {
                                btn.Click();
                                Console.WriteLine($"[UIA] 已点击扩展刷新: {btn.Name}");
                                return;
                            }
                        }
                    }
                }
            }

            Console.WriteLine("[UIA] 未找到扩展刷新按钮");
        });
    }

    /// <summary>
    /// Clicks Chrome's "Keep" / "保留" button on the dangerous download confirmation bar.
    /// </summary>
    public static bool TryClickKeepButton()
    {
        try
        {
            using var automation = new UIA3Automation();
            var chromeWindows = FindChromeWindows(automation);

            foreach (var win in chromeWindows)
            {
                // Search for Keep/保留 button in all descendants
                var buttons = win.FindAllDescendants(cf =>
                    cf.ByControlType(ControlType.Button));

                foreach (var btn in buttons)
                {
                    var name = btn.Name ?? "";
                    if (name == "保留" || name == "Keep" ||
                        name == "保留危险文件" || name == "Keep dangerous file")
                    {
                        btn.Click();
                        Console.WriteLine($"[UIA] 已点击「{name}」");
                        return true;
                    }
                }
            }
        }
        catch
        {
            // UIA failure shouldn't block main flow
        }
        return false;
    }

    /// <summary>
    /// Periodically tries to click Keep button while download is in progress.
    /// </summary>
    public static async Task ClickKeepButtonAsync(int timeoutMs = 30000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            TryClickKeepButton();
            await Task.Delay(2000);
        }
    }

    /// <summary>
    /// 纯 UIA：在 Chrome 原生下载浮窗（非页面 DOM）中找到并真实鼠标点击「保留」。
    /// </summary>
    public static async Task<bool> ClickKeepInDownloadPanelAsync(
        string fileHint = "",
        int timeoutMs = 90000)
    {
        await ActivateCpmsChromeAsync("中国移动");
        using var automation = new UIA3Automation();
        var cf = automation.ConditionFactory;
        var desktop = automation.GetDesktop();
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var loggedOnce = false;
        var panelOpened = false;
        var attempts = 0;

        Console.WriteLine("[UIA] 在 Chrome 下载浮窗中搜索「保留」（非页面 DOM）...");

        while (DateTime.UtcNow < deadline)
        {
            attempts++;
            try
            {
                var chromeWindows = desktop.FindAllChildren(cf.ByControlType(ControlType.Window))
                    .Where(w => w.ClassName == "Chrome_WidgetWin_1")
                    .OrderByDescending(w => ScoreChromeWindow(w, "中国移动"))
                    .ToArray();

                foreach (var win in chromeWindows)
                {
                    if (ScoreChromeWindow(win, "中国移动") < 0)
                        continue;

                    try
                    {
                        var hwnd = win.Properties.NativeWindowHandle.Value;
                        if (hwnd != IntPtr.Zero)
                            ForceForeground(hwnd);
                    }
                    catch { /* ignore */ }

                    if (!panelOpened)
                    {
                        var dlToolbarBtn = FindDownloadToolbarButton(win, cf);
                        if (dlToolbarBtn != null)
                        {
                            ClickElementWithMouse(dlToolbarBtn);
                            Console.WriteLine("[UIA] 已点击 Chrome 工具栏「下载」按钮，打开浮窗（仅一次）...");
                            panelOpened = true;
                            await Task.Delay(1500);
                        }
                    }

                    foreach (var root in EnumerateDownloadBubbleRoots(win, cf))
                    {
                        if (TryClickKeepInElement(root, cf, fileHint, ref loggedOnce, out var clickedWin))
                        {
                            await Task.Delay(1500);
                            await HandleSecondaryConfirmationAsync(clickedWin ?? win, cf);
                            return true;
                        }
                    }

                    if (!loggedOnce)
                    {
                        loggedOnce = true;
                        LogChromeElements(win, cf);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UIA] 浮窗扫描异常: {ex.Message}");
            }

            if (attempts % 5 == 0)
                Console.WriteLine($"[UIA] 第 {attempts} 次重试搜索 Chrome 浮窗「保留」...");

            await Task.Delay(2000);
        }

        Console.WriteLine("[UIA] 未在 Chrome 下载浮窗中找到「保留」");
        return false;
    }

    /// <summary>收集 Chrome 下载浮窗可能出现的 UIA 根节点（主窗口、子窗口、下载 Pane）</summary>
    private static IEnumerable<AutomationElement> EnumerateDownloadBubbleRoots(AutomationElement win, ConditionFactory cf)
    {
        yield return win;

        foreach (var childWin in win.FindAllChildren(cf.ByControlType(ControlType.Window)))
        {
            yield return childWin;
            foreach (var gc in childWin.FindAllChildren(cf.ByControlType(ControlType.Window)))
                yield return gc;
        }

        foreach (var pane in win.FindAllDescendants(cf.ByControlType(ControlType.Pane)))
        {
            var name = pane.Name ?? "";
            var cls = pane.ClassName ?? "";
            if (name.Contains("下载", StringComparison.Ordinal) ||
                name.Contains("download", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("近期", StringComparison.Ordinal) ||
                name.Contains("保留", StringComparison.Ordinal) ||
                cls.Contains("Download", StringComparison.OrdinalIgnoreCase))
            {
                yield return pane;
            }
        }
    }

    /// <summary>在 Chrome 浮窗 UIA 子树中搜索并真实鼠标点击「保留」</summary>
    private static bool TryClickKeepInElement(
        AutomationElement parent,
        ConditionFactory cf,
        string fileHint,
        ref bool loggedOnce,
        out AutomationElement? clickedWin)
    {
        clickedWin = parent;
        var keepEl = FindKeepElement(parent, cf, fileHint);
        if (keepEl == null)
        {
            if (!loggedOnce)
            {
                var allElements = parent.FindAllDescendants();
                if (allElements.Length > 0)
                {
                    loggedOnce = true;
                    Console.WriteLine($"[UIA] {parent.Name} 浮窗子树 {allElements.Length} 元素，「保留」相关:");
                    foreach (var el in allElements)
                    {
                        var name = el.Name ?? "";
                        if (name.Length > 0 && name.Length < 60 &&
                            (name.Contains("保留") || name.Contains("危险") || name.Contains("Keep") ||
                             name.Contains("Dangerous") || name.Contains("不安全")))
                        {
                            Console.WriteLine($"  [{el.ControlType}] {name} class={el.ClassName ?? ""}");
                        }
                    }
                }
            }

            return false;
        }

        var label = keepEl.Name ?? "";
        var rect = keepEl.BoundingRectangle;
        Console.WriteLine($"[UIA] 浮窗找到「保留」: [{keepEl.ControlType}] {label} at ({(int)(rect.Left + rect.Width / 2)}, {(int)(rect.Top + rect.Height / 2)})");
        ClickElementWithMouse(keepEl);
        return true;
    }

    private static void ClickElementWithMouse(AutomationElement el)
    {
        var rect = el.BoundingRectangle;
        if (rect.Width > 0 && rect.Height > 0)
        {
            var x = (int)(rect.Left + rect.Width / 2);
            var y = (int)(rect.Top + rect.Height / 2);
            FlaUI.Core.Input.Mouse.MoveTo(new Point(x, y));
            Thread.Sleep(100);
            FlaUI.Core.Input.Mouse.Click(FlaUI.Core.Input.MouseButton.Left);
            return;
        }

        el.Click();
    }

    /// <summary>处理 Chrome 浮窗点击「保留」后的二次确认（仍在 Chrome UIA 树内）</summary>
    private static async Task HandleSecondaryConfirmationAsync(AutomationElement win, ConditionFactory cf)
    {
        await Task.Delay(1000);
        foreach (var root in EnumerateDownloadBubbleRoots(win, cf))
        {
            var buttons = root.FindAllDescendants(cf.ByControlType(ControlType.Button));
            foreach (var btn in buttons)
            {
                var name = btn.Name ?? "";
                if (name.Contains("仍要保留") || name.Contains("仍然保留") || name == "Keep anyway")
                {
                    Console.WriteLine($"[UIA] 浮窗二次确认: {name}");
                    ClickElementWithMouse(btn);
                    return;
                }
            }
        }
    }

    /// <summary>OS-level click at screen coordinates (isTrusted: true).</summary>
    public static async Task ClickAtAsync(int x, int y)
    {
        await Task.Run(() =>
        {
            FlaUI.Core.Input.Mouse.MoveTo(new System.Drawing.Point(x, y));
            System.Threading.Thread.Sleep(100);
            FlaUI.Core.Input.Mouse.Click(FlaUI.Core.Input.MouseButton.Left);
        });
    }

    /// <summary>在 CPMS 报表页用真实鼠标点击「导出」按钮。</summary>
    public static async Task<bool> ClickExportButtonAsync(int timeoutMs = 20000)
    {
        await ActivateCpmsChromeAsync("计划建设");
        using var automation = new UIA3Automation();
        var cf = automation.ConditionFactory;
        var desktop = automation.GetDesktop();
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            var windows = FindChromeWindows(automation)
                .Where(w => ScoreChromeWindow(w, "计划建设") > 0)
                .OrderByDescending(w => ScoreChromeWindow(w, "计划建设"));

            foreach (var win in windows)
            {
                var buttons = win.FindAllDescendants(cf.ByControlType(ControlType.Button));
                foreach (var btn in buttons)
                {
                    var name = btn.Name ?? "";
                    if (name is not ("导出" or "导 出"))
                        continue;

                    var rect = btn.BoundingRectangle;
                    if (rect.Width <= 0 || rect.Height <= 0)
                        continue;

                    var x = (int)(rect.Left + rect.Width / 2);
                    var y = (int)(rect.Top + rect.Height / 2);
                    Console.WriteLine($"[UIA] 报表页「导出」按钮 at ({x}, {y})");
                    FlaUI.Core.Input.Mouse.MoveTo(new System.Drawing.Point(x, y));
                    Thread.Sleep(100);
                    FlaUI.Core.Input.Mouse.Click(FlaUI.Core.Input.MouseButton.Left);
                    return true;
                }
            }

            await Task.Delay(1500);
        }

        return false;
    }

    /// <summary>
    /// Finds and clicks the "下载" button in the CPMS download list row containing the serial number.
    /// Uses FlaUI to perform a real OS-level click (isTrusted: true).
    /// Returns true if clicked, false if not found.
    /// </summary>
    public static async Task<bool> ClickDownloadButtonBySerialAsync(string serialNumber, int timeoutMs = 30000)
    {
        await ActivateCpmsChromeAsync(serialNumber);
        using var automation = new UIA3Automation();
        var cf = automation.ConditionFactory;
        var desktop = automation.GetDesktop();
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        for (var scanAttempt = 1; ; scanAttempt++)
        {
            try
            {
                var windows = desktop.FindAllChildren(cf.ByControlType(ControlType.Window));
                Console.WriteLine($"[UIA] 第 {scanAttempt} 次扫描，桌面共 {windows.Length} 个窗口");

                var chromeWindows = windows
                    .Where(w => w.ClassName == "Chrome_WidgetWin_1")
                    .OrderByDescending(w =>
                        (w.Name ?? "").Contains("中国移动", StringComparison.Ordinal) ||
                        (w.Name ?? "").Contains("cpms", StringComparison.OrdinalIgnoreCase) ||
                        (w.Name ?? "").Contains("计划建设", StringComparison.Ordinal))
                    .ToArray();

                foreach (var win in chromeWindows)
                {
                    Console.WriteLine($"[UIA] 找到 Chrome 窗口: {win.Name}");

                    try
                    {
                        var hwnd = win.Properties.NativeWindowHandle.Value;
                        if (hwnd != IntPtr.Zero)
                        {
                            ForceForeground(hwnd);
                            await Task.Delay(400);
                        }
                    }
                    catch (Exception fgEx)
                    {
                        Console.WriteLine($"[UIA] 前台激活失败（非致命）: {fgEx.Message}");
                    }

                    var allElements = win.FindAllDescendants();
                    Console.WriteLine($"[UIA] 扫描 {allElements.Length} 个元素...");

                    // Collect all "下载" buttons with valid bounding rectangles
                    var downloadButtons = new List<(AutomationElement btn, Rectangle rect)>();
                    foreach (var el in allElements)
                    {
                        if (el.ControlType == ControlType.Button && (el.Name ?? "") == "下载")
                        {
                            var rect = el.BoundingRectangle;
                            if (rect.Width > 0 && rect.Height > 0)
                            {
                                downloadButtons.Add((el, rect));
                            }
                        }
                    }

                    Console.WriteLine($"[UIA] 找到 {downloadButtons.Count} 个「下载」按钮");

                    if (downloadButtons.Count == 0)
                    {
                        if (DateTime.UtcNow >= deadline)
                        {
                            Console.WriteLine("[UIA] 超时，未找到下载按钮");
                            return false;
                        }
                        continue; // try next Chrome window or retry
                    }

                    // Try to match by serial number proximity
                    if (!string.IsNullOrEmpty(serialNumber))
                    {
                        var serialElements = new List<AutomationElement>();
                        foreach (var el in allElements)
                        {
                            if ((el.Name ?? "").Contains(serialNumber))
                                serialElements.Add(el);
                        }

                        if (serialElements.Count > 0)
                        {
                            Console.WriteLine($"[UIA] 找到 {serialElements.Count} 个包含流水号的元素");
                            var serialY = serialElements[0].BoundingRectangle.Top +
                                          serialElements[0].BoundingRectangle.Height / 2;

                            AutomationElement? closestBtn = null;
                            var closestDist = double.MaxValue;
                            Rectangle closestRect = default;

                            foreach (var (btn, rect) in downloadButtons)
                            {
                                var dist = Math.Abs(rect.Top + rect.Height / 2 - serialY);
                                if (dist < closestDist)
                                {
                                    closestDist = dist;
                                    closestBtn = btn;
                                    closestRect = rect;
                                }
                            }

                            if (closestBtn != null && closestDist < 200)
                            {
                                var x = (int)(closestRect.Left + closestRect.Width / 2);
                                var y = (int)(closestRect.Top + closestRect.Height / 2);
                                Console.WriteLine($"[UIA] 流水号旁下载按钮 at ({x}, {y}), 距离 {closestDist:F0}px");
                                FlaUI.Core.Input.Mouse.MoveTo(new System.Drawing.Point(x, y));
                                Thread.Sleep(100);
                                FlaUI.Core.Input.Mouse.Click(FlaUI.Core.Input.MouseButton.Left);
                                return true;
                            }
                        }
                    }

                    // Fallback: click the first "下载" button (latest export is on top)
                    {
                        var (firstBtn, firstRect) = downloadButtons[0];
                        var fx = (int)(firstRect.Left + firstRect.Width / 2);
                        var fy = (int)(firstRect.Top + firstRect.Height / 2);
                        Console.WriteLine($"[UIA] 点击第一个下载按钮 at ({fx}, {fy}) (共 {downloadButtons.Count} 个)");
                        FlaUI.Core.Input.Mouse.MoveTo(new System.Drawing.Point(fx, fy));
                        Thread.Sleep(100);
                        FlaUI.Core.Input.Mouse.Click(FlaUI.Core.Input.MouseButton.Left);
                        return true;
                    }
                }

                // No Chrome window found this round
                if (DateTime.UtcNow >= deadline)
                {
                    Console.WriteLine("[UIA] 超时，未找到 Chrome 窗口");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UIA] 扫描异常: {ex.Message}");
                if (DateTime.UtcNow >= deadline) return false;
            }

            await Task.Delay(2000);
        }
    }

    /// <summary>在所有控件类型中搜索"保留"相关元素，优先匹配包含 fileHint 的</summary>
    private static AutomationElement? FindKeepElement(AutomationElement parent, ConditionFactory cf, string fileHint = "")
    {
        // 收集所有"保留"相关的元素
        var candidates = new List<(AutomationElement el, string name)>();

        // 搜索 Button
        var buttons = parent.FindAllDescendants(cf.ByControlType(ControlType.Button));
        foreach (var el in buttons)
        {
            var name = el.Name ?? "";
            if (IsKeepButton(name))
                candidates.Add((el, name));
        }

        // Chrome 浮窗里「保留」可能是 Button / Hyperlink / Text / ListItem / Custom
        foreach (var ctrlType in new[] { ControlType.Hyperlink, ControlType.Text, ControlType.ListItem, ControlType.Custom })
        {
            var elements = parent.FindAllDescendants(cf.ByControlType(ctrlType));
            foreach (var el in elements)
            {
                var name = el.Name ?? "";
                if (name.StartsWith("保留") || name.Contains("Keep dangerous") || name.Contains("保留危险"))
                    candidates.Add((el, name));
            }
        }

        if (candidates.Count == 0)
            return null;

        // 优先匹配包含 fileHint（流水号）的按钮
        if (!string.IsNullOrEmpty(fileHint))
        {
            var match = candidates.FirstOrDefault(c => c.name.Contains(fileHint));
            if (match.el != null)
            {
                Console.WriteLine($"[UIA] 匹配到目标文件保留按钮: {match.name}");
                return match.el;
            }
        }

        var first = candidates[0];
        Console.WriteLine($"[UIA] 使用第一个保留按钮: {first.name}");
        return first.el;
    }

    /// <summary>查找 Chrome 工具栏上的下载按钮（优先「有新下载」提示，避免误匹配页面按钮）</summary>
    private static AutomationElement? FindDownloadToolbarButton(AutomationElement win, ConditionFactory cf)
    {
        string[] dlToolbarNames =
        [
            "有新下载好的内容",
            "下载内容",
            "Show downloads",
            "Downloads",
            "下载",
        ];

        foreach (var dlName in dlToolbarNames)
        {
            var found = win.FindAllDescendants(cf.ByName(dlName));
            if (found.Length > 0)
                return found[0];
        }

        var panes = win.FindAllDescendants(cf.ByClassName("PinnedToolbarActionsContainer"));
        foreach (var pane in panes)
        {
            AutomationElement? fallback = null;
            foreach (var child in pane.FindAllChildren())
            {
                var name = child.Name ?? "";
                if (name.Contains("有新下载", StringComparison.Ordinal))
                    return child;
                if (name is "下载内容" or "Downloads" ||
                    name.Equals("下载", StringComparison.Ordinal))
                    fallback ??= child;
            }

            if (fallback != null)
                return fallback;
        }

        var toolbars = win.FindAllDescendants(cf.ByControlType(ControlType.ToolBar));
        foreach (var tb in toolbars)
        {
            foreach (var b in tb.FindAllChildren(cf.ByControlType(ControlType.Button)))
            {
                var bName = b.Name ?? "";
                if (bName is "下载内容" or "Downloads")
                    return b;
            }
        }

        return null;
    }

    /// <summary>打印 Chrome 窗口中的所有控件（诊断用）</summary>
    private static void LogChromeElements(AutomationElement win, ConditionFactory cf)
    {
        var buttons = win.FindAllDescendants(cf.ByControlType(ControlType.Button));
        Console.WriteLine($"[UIA] Chrome 窗口共 {buttons.Length} 个 Button:");
        foreach (var b in buttons)
            Console.WriteLine($"  [{b.Name ?? "(null)"}]");

        var toolbars = win.FindAllDescendants(cf.ByControlType(ControlType.ToolBar));
        foreach (var tb in toolbars)
        {
            var children = tb.FindAllChildren();
            Console.WriteLine($"[UIA] ToolBar ({tb.Name ?? "anon"}) 子元素:");
            foreach (var child in children)
                Console.WriteLine($"  [{child.ControlType}] {child.Name ?? "(null)"} class={child.ClassName ?? ""}");
        }

        // 打印所有 Pane 子元素（下载浮窗可能是 Pane）
        var panes = win.FindAllDescendants(cf.ByControlType(ControlType.Pane));
        Console.WriteLine($"[UIA] 共 {panes.Length} 个 Pane:");
        foreach (var p in panes)
        {
            var name = p.Name ?? "";
            if (name.Contains("下载") || name.Contains("download") || name.Contains("近期")
                || name.Contains("保留") || name.Contains("记录"))
            {
                Console.WriteLine($"  *** [{p.ClassName}] {name}");
                var paneChildren = p.FindAllChildren();
                foreach (var child in paneChildren)
                    Console.WriteLine($"    [{child.ControlType}] {child.Name ?? "(null)"} class={child.ClassName ?? ""}");
            }
        }

        // 打印所有 Hyperlink
        var links = win.FindAllDescendants(cf.ByControlType(ControlType.Hyperlink));
        if (links.Length > 0)
        {
            Console.WriteLine($"[UIA] 共 {links.Length} 个 Hyperlink:");
            foreach (var l in links)
                Console.WriteLine($"  [{l.Name ?? "(null)"}]");
        }

        // 打印所有 ListItem
        var items = win.FindAllDescendants(cf.ByControlType(ControlType.ListItem));
        if (items.Length > 0)
        {
            Console.WriteLine($"[UIA] 共 {items.Length} 个 ListItem:");
            foreach (var item in items)
                Console.WriteLine($"  [{item.Name ?? "(null)"}]");
        }
    }

    private static bool IsKeepButton(string name)
    {
        return name == "保留" || name == "Keep"
            || name.Contains("保留危险") || name.Contains("Keep dangerous")
            || name.Contains("保留不安全") || name.Contains("Keep anyway")
            || (name.Contains("保留") && name.Contains("不安全"))
            || (name.StartsWith("保留") && name.Contains("文件"));
    }

    private static List<Window> FindChromeWindows(UIA3Automation automation)
    {
        var desktop = automation.GetDesktop();
        var allWindows = desktop.FindAllChildren(cf =>
            cf.ByControlType(ControlType.Window));

        var result = new List<Window>();
        foreach (var w in allWindows)
        {
            if (w.ClassName == "Chrome_WidgetWin_1" && w is Window win)
                result.Add(win);
        }
        return result;
    }
}
