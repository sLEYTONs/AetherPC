using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace AetherPC.Core.Windows;

/// <summary>
/// Utilidades de proceso/privilegios de Windows compartidas entre Infrastructure y Optimization
/// (ambos proyectos referencian Core, ninguno referencia al otro). Todo aquí es best-effort:
/// si el usuario no es admin o el privilegio no existe en el token, falla en silencio y el
/// llamador debe seguir con el flujo normal (Access Denied real, no una excepción inesperada).
/// </summary>
[SupportedOSPlatform("windows")]
public static class ProcessPrivileges
{
    private static readonly object Lock = new();
    private static bool _attempted;
    private static bool _lastResult;

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Habilita SeDebugPrivilege / SeTakeOwnershipPrivilege / SeIncreaseBasePriorityPrivilege
    /// en el token del proceso actual (si están disponibles). Por defecto cachea el resultado;
    /// pasa force:true al inicio de un plan Apply para reintentar si hace falta.
    /// </summary>
    public static bool EnableDebugAndPriorityPrivileges(bool force = false)
    {
        lock (Lock)
        {
            if (_attempted && !force) return _lastResult;
            _attempted = true;
            _lastResult = TryEnableAll();
            return _lastResult;
        }
    }

    /// <summary>TerminateProcess vía OpenProcess(PROCESS_TERMINATE) — alternativa a Process.Kill.</summary>
    public static bool TryTerminateNative(int pid)
    {
        var handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(ProcessTerminate | ProcessQueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero) return false;
            return TerminateProcess(handle, 1);
        }
        catch { return false; }
        finally { if (handle != IntPtr.Zero) CloseHandle(handle); }
    }

    /// <summary>
    /// Comprueba el flag nativo ProcessBreakOnTermination (el mismo que usa Task Manager para
    /// advertir "Terminar este proceso puede hacer que el sistema sea inestable"). Señal autoritativa
    /// de proceso crítico del SO, independiente del nombre/ruta.
    /// </summary>
    public static bool IsBreakOnTerminationProcess(int pid)
    {
        var handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero) return false;
            var value = 0;
            var status = NtQueryInformationProcess(handle, ProcessBreakOnTerminationClass, ref value, sizeof(int), out _);
            return status == 0 && value != 0;
        }
        catch { return false; }
        finally { if (handle != IntPtr.Zero) CloseHandle(handle); }
    }

    /// <summary>Establece la clase de prioridad vía OpenProcess(PROCESS_SET_INFORMATION) — a veces
    /// funciona donde Process.PriorityClass (que pide más acceso) falla por Access Denied.</summary>
    public static bool TrySetPriorityNative(int pid, int priorityClass)
    {
        var handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(ProcessSetInformation | ProcessQueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero) return false;
            return SetPriorityClass(handle, priorityClass);
        }
        catch { return false; }
        finally { if (handle != IntPtr.Zero) CloseHandle(handle); }
    }

    /// <summary>Abre un handle con únicamente PROCESS_SUSPEND_RESUME (menos privilegio que Process.Handle).</summary>
    public static IntPtr OpenSuspendResumeHandle(int pid)
    {
        try { return OpenProcess(ProcessSuspendResume, false, pid); }
        catch { return IntPtr.Zero; }
    }

    public static void CloseNativeHandle(IntPtr handle)
    {
        if (handle != IntPtr.Zero) { try { CloseHandle(handle); } catch { /* */ } }
    }

    private static bool TryEnableAll()
    {
        var token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out token))
                return false;

            var names = new[]
            {
                "SeDebugPrivilege",
                "SeTakeOwnershipPrivilege",
                "SeIncreaseBasePriorityPrivilege",
                "SeRestorePrivilege",
                "SeBackupPrivilege"
            };
            var any = false;
            foreach (var name in names)
                any |= EnablePrivilege(token, name);
            return any;
        }
        catch { return false; }
        finally { if (token != IntPtr.Zero) CloseHandle(token); }
    }

    private static bool EnablePrivilege(IntPtr token, string privilegeName)
    {
        try
        {
            if (!LookupPrivilegeValue(null, privilegeName, out var luid)) return false;
            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled
            };
            SetLastError(0);
            var ok = AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            return ok && Marshal.GetLastWin32Error() == 0;
        }
        catch { return false; }
    }

    private const int ProcessTerminate = 0x0001;
    private const int ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessSetInformation = 0x0200;
    private const int ProcessSuspendResume = 0x0800;
    private const int ProcessBreakOnTerminationClass = 0x1D; // ProcessBreakOnTermination
    private const int TokenAdjustPrivileges = 0x0020;
    private const int TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPriorityClass(IntPtr hProcess, int dwPriorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void SetLastError(uint dwErrCode);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, int desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle, bool disableAllPrivileges, ref TOKEN_PRIVILEGES newState,
        int bufferLengthInBytes, IntPtr previousState, IntPtr returnLengthInBytes);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle, int processInformationClass, ref int processInformation,
        int processInformationLength, out int returnLength);
}
