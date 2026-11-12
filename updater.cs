using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal sealed class Updater : Form
{
    private readonly string source;
    private readonly string root;
    private readonly int parentPid;
    private readonly string launcherName;
    private readonly ProgressBar bar = new ProgressBar();
    private readonly Label label = new Label();

    private Updater(string source, string root, int parentPid, string launcherName)
    {
        this.source = source;
        this.root = root;
        this.parentPid = parentPid;
        this.launcherName = launcherName;
        Text = "Mini Battlegrounds 更新器";
        ClientSize = new Size(430, 110);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ControlBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        bar.SetBounds(20, 55, 390, 20);
        bar.Minimum = 0;
        bar.Maximum = 100;
        label.SetBounds(20, 18, 390, 28);
        label.Text = "等待旧启动器退出…";
        Controls.Add(label);
        Controls.Add(bar);
        Shown += (sender, args) => RunUpdate();
    }

    [STAThread]
    private static void Main(string[] args)
    {
        string source = null, root = null, launcher = "Start Game.exe";
        var pid = 0;
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--source") source = args[++i];
            else if (args[i] == "--root") root = args[++i];
            else if (args[i] == "--pid") int.TryParse(args[++i], out pid);
            else if (args[i] == "--launch") launcher = args[++i];
        }
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var jobPath = Path.Combine(Environment.CurrentDirectory, "update-job.txt");
        if (!File.Exists(jobPath))
            jobPath = Path.Combine(baseDir, "update-job.txt");
        if ((string.IsNullOrEmpty(source) || string.IsNullOrEmpty(root)) && File.Exists(jobPath))
        {
            var lines = File.ReadAllLines(jobPath, Encoding.UTF8);
            if (lines.Length >= 4)
            {
                source = lines[0].Trim().Trim('"');
                root = lines[1].Trim().Trim('"');
                int.TryParse(lines[2].Trim(), out pid);
                launcher = lines[3].Trim().Trim('"');
            }
        }
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(root) || !Directory.Exists(source))
        {
            var launcherPath = Path.Combine(baseDir, "Start Game.exe");
            if (!File.Exists(launcherPath))
            {
                MessageBox.Show("没有找到 Start Game.exe。请把整个 ZIP 完整解压后再运行。", "Mini Battlegrounds");
                return;
            }
            MessageBox.Show("更新器由 Start Game.exe 自动调用。\n现在打开启动器，启动器会自动检查并安装更新。",
                "Mini Battlegrounds");
            try
            {
                Process.Start(new ProcessStartInfo(launcherPath) { WorkingDirectory = baseDir });
            }
            catch (Exception error)
            {
                MessageBox.Show("启动 Start Game.exe 失败：\n" + error.Message, "Mini Battlegrounds");
            }
            return;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new Updater(source, root, pid, launcher));
    }

    private void Set(string text, int percent)
    {
        label.Text = text;
        bar.Value = Math.Max(0, Math.Min(100, percent));
        Application.DoEvents();
    }

    private static void InstallFiles(string source, string root, Action<string> log, Action<int> progress)
    {
        var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
        var destinations = new List<string>();
        var backups = new Dictionary<string, string>();
        var backupRoot = Path.Combine(root, "update-backup-" + Guid.NewGuid().ToString("N"));
        // Fail before changing files when an existing game/launcher still holds
        // a target open. Later races are covered by atomic replacement/rollback.
        foreach (var file in files)
        {
            var relative = file.Substring(source.Length).TrimStart('\\', '/');
            var target = Path.Combine(root, relative);
            if (File.Exists(target))
                using (File.Open(target, FileMode.Open, FileAccess.Read, FileShare.None)) { }
        }
        Directory.CreateDirectory(backupRoot);
        try
        {
            for (int index = 0; index < files.Length; index++)
            {
                var relative = files[index].Substring(source.Length).TrimStart('\\', '/');
                var target = Path.Combine(root, relative);
                var temporary = target + ".update-" + Guid.NewGuid().ToString("N");
                var directory = Path.GetDirectoryName(target);
                Directory.CreateDirectory(directory);
                var backup = Path.Combine(backupRoot, relative);
                if (File.Exists(target))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backup));
                    File.Copy(target, backup);
                    backups.Add(target, backup);
                    File.SetAttributes(target, FileAttributes.Normal);
                }
                else backups.Add(target, null);
                try
                {
                    File.Copy(files[index], temporary);
                    for (int attempt = 1; ; attempt++)
                    {
                        try
                        {
                            if (File.Exists(target)) File.Replace(temporary, target, null);
                            else File.Move(temporary, target);
                            destinations.Add(target);
                            break;
                        }
                        catch (IOException error)
                        {
                            log("replace blocked " + relative + " attempt=" + attempt + " " + error.Message);
                            if (attempt >= 3) throw;
                            Thread.Sleep(1500);
                        }
                        catch (UnauthorizedAccessException error)
                        {
                            log("replace denied " + relative + " attempt=" + attempt + " " + error.Message);
                            if (attempt >= 3) throw;
                            Thread.Sleep(1500);
                        }
                    }
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                log("replaced " + relative);
                progress((index + 1) * 100 / Math.Max(1, files.Length));
            }
        }
        catch
        {
            bool restored = true;
            destinations.Reverse();
            foreach (var target in destinations)
            {
                try
                {
                    if (backups[target] == null) File.Delete(target);
                    else File.Copy(backups[target], target, true);
                }
                catch (Exception error)
                {
                    restored = false;
                    log("rollback failed " + target + " " + error.Message);
                }
            }
            if (restored) Directory.Delete(backupRoot, true);
            else log("Recovery backups retained at " + backupRoot);
            throw;
        }
        Directory.Delete(backupRoot, true);
    }

    private void RunUpdate()
    {
        try
        {
            if (parentPid > 0)
            {
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        var process = Process.GetProcessById(parentPid);
                        if (process.HasExited)
                            break;
                    }
                    catch (ArgumentException)
                    {
                        break;
                    }
                    Set("等待旧启动器退出…", 8);
                    Thread.Sleep(150);
                }
            }
            if (parentPid > 0)
            {
                try
                {
                    if (!Process.GetProcessById(parentPid).HasExited)
                        throw new IOException("旧启动器仍在运行，请关闭后重试更新。");
                }
                catch (ArgumentException) { }
            }
            Set("正在安装更新…", 15);
            var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
            var count = Math.Max(1, files.Length);
            var logPath = Path.Combine(root, "updater.log");
            Action<string> log = message =>
            {
                try { File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine); } catch { }
            };
            log("update start: source=" + source + " root=" + root + " files=" + files.Length);
            InstallFiles(source, root, log,
                percent => Set("正在安装更新…", 15 + percent * 78 / 100));
            try
            {
                File.Delete(Path.Combine(root, "README.txt"));
                File.Delete(Path.Combine(root, "version.txt"));
            }
            catch
            {
            }
            Set("更新完成，正在启动…", 98);
            var launcher = Path.Combine(root, launcherName);
            if (File.Exists(launcher))
                Process.Start(new ProcessStartInfo(launcher) { WorkingDirectory = root });
            try { File.Delete(Path.Combine(root, "update-job.txt")); } catch { }
            try { Directory.Delete(source, true); } catch { }
            Close();
        }
        catch (Exception error)
        {
            try
            {
                File.AppendAllText(Path.Combine(root, "updater.log"),
                    DateTime.Now.ToString("s") + " source=" + source + " root=" + root + Environment.NewLine +
                    error + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
            MessageBox.Show("更新安装失败：\n" + error.Message + "\n\n日志：" + Path.Combine(root, "updater.log"), "Mini Battlegrounds");
            Close();
        }
    }
}
