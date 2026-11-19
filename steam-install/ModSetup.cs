// Mini Battlegrounds LAN - one-shot installer for players who own a
// distribution of the game that CONTAINS map 102 data (channel builds
// such as the 4399 release). Converts the player's own game copy into a
// LAN-mod install without modifying the original files and without
// bundling any original game asset.
//
// .NET Framework 4 console tool - no Python, no compiler, no Mono.Cecil
// on the player machine: the patch tool itself ships in payload\tools.
//
// Usage:
//   ModSetup.exe                     locate the game automatically, fall
//                                    back to a prompt if not found
//   ModSetup.exe --game <dir>        use an explicit game directory
//   ModSetup.exe --out <dir>         install root (default .\MiniBattlegrounds-LAN)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

internal static class ModSetup
{
    const uint SteamAppId = 658470;          // "Mini Battlegrounds"
    const string WorldScene = "cs_scene_102";
    static int failures;

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string game = null, installRoot = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--game" && i + 1 < args.Length) game = args[++i];
            else if (args[i] == "--out" && i + 1 < args.Length) installRoot = args[++i];
        }

        Console.WriteLine("Mini Battlegrounds LAN - Steam/standalone game converter");
        Console.WriteLine("======================================================");

        var payload = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "payload"));
        if (!Directory.Exists(payload))
        {
            Fail("payload directory missing next to ModSetup.exe: " + payload);
            return 2;
        }

        if (string.IsNullOrEmpty(game))
        {
            game = LocateSteamGame();
            if (game == null)
            {
                Console.Write("未自动找到游戏目录，请输入原版游戏目录（包含 CrazyShooter.exe）: ");
                game = Console.ReadLine();
            }
        }
        if (string.IsNullOrWhiteSpace(game) || !Directory.Exists(game))
        {
            Fail("游戏目录不存在: " + game);
            return 2;
        }
        game = Path.GetFullPath(game);
        Console.WriteLine("游戏目录: " + game);

        // ---- compatibility gate ------------------------------------------
        // Map 102 needs: cs_scene_102 scene data, the 127.0.0.1 login host
        // literal and the 43120 port constant. Channel builds that predate
        // the map (Steam build 658470 included) fail here by design - they
        // cannot load map 102 no matter how the binary is patched.
        if (!CheckCompatibility(game))
            return 3;

        if (string.IsNullOrEmpty(installRoot))
            installRoot = Path.GetFullPath("MiniBattlegrounds-LAN");
        Console.WriteLine("安装目录: " + installRoot);

        // ---- copy the player's own game -----------------------------------
        var clientRoot = Path.Combine(installRoot, "client");
        Console.WriteLine("复制原版游戏（不修改 Steam 目录）...");
        CopyDirectory(game, clientRoot, new[] { "output_log.txt" });

        // ---- patch the client assembly -------------------------------------
        var managed = Path.Combine(clientRoot, "CrazyShooter_Data", "Managed");
        var patchTool = Path.Combine(payload, "tools", "PatchLanCombat.exe");
        var cecil = Path.Combine(payload, "tools", "Mono.Cecil.dll");
        var roomUi = Path.Combine(payload, "LanRoomUI.dll");
        var original = Path.Combine(managed, "Assembly-CSharp.dll");
        if (!File.Exists(patchTool) || !File.Exists(cecil) || !File.Exists(roomUi))
        {
            Fail("payload 缺少补丁工具（tools\\PatchLanCombat.exe / Mono.Cecil.dll / LanRoomUI.dll）");
            return 2;
        }
        File.Copy(original, original + ".original", true);   // keep a pristine copy
        Console.WriteLine("应用联机补丁...");
        // Cecil resolves UnityEngine from the patched dll's directory and the
        // process cwd - the player's Managed folder carries both, so run the
        // patch tool there. PatchLanCombat keeps the input module open while
        // writing, so patch to a temp file and swap on success.
        var patched = original + ".patched";
        var patch = Run(patchTool, quote(original) + " " + quote(patched) + " " + quote(roomUi),
                        managed);
        if (patch != 0)
        {
            Fail("补丁失败（退出码 " + patch + "）- 该游戏版本可能不被支持");
            return 4;
        }
        File.Copy(patched, original, true);
        File.Delete(patched);
        File.Copy(roomUi, Path.Combine(managed, "LanRoomUI.dll"), true);

        // ---- server + launcher --------------------------------------------
        var serverSrc = Path.Combine(payload, "server");
        var serverDst = Path.Combine(installRoot, "server");
        Console.WriteLine("安装 LAN 服务端...");
        CopyDirectory(serverSrc, serverDst, null);   // server.exe + 3 extracted JSON data files
        foreach (var name in new[] { "Start Game.exe", "Updater.exe" })
        {
            var src = Path.Combine(payload, name);
            if (File.Exists(src)) File.Copy(src, Path.Combine(installRoot, name), true);
        }

        // ---- summary -------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("安装完成。");
        Console.WriteLine("  开房: 运行 " + Path.Combine(serverDst, "server.exe") + "（默认 8 人 / 300 秒回合）");
        Console.WriteLine("  加入: 运行 " + Path.Combine(installRoot, "Start Game.exe") + "，输入房主局域网 IPv4");
        Console.WriteLine("  端口: TCP 43120/43123/43134，UDP 43135（防火墙放行 server.exe）");
        if (failures > 0) Console.WriteLine("（过程中有 " + failures + " 个非致命警告）");
        return 0;
    }

    // ---- compatibility gate ----------------------------------------------
    static bool CheckCompatibility(string game)
    {
        var managed = Path.Combine(game, "CrazyShooter_Data", "Managed");
        var dll = Path.Combine(managed, "Assembly-CSharp.dll");
        var scene = Path.Combine(game, "Data", WorldScene);
        var problems = new List<string>();

        if (!File.Exists(dll)) problems.Add("缺少 " + dll);
        if (!File.Exists(scene)) problems.Add("缺少地图 102 场景数据 " + scene + " —— 该渠道的游戏版本不包含地图 102（Steam 版 658470 只有地图 1 与大厅场景），无法运行本 Mod 的地图 102 联机。请使用包含地图 102 的发行版（例如 4399 渠道的最新版）。");

        if (File.Exists(dll))
        {
            var bytes = File.ReadAllBytes(dll);
            var host = Encoding.Unicode.GetBytes("127.0.0.1");
            var hasHost = IndexOf(bytes, host) >= 0;
            var portIl = new byte[5];
            portIl[0] = 0x20;
            BitConverter.GetBytes((uint)43120).CopyTo(portIl, 1);
            var hasPort = IndexOf(bytes, portIl) >= 0;
            if (!hasHost) problems.Add("登录宿主地址常量缺失（非 LAN 化的发行版形态）");
            if (!hasPort) problems.Add("登录端口常量不是 43120（旧版或 Steam API 登录形态，无法按本项目方式联机）");
        }

        if (problems.Count == 0)
        {
            Console.WriteLine("兼容性检查通过：包含地图 102 数据与 LAN 登录形态。");
            return true;
        }
        foreach (var p in problems) Fail(p);
        return false;
    }

    // ---- Steam discovery ---------------------------------------------------
    static string LocateSteamGame()
    {
        var steamRoot = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (string.IsNullOrEmpty(steamRoot))
            steamRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        if (!Directory.Exists(steamRoot)) return null;

        var candidates = new List<string> { steamRoot };
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                candidates.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        foreach (var lib in candidates)
        {
            var manifest = Path.Combine(lib, "steamapps", "appmanifest_" + SteamAppId + ".acf");
            if (!File.Exists(manifest)) continue;
            var text = File.ReadAllText(manifest);
            var dir = System.Text.RegularExpressions.Regex.Match(text, "\"installdir\"\\s+\"([^\"]+)\"");
            if (!dir.Success) continue;
            var game = Path.Combine(Path.Combine(lib, "common"), dir.Groups[1].Value);
            if (Directory.Exists(game)) return game;
        }
        return null;
    }

    // ---- helpers ------------------------------------------------------------
    static void Fail(string message) { failures++; Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("FAIL: " + message); Console.ResetColor(); }
    static string quote(string path) { return "\"" + path + "\""; }
    static int Run(string exe, string arguments, string workdir)
    {
        var psi = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (workdir != null) psi.WorkingDirectory = workdir;
        var proc = Process.Start(psi);
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (stdout.Length > 0) Console.Write(stdout);
        if (stderr.Length > 0) { Console.ForegroundColor = ConsoleColor.Yellow; Console.Write(stderr); Console.ResetColor(); }
        return proc.ExitCode;
    }
    static void CopyDirectory(string source, string destination, string[] skip)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            var name = Path.GetFileName(file);
            if (skip != null && System.Linq.Enumerable.Contains(skip, name)) continue;
            File.Copy(file, Path.Combine(destination, name), true);
        }
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)), skip);
    }
    static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Length && ok; j++)
                ok = haystack[i + j] == needle[j];
            if (ok) return i;
        }
        return -1;
    }
}
