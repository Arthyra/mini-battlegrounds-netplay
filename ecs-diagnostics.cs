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
#if ECS_ACCEPTANCE
    private static long captureSceneAt;

    private static void CaptureScene(string phase)
    {
        try
        {
            var text = new System.Text.StringBuilder("scene phase=" + phase);
            text.Append(" name=").Append(UnityEngine.Application.loadedLevelName);
            var camera = UnityEngine.Camera.main;
            if (camera != null)
            {
                text.Append(" camera=").Append(camera.GetInstanceID()).Append(" components=");
                foreach (var component in camera.GetComponents<UnityEngine.Component>())
                    if (component != null) text.Append(component.GetType().FullName).Append(',');
            }
            var renderers = UnityEngine.Object.FindObjectsOfType(typeof(UnityEngine.Renderer));
            text.Append(" renderers=").Append(renderers.Length);
            var indices = new System.Collections.Generic.SortedDictionary<int, int>();
            foreach (UnityEngine.Renderer renderer in renderers)
            {
                int index = renderer.lightmapIndex;
                if (!indices.ContainsKey(index)) indices[index] = 0;
                indices[index]++;
            }
            text.Append(" lightmap_indices=");
            foreach (var pair in indices) text.Append(pair.Key).Append(':').Append(pair.Value).Append(',');
            var maps = UnityEngine.LightmapSettings.lightmaps;
            text.Append(" lightmaps=").Append(maps.Length);
            foreach (var map in maps)
                foreach (var property in map.GetType().GetProperties())
                    if (typeof(UnityEngine.Texture).IsAssignableFrom(property.PropertyType))
                    {
                        var texture = property.GetValue(map, null) as UnityEngine.Texture;
                        text.Append(' ').Append(property.Name).Append('=');
                        if (texture == null) text.Append("null");
                        else text.Append(texture.name).Append('#').Append(texture.GetInstanceID())
                            .Append(':').Append(texture.width).Append('x').Append(texture.height);
                    }
            Write(text.ToString());
        }
        catch (Exception error) { Write("scene diagnostic error=" + error.GetType().Name); }
    }
#endif

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
#if ECS_ACCEPTANCE
        // Join is dispatched on the main thread. Inspect the old scene before
        // stage teardown and the replacement after loading, only in test builds.
        if (measureFrames && tag == "jb" && code == 30)
        {
            CaptureScene("join.before");
            captureSceneAt = clock.ElapsedMilliseconds + 3000;
        }
#endif
    }

    public static void Frame()
    {
        if (!measureFrames) return;
        long now = clock.ElapsedMilliseconds;
#if ECS_ACCEPTANCE
        if (captureSceneAt != 0 && now >= captureSceneAt)
        {
            captureSceneAt = 0;
            CaptureScene("join.after");
        }
#endif
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
