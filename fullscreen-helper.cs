using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

internal static class FullscreenHelper
{
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr param);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    private delegate bool EnumProc(IntPtr hWnd, IntPtr param);
    private static uint targetPid;
    private static IntPtr best;

    private static bool Callback(IntPtr hWnd, IntPtr param)
    {
        uint pid;
        GetWindowThreadProcessId(hWnd, out pid);
        if (pid == targetPid && IsWindowVisible(hWnd))
            best = hWnd;
        return true;
    }

    private static void Main(string[] args)
    {
        SetProcessDPIAware();
        if (args.Length < 2 || args[0] != "--pid" || !uint.TryParse(args[1], out targetPid))
            return;
        var screenW = GetSystemMetrics(0);
        var screenH = GetSystemMetrics(1);
        for (var a = 2; a + 1 < args.Length; a++)
        {
            if (args[a] == "--width") int.TryParse(args[a + 1], out screenW);
            if (args[a] == "--height") int.TryParse(args[a + 1], out screenH);
        }
        Process process;
        try { process = Process.GetProcessById((int)targetPid); }
        catch { return; }
        for (var i = 0; i < 48; i++)
        {
            process.Refresh();
            best = process.MainWindowHandle;
            if (best != IntPtr.Zero)
            {
                var style = GetWindowLong(best, -16);
                style &= ~(0x00C00000 | 0x00040000);
                SetWindowLong(best, -16, style);
                SetWindowPos(best, IntPtr.Zero, 0, 0, screenW, screenH, 0x0040 | 0x0004);
            }
            Thread.Sleep(250);
        }
    }
}
