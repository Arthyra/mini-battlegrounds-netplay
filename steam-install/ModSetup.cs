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
using System.Security.Cryptography;
using Microsoft.Win32;

internal static class ModSetup
{
    const uint SteamAppId = 658470;          // "Mini Battlegrounds"
    const string WorldScene = "cs_scene_102";
    static int failures;

    static int Main(string[] args)
    {
        try { return Install(args); }
        catch (Exception error) { Fail("安装失败：" + error.Message); return 6; }
    }

    static int Install(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string game = null, installRoot = null;
        bool allowUnknown = false, checkOnly = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--game" && i + 1 < args.Length) game = args[++i];
            else if (args[i] == "--out" && i + 1 < args.Length) installRoot = args[++i];
            else if (args[i] == "--allow-unknown-version") allowUnknown = true;
            else if (args[i] == "--check-only") checkOnly = true;
            else if (args[i] == "--help") {
                Console.WriteLine("ModSetup.exe --game <directory> [--out <new-directory>] [--check-only]");
                return 0;
            }
            else { Fail("未知或缺少参数值：" + args[i]); return 2; }
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
        // Validate output *before* copying any files.
        if (!CheckCompatibility(game, payload, allowUnknown))
            return 3;

        installRoot = Path.GetFullPath(string.IsNullOrEmpty(installRoot) ? "MiniBattlegrounds-LAN" : installRoot);
        var gamePath = game.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var outPath = installRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        bool overlaps = string.Equals(outPath, gamePath, StringComparison.OrdinalIgnoreCase) ||
            outPath.StartsWith(gamePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            gamePath.StartsWith(outPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (overlaps || Directory.Exists(installRoot) || File.Exists(installRoot)) {
            Fail("安装目录不能与游戏目录重叠，也不能已经存在。请指定新的独立目录。");
            return 5;
        }
        if (checkOnly) { Console.WriteLine("兼容性验证成功（未安装）。"); return 0; }
        foreach (var name in new[] { "tools/PatchLanCombat.exe", "tools/Mono.Cecil.dll",
            "LanRoomUI.dll", "server/server.exe", "Start Game.exe", "Updater.exe",
            "ExtractLocalData.exe" }) {
            if (!File.Exists(Path.Combine(payload, name.Replace('/', Path.DirectorySeparatorChar)))) {
                Fail("安装包缺失必要文件：" + name);
                return 2;
            }
        }
        var parent = Path.GetDirectoryName(installRoot);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) {
            Fail("安装目标的父目录不存在。");
            return 5;
        }
        long requiredBytes = 512L * 1024 * 1024;
        foreach (var file in Directory.GetFiles(game, "*", SearchOption.AllDirectories))
            checked { requiredBytes += new FileInfo(file).Length; }
        var drive = new DriveInfo(Path.GetPathRoot(installRoot));
        if (!drive.IsReady || drive.AvailableFreeSpace < requiredBytes) {
            Fail("磁盘空间不足：需要原版游戏大小再加 512MiB 余量。");
            return 5;
        }

        // Build in a fresh sibling directory; only publish it on complete success.
        var stage = installRoot + ".partial-" + Guid.NewGuid().ToString("N");
        try {
            var clientRoot = Path.Combine(stage, "client");
            Console.WriteLine("复制原版游戏到临时目录（原版不变）...");
            CopyDirectory(game, clientRoot, new[] { "output_log.txt" });
            var managed = Path.Combine(clientRoot, "CrazyShooter_Data", "Managed");
            var original = Path.Combine(managed, "Assembly-CSharp.dll");
            File.Copy(original, original + ".original", false);
            var patched = original + ".patched";
            var patch = Run(Path.Combine(payload, "tools", "PatchLanCombat.exe"),
                quote(original) + " " + quote(patched) + " " + quote(Path.Combine(payload, "LanRoomUI.dll")),
                managed);
            if (patch != 0 || !File.Exists(patched)) {
                Fail("补丁失败（退出码 " + patch + "），临时目录将被清理。");
                return 4;
            }
            File.Copy(patched, original, true);
            File.Delete(patched);
            File.Copy(Path.Combine(payload, "LanRoomUI.dll"), Path.Combine(managed, "LanRoomUI.dll"), true);

            var serverDst = Path.Combine(stage, "server");
            Directory.CreateDirectory(serverDst);
            File.Copy(Path.Combine(payload, "server", "server.exe"), Path.Combine(serverDst, "server.exe"));
            Console.WriteLine("从玩家自己的游戏文件生成地图与皮肤数据...");
            var extract = Run(Path.Combine(payload, "ExtractLocalData.exe"),
                "--client " + quote(clientRoot) + " --output " + quote(serverDst), stage);
            if (extract != 0) { Fail("资源提取失败（退出码 " + extract + "）。"); return 4; }
            foreach (var name in new[] {
                "map102-vehicle-obstacles.json", "map102-vehicle-props.json", "skin-catalog.json"
            }) {
                var file = Path.Combine(serverDst, name);
                if (!File.Exists(file) || new FileInfo(file).Length == 0) {
                    Fail("未生成必需的服务器数据：" + name);
                    return 4;
                }
            }
            foreach (var name in new[] { "Start Game.exe", "Updater.exe", "Fullscreen.exe" }) {
                var src = Path.Combine(payload, name);
                if (File.Exists(src)) File.Copy(src, Path.Combine(stage, name));
            }
            Directory.Move(stage, installRoot);
            Console.WriteLine("安装完成：" + installRoot);
            Console.WriteLine("运行 Start Game.exe 创建或加入 LAN 房间。");
            return 0;
        }
        finally {
            if (Directory.Exists(stage))
                try { Directory.Delete(stage, true); }
                catch (Exception e) { Console.Error.WriteLine("清理临时目录失败：" + e.Message + "，请检查 " + stage); }
        }
    }

    // ---- compatibility gate ----------------------------------------------
    static bool CheckCompatibility(string game, string payload, bool allowUnknown)
    {
        var managed = Path.Combine(game, "CrazyShooter_Data", "Managed");
        var dll = Path.Combine(managed, "Assembly-CSharp.dll");
        var scene = Path.Combine(game, "Data", WorldScene);
        var problems = new List<string>();
        if (!File.Exists(dll)) problems.Add("缺少原版程序集。");
        if (!File.Exists(scene)) problems.Add("缺少地图 102 场景；Steam 版不适用。");
        if (File.Exists(dll)) {
            var bytes = File.ReadAllBytes(dll);
            if (IndexOf(bytes, Encoding.Unicode.GetBytes("127.0.0.1")) < 0)
                problems.Add("登录地址特征不匹配。");
            if (IndexOf(bytes, new byte[] { 0x20, 0x70, 0xA8, 0, 0 }) < 0)
                problems.Add("LAN 登录端口特征不匹配。");
            string digest;
            using (var stream = File.OpenRead(dll))
            using (var sha = SHA256.Create())
                digest = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            Console.WriteLine("原版 Assembly-CSharp.dll SHA256: " + digest);
            var manifest = Path.Combine(payload, "supported-assemblies.sha256");
            bool known = false;
            if (File.Exists(manifest))
                foreach (var line in File.ReadAllLines(manifest)) {
                    var first = line.Split('#')[0].Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (first.Length > 0 && first[0].Length == 64 &&
                        string.Equals(first[0], digest, StringComparison.OrdinalIgnoreCase)) known = true;
                }
            if (!known) {
                if (allowUnknown) Console.WriteLine("警告：手动允许未知版本，补丁可能失败。");
                else problems.Add("未通过完整 SHA256 白名单校验，禁止自动安装。");
            }
        }
        foreach (var item in problems) Fail(item);
        if (problems.Count == 0) Console.WriteLine("兼容性验证通过。");
        return problems.Count == 0;
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
        var psi = new ProcessStartInfo(exe, arguments) {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workdir,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        using (var process = new Process { StartInfo = psi }) {
            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                if (e.Data != null) Console.WriteLine(e.Data);
            };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                if (e.Data != null) Console.Error.WriteLine(e.Data);
            };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();
            return process.ExitCode;
        }
    }
    static void CopyDirectory(string source, string destination, string[] skip)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("不支持复制符号链接目录：" + source);
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            var name = Path.GetFileName(file);
            if (skip != null && System.Linq.Enumerable.Contains(skip, name)) continue;
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("不支持复制符号链接文件：" + file);
            File.Copy(file, Path.Combine(destination, name), false);
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
