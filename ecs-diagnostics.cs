using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

// Acceptance logging is opt-in. Only Connect/Frame timing is recorded.
public static class EcsDiagnostics
{
    private static readonly object gate = new object();
    private static readonly string log = Argument("-ecs-diagnostics=") ?? "mbg-network.log";
    private static readonly bool measureFrames = Argument("-ecs-diagnostics=") != null;
    private static readonly Stopwatch clock = Stopwatch.StartNew();
    private static long lastFrame;
    private static long lastReport;
    private static long maxFrame;
    private static int frames;
    private static int slowFrames;

    private static string Argument(string prefix)
    {
        foreach (string arg in Environment.GetCommandLineArgs())
            if (arg.StartsWith(prefix, StringComparison.Ordinal)) return arg.Substring(prefix.Length);
        return null;
    }

    public static int LoginPort(int fallback)
    {
#if ECS_ACCEPTANCE
        int port;
        if (int.TryParse(Argument("-ecs-test-login-port="), out port) && port >= 1024 && port <= 65535)
            return port;
#endif
        return fallback;
    }

    private static void Write(string value)
    {
        if (string.IsNullOrEmpty(log)) return;
        try
        {
            Monitor.Enter(gate);
            try
            {
                if (File.Exists(log) && new FileInfo(log).Length > 1024 * 1024)
                {
                    if (File.Exists(log + ".previous")) File.Delete(log + ".previous");
                    File.Move(log, log + ".previous");
                }
                File.AppendAllText(log, DateTime.UtcNow.ToString("o") + " thread=" +
                    Thread.CurrentThread.ManagedThreadId + " " + value + Environment.NewLine);
            }
            finally { Monitor.Exit(gate); }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void Trace(string tag, int code)
    {
        if (tag == "rx" && code != 1 && (code < 34 || code > 40)) return;
        Write("net tag=" + tag + " event=" + code);
    }

    public static void Frame()
    {
        if (!measureFrames) return;
        long now = clock.ElapsedMilliseconds;
        if (lastFrame != 0)
        {
            long delta = now - lastFrame;
            maxFrame = Math.Max(maxFrame, delta);
            if (delta >= 100) slowFrames++;
            frames++;
        }
        lastFrame = now;
        if (now - lastReport < 1000) return;
        Write("frames count=" + frames + " max_ms=" + maxFrame + " over100ms=" + slowFrames);
        lastReport = now;
        maxFrame = frames = slowFrames = 0;
    }
}
