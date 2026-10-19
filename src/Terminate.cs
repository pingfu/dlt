using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Dlt;

/// <summary>Ends the process without the orderly runtime shutdown, for use from the Ctrl+C handler.</summary>
internal static class Terminate
{
    public static void Now(int exitCode)
    {
        if (OperatingSystem.IsWindows())
            TerminateProcess(GetCurrentProcess(), (uint)exitCode);
        // Not Windows, or TerminateProcess somehow returned: fall back to the runtime's own hard kill.
        Process.GetCurrentProcess().Kill();
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);
}
