using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class LanLauncher : Form
{
    private const int DiscoveryPort = 43122;
    private const string ReplyPrefix = "MBG-LAN-ROOM-1 ";
    private const string OfficialHost = "198.51.100.10";
    private static readonly string Client = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "client");
    private static readonly string Settings = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher.ini");
    private static readonly string ServerFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "server.txt");
    private readonly ComboBox address = new ComboBox();
    private readonly Button join = new Button();
    private readonly Button search = new Button();
    private readonly Button create = new Button();
    private readonly Label status = new Label();
    private readonly Label official = new Label();
    private Process serverProcess;
    private readonly string profile;

    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
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
        Application.Run(new LanLauncher());
    }

    private LanLauncher()
    {
        LoadSettings(out profile);
        SaveSettings(profile);
        Text = "Mini Battlegrounds LAN";
        ClientSize = new Size(460, 150);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        status.SetBounds(15, 12, 430, 28);
        official.SetBounds(15, 42, 430, 18);
        official.Text = "\u516c\u5171\u670d\u52a1\u5668\uff1a" + OfficialHost;
        address.SetBounds(15, 62, 430, 26);
        search.SetBounds(165, 105, 110, 28);
        search.Text = "\u641c\u7d22\u5c40\u57df\u7f51";
        create.SetBounds(15, 105, 140, 28);
        create.Text = "\u521b\u5efa\u5c40\u57df\u7f51\u623f\u95f4";
        join.SetBounds(320, 105, 125, 28);
        join.Text = "\u52a0\u5165\u623f\u95f4";
        Controls.AddRange(new Control[] { status, official, address, create, search, join });
        AcceptButton = join;
        address.Items.Add(OfficialHost);
        address.Text = OfficialHost;
        if (File.Exists(ServerFile))
        {
            var configured = File.ReadAllText(ServerFile).Trim();
            if (configured.Length > 0)
                address.Text = configured;
        }
        Shown += async (sender, args) =>
        {
            await FindAndShow(true);
            await CheckForUpdate();
        };
        search.Click += async (sender, args) => await FindAndShow(false);
        join.Click += (sender, args) => JoinSelected();
        create.Click += (sender, args) => ToggleRoom();
        FormClosing += (sender, args) => StopRoom();
    }

    private async Task FindAndShow(bool autoJoin)
    {
        search.Enabled = false;
        status.Text = "\u6b63\u5728\u67e5\u627e\u5c40\u57df\u7f51\u623f\u95f4\u2026";
        var rooms = await Task.Run(() => FindRooms());
        if (IsDisposed)
            return;
        var manualAddress = address.Text;
        address.Items.Clear();
        address.Items.Add(OfficialHost);
        foreach (var room in rooms)
            address.Items.Add(room);
        address.Text = manualAddress.Length > 0 ? manualAddress : OfficialHost;
        search.Enabled = true;
        status.Text = rooms.Count > 0
            ? "\u53d1\u73b0\u5c40\u57df\u7f51\u623f\u95f4\uff1b\u4e5f\u53ef\u76f4\u63a5\u52a0\u5165\u516c\u5171\u670d\u52a1\u5668\u3002"
            : "\u672a\u641c\u5230\u5c40\u57df\u7f51\u623f\u95f4\u3002\u53ef\u76f4\u63a5\u52a0\u5165\u516c\u5171\u670d\u52a1\u5668\uff0c\u6216\u70b9\u300c\u521b\u5efa\u5c40\u57df\u7f51\u623f\u95f4\u300d\u3002";
        if (autoJoin && rooms.Count == 1 && manualAddress.Length == 0)
            JoinSelected();
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

    private void JoinSelected()
    {
        string host;
        if (!TryHost(address.Text, out host))
        {
            status.Text = "\u8bf7\u8f93\u5165\u623f\u4e3b\u7684 IPv4 \u5730\u5740\u6216\u9080\u8bf7\u94fe\u63a5\u3002";
            return;
        }
        if (StartGame(host, profile))
            Close();
    }

    private sealed class UpdateInfo
    {
        public string Version;
        public string Url;
        public string Sha256;
    }

    private static readonly string VersionFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "version.txt");
    private const string UpdateUrl = "http://198.51.100.10:8080/update/latest.json";

    private async Task CheckForUpdate()
    {
        UpdateInfo latest;
        try
        {
            latest = await Task.Run(() => FetchUpdate());
        }
        catch
        {
            return;
        }
        if (latest == null || !IsNewer(latest.Version, LocalVersion()))
            return;
        if (MessageBox.Show(
                "发现新版本 v" + latest.Version + "（当前 v" + LocalVersion() + "）。\n是否现在从 ECS 下载并自动更新？",
                "Mini Battlegrounds", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
            return;

        Cursor.Current = Cursors.WaitCursor;
        string error = null;
        try
        {
            await Task.Run(() => ApplyUpdate(latest));
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }
        finally
        {
            Cursor.Current = Cursors.Default;
        }
        if (error != null)
        {
            MessageBox.Show("自动更新失败：\n" + error, "Mini Battlegrounds");
            return;
        }
        MessageBox.Show("更新完成，正在启动新版本。", "Mini Battlegrounds");
        Close();
        Process.Start(new ProcessStartInfo(Application.ExecutablePath)
        {
            WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
        });
    }

    private static UpdateInfo FetchUpdate()
    {
        using (var client = new WebClient())
        {
            client.Encoding = Encoding.UTF8;
            var json = client.DownloadString(UpdateUrl);
            return new UpdateInfo
            {
                Version = JsonValue(json, "version"),
                Url = JsonValue(json, "url"),
                Sha256 = JsonValue(json, "sha256"),
            };
        }
    }

    private static void ApplyUpdate(UpdateInfo latest)
    {
        if (string.IsNullOrWhiteSpace(latest.Url))
            throw new InvalidOperationException("更新地址为空。");
        var temporary = Path.Combine(Path.GetTempPath(), "mbg-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var archive = Path.Combine(temporary, "update.zip");
            using (var client = new WebClient())
                client.DownloadFile(latest.Url, archive);
            if (!string.IsNullOrEmpty(latest.Sha256))
            {
                using (var sha = SHA256.Create())
                using (var stream = File.OpenRead(archive))
                {
                    var actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                    if (actual != latest.Sha256.ToLowerInvariant())
                        throw new InvalidDataException("更新包校验失败。");
                }
            }
            var extracted = Path.Combine(temporary, "extract");
            ZipFile.ExtractToDirectory(archive, extracted);
            var newClient = Path.Combine(extracted, "client");
            if (!Directory.Exists(newClient))
                throw new InvalidDataException("更新包缺少 client 目录。");
            CopyDirectory(newClient, Client);
            var root = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var name in new[] { "README.txt", "server.txt", "version.txt" })
            {
                var source = Path.Combine(extracted, name);
                if (File.Exists(source))
                    File.Copy(source, Path.Combine(root, name), true);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(temporary, true);
            }
            catch
            {
            }
        }
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
            var text = File.ReadAllText(VersionFile).Trim();
            return text.Length > 0 ? text : "0.0";
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
            serverProcess = Process.Start(info);
            serverProcess.OutputDataReceived += (sender, args) => { };
            serverProcess.ErrorDataReceived += (sender, args) => { };
            serverProcess.BeginOutputReadLine();
            serverProcess.BeginErrorReadLine();
            if (!WaitForPort(host, 43120, 12000))
            {
                StopRoom();
                MessageBox.Show("服务端没有在 12 秒内启动。请检查防火墙或服务端文件。", "Mini Battlegrounds");
                return;
            }
            address.Text = host;
            status.Text = "局域网房间已创建：" + host;
            create.Text = "关闭房间";
            Process.Start(new ProcessStartInfo(Path.Combine(Client, "CrazyShooter.exe"),
                "-screen-fullscreen 0 -lan-host=" + host + " -lan-profile=slot-1")
            {
                WorkingDirectory = Client
            });
        }
        catch (Exception error)
        {
            StopRoom();
            MessageBox.Show("创建房间失败：\n" + error.Message, "Mini Battlegrounds");
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
        if (serverProcess != null)
        {
            try
            {
                if (!serverProcess.HasExited)
                    serverProcess.Kill();
            }
            catch
            {
            }
            serverProcess = null;
        }
        create.Text = "\u521b\u5efa\u623f\u95f4";
        if (status.Text.StartsWith("\u5c40\u57df\u7f51\u623f\u95f4\u5df2\u521b\u5efa", StringComparison.Ordinal))
            status.Text = "\u623f\u95f4\u5df2\u5173\u95ed";
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

    private static bool StartGame(string host, string profile)
    {
        var executable = Path.Combine(Client, "CrazyShooter.exe");
        if (!File.Exists(executable))
        {
            MessageBox.Show("\u8bf7\u5148\u5c06 ZIP \u5168\u90e8\u89e3\u538b\uff0c\u4fdd\u7559 client \u76ee\u5f55\u3002", "Mini Battlegrounds");
            return false;
        }
        var arguments = "-screen-fullscreen 0 -lan-host=" + host + " -lan-profile=" + profile;
        try
        {
            Process.Start(new ProcessStartInfo(executable, arguments)
            {
                WorkingDirectory = Client
            });
            return true;
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            MessageBox.Show(error.Message, "Mini Battlegrounds");
            return false;
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







