using FlaUI.UIA3;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using System.Drawing;
using ChromeAutomation.Client;

namespace ChromeAutomation.CpszzNavigate;

/// <summary>
/// Uses FlaUI (UIA3) to interact with Chrome and Windows UI:
/// - Handle Chrome dangerous download prompts (保留/Keep)
/// - Run downloaded JNLP files
/// </summary>
public static class ErpUiaHelper
{
    /// <summary>Oracle ERP 页面请求失败时的典型错误文案，出现时需重试点击。</summary>
    public const string RequestProcessingError = "Unexpected error while processing the request.";

    /// <summary>
    /// RPA: 打开 Chrome 下载浮窗，找到并点击"保留"按钮处理 JNLP 文件。
    /// Chrome 拦截 .jnlp 危险下载后需要点击"保留"。
    /// </summary>
    public static async Task<bool> ClickKeepInDownloadPanelAsync(string fileHint = ".jnlp", int timeoutMs = 90000)
    {
        using var automation = new UIA3Automation();
        var cf = automation.ConditionFactory;
        var desktop = automation.GetDesktop();
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var loggedOnce = false;
        var panelOpened = false;
        var attempts = 0;

        while (DateTime.UtcNow < deadline)
        {
            attempts++;
            try
            {
                var windows = desktop.FindAllChildren(cf.ByControlType(ControlType.Window));
                foreach (var win in windows)
                {
                    if (win.ClassName != "Chrome_WidgetWin_1") continue;

                    // 打开下载浮窗
                    var dlToolbarBtn = FindDownloadToolbarButton(win, cf);
                    if (dlToolbarBtn != null)
                    {
                        dlToolbarBtn.Click();
                        if (!panelOpened)
                        {
                            Console.WriteLine("[UIA] 已点击下载工具栏按钮，等待浮窗...");
                            panelOpened = true;
                        }
                        await Task.Delay(2000);
                    }

                    // 搜索"保留"元素
                    var keepEl = FindKeepElement(win, cf, fileHint);
                    if (keepEl != null)
                    {
                        Console.WriteLine($"[UIA] 找到保留元素: [{keepEl.ControlType}] {keepEl.Name}");
                        keepEl.Click();
                        Console.WriteLine("[UIA] 已点击保留");

                        // 二次确认
                        await Task.Delay(1500);
                        await HandleSecondaryConfirmationAsync(win, cf);

                        return true;
                    }

                    if (!loggedOnce)
                    {
                        loggedOnce = true;
                        LogChromeElements(win, cf);
                    }
                }
            }
            catch { }

            if (attempts % 5 == 0)
                Console.WriteLine($"[UIA] 第 {attempts} 次重试搜索保留按钮...");

            await Task.Delay(3000);
        }

        Console.WriteLine("[UIA] 未找到保留按钮");
        return false;
    }

    /// <summary>
    /// RPA: 运行已下载的 JNLP 文件。
    /// 在 Chrome 下载面板中找到 .jnlp 文件并点击运行，或者在系统下载目录中找到并双击运行。
    /// </summary>
    public static async Task<bool> RunDownloadedJnlpAsync(int timeoutMs = 30000)
    {
        // 等待文件下载完成
        await Task.Delay(3000);

        return await Task.Run(() =>
        {
            // 方法1: 在 Chrome 下载浮窗中找到 JNLP 文件并点击
            try
            {
                using var automation = new UIA3Automation();
                var cf = automation.ConditionFactory;
                var desktop = automation.GetDesktop();
                var windows = desktop.FindAllChildren(cf.ByControlType(ControlType.Window));

                foreach (var win in windows)
                {
                    if (win.ClassName != "Chrome_WidgetWin_1") continue;

                    // 打开下载浮窗
                    var dlToolbarBtn = FindDownloadToolbarButton(win, cf);
                    if (dlToolbarBtn != null)
                    {
                        dlToolbarBtn.Click();
                        Thread.Sleep(2000);
                    }

                    // 搜索包含 .jnlp 的元素
                    var allElements = win.FindAllDescendants();
                    foreach (var el in allElements)
                    {
                        var name = el.Name ?? "";
                        if (name.Contains(".jnlp", StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"[UIA] 找到 JNLP 元素: [{el.ControlType}] {name}");
                            // 双击运行
                            var rect = el.BoundingRectangle;
                            if (rect.Width > 0 && rect.Height > 0)
                            {
                                var x = (int)(rect.Left + rect.Width / 2);
                                var y = (int)(rect.Top + rect.Height / 2);
                                FlaUI.Core.Input.Mouse.MoveTo(new System.Drawing.Point(x, y));
                                Thread.Sleep(200);
                                FlaUI.Core.Input.Mouse.DoubleClick(FlaUI.Core.Input.MouseButton.Left);
                                Console.WriteLine($"[UIA] 已双击运行 JNLP at ({x}, {y})");
                                return true;
                            }
                            else
                            {
                                // 元素不可见，尝试直接点击
                                el.Click();
                                Console.WriteLine("[UIA] 已点击 JNLP 元素");
                                return true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UIA] Chrome 内运行 JNLP 失败: {ex.Message}");
            }

            // 方法2: 在系统下载目录中查找并运行最新的 JNLP 文件
            try
            {
                var downloadsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads");

                var jnlpFiles = new DirectoryInfo(downloadsDir)
                    .GetFiles("*.jnlp")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToList();

                if (jnlpFiles.Count > 0)
                {
                    var latest = jnlpFiles[0];
                    Console.WriteLine($"[UIA] 找到最新 JNLP 文件: {latest.FullName} ({latest.LastWriteTime:HH:mm:ss})");

                    // 使用系统关联程序打开 JNLP
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = latest.FullName,
                        UseShellExecute = true
                    });
                    Console.WriteLine("[UIA] 已启动 JNLP 文件");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UIA] 文件系统运行 JNLP 失败: {ex.Message}");
            }

            Console.WriteLine("[UIA] 未找到 JNLP 文件");
            return false;
        });
    }

    /// <summary>
    /// 处理 Java Web Start 安全弹窗:
    /// Strategy 0 (JAB): 用 Java Access Bridge 直接操作无障碍树（复选框 + 运行按钮）。
    /// Strategy 1 (FlaUI): 用 Alt+I 勾选 "我接受风险(I)"，Alt+R 点击"运行(R)"。
    /// </summary>
    public static async Task<bool> HandleJavaSecurityDialogAsync(int timeoutMs = 90000, int maxClickAttempts = 10)
    {
        return await Task.Run(async () =>
        {
            // Strategy 0: Connect to JAB helper
            JabClient? jab = null;
            try
            {
                jab = new JabClient();
                await jab.ConnectAsync();
                Console.WriteLine("[JAB] JAB helper 已连接");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JAB] 连接失败（回退到 FlaUI 快捷键）: {ex.Message}");
                jab?.Dispose();
                jab = null;
            }

            try
            {
                using var automation = new UIA3Automation();
                var cf = automation.ConditionFactory;
                var desktop = automation.GetDesktop();
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                var handledCount = 0;

                while (DateTime.UtcNow < deadline && handledCount < maxClickAttempts)
                {
                    try
                    {
                        var allWindows = desktop.FindAllChildren(cf.ByControlType(ControlType.Window));

                        // Close "详细信息" sub-dialog if open
                        foreach (var win in allWindows)
                        {
                            var wName = win.Name ?? "";
                            var wClass = win.ClassName ?? "";
                            if (wClass.StartsWith("SunAwt") && wName.Contains("详细"))
                            {
                                Console.WriteLine("[UIA] 关闭「详细信息」子窗口");
                                if (win is Window cw) cw.Focus();
                                Thread.Sleep(300);
                                FlaUI.Core.Input.Keyboard.Pressing(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESC);
                                Thread.Sleep(1000);
                            }
                        }

                        // Handle security dialog
                        foreach (var win in allWindows)
                        {
                            var winClass = win.ClassName ?? "";
                            var winName = win.Name ?? "";

                            // Only handle SunAwtDialog (security dialog)
                            if (winClass != "SunAwtDialog") continue;

                            // Skip loading/progress dialogs
                            if (winName.Contains("启动") || winName.Contains("Starting") || winName.Contains("加载"))
                                continue;

                            var rect = win.BoundingRectangle;
                            Console.WriteLine($"[UIA] Java 安全弹窗: \"{winName}\" size={rect.Width}x{rect.Height}");

                            // Strategy 0: JAB — uses Java accessibility tree
                            if (jab != null)
                            {
                                Console.WriteLine("[JAB] 策略0: JAB 无障碍树交互...");
                                var jabOk = await JabHandleDialogAsync(jab, win);
                                if (jabOk)
                                {
                                    handledCount++;
                                    Console.WriteLine($"[JAB] ✓ 安全弹窗已处理 ({handledCount}/{maxClickAttempts})");
                                    continue;
                                }
                                Console.WriteLine("[JAB] 未成功，回退到 FlaUI 快捷键...");
                            }

                            // Strategy 1: FlaUI Alt+I / Alt+R
                            if (win is Window w) w.Focus();
                            Thread.Sleep(800);

                            Console.WriteLine("[UIA] 快捷键: Alt+I(勾选复选框) → Alt+R(点击运行)");

                            // Alt+I to check the checkbox
                            FlaUI.Core.Input.Keyboard.Pressing(FlaUI.Core.WindowsAPI.VirtualKeyShort.LMENU);
                            Thread.Sleep(100);
                            FlaUI.Core.Input.Keyboard.Pressing(FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_I);
                            Thread.Sleep(100);
                            FlaUI.Core.Input.Keyboard.Release(FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_I);
                            Thread.Sleep(100);
                            FlaUI.Core.Input.Keyboard.Release(FlaUI.Core.WindowsAPI.VirtualKeyShort.LMENU);
                            Thread.Sleep(800);

                            // Alt+R to click "运行(R)"
                            FlaUI.Core.Input.Keyboard.Pressing(FlaUI.Core.WindowsAPI.VirtualKeyShort.LMENU);
                            Thread.Sleep(100);
                            FlaUI.Core.Input.Keyboard.Pressing(FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_R);
                            Thread.Sleep(100);
                            FlaUI.Core.Input.Keyboard.Release(FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_R);
                            Thread.Sleep(100);
                            FlaUI.Core.Input.Keyboard.Release(FlaUI.Core.WindowsAPI.VirtualKeyShort.LMENU);
                            Thread.Sleep(2000);

                            handledCount++;
                            Console.WriteLine($"[UIA] 已处理 Java 安全弹窗 ({handledCount}/{maxClickAttempts})");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[UIA] Java 弹窗异常: {ex.Message}");
                    }

                    Thread.Sleep(1500);
                }

                return handledCount > 0;
            }
            finally
            {
                jab?.Dispose();
            }
        });
    }

    /// <summary>
    /// Strategy 0: Use Java Access Bridge to directly interact with the security dialog.
    /// Walks the Java accessibility tree to find checkbox and button by role/name.
    /// Uses DoActionAsync (vmId+ac handle) for reliable clicking — avoids re-searching.
    /// </summary>
    static async Task<bool> JabHandleDialogAsync(JabClient jab, AutomationElement win)
    {
        try
        {
            // Get native window handle from FlaUI element
            var hwnd = win.Properties.NativeWindowHandle.Value;
            var hwndLong = (long)hwnd;

            // Step 1: Find and check the "I accept" checkbox
            // Chinese: "我接受风险并希望运行此应用程序(I)"
            // English: "I accept the risk and want to run this application"
            var checkbox = await jab.FindNodeAsync(hwndLong, nameContains: "接受");
            if (checkbox == null)
                checkbox = await jab.FindNodeAsync(hwndLong, nameContains: "accept");

            if (checkbox != null)
            {
                Console.WriteLine($"[JAB] 复选框: name='{checkbox.name}' vmId={checkbox.vmId} ac={checkbox.ac} states={checkbox.states}");
                if (!checkbox.states.Contains("checked"))
                {
                    // Use DoActionAsync with vmId+ac handle — direct, no re-search
                    var ok = await jab.DoActionAsync(checkbox.vmId, checkbox.ac);
                    Console.WriteLine($"[JAB] 勾选复选框(DoAction): {(ok ? "成功" : "失败")}");

                    if (!ok)
                    {
                        // Fallback: try ClickNodeAsync (OS-level click at center)
                        Console.WriteLine("[JAB] 回退到 ClickNode...");
                        ok = await jab.ClickNodeAsync(hwndLong, nameContains: checkbox.name);
                        Console.WriteLine($"[JAB] 勾选复选框(ClickNode): {(ok ? "成功" : "失败")}");
                    }
                }
                else
                {
                    Console.WriteLine("[JAB] 复选框已勾选，跳过");
                }
                Thread.Sleep(800);
            }
            else
            {
                Console.WriteLine("[JAB] 未找到复选框，继续查找运行按钮...");
            }

            // Step 2: Find and click the "Run" button
            // Chinese: "运行(R)"  English: "Run"
            var runBtn = await jab.FindNodeAsync(hwndLong, role: "push button", nameContains: "运行");
            if (runBtn == null)
                runBtn = await jab.FindNodeAsync(hwndLong, role: "push button", nameContains: "Run");
            if (runBtn == null)
                runBtn = await jab.FindNodeAsync(hwndLong, nameContains: "运行");
            if (runBtn == null)
                runBtn = await jab.FindNodeAsync(hwndLong, nameContains: "Run");

            if (runBtn == null)
            {
                Console.WriteLine("[JAB] 未找到运行按钮");
                return false;
            }

            Console.WriteLine($"[JAB] 运行按钮: name='{runBtn.name}' vmId={runBtn.vmId} ac={runBtn.ac} states={runBtn.states}");

            var clicked = await jab.DoActionAsync(runBtn.vmId, runBtn.ac);
            Console.WriteLine($"[JAB] 点击运行(DoAction): {(clicked ? "成功" : "失败")}");

            if (!clicked)
            {
                Console.WriteLine("[JAB] 回退到 ClickNode...");
                clicked = await jab.ClickNodeAsync(hwndLong, nameContains: runBtn.name);
                Console.WriteLine($"[JAB] 点击运行(ClickNode): {(clicked ? "成功" : "失败")}");
            }

            Thread.Sleep(1500);
            return clicked;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[JAB] 异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>处理 Chrome 点击"保留"后的二次确认弹窗</summary>
    private static async Task HandleSecondaryConfirmationAsync(AutomationElement win, ConditionFactory cf)
    {
        await Task.Delay(1000);
        var buttons = win.FindAllDescendants(cf.ByControlType(ControlType.Button));
        foreach (var btn in buttons)
        {
            var name = btn.Name ?? "";
            if (name.Contains("仍要保留") || name.Contains("仍然保留") || name == "Keep anyway")
            {
                Console.WriteLine($"[UIA] 二次确认: {name}");
                btn.Click();
                return;
            }
        }
    }

    private static AutomationElement? FindKeepElement(AutomationElement parent, ConditionFactory cf, string fileHint = "")
    {
        var candidates = new List<(AutomationElement el, string name)>();

        var buttons = parent.FindAllDescendants(cf.ByControlType(ControlType.Button));
        foreach (var el in buttons)
        {
            var name = el.Name ?? "";
            if (IsKeepButton(name))
                candidates.Add((el, name));
        }

        foreach (var ctrlType in new[] { ControlType.Hyperlink, ControlType.Text, ControlType.ListItem })
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

        if (!string.IsNullOrEmpty(fileHint))
        {
            var match = candidates.FirstOrDefault(c =>
                c.name.Contains(fileHint) || c.name.Contains(".jnlp"));
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

    private static AutomationElement? FindDownloadToolbarButton(AutomationElement win, ConditionFactory cf)
    {
        string[] dlToolbarNames = ["下载内容", "下载", "Downloads", "Show downloads"];
        foreach (var dlName in dlToolbarNames)
        {
            var found = win.FindAllDescendants(cf.ByName(dlName));
            if (found.Length > 0)
            {
                return found[0];
            }
        }

        var toolbars = win.FindAllDescendants(cf.ByControlType(ControlType.ToolBar));
        foreach (var tb in toolbars)
        {
            var tbButtons = tb.FindAllChildren(cf.ByControlType(ControlType.Button));
            foreach (var b in tbButtons)
            {
                var bName = b.Name ?? "";
                if (bName == "下载" || bName.Equals("Downloads", StringComparison.OrdinalIgnoreCase)
                    || bName == "下载内容")
                {
                    return b;
                }
            }
        }

        var panes = win.FindAllDescendants(cf.ByClassName("PinnedToolbarActionsContainer"));
        foreach (var pane in panes)
        {
            var children = pane.FindAllChildren();
            foreach (var child in children)
            {
                var name = child.Name ?? "";
                if (name.Contains("下载") || name.Contains("download"))
                {
                    return child;
                }
            }
        }

        return null;
    }

    private static void LogChromeElements(AutomationElement win, ConditionFactory cf)
    {
        var buttons = win.FindAllDescendants(cf.ByControlType(ControlType.Button));
        Console.WriteLine($"[UIA] Chrome 窗口共 {buttons.Length} 个 Button:");
        foreach (var b in buttons)
            Console.WriteLine($"  [{b.Name ?? "(null)"}]");

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
                    Console.WriteLine($"    [{child.ControlType}] {child.Name ?? "(null)"}");
            }
        }

        var links = win.FindAllDescendants(cf.ByControlType(ControlType.Hyperlink));
        if (links.Length > 0)
        {
            Console.WriteLine($"[UIA] 共 {links.Length} 个 Hyperlink:");
            foreach (var l in links)
                Console.WriteLine($"  [{l.Name ?? "(null)"}]");
        }

        var items = win.FindAllDescendants(cf.ByControlType(ControlType.ListItem));
        if (items.Length > 0)
        {
            Console.WriteLine($"[UIA] 共 {items.Length} 个 ListItem:");
            foreach (var item in items)
                Console.WriteLine($"  [{item.Name ?? "(null)"}]");
        }
    }

    /// <summary>
    /// RPA: 在 Chrome 窗口中展开菜单树并点击目标项。
    /// 确保在同一个窗口中完成展开和点击，避免多窗口问题。
    /// </summary>
    public static async Task<bool> ExpandAndClickInChromeAsync(
        string expandText, string targetText, int timeoutMs = 30000)
    {
        return await Task.Run(() =>
        {
            using var automation = new UIA3Automation();
            var cf = automation.ConditionFactory;
            var desktop = automation.GetDesktop();
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            var expanded = false;

            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var windows = desktop.FindAllChildren(cf.ByControlType(ControlType.Window));
                    foreach (var win in windows)
                    {
                        if (win.ClassName != "Chrome_WidgetWin_1") continue;

                        // Step 1: Check if target is already visible
                        var target = FindVisibleHyperlink(win, cf, targetText);
                        if (target != null)
                        {
                            Console.WriteLine($"[UIA] 目标已可见: \"{target.Name}\"");
                            ClickElement(target, win);
                            return true;
                        }

                        // Step 2: If not expanded yet, try to expand
                        if (!expanded)
                        {
                            var expandEl = FindVisibleHyperlink(win, cf, expandText);
                            if (expandEl != null)
                            {
                                Console.WriteLine($"[UIA] 展开: \"{expandEl.Name}\"");
                                ClickElement(expandEl, win);
                                expanded = true;
                                Thread.Sleep(4000); // Wait for sub-items to load
                                break; // Re-scan windows after expansion
                            }
                        }
                    }
                }
                catch { }

                Thread.Sleep(2000);
            }

            Console.WriteLine($"[UIA] ExpandAndClick 超时: expand={expandText}, target={targetText}");
            return false;
        });
    }

    private static AutomationElement? FindVisibleHyperlink(AutomationElement win, ConditionFactory cf, string text, bool exact = false)
    {
        var hyperlinks = win.FindAllDescendants(cf.ByControlType(ControlType.Hyperlink));
        foreach (var link in hyperlinks)
        {
            var name = link.Name ?? "";
            var match = exact ? name == text : name.Contains(text);
            if (!match) continue;
            var rect = link.BoundingRectangle;
            if (rect.Width <= 0 || rect.Height <= 0) continue;
            return link;
        }

        // Also search ListItem
        var items = win.FindAllDescendants(cf.ByControlType(ControlType.ListItem));
        foreach (var item in items)
        {
            var name = item.Name ?? "";
            var match = exact ? name == text : name.Contains(text);
            if (!match) continue;
            var rect = item.BoundingRectangle;
            if (rect.Width <= 0 || rect.Height <= 0) continue;
            return item;
        }

        return null;
    }

    private static void ClickElement(AutomationElement el, AutomationElement win)
    {
        var rect = el.BoundingRectangle;
        var x = (int)(rect.Left + rect.Width / 2);
        var y = (int)(rect.Top + rect.Height / 2);
        if (win is Window w) w.Focus();
        Thread.Sleep(300);
        FlaUI.Core.Input.Mouse.MoveTo(new Point(x, y));
        Thread.Sleep(200);
        FlaUI.Core.Input.Mouse.Click(FlaUI.Core.Input.MouseButton.Left);
        Console.WriteLine($"[UIA] 已点击 \"{el.Name}\" at ({x}, {y})");
    }

    private static bool IsKeepButton(string name)
    {
        return name == "保留" || name == "Keep"
            || name.Contains("保留危险") || name.Contains("Keep dangerous")
            || (name.StartsWith("保留") && name.Contains("文件"));
    }

    /// <summary>
    /// RPA: 在包含 ERP 内容的 Chrome 窗口中查找并点击元素。
    /// 先定位正确的 Chrome 窗口（包含 Oracle ERP 内容的），
    /// 再在其中查找目标元素，避免误操作其它软件或 Chrome 窗口。
    /// </summary>
    public static async Task<bool> ClickElementByTextInChromeAsync(string text, bool exact = false, int timeoutMs = 15000)
    {
        return await Task.Run(() =>
        {
            using var automation = new UIA3Automation();
            var cf = automation.ConditionFactory;
            var desktop = automation.GetDesktop();
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            var logged = false;

            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var windows = desktop.FindAllChildren(cf.ByControlType(ControlType.Window));
                    foreach (var win in windows)
                    {
                        if (win.ClassName != "Chrome_WidgetWin_1") continue;

                        // Verify this is the ERP Chrome window by checking its content
                        if (!IsErpWindow(win, cf))
                        {
                            if (!logged)
                            {
                                Console.WriteLine($"[UIA] 跳过非 ERP 窗口: {win.Name ?? "(untitled)"}");
                            }
                            continue;
                        }

                        if (!logged)
                        {
                            logged = true;
                            Console.WriteLine($"[UIA] 找到 ERP 窗口: {win.Name ?? "(ERP)"}");
                        }

                        // Search Hyperlink elements first
                        var hyperlinks = win.FindAllDescendants(cf.ByControlType(ControlType.Hyperlink));
                        foreach (var link in hyperlinks)
                        {
                            var name = link.Name ?? "";
                            var match = exact ? name == text : name.Contains(text);
                            if (!match) continue;

                            var rect = link.BoundingRectangle;
                            if (rect.Width <= 0 || rect.Height <= 0) continue;

                            ClickElement(link, win);
                            return true;
                        }

                        // Fallback: search ListItem elements
                        var listItems = win.FindAllDescendants(cf.ByControlType(ControlType.ListItem));
                        foreach (var item in listItems)
                        {
                            var name = item.Name ?? "";
                            var match = exact ? name == text : name.Contains(text);
                            if (!match) continue;

                            var rect = item.BoundingRectangle;
                            if (rect.Width <= 0 || rect.Height <= 0) continue;

                            ClickElement(item, win);
                            return true;
                        }
                    }
                }
                catch { }

                if (!logged)
                {
                    // Try to find any Chrome window and show what's there
                    var allWindows = desktop.FindAllChildren(cf.ByControlType(ControlType.Window));
                    foreach (var w in allWindows)
                    {
                        if (w.ClassName == "Chrome_WidgetWin_1")
                        {
                            Console.WriteLine($"[UIA] Chrome 窗口: {w.Name ?? "(untitled)"}");
                        }
                    }
                    logged = true;
                }

                Thread.Sleep(2000);
            }

            Console.WriteLine($"[UIA] 未找到包含 \"{text}\" 的元素");
            return false;
        });
    }

    /// <summary>检测 ERP 页面是否出现请求处理错误（需重试点击）。</summary>
    public static async Task<bool> HasRequestProcessingErrorAsync(
        ChromeController? chrome = null,
        int? tabId = null,
        int delayBeforeCheckMs = 0,
        CancellationToken ct = default)
    {
        if (delayBeforeCheckMs > 0)
            await Task.Delay(delayBeforeCheckMs, ct);

        if (chrome != null)
        {
            try
            {
                var body = await chrome.GetBodyTextAsync(tabId, ct);
                if (ContainsRequestProcessingError(body))
                    return true;
            }
            catch { }
        }

        return await Task.Run(() =>
        {
            using var automation = new UIA3Automation();
            var cf = automation.ConditionFactory;
            var desktop = automation.GetDesktop();

            foreach (var win in desktop.FindAllChildren(cf.ByControlType(ControlType.Window)))
            {
                if (win.ClassName != "Chrome_WidgetWin_1" || !IsErpWindow(win, cf))
                    continue;

                foreach (var textEl in win.FindAllDescendants(cf.ByControlType(ControlType.Text)))
                {
                    if (ContainsRequestProcessingError(textEl.Name))
                        return true;
                }

                foreach (var doc in win.FindAllDescendants(cf.ByControlType(ControlType.Document)))
                {
                    if (ContainsRequestProcessingError(doc.Name))
                        return true;
                }
            }

            return false;
        }, ct);
    }

    private static bool ContainsRequestProcessingError(string? text) =>
        !string.IsNullOrEmpty(text)
        && (text.Contains(RequestProcessingError, StringComparison.OrdinalIgnoreCase)
            || text.Contains("Unexpected error while processing", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 判断 Chrome 窗口是否包含 Oracle ERP 内容。
    /// 通过检查窗口标题或子元素中是否包含 ERP 相关文本来判断。
    /// </summary>
    private static bool IsErpWindow(AutomationElement win, ConditionFactory cf)
    {
        var winName = win.Name ?? "";

        // Check window title for ERP indicators
        if (winName.Contains("ERP") || winName.Contains("E-Business") ||
            winName.Contains("Oracle") || winName.Contains("项目投资"))
        {
            return true;
        }

        // Check for ERP-specific hyperlinks (e.g., "展开", "收起", "CUX:")
        // These only appear in the Oracle ERP tree menu
        try
        {
            var links = win.FindAllDescendants(cf.ByControlType(ControlType.Hyperlink));
            foreach (var link in links)
            {
                var name = link.Name ?? "";
                if (name.Contains("CUX:") || name.Contains("展开 303") || name.Contains("收起 303") ||
                    name.Contains("项目查询岗"))
                {
                    return true;
                }
            }
        }
        catch { }

        return false;
    }
}
