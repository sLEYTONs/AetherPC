using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string root = AppDomain.CurrentDomain.BaseDirectory;
        string target = Path.Combine(root, "app", "AetherPC.exe");

        if (!File.Exists(target))
        {
            MessageBoxW(IntPtr.Zero, "No se encontro app\\AetherPC.exe.\nReinstala AetherPC.", "AetherPC", 0x10);
            return 1;
        }

        ProcessStartInfo psi = new ProcessStartInfo();
        psi.FileName = target;
        psi.WorkingDirectory = Path.GetDirectoryName(target);
        psi.UseShellExecute = false;
        psi.Arguments = QuoteArgs(args);

        using (Process p = Process.Start(psi))
        {
            if (p == null)
                return 1;
            p.WaitForExit();
            return p.ExitCode;
        }
    }

    private static string QuoteArgs(string[] args)
    {
        if (args == null || args.Length == 0)
            return "";
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < args.Length; i++)
        {
            if (i > 0)
                sb.Append(' ');
            string a = args[i] ?? "";
            if (a.IndexOf(' ') >= 0 || a.IndexOf('"') >= 0)
                sb.Append('"').Append(a.Replace("\"", "\\\"")).Append('"');
            else
                sb.Append(a);
        }
        return sb.ToString();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
