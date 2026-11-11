using System;
using System.Diagnostics;
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
            Set("正在安装更新…", 15);
            var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
            var count = Math.Max(1, files.Length);
            for (var i = 0; i < files.Length; i++)
            {
                var relative = files[i].Substring(source.Length).TrimStart('\\', '/');
                var destination = Path.Combine(root, relative);
                if (string.Equals(Path.GetFullPath(destination), Path.GetFullPath(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase))
                    continue;
                var directory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                // Old installs may carry read-only files (early zips preserved
                // the attribute); File.Copy(overwrite) fails on those.
                if (File.Exists(destination))
                    try { File.SetAttributes(destination, FileAttributes.Normal); } catch { }
                File.Copy(files[i], destination, true);
                Set("正在安装：" + relative, 15 + (i + 1) * 78 / count);
            }
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
