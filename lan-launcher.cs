using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Net.Sockets;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class LanLauncher : Form
{
    private const int DiscoveryPort = 43122;
    private const string ReplyPrefix = "MBG-LAN-ROOM-1 ";
    private const string CloudHost = "198.51.100.10";
    private static readonly string Client = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "client");
    private static readonly string Settings = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher.ini");
    private readonly Button cloud = new Button();
    private readonly Button search = new Button();
    private readonly Button create = new Button();
    private readonly Button manual = new Button();
    private readonly Label status = new Label();
    private Process serverProcess;
    private Process gameProcess;
    private Button logButton;
    private Button firewallButton;
    private string serverLogPath;
    private readonly StringBuilder serverLog = new StringBuilder();
    private readonly object serverLogLock = new object();
    private readonly string profile;

    [STAThread]
    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(uint process);

    private static void Main(string[] args)
    {
        // The update endpoint is HTTPS only; TLS 1.2 must be available even
        // when the installed runtime predates the OS default.
        try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
        applyUpdateSilently = args != null && Array.IndexOf(args, "--apply-update") >= 0;
        if (applyUpdateSilently)
        {
            // Headless runs print results to the parent console.
            try { AttachConsole(0xFFFFFFFFu); } catch { }
        }
        try { SetProcessDPIAware(); } catch { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            File.Delete(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "version.txt"));
            File.Delete(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "README.txt"));
        }
        catch
        {
        }
        StripZoneMarkers();
        EnsureDesktopShortcut();
        if (args.Length == 2 && args[0] == "--host")
        {
            string host;
            string savedProfile;
            LoadSettings(out savedProfile);
            if (TryHost(args[1], out host))
                StartGame(host, savedProfile);
            else
                MessageBox.Show("\u8bf7\u8f93\u5165\u6709\u6548\u7684 IPv4 \u5730\u5740\u3002");
            return;
        }
        if (applyUpdateSilently)
        {
            // Headless update mode: fetch, verify, download, apply and hand
            // over to the installer without any UI, for automated checks.
            var exitCode = 0;
            try
            {
                var latest = FetchUpdate();
                LogUpdate("manifest ok: v" + latest.Version + " url=" + latest.Url);
                if (!IsNewer(latest.Version, LocalVersion()))
                    Console.WriteLine("NO-UPDATE current=" + LocalVersion() + " latest=" + latest.Version);
                else
                {
                    var staging = ApplyUpdate(latest, new Progress<UpdateProgress>());
                    LogUpdate("apply ok, staging=" + staging);
                    StartInstaller(staging);
                    Console.WriteLine("UPDATE-OK staging=" + staging);
                }
            }
            catch (Exception headlessError)
            {
                LogUpdate("update failed: " + headlessError.GetType().Name + " " + headlessError.Message);
                Console.WriteLine("UPDATE-FAIL " + headlessError.GetType().Name + " " + headlessError.Message);
                exitCode = 1;
            }
            Environment.Exit(exitCode);
        }
        Application.Run(new LanLauncher());
    }

    private static void StripZoneMarkers()
    {
        // The release zip carries Mark-of-the-Web; Windows then shows the
        // "unknown publisher" prompt on every launch of every exe. Stripping
        // the zone marker once keeps later launches quiet (a real code
        // signing certificate is the only way to remove the first prompt).
        try
        {
            var root = AppDomain.CurrentDomain.BaseDirectory;
            var targets = new List<string>(Directory.GetFiles(root, "*.exe"));
            var client = Path.Combine(root, "client");
            if (Directory.Exists(client))
            {
                targets.AddRange(Directory.GetFiles(client, "*.exe", SearchOption.AllDirectories));
            }
            foreach (var file in targets)
            {
                try { File.Delete(file + ":Zone.Identifier"); } catch { }
            }
        }
        catch
        {
        }
    }

    private static void EnsureDesktopShortcut()
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (desktop.Length == 0)
                return;
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
                return;
            var shell = Activator.CreateInstance(shellType);
            var path = Path.Combine(desktop, "Mini Battle.lnk");
            var shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
            var type = shortcut.GetType();
            type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { Application.ExecutablePath });
            type.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { AppDomain.CurrentDomain.BaseDirectory });
            type.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "Mini Battlegrounds LAN" });
            var icon = Path.Combine(Client, "CrazyShooter.exe");
            if (!File.Exists(icon))
                icon = Application.ExecutablePath;
            type.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { icon + ",0" });
            type.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }
        catch
        {
        }
    }

    private LanLauncher()
    {
        LoadSettings(out profile);
        SaveSettings(profile);
        Text = "Mini Battlegrounds LAN";
        ClientSize = new Size(480, 275);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        status.SetBounds(15, 15, 450, 64);
        status.Text = "版本 v" + LocalVersion() + "\n加入云服务器，或创建/搜索局域网房间。";
        cloud.SetBounds(15, 85, 450, 40);
        cloud.Text = "加入云服务器（杭州）";
        create.SetBounds(15, 140, 140, 30);
        create.Text = "创建局域网房间";
        search.SetBounds(165, 140, 110, 30);
        search.Text = "搜索局域网房间";
        manual.SetBounds(285, 140, 130, 30);
        manual.Text = "手动地址…";
        logButton = new Button();
        logButton.SetBounds(425, 140, 55, 30);
        logButton.Text = "日志";
        logButton.Enabled = false;
        logButton.Click += (sender, args) => OpenServerLog();
        firewallButton = new Button();
        firewallButton.SetBounds(15, 180, 140, 30);
        firewallButton.Text = "防火墙放行";
        firewallButton.Click += (sender, args) => AllowFirewall(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "server", "server.exe"));
        Controls.AddRange(new Control[] { status, cloud, create, search, manual, logButton, firewallButton });
        AcceptButton = cloud;
        Shown += async (sender, args) => await CheckForUpdate(applyUpdateSilently);
        cloud.Click += (sender, args) => JoinHost(CloudHost);
        search.Click += async (sender, args) => await SearchLan();
        manual.Click += (sender, args) => PromptForAddress();
        create.Click += (sender, args) => ToggleRoom();
        FormClosing += (sender, args) => StopRoom();
    }

    private async Task SearchLan()
    {
        search.Enabled = false;
        status.Text = "正在搜索局域网房间…";
        var rooms = await Task.Run(() => FindRooms());
        if (IsDisposed)
            return;
        search.Enabled = true;
        if (rooms.Count == 0)
        {
            status.Text = "未发现局域网房间。可以创建房间，或手动输入房主 IP。";
            return;
        }
        if (rooms.Count == 1)
        {
            JoinHost(rooms.First());
            return;
        }
        var choice = ChooseRoom(rooms);
        if (choice != null)
            JoinHost(choice);
        else
            status.Text = "已取消选择；可重新搜索。";
    }

    private static SortedSet<string> FindRooms()
    {
        var rooms = new SortedSet<string>();
        try
        {
            using (var udp = new UdpClient(0))
            {
                udp.EnableBroadcast = true;
                var request = Encoding.ASCII.GetBytes("MBG-LAN-DISCOVER-1");
                udp.Send(request, request.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
                var elapsed = Stopwatch.StartNew();
                while (elapsed.ElapsedMilliseconds < 1100)
                {
                    udp.Client.ReceiveTimeout = Math.Max(1, 1100 - (int)elapsed.ElapsedMilliseconds);
                    var remote = new IPEndPoint(IPAddress.Any, 0);
                    var reply = Encoding.ASCII.GetString(udp.Receive(ref remote));
                    string host;
                    if (remote.Port == DiscoveryPort && reply.StartsWith(ReplyPrefix, StringComparison.Ordinal)
                        && TryHost(reply.Substring(ReplyPrefix.Length), out host))
                        rooms.Add(host);
                }
            }
        }
        catch (SocketException)
        {
            // No broadcast response. A manual address remains available.
        }
        return rooms;
    }

    private void JoinHost(string text)
    {
        string host;
        if (!TryHost(text, out host))
        {
            status.Text = "地址无效，请输入房主 IPv4。";
            return;
        }
        status.Text = "正在检查 " + host + " 的登录/世界/战斗端口…";
        foreach (var port in new[] { 43120, 43123, 43134 })
        {
            if (!WaitForPort(host, port, port == 43120 ? 3000 : 1800))
            {
                status.Text = "连不上 " + host + ":" + port + "。\n检查房主 server.exe 的 Windows 防火墙（专用网络）和是否在同一 Wi-Fi。";
                return;
            }
        }
        status.Text = "已启动客户端：目标 " + host + "，档案 " + profile + "。";
        gameProcess = StartGame(host, profile);
        if (gameProcess == null)
            status.Text = "启动客户端失败。检查 client\\CrazyShooter.exe 是否完整。";
    }

    private string ChooseRoom(System.Collections.Generic.IEnumerable<string> rooms)
    {
        using (var dialog = new Form())
        {
            dialog.Text = "选择局域网房间";
            dialog.ClientSize = new Size(330, 130);
            dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.MaximizeBox = false;
            dialog.MinimizeBox = false;
            var label = new Label { Text = "发现多个房间，请选择：", Bounds = new Rectangle(15, 12, 300, 22) };
            var list = new ComboBox { Bounds = new Rectangle(15, 40, 300, 26), DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (var room in rooms)
                list.Items.Add(room);
            if (list.Items.Count > 0)
                list.SelectedIndex = 0;
            var ok = new Button { Text = "加入", Bounds = new Rectangle(145, 85, 80, 28), DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "取消", Bounds = new Rectangle(235, 85, 80, 28), DialogResult = DialogResult.Cancel };
            dialog.Controls.AddRange(new Control[] { label, list, ok, cancel });
            dialog.AcceptButton = ok;
            dialog.CancelButton = cancel;
            return dialog.ShowDialog(this) == DialogResult.OK && list.SelectedItem != null
                ? list.SelectedItem.ToString()
                : null;
        }
    }

    private void PromptForAddress()
    {
        using (var dialog = new Form())
        {
            dialog.Text = "手动输入房主地址";
            dialog.ClientSize = new Size(340, 120);
            dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.MaximizeBox = false;
            dialog.MinimizeBox = false;
            var label = new Label { Text = "房主 IPv4：", Bounds = new Rectangle(15, 18, 300, 20) };
            var input = new TextBox { Bounds = new Rectangle(15, 42, 310, 24) };
            var ok = new Button { Text = "连接", Bounds = new Rectangle(155, 80, 80, 28), DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "取消", Bounds = new Rectangle(245, 80, 80, 28), DialogResult = DialogResult.Cancel };
            dialog.Controls.AddRange(new Control[] { label, input, ok, cancel });
            dialog.AcceptButton = ok;
            dialog.CancelButton = cancel;
            if (dialog.ShowDialog(this) == DialogResult.OK)
                JoinHost(input.Text);
        }
    }

    private sealed class UpdateInfo
    {
        public string Version;
        public string Url;
        public string Sha256;
        public string Signature;
    }

    // Tried in order. The RSA endpoint (ISRG Root X1) works on older .NET
    // and Windows builds where the ECDSA/ISRG Root YE chain is untrusted;
    // the legacy HTTP entry stays as the final compatibility fallback - the
    // RSA manifest signature is enforced on every entry, so authenticity
    // never depends on the transport.
    private static readonly string[] UpdateUrls =
    {
        "https://198.51.100.10:8445/mbg-update/latest.json",
        "https://198.51.100.10/mbg-update/latest.json",
        "http://198.51.100.10:8080/update/latest.json",
    };
    private static bool applyUpdateSilently;

    private static void LogUpdate(string message)
    {
        try
        {
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine;
            File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher-update.log"), line);
        }
        catch
        {
        }
    }

    private sealed class UpdateProgress
    {
        public int Percent;
        public string Text;
    }

    private sealed class UpdateProgressForm : Form
    {
        private readonly ProgressBar bar = new ProgressBar();
        private readonly Label label = new Label();

        public UpdateProgressForm()
        {
            Text = "Mini Battlegrounds 更新";
            ClientSize = new Size(430, 120);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            TopMost = true;
            bar.SetBounds(20, 55, 390, 22);
            bar.Minimum = 0;
            bar.Maximum = 100;
            label.SetBounds(20, 18, 390, 30);
            label.Text = "准备更新…";
            Controls.Add(label);
            Controls.Add(bar);
        }

        public void Report(UpdateProgress progress)
        {
            if (IsDisposed)
                return;
            bar.Value = Math.Max(0, Math.Min(100, progress.Percent));
            label.Text = (progress.Text ?? "更新中…") + "  " + progress.Percent + "%";
        }
    }

    private async Task CheckForUpdate(bool applyUpdateSilently)
    {
        UpdateInfo latest;
        try
        {
            latest = await Task.Run(() => FetchUpdate());
            LogUpdate("manifest fetched: v" + latest.Version + " url=" + latest.Url);
        }
        catch (InvalidDataException invalid)
        {
            LogUpdate("manifest signature REJECTED: " + invalid.Message);
            if (!applyUpdateSilently)
                MessageBox.Show("更新清单验证失败，已拒绝自动更新：\n" + invalid.Message,
                    "Mini Battlegrounds", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        catch
        {
            return;
        }
        if (latest == null || !IsNewer(latest.Version, LocalVersion()))
            return;
        if (!applyUpdateSilently && MessageBox.Show(
                "发现新版本 v" + latest.Version + "（当前 v" + LocalVersion() + "）。\n是否现在从 ECS 下载并自动更新？",
                "Mini Battlegrounds", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
            return;

        string error = null;
        using (var dialog = new UpdateProgressForm())
        {
            dialog.Show(this);
            var reporter = new Progress<UpdateProgress>(dialog.Report);
            string staging = null;
            try
            {
                await Task.Run(() => { staging = ApplyUpdate(latest, reporter); });
            }
            catch (Exception exception)
            {
                LogUpdate("apply failed: " + exception.GetType().Name + " " + exception.Message);
                error = exception.Message;
            }
            finally
            {
                dialog.Close();
            }
            if (error == null)
            {
                try
                {
                    StartInstaller(staging);
                }
                catch (Exception exception)
                {
                    error = exception.Message;
                }
            }
        }
        if (error != null)
        {
            if (!applyUpdateSilently)
                MessageBox.Show("自动更新失败：\n" + error, "Mini Battlegrounds");
            return;
        }
        // No modal here: the updater window ("等待旧启动器退出…") is the
        // progress feedback, and a blocking dialog here deadlocks it until
        // the user clicks OK. Close immediately so the installer proceeds
        // and restarts the launcher itself.
        Close();
    }

    // Pinned RSA public key. The corresponding private key MUST remain offline.
    private const string UpdatePublicModulus = "vswNeqadE/dVs5VKr7oCK/ihRkwymqGtPVbVpT6WqkzqsVroH+ElJep0PjwYIoCFul3LJQOQTIQVCF6vMsk5y/azICGTjHPQUlBXXqypPI6GhsuyS303A+IqA5E3HOExd7iO5vTJOnLeDRVIVJmOg3QBKwMc8P1PuCaw5FeLB8x++LTPTsKr8GG33E6Cy6wNWOq7os/GBEwfYcvY6hmcC8gRo2LpetDBALBPI2aS0ewz1LKAyHVTAaQfF6MbwFggjzjyWP08RHEIGkNoAPluezJLuXy9W0N2Z2r3+xH/iwzjsyFdtsxjN0BcFTkLlI57h/ur+EIEgOWZANW6Tzjwkw==";

    private static bool VerifyUpdateManifest(UpdateInfo info)
    {
        if (string.IsNullOrEmpty(info.Version) || string.IsNullOrEmpty(info.Url) ||
            string.IsNullOrEmpty(info.Sha256) || info.Sha256.Length != 64 ||
            string.IsNullOrEmpty(info.Signature))
            return false;
        foreach (char ch in info.Sha256)
            if (!Uri.IsHexDigit(ch))
                return false;
        Uri uri;
        if (!Uri.TryCreate(info.Url, UriKind.Absolute, out uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return false;
        byte[] payload = Encoding.UTF8.GetBytes("MBG-LAN-UPDATE-1\n" +
            info.Version + "\n" + info.Url + "\n" + info.Sha256.ToLowerInvariant() + "\n");
        try
        {
            // The legacy PROV_RSA_FULL CSP may reject SHA256 with
            // "Invalid algorithm specified" on .NET Framework.
            // PROV_RSA_AES (24) supports SHA-2 RSA signatures on Windows.
            using (var rsa = new RSACryptoServiceProvider(new CspParameters(24)))
            {
                rsa.PersistKeyInCsp = false;
                rsa.ImportParameters(new RSAParameters {
                    Modulus = Convert.FromBase64String(UpdatePublicModulus),
                    Exponent = Convert.FromBase64String("AQAB")
                });
                return rsa.VerifyData(payload, CryptoConfig.MapNameToOID("SHA256"),
                    Convert.FromBase64String(info.Signature));
            }
        }
        catch (CryptographicException) { return false; }
        catch (FormatException) { return false; }
    }

    private static UpdateInfo FetchUpdate()
    {
        Exception lastError = null;
        foreach (var url in UpdateUrls)
        {
            try
            {
                LogUpdate("fetch update manifest from " + url);
                return FetchUpdateFrom(url);
            }
            catch (Exception error)
            {
                LogUpdate("manifest fetch failed at " + url + ": " + error.GetType().Name + " " + error.Message);
                lastError = error;
            }
        }
        throw lastError ?? new InvalidOperationException("没有可用的更新服务器。");
    }

    private static UpdateInfo FetchUpdateFrom(string url)
    {
        using (var client = new WebClient())
        {
            client.Proxy = null;
            client.Encoding = Encoding.UTF8;
            var json = client.DownloadString(url);
            var latest = new UpdateInfo
            {
                Version = JsonValue(json, "version"),
                Url = JsonValue(json, "url"),
                Sha256 = JsonValue(json, "sha256"),
                Signature = JsonValue(json, "signature"),
            };
            if (!VerifyUpdateManifest(latest))
                throw new InvalidDataException("更新清单签名无效，或下载地址/校验值被修改。");
            return latest;
        }
    }

    private static void DownloadUpdate(string url, string path, IProgress<UpdateProgress> progress)
    {
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.Proxy = null;
        request.Timeout = 30000;
        request.ReadWriteTimeout = 30000;
        request.UserAgent = "MiniBattlegroundsUpdater/1.0";
        request.Expect = null;
        using (var response = (HttpWebResponse)request.GetResponse())
        using (var input = response.GetResponseStream())
        using (var output = File.Create(path))
        {
            var total = response.ContentLength;
            var buffer = new byte[64 * 1024];
            long read = 0;
            int count;
            while (input != null && (count = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, count);
                read += count;
                var percent = total > 0 ? 5 + (int)(read * 65 / total) : 35;
                progress.Report(new UpdateProgress { Percent = percent, Text = "下载更新包…" });
            }
        }
    }

    private static string ApplyUpdate(UpdateInfo latest, IProgress<UpdateProgress> progress)
    {
        if (string.IsNullOrWhiteSpace(latest.Url))
            throw new InvalidOperationException("更新地址为空。");
        // 360 等安全软件会拦截未签名程序写入受保护的 %TEMP%，但通常放行
        // 安装目录本身，所以 staging 优先放在启动器目录下。
        var staging = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            "update-stage-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
        }
        catch (UnauthorizedAccessException)
        {
            staging = Path.Combine(Path.GetTempPath(), "mbg-update-stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
        }
        catch (IOException)
        {
            staging = Path.Combine(Path.GetTempPath(), "mbg-update-stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
        }
        var archive = Path.Combine(staging, "update.zip");
        try
        {
            progress.Report(new UpdateProgress { Percent = 2, Text = "连接更新服务器…" });
            for (var attempt = 1; ; attempt++)
            {
                DownloadUpdate(latest.Url, archive, progress);
                progress.Report(new UpdateProgress { Percent = 72, Text = "校验更新包…" });
                var valid = true;
                using (var sha = SHA256.Create())
                using (var stream = File.OpenRead(archive))
                {
                    var actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                    valid = actual == latest.Sha256.ToLowerInvariant();
                }
                if (valid)
                    break;
                try { File.Delete(archive); } catch { }
                if (attempt >= 3)
                    throw new InvalidDataException("更新包校验失败，已重试 3 次。");
                progress.Report(new UpdateProgress { Percent = 3, Text = "校验失败，正在重试…" });
                System.Threading.Thread.Sleep(1000);
            }
            progress.Report(new UpdateProgress { Percent = 80, Text = "解压更新包…" });
            // 360 等安全软件会在大文件解压时短暂锁定目录，瞬时拒绝需重试。
            for (var extractAttempt = 1; ; extractAttempt++)
            {
                try
                {
                    ZipFile.ExtractToDirectory(archive, staging);
                    break;
                }
                catch (UnauthorizedAccessException extractError)
                {
                    if (extractAttempt >= 3)
                        throw;
                    LogUpdate("extract attempt " + extractAttempt + " blocked, retrying: " + extractError.Message);
                    System.Threading.Thread.Sleep(2000);
                }
                catch (IOException extractError)
                {
                    if (extractAttempt >= 3)
                        throw;
                    LogUpdate("extract attempt " + extractAttempt + " blocked, retrying: " + extractError.Message);
                    System.Threading.Thread.Sleep(2000);
                }
            }
            try { File.Delete(archive); } catch { }
            if (!Directory.Exists(Path.Combine(staging, "client")))
                throw new InvalidDataException("更新包缺少 client 目录。");
            if (!File.Exists(Path.Combine(staging, "Updater.exe")))
                throw new InvalidDataException("更新包缺少 Updater.exe。");
            progress.Report(new UpdateProgress { Percent = 95, Text = "准备独立更新器…" });
            // Downloaded files carry Mark-of-the-Web; every later launch
            // would trigger a security warning. Strip it while staging.
            foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                try { File.Delete(file + ":Zone.Identifier"); } catch { }
            }
            return staging;
        }
        catch
        {
            try { Directory.Delete(staging, true); } catch { }
            throw;
        }
    }

    private static void StartInstaller(string staging)
    {
        if (string.IsNullOrEmpty(staging) || !Directory.Exists(staging))
            throw new InvalidOperationException("更新暂存目录不存在。");
        var updaterSource = Path.Combine(staging, "Updater.exe");
        if (!File.Exists(updaterSource))
            throw new InvalidDataException("更新包缺少 Updater.exe。");
        var root = AppDomain.CurrentDomain.BaseDirectory;
        StopOwnServers(Path.Combine(root, "server", "server.exe"));
        // Run the updater from the staging directory: security suites block
        // unsigned exes materialising in %TEMP%.
        var updater = Path.Combine(staging, "mbg-updater.exe");
        File.Copy(updaterSource, updater, true);
        var launcherName = Path.GetFileName(Application.ExecutablePath);
        var job = Path.Combine(root, "update-job.txt");
        File.WriteAllLines(job, new[]
        {
            staging,
            root,
            Process.GetCurrentProcess().Id.ToString(),
            launcherName,
        }, new UTF8Encoding(false));
        Process.Start(new ProcessStartInfo(updater)
        {
            WorkingDirectory = root,
            UseShellExecute = true
        });
    }

    private static void StartReplacement(string newLauncher)
    {
        var root = AppDomain.CurrentDomain.BaseDirectory;
        var currentName = Path.GetFileName(Application.ExecutablePath);
        var newName = Path.GetFileName(newLauncher);
        var script = Path.Combine(root, "apply-update.cmd");
        var pid = Process.GetCurrentProcess().Id;
        var lines = new[]
        {
            "@echo off",
            "cd /d \"%~dp0\"",
            ":wait",
            "tasklist /FI \"PID eq " + pid + "\" | find \"" + pid + "\" >nul",
            "if not errorlevel 1 (",
            "  timeout /t 1 /nobreak >nul",
            "  goto wait",
            ")",
            "for /l %%i in (1,1,20) do (",
            "  move /Y \"" + newName + "\" \"" + currentName + "\" >nul 2>&1",
            "  if not errorlevel 1 goto done",
            "  timeout /t 1 /nobreak >nul",
            ")",
            ":done",
            "start \"\" \"" + currentName + "\"",
            "del \"%~f0\"",
        };
        File.WriteAllLines(script, lines, Encoding.Default);
        Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + script + "\"")
        {
            WorkingDirectory = root,
            UseShellExecute = true,
            CreateNoWindow = true
        });
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static string LocalVersion()
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(Application.ExecutablePath).FileVersion;
            Version parsed;
            if (Version.TryParse(version, out parsed))
                return parsed.ToString(3);
            return string.IsNullOrEmpty(version) ? "0.0" : version;
        }
        catch
        {
            return "0.0";
        }
    }

    private static bool IsNewer(string remote, string local)
    {
        Version remoteVersion, localVersion;
        if (!Version.TryParse(remote, out remoteVersion))
            return false;
        if (!Version.TryParse(local, out localVersion))
            localVersion = new Version(0, 0);
        return remoteVersion > localVersion;
    }

    private static string JsonValue(string json, string key)
    {
        var keyIndex = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
        if (keyIndex < 0)
            return "";
        var colon = json.IndexOf(':', keyIndex);
        if (colon < 0)
            return "";
        var quote = json.IndexOf('"', colon + 1);
        if (quote < 0)
            return "";
        var end = json.IndexOf('"', quote + 1);
        return end < 0 ? "" : json.Substring(quote + 1, end - quote - 1);
    }

    private static string LanAddress()
    {
        string fallback = "";
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up ||
                network.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                network.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                continue;
            var name = (network.Name + " " + network.Description).ToLowerInvariant();
            if (name.Contains("vmware") || name.Contains("virtualbox") || name.Contains("hyper-v") ||
                name.Contains("vethernet") || name.Contains("wsl") || name.Contains("tap-") ||
                name.Contains("wi-fi direct") || name.Contains("bluetooth") || name.Contains("zerotier") ||
                name.Contains("tailscale"))
                continue;
            var properties = network.GetIPProperties();
            var gateway = false;
            foreach (var item in properties.GatewayAddresses)
                if (item.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !item.Address.Equals(IPAddress.Any) && !IPAddress.IsLoopback(item.Address))
                    gateway = true;
            foreach (var unicast in properties.UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
                    continue;
                var bytes = address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254)
                    continue;
                var isPrivate = bytes[0] == 10 || (bytes[0] == 192 && bytes[1] == 168) ||
                    (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31);
                if (!isPrivate)
                    continue;
                if (gateway)
                    return address.ToString();
                if (fallback.Length == 0)
                    fallback = address.ToString();
            }
        }
        return fallback.Length > 0 ? fallback : "127.0.0.1";
    }
    private void ToggleRoom()
    {
        if (serverProcess != null && !serverProcess.HasExited)
        {
            StopRoom();
            return;
        }
        var serverExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "server", "server.exe");
        if (!File.Exists(serverExe))
        {
            MessageBox.Show("这个客户端包没有附带服务端，不能创建房间。\n请使用房主版，或连接到已有房间。",
                "Mini Battlegrounds");
            return;
        }
        var host = LanAddress();
        if (host.Length == 0)
        {
            MessageBox.Show("没有找到可用的局域网 IPv4。\n请先连接同一个 Wi-Fi/手机热点，再创建房间。", "Mini Battlegrounds");
            return;
        }
        if (!AllowFirewall(serverExe))
        {
            MessageBox.Show("没有成功放行 server.exe 的防火墙。\n请在 Windows 防火墙允许 server\\server.exe 的专用/公用网络访问。", "Mini Battlegrounds");
            return;
        }
        try
        {
            var info = new ProcessStartInfo(serverExe,
                "--players 8 --host " + host + " --bind " + host + " --owner-profile slot-1 --admin-port 0")
            {
                WorkingDirectory = Path.GetDirectoryName(serverExe),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            StopOwnServers(serverExe);
            serverLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher-server.log");
            try { File.WriteAllText(serverLogPath, ""); } catch { }
            serverProcess = Process.Start(info);
            serverProcess.OutputDataReceived += (sender, args) => AppendServerLog(args.Data);
            serverProcess.ErrorDataReceived += (sender, args) => AppendServerLog(args.Data);
            serverProcess.BeginOutputReadLine();
            serverProcess.BeginErrorReadLine();
            if (!WaitForPort(host, 43120, 6000))
            {
                var detail = serverLog.ToString().Trim();
                var exit = serverProcess != null && serverProcess.HasExited ? " 退出码 " + serverProcess.ExitCode : "";
                StopRoom();
                MessageBox.Show("服务端没有在 6 秒内启动。" + exit +
                    "\n\n" + (detail.Length > 0 ? detail : "没有服务端输出；检查 server\\server.exe、防火墙和端口占用。") +
                    "\n\n日志：" + serverLogPath, "Mini Battlegrounds");
                return;
            }
            status.Text = "局域网房间已创建：" + host + "\n房主档案 slot-1；服务端日志已开启。";
            create.Text = "关闭局域网房间";
            logButton.Enabled = true;
            gameProcess = StartGame(host, "slot-1");
        }
        catch (Exception error)
        {
            StopRoom();
            MessageBox.Show("创建房间失败：\n" + error.Message, "Mini Battlegrounds");
        }
    }

    private static bool FirewallRuleExists(string serverExe)
    {
        try
        {
            var info = new ProcessStartInfo("netsh",
                "advfirewall firewall show rule name=\"Mini Battlegrounds LAN Server\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var process = Process.Start(info))
            {
                var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                process.WaitForExit(3000);
                return output.IndexOf(serverExe, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool AllowFirewall(string serverExe)
    {
        if (!File.Exists(serverExe) || FirewallRuleExists(serverExe))
            return true;
        try
        {
            var command = "/c netsh advfirewall firewall delete rule name=\"Mini Battlegrounds LAN Server\" >nul 2>&1 & " +
                "netsh advfirewall firewall add rule name=\"Mini Battlegrounds LAN Server\" dir=in action=allow program=\"" +
                serverExe + "\" enable=yes profile=any";
            var info = new ProcessStartInfo("cmd.exe", command)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using (var process = Process.Start(info))
            {
                process.WaitForExit(15000);
                return process.HasExited && process.ExitCode == 0;
            }
        }
        catch
        {
            return false;
        }
    }

    private static void StopOwnServers(string expected)
    {
        foreach (var process in Process.GetProcessesByName("server"))
        {
            try
            {
                if (!process.HasExited &&
                    string.Equals(process.MainModule.FileName, expected, StringComparison.OrdinalIgnoreCase))
                    process.Kill();
            }
            catch
            {
            }
        }
    }

    private static bool WaitForPort(string host, int port, int timeout)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var task = client.ConnectAsync(host, port);
                    if (task.Wait(500) && client.Connected)
                        return true;
                }
            }
            catch
            {
            }
            System.Threading.Thread.Sleep(250);
        }
        return false;
    }

    private void StopRoom()
    {
        var wasHosting = serverProcess != null;
        if (serverProcess != null)
        {
            var pid = serverProcess.Id;
            try
            {
                var killer = Process.Start(new ProcessStartInfo("taskkill.exe", "/PID " + pid + " /T /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (killer != null)
                    killer.WaitForExit(4000);
            }
            catch
            {
                try { serverProcess.Kill(); } catch { }
            }
            serverProcess = null;
        }
        if (wasHosting && gameProcess != null)
        {
            try
            {
                if (!gameProcess.HasExited)
                    gameProcess.Kill();
            }
            catch
            {
            }
        }
        if (wasHosting)
            gameProcess = null;
        logButton.Enabled = false;
        create.Text = "\u521b\u5efa\u5c40\u57df\u7f51\u623f\u95f4";
        if (wasHosting)
            status.Text = "局域网房间已关闭，房主客户端已退出。";
    }

    private void AppendServerLog(string line)
    {
        if (string.IsNullOrEmpty(line))
            return;
        lock (serverLogLock)
        {
            serverLog.AppendLine(line);
            try
            {
                if (!string.IsNullOrEmpty(serverLogPath))
                    File.AppendAllText(serverLogPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
            }
        }
    }

    private void OpenServerLog()
    {
        if (string.IsNullOrEmpty(serverLogPath) || !File.Exists(serverLogPath))
        {
            MessageBox.Show("还没有服务端日志。先创建一次局域网房间。", "Mini Battlegrounds");
            return;
        }
        try
        {
            Process.Start("notepad.exe", "\"" + serverLogPath + "\"");
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "Mini Battlegrounds");
        }
    }

    private static bool TryHost(string text, out string host)
    {
        text = text.Trim();
        Uri link;
        if (Uri.TryCreate(text, UriKind.Absolute, out link) &&
            (link.Scheme == Uri.UriSchemeHttp || link.Scheme == Uri.UriSchemeHttps))
            text = link.Host;
        IPAddress ip;
        if (IPAddress.TryParse(text, out ip) && ip.AddressFamily == AddressFamily.InterNetwork &&
            !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.Broadcast))
        {
            host = ip.ToString();
            return true;
        }
        host = null;
        return false;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int width, int height, uint flags);

    private static void MakeBorderless(Process process)
    {
        if (process == null)
            return;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                for (var i = 0; i < 80; i++)
                {
                    process.Refresh();
                    var handle = process.MainWindowHandle;
                    if (handle != IntPtr.Zero)
                    {
                        var style = (int)GetWindowLong(handle, -16);
                        style &= ~(0x00C00000 | 0x00040000); // caption + thick frame
                        SetWindowLong(handle, -16, style);
                        var bounds = Screen.PrimaryScreen.Bounds;
                        SetWindowPos(handle, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0040 | 0x0004);
                    }
                    System.Threading.Thread.Sleep(250);
                }
            }
            catch
            {
            }
        });
    }

    private static Process StartGame(string host, string profile)
    {
        var executable = Path.Combine(Client, "CrazyShooter.exe");
        if (!File.Exists(executable))
        {
            MessageBox.Show("\u8bf7\u5148\u5c06 ZIP \u5168\u90e8\u89e3\u538b\uff0c\u4fdd\u7559 client \u76ee\u5f55\u3002", "Mini Battlegrounds");
            return null;
        }
        var bounds = Screen.PrimaryScreen.Bounds;
        var arguments = "-popupwindow -screen-width " + bounds.Width + " -screen-height " + bounds.Height +
            " -screen-fullscreen 0 -lan-host=" + host + " -lan-profile=" + profile;
        try
        {
            var process = Process.Start(new ProcessStartInfo(executable, arguments)
            {
                WorkingDirectory = Client
            });
            MakeBorderless(process);
            try
            {
                var helper = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fullscreen.ps1");
                if (File.Exists(helper))
                {
                    var helperBounds = Screen.PrimaryScreen.Bounds;
                    var helperArgs = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + helper +
                        "\" -ProcessId " + process.Id + " -Width " + helperBounds.Width + " -Height " + helperBounds.Height;
                    Process.Start(new ProcessStartInfo("powershell.exe", helperArgs)
                    {
                        WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                }
            }
            catch
            {
            }
            return process;
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            MessageBox.Show(error.Message, "Mini Battlegrounds");
            return null;
        }
    }

    private static void LoadSettings(out string profile)
    {
        profile = null;
        try
        {
            if (File.Exists(Settings))
            {
                foreach (var line in File.ReadAllLines(Settings, Encoding.UTF8))
                {
                    var split = line.IndexOf('=');
                    if (split <= 0)
                        continue;
                    if (line.Substring(0, split).Trim() == "profile")
                        profile = line.Substring(split + 1).Trim();
                }
            }
        }
        catch (IOException)
        {
        }
        if (string.IsNullOrWhiteSpace(profile))
            profile = DefaultProfile();
    }

    private static string DefaultProfile()
    {
        var builder = new StringBuilder("pc-");
        foreach (var ch in Environment.MachineName.ToLowerInvariant())
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '_')
                builder.Append(ch);
        }
        if (builder.Length < 5)
            builder.Append(Guid.NewGuid().ToString("N").Substring(0, 6));
        var value = builder.ToString();
        return value.Length > 32 ? value.Substring(0, 32) : value;
    }

    private static void SaveSettings(string profile)
    {
        try
        {
            File.WriteAllText(Settings, "profile=" + profile + Environment.NewLine, new UTF8Encoding(true));
        }
        catch (IOException)
        {
        }
    }
}
























