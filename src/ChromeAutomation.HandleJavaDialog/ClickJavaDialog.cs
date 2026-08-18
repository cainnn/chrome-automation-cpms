using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;

namespace HandleJavaDialog;

/// <summary>
/// Handles Java security dialog by:
/// 1. Moving the dialog to the primary monitor (multi-monitor aware)
/// 2. Clicking checkbox + Run button with real mouse input
/// </summary>
static class ClickJavaDialog
{
    public static void Run()
    {
        Console.WriteLine("=== Click Java Security Dialog v2 (multi-monitor aware) ===");

        var hWnd = FindDialog();
        if (hWnd == IntPtr.Zero)
        {
            Console.WriteLine("No Java security dialog found.");
            return;
        }

        Console.WriteLine($"Dialog handle: {hWnd}");

        GetWindowRect(hWnd, out var origRect);
        int w = origRect.Right - origRect.Left;
        int h = origRect.Bottom - origRect.Top;
        Console.WriteLine($"Original pos: ({origRect.Left},{origRect.Top})-({origRect.Right},{origRect.Bottom}) size={w}x{h}");

        // Step 1: Move dialog to center of primary monitor
        int newX = Math.Max((1920 - w) / 2, 50);
        int newY = Math.Max((1080 - h) / 2, 50);
        Console.WriteLine($"Moving to primary monitor: ({newX},{newY})");
        SetWindowPos(hWnd, HWND_TOPMOST, newX, newY, w, h, SWP_SHOWWINDOW);
        Thread.Sleep(800);

        // Verify new position
        GetWindowRect(hWnd, out var newRect);
        Console.WriteLine($"New pos: ({newRect.Left},{newRect.Top})-({newRect.Right},{newRect.Bottom})");
        w = newRect.Right - newRect.Left;
        h = newRect.Bottom - newRect.Top;

        // Step 2: Force foreground
        ForceForeground(hWnd);
        Thread.Sleep(1500);

        // Step 3: Take screenshot to see where the controls actually are
        Console.WriteLine("\n=== Taking screenshot for analysis ===");
        var screenshotPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "java_dialog_screenshot.png");
        TakeScreenshot(screenshotPath);
        Console.WriteLine($"Screenshot saved: {screenshotPath}");

        // Step 4: Click the checkbox and Run button
        // The dialog has a standard Java security layout:
        //   - Warning icon + text at top
        //   - Application info in the middle
        //   - Checkbox "我接受风险并希望运行此应用程序" near bottom
        //   - "运行" and "取消" buttons at bottom

        Console.WriteLine("\n=== Clicking checkbox ===");

        // Try multiple checkbox positions (relative to dialog)
        var cbPositions = new (int x, int y, string label)[]
        {
            (25, (int)(h * 0.78), "78%"),
            (30, (int)(h * 0.75), "75%"),
            (35, (int)(h * 0.80), "80%"),
            (20, (int)(h * 0.73), "73%"),
            (40, (int)(h * 0.77), "77%"),
        };

        foreach (var (cbX, cbY, label) in cbPositions)
        {
            int screenX = newRect.Left + cbX;
            int screenY = newRect.Top + cbY;
            Console.WriteLine($"  Checkbox attempt at relative ({cbX},{cbY}) [{label}] -> screen ({screenX},{screenY})");
            RealClick(screenX, screenY);
            Thread.Sleep(400);

            // After clicking checkbox, try clicking Run button
            var btnPositions = new (int bx, int by, string bLabel)[]
            {
                ((int)(w * 0.82), (int)(h * 0.91), "82%x91%"),
                ((int)(w * 0.75), (int)(h * 0.88), "75%x88%"),
                ((int)(w * 0.85), (int)(h * 0.93), "85%x93%"),
                ((int)(w * 0.70), (int)(h * 0.90), "70%x90%"),
            };

            foreach (var (bx, by, bLabel) in btnPositions)
            {
                int bsx = newRect.Left + bx;
                int bsy = newRect.Top + by;
                Console.WriteLine($"    Run button attempt at ({bx},{by}) [{bLabel}] -> screen ({bsx},{bsy})");
                RealClick(bsx, bsy);
                Thread.Sleep(600);

                if (FindDialog() == IntPtr.Zero)
                {
                    Console.WriteLine("✓ Dialog dismissed!");
                    return;
                }
            }

            // Uncheck if checkbox was checked at wrong position - try clicking again
            RealClick(screenX, screenY);
            Thread.Sleep(200);
        }

        // Step 5: Screenshot after attempts for debugging
        var afterPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "java_dialog_after.png");
        TakeScreenshot(afterPath);
        Console.WriteLine($"\nAfter screenshot saved: {afterPath}");

        // Step 6: Last resort - grid scan the entire dialog
        Console.WriteLine("\n=== Grid scan: clicking every 40px across the dialog ===");
        for (int gy = (int)(h * 0.70); gy < h - 20; gy += 40)
        {
            for (int gx = 15; gx < w - 15; gx += 40)
            {
                int sx = newRect.Left + gx;
                int sy = newRect.Top + gy;
                Console.Write($"  ({gx},{gy})");
                RealClick(sx, sy);
                Thread.Sleep(100);

                if (FindDialog() == IntPtr.Zero)
                {
                    Console.WriteLine("\n✓ Dialog dismissed by grid scan!");
                    return;
                }
            }
            Console.WriteLine();
        }

        Console.WriteLine("✗ Could not dismiss dialog.");
    }

    static void RealClick(int screenX, int screenY)
    {
        SetCursorPos(screenX, screenY);
        Thread.Sleep(150);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(30);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }

    static void TakeScreenshot(string path)
    {
        using var bmp = new System.Drawing.Bitmap(
            System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width,
            System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.CopyFromScreen(0, 0, 0, 0, bmp.Size);
        bmp.Save(path);
    }

    static IntPtr FindDialog()
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

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern int GetWindowText(IntPtr hWnd, StringBuilder lpWindowText, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")]
    static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")]
    static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    const uint MOUSEEVENTF_LEFTUP = 0x0004;
    const int SW_RESTORE = 9;
    const uint SWP_SHOWWINDOW = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }
}
