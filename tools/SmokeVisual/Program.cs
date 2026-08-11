using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

internal static class SmokeVisual
{
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private struct RECT { public int Left, Top, Right, Bottom; }

    private static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "shots");
        Directory.CreateDirectory(outDir);

        foreach (var p in Process.GetProcessesByName("AetherPC"))
        {
            try { p.Kill(true); } catch { /* ignore */ }
        }
        Thread.Sleep(800);

        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string defaultExe = Path.Combine(repoRoot, "dist", "Payload", "AetherPC.exe");
        if (!File.Exists(defaultExe))
            defaultExe = Path.Combine(repoRoot, "dist", "Normal", "AetherPC.exe");
        string exe = args.Length > 1 ? args[1] : defaultExe;
        var proc = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        if (proc is null) { Console.WriteLine("FAIL start"); return 2; }

        IntPtr hwnd = IntPtr.Zero;
        for (int i = 0; i < 60 && hwnd == IntPtr.Zero; i++)
        {
            Thread.Sleep(500);
            hwnd = FindMainWindow((uint)proc.Id);
        }
        if (hwnd == IntPtr.Zero) { Console.WriteLine("FAIL no window"); return 3; }
        Console.WriteLine($"HWND={hwnd.ToInt64()} PID={proc.Id}");

        // Close onboarding if present (second window)
        Thread.Sleep(500);
        TryDismissOnboarding((uint)proc.Id);
        Thread.Sleep(400);
        hwnd = FindMainWindow((uint)proc.Id);
        if (hwnd == IntPtr.Zero) { Console.WriteLine("FAIL main after onboard"); return 4; }

        ShowWindow(hwnd, 9);
        SetForegroundWindow(hwnd);
        Thread.Sleep(300);

        var sizes = new (string Name, int W, int H)[]
        {
            ("1024x720", 1024, 720),
            ("1280x720", 1280, 720),
            ("1366x768", 1366, 768),
            ("900x560", 900, 560),
        };

        string[] pages =
        {
            "Inicio", "Hardware", "Monitor", "Procesos", "Servicios", "Optimizar",
            "Modo Bestia", "Limpieza", "Gaming", "Diagnóstico", "Seguridad",
            "Drivers", "Historial", "Configuración"
        };

        // Also English aliases if UI already EN
        string[] pagesEn =
        {
            "Home", "Hardware", "Monitor", "Processes", "Services", "Optimize",
            "Beast Mode", "Cleanup", "Gaming", "Diagnostics", "Security",
            "Drivers", "History", "Settings"
        };

        foreach (var (name, w, h) in sizes)
        {
            ShowWindow(hwnd, 1); // SW_SHOWNORMAL
            Thread.Sleep(150);
            bool moved = MoveWindow(hwnd, 20, 20, w, h, true);
            if (!moved)
                moved = SetWindowPos(hwnd, IntPtr.Zero, 20, 20, w, h, 0x0040);
            Thread.Sleep(700);
            GetWindowRect(hwnd, out var rr);
            Console.WriteLine($"size {name} move={moved} actual={(rr.Right - rr.Left)}x{(rr.Bottom - rr.Top)}");
            Capture(hwnd, Path.Combine(outDir, $"Inicio-{name}.png"));
        }

        // Maximize home
        ShowWindow(hwnd, 3);
        Thread.Sleep(800);
        Capture(hwnd, Path.Combine(outDir, "Inicio-maximized.png"));
        ShowWindow(hwnd, 9);
        MoveWindow(hwnd, 20, 20, 1280, 800, true);
        Thread.Sleep(500);

        for (int i = 0; i < pages.Length; i++)
        {
            bool clicked = ClickNav(hwnd, pages[i]) || ClickNav(hwnd, pagesEn[i]);
            Console.WriteLine($"nav {pages[i]} clicked={clicked}");
            Thread.Sleep(900);
            Capture(hwnd, Path.Combine(outDir, $"page-{Sanitize(pages[i])}-1280.png"));
        }

        // Small window on Monitor + Optimize + Processes
        string[] critical = { "Monitor", "Optimizar", "Optimize", "Procesos", "Processes", "Gaming", "Configuración", "Settings" };
        MoveWindow(hwnd, 20, 20, 1024, 720, true);
        Thread.Sleep(400);
        foreach (var p in critical)
        {
            if (!ClickNav(hwnd, p)) continue;
            Thread.Sleep(800);
            Capture(hwnd, Path.Combine(outDir, $"page-{Sanitize(p)}-1024.png"));
        }

        Console.WriteLine("SMOKE_OK");
        return 0;
    }

    private static string Sanitize(string s) => s.Replace(' ', '_');

    private static IntPtr FindMainWindow(uint pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out uint p);
            if (p != pid) return true;
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, 256);
            if (sb.Length == 0) return true;
            // Prefer main title AetherPC without dialog quirks
            if (sb.ToString().Contains("AetherPC", StringComparison.OrdinalIgnoreCase))
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static void TryDismissOnboarding(uint pid)
    {
        // Click Empezar / Start if onboarding is showing
        AutomationElement root = AutomationElement.RootElement;
        var cond = new PropertyCondition(AutomationElement.ProcessIdProperty, (int)pid);
        foreach (AutomationElement win in root.FindAll(TreeScope.Children, cond))
        {
            try
            {
                var name = win.Current.Name ?? "";
                Console.WriteLine("window: " + name);
                var btn = win.FindFirst(TreeScope.Descendants,
                    new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                        new PropertyCondition(AutomationElement.NameProperty, "Empezar con AetherPC")));
                btn ??= win.FindFirst(TreeScope.Descendants,
                    new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                        new PropertyCondition(AutomationElement.NameProperty, "Start with AetherPC")));
                if (btn?.GetCurrentPattern(InvokePattern.Pattern) is InvokePattern inv)
                {
                    inv.Invoke();
                    Thread.Sleep(1000);
                }
            }
            catch (Exception ex) { Console.WriteLine("onboard: " + ex.Message); }
        }
    }

    private static bool ClickNav(IntPtr hwnd, string label)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null) return false;
            SetForegroundWindow(hwnd);
            Thread.Sleep(150);

            var all = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement el in all)
            {
                string n = "";
                try { n = el.Current.Name ?? ""; } catch { continue; }
                if (string.IsNullOrWhiteSpace(n)) continue;
                if (!n.Equals(label, StringComparison.OrdinalIgnoreCase) &&
                    !n.Contains(label, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    if (el.TryGetCurrentPattern(InvokePattern.Pattern, out object? pat) && pat is InvokePattern inv)
                    {
                        inv.Invoke();
                        return true;
                    }
                }
                catch { /* try click point */ }

                try
                {
                    var pt = el.GetClickablePoint();
                    ClickScreen((int)pt.X, (int)pt.Y);
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("clickpoint " + n + ": " + ex.Message);
                }
            }

            // Debug dump once
            Console.WriteLine("buttons available:");
            int shown = 0;
            foreach (AutomationElement el in all)
            {
                try
                {
                    var n = el.Current.Name ?? "";
                    if (string.IsNullOrWhiteSpace(n)) continue;
                    Console.WriteLine("  - [" + n + "]");
                    if (++shown > 40) break;
                }
                catch { /* */ }
            }
        }
        catch (Exception ex) { Console.WriteLine("ClickNav " + label + ": " + ex.Message); }
        return false;
    }

    [DllImport("user32.dll")] private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int X, int Y);
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    private static void ClickScreen(int x, int y)
    {
        SetCursorPos(x, y);
        Thread.Sleep(40);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(30);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }

    private static void Capture(IntPtr hwnd, string path)
    {
        SetForegroundWindow(hwnd);
        Thread.Sleep(200);
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9);
        GetWindowRect(hwnd, out var r);
        int w = Math.Max(1, r.Right - r.Left);
        int h = Math.Max(1, r.Bottom - r.Top);
        if (w < 100 || h < 100) { Console.WriteLine("skip tiny " + path); return; }
        using var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, h));
        bmp.Save(path, ImageFormat.Png);
        Console.WriteLine($"saved {Path.GetFileName(path)} {w}x{h}");
    }
}
