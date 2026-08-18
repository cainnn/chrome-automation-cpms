using System.Runtime.InteropServices;
using System.Text;
using ChromeAutomation.Client;

namespace ChromeAutomation.CpmsExport;

/// <summary>
/// Handles Java Web Start security dialogs (SunAwtDialog).
/// Strategy: move dialog to primary monitor (dual-monitor safe) + real mouse click.
/// Falls back to Alt+I / Alt+R keyboard accelerators via FlaUI.
/// </summary>
public static class JavaDialogHelper
{
    /// <summary>
    /// Waits for and dismisses a Java security dialog.
    /// Strategy 0 (JAB): Use Java Access Bridge to directly interact with the dialog's
    /// accessibility tree — find checkbox and button by role/name, no coordinate estimation.
    /// Strategy 1: Move dialog to primary monitor + real mouse click.
    /// Strategy 2: FlaUI Alt+I / Alt+R keyboard accelerators.
    /// </summary>
    public static async Task<bool> HandleSecurityDialogAsync(int timeoutMs = 90000)
    {
        return await Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            var handled = false;

            // Connect to JAB helper once at the start
            JabClient? jab = null;
            try
            {
                jab = new JabClient();
                await jab.ConnectAsync();
                Console.WriteLine("[JavaDialog] JAB helper 已连接");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JavaDialog] JAB helper 连接失败（将回退到坐标/快捷键策略）: {ex.Message}");
                jab?.Dispose();
                jab = null;
            }

            try
            {
                while (DateTime.UtcNow < deadline)
                {
                    // Close "详细信息" sub-dialogs first
                    CloseDetailsDialogs();

                    var hWnd = FindSecurityDialog();
                    if (hWnd == IntPtr.Zero)
                    {
                        Thread.Sleep(2000);
                        continue;
                    }

                    GetWindowRect(hWnd, out var rect);
                    Console.WriteLine($"[JavaDialog] 安全弹窗 handle={hWnd} pos=({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom})");

                    // Strategy 0: JAB — uses Java accessibility tree (most reliable)
                    if (jab != null)
                    {
                        Console.WriteLine("[JavaDialog] 策略0: JAB 无障碍树交互...");
                        handled = await JabHandleDialogAsync(jab, hWnd);
                        if (handled)
                        {
                            Console.WriteLine("[JavaDialog] ✓ JAB 策略成功");
                            Thread.Sleep(1500);

                            // Check for a second security dialog (Java sometimes shows two)
                            var secondDialog = FindSecurityDialog();
                            if (secondDialog != IntPtr.Zero)
                            {
                                Console.WriteLine("[JavaDialog] 检测到第二个安全弹窗，继续处理...");
                                await JabHandleDialogAsync(jab, secondDialog);
                            }
                            return true;
                        }
                        Console.WriteLine("[JavaDialog] JAB 策略未成功，回退到其他策略...");
                    }

                    // Strategy 1: Move to primary monitor + real mouse click
                    handled = ClickAfterMove(hWnd, rect);

                    if (!handled)
                    {
                        // Strategy 2: FlaUI Alt+I + Alt+R
                        Console.WriteLine("[JavaDialog] 坐标点击失败，尝试 FlaUI Alt+I/Alt+R...");
                        handled = FlaUIAltClick(hWnd);
                    }

                    if (handled)
                    {
                        Console.WriteLine("[JavaDialog] ✓ 安全弹窗已处理");
                        Thread.Sleep(1500);

                        // Check for a second security dialog (Java sometimes shows two)
                        var secondDialog = FindSecurityDialog();
                        if (secondDialog != IntPtr.Zero)
                        {
                            Console.WriteLine("[JavaDialog] 检测到第二个安全弹窗，继续处理...");
                            GetWindowRect(secondDialog, out var rect2);
                            ClickAfterMove(secondDialog, rect2);
                        }
                        return true;
                    }

                    Thread.Sleep(2000);
                }
            }
            finally
            {
                jab?.Dispose();
            }

            Console.WriteLine("[JavaDialog] 未检测到 Java 安全弹窗");
            return false;
        });
    }

    /// <summary>
    /// Strategy 0: Use Java Access Bridge to directly interact with the security dialog.
    /// Walks the Java accessibility tree to find the "I accept" checkbox and "Run" button.
    /// Uses DoActionAsync (vmId+ac handle) for reliable clicking — avoids re-searching.
    /// </summary>
    static async Task<bool> JabHandleDialogAsync(JabClient jab, IntPtr hWnd)
    {
        try
        {
            var hwndLong = hWnd.ToInt64();

            // Step 1: Find and check the "I accept" checkbox
            var checkbox = await jab.FindNodeAsync(hwndLong, nameContains: "接受");
            if (checkbox == null)
                checkbox = await jab.FindNodeAsync(hwndLong, nameContains: "accept");

            if (checkbox != null)
            {
                Console.WriteLine($"[JavaDialog-JAB] 复选框: name='{checkbox.name}' vmId={checkbox.vmId} ac={checkbox.ac} states={checkbox.states}");

                if (!checkbox.states.Contains("checked"))
                {
                    var ok = await jab.DoActionAsync(checkbox.vmId, checkbox.ac);
                    Console.WriteLine($"[JavaDialog-JAB] 勾选复选框(DoAction): {(ok ? "成功" : "失败")}");

                    if (!ok)
                    {
                        Console.WriteLine("[JavaDialog-JAB] 回退到 ClickNode...");
                        ok = await jab.ClickNodeAsync(hwndLong, nameContains: checkbox.name);
                        Console.WriteLine($"[JavaDialog-JAB] 勾选复选框(ClickNode): {(ok ? "成功" : "失败")}");
                    }
                }
                else
                {
                    Console.WriteLine("[JavaDialog-JAB] 复选框已勾选，跳过");
                }
                Thread.Sleep(800);
            }
            else
            {
                Console.WriteLine("[JavaDialog-JAB] 未找到复选框，继续查找运行按钮...");
            }

            // Step 2: Find and click the "Run" button
            var runBtn = await jab.FindNodeAsync(hwndLong, role: "push button", nameContains: "运行");
            if (runBtn == null)
                runBtn = await jab.FindNodeAsync(hwndLong, role: "push button", nameContains: "Run");
            if (runBtn == null)
                runBtn = await jab.FindNodeAsync(hwndLong, nameContains: "运行");
            if (runBtn == null)
                runBtn = await jab.FindNodeAsync(hwndLong, nameContains: "Run");

            if (runBtn == null)
            {
                Console.WriteLine("[JavaDialog-JAB] 未找到运行按钮");
                return false;
            }

            Console.WriteLine($"[JavaDialog-JAB] 运行按钮: name='{runBtn.name}' vmId={runBtn.vmId} ac={runBtn.ac} states={runBtn.states}");

            var clicked = await jab.DoActionAsync(runBtn.vmId, runBtn.ac);
            Console.WriteLine($"[JavaDialog-JAB] 点击运行(DoAction): {(clicked ? "成功" : "失败")}");

            if (!clicked)
            {
                Console.WriteLine("[JavaDialog-JAB] 回退到 ClickNode...");
                clicked = await jab.ClickNodeAsync(hwndLong, nameContains: runBtn.name);
                Console.WriteLine($"[JavaDialog-JAB] 点击运行(ClickNode): {(clicked ? "成功" : "失败")}");
            }

            Thread.Sleep(1500);

            // Verify the dialog is gone
            var gone = FindSecurityDialog() == IntPtr.Zero;
            Console.WriteLine($"[JavaDialog-JAB] 弹窗已消失: {gone}");
            return clicked || gone;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[JavaDialog-JAB] 异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Strategy 1: Move dialog to primary monitor center, then click checkbox + Run button.
    /// This handles the dual-monitor issue where dialog coordinates are off-screen.
    /// </summary>
    static bool ClickAfterMove(IntPtr hWnd, RECT origRect)
    {
        int w = origRect.Right - origRect.Left;
        int h = origRect.Bottom - origRect.Top;

        // Move to center of primary monitor
        int newX = Math.Max((1920 - w) / 2, 50);
        int newY = Math.Max((1080 - h) / 2, 50);
        SetWindowPos(hWnd, HWND_TOPMOST, newX, newY, w, h, SWP_SHOWWINDOW);
        Thread.Sleep(800);

        // Verify new position
        GetWindowRect(hWnd, out var newRect);
        Console.WriteLine($"[JavaDialog] 移动到 ({newRect.Left},{newRect.Top})-({newRect.Right},{newRect.Bottom})");
        w = newRect.Right - newRect.Left;
        h = newRect.Bottom - newRect.Top;

        // Force foreground
        ForceForeground(hWnd);
        Thread.Sleep(1000);

        // Click checkbox + Run button at estimated positions
        // Standard Java security dialog layout (570x359):
        //   Checkbox at ~x:25, y:78% from top
        //   Run button at ~x:82%, y:91% from top
        var attempts = new (int cbX, int cbY, int btnX, int btnY)[]
        {
            (25, (int)(h * 0.78), (int)(w * 0.82), (int)(h * 0.91)),
            (30, (int)(h * 0.75), (int)(w * 0.78), (int)(h * 0.88)),
            (35, (int)(h * 0.80), (int)(w * 0.85), (int)(h * 0.93)),
        };

        foreach (var (cbX, cbY, btnX, btnY) in attempts)
        {
            // Click checkbox
            int cbScreenX = newRect.Left + cbX;
            int cbScreenY = newRect.Top + cbY;
            RealClick(cbScreenX, cbScreenY);
            Thread.Sleep(600);

            // Click Run button
            int btnScreenX = newRect.Left + btnX;
            int btnScreenY = newRect.Top + btnY;
            RealClick(btnScreenX, btnScreenY);
            Thread.Sleep(800);

            if (FindSecurityDialog() == IntPtr.Zero)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Strategy 2: Use FlaUI to send Alt+I (checkbox) + Alt+R (Run) keyboard accelerators.
    /// </summary>
    static bool FlaUIAltClick(IntPtr targetHWnd)
    {
        try
        {
            using var automation = new FlaUI.UIA3.UIA3Automation();
            var cf = automation.ConditionFactory;
            var desktop = automation.GetDesktop();
            var allWindows = desktop.FindAllChildren(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Window));

            foreach (var win in allWindows)
            {
                if (win.ClassName != "SunAwtDialog") continue;
                var winName = win.Name ?? "";
                if (winName.Contains("启动") || winName.Contains("Starting") || winName.Contains("详细") || winName.Contains("加载"))
                    continue;

                Console.WriteLine($"[JavaDialog] FlaUI: 找到弹窗 '{winName}'");

                if (win is FlaUI.Core.AutomationElements.Window w) w.Focus();
                Thread.Sleep(800);

                // Alt+I (checkbox "我接受风险(I)")
                FlaUI.Core.Input.Keyboard.Pressing(FlaUI.Core.WindowsAPI.VirtualKeyShort.LMENU);
                Thread.Sleep(100);
                FlaUI.Core.Input.Keyboard.Pressing(FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_I);
                Thread.Sleep(100);
                FlaUI.Core.Input.Keyboard.Release(FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_I);
                Thread.Sleep(100);
                FlaUI.Core.Input.Keyboard.Release(FlaUI.Core.WindowsAPI.VirtualKeyShort.LMENU);
                Thread.Sleep(800);

                // Alt+R (Run "运行(R)")
                FlaUI.Core.Input.Keyboard.Pressing(FlaUI.Core.WindowsAPI.VirtualKeyShort.LMENU);
                Thread.Sleep(100);
                FlaUI.Core.Input.Keyboard.Pressing(FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_R);
                Thread.Sleep(100);
                FlaUI.Core.Input.Keyboard.Release(FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_R);
                Thread.Sleep(100);
                FlaUI.Core.Input.Keyboard.Release(FlaUI.Core.WindowsAPI.VirtualKeyShort.LMENU);
                Thread.Sleep(2000);

                return FindSecurityDialog() == IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[JavaDialog] FlaUI 失败: {ex.Message}");
        }
        return false;
    }

    /// <summary>Close "详细信息" sub-dialogs that block the security dialog.</summary>
    static void CloseDetailsDialogs()
    {
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var cn = new StringBuilder(256);
            var name = new StringBuilder(256);
            GetClassName(h, cn, 256);
            GetWindowText(h, name, 256);
            if (cn.ToString() == "SunAwtDialog" && name.ToString().Contains("详细"))
            {
                PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                Console.WriteLine("[JavaDialog] 关闭详细信息子窗口");
            }
            return true;
        }, IntPtr.Zero);
    }

    static IntPtr FindSecurityDialog()
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var cn = new StringBuilder(256);
            var name = new StringBuilder(256);
            GetClassName(h, cn, 256);
            GetWindowText(h, name, 256);
            var n = name.ToString();
            if (cn.ToString() == "SunAwtDialog"
                && !n.Contains("启动") && !n.Contains("Starting")
                && !n.Contains("详细") && !n.Contains("加载"))
            {
                result = h;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    static void RealClick(int screenX, int screenY)
    {
        SetCursorPos(screenX, screenY);
        Thread.Sleep(150);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(30);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }

    static void ForceForeground(IntPtr hWnd)
    {
        var fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var myThread = GetCurrentThreadId();
        if (fgThread != myThread)
            AttachThreadInput(myThread, fgThread, true);
        ShowWindow(hWnd, SW_RESTORE);
        SetForegroundWindow(hWnd);
        Thread.Sleep(500);
        if (fgThread != myThread)
            AttachThreadInput(myThread, fgThread, false);
    }

    // P/Invoke
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern int GetWindowText(IntPtr hWnd, StringBuilder lpWindowText, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] static extern IntPtr PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint WM_CLOSE = 0x0010;
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    const uint MOUSEEVENTF_LEFTUP = 0x0004;
    const int SW_RESTORE = 9;
    const uint SWP_SHOWWINDOW = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }
}
