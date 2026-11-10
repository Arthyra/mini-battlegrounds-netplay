using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class LanLauncher : Form
{
    private const int DiscoveryPort = 43122;
    private const string ReplyPrefix = "MBG-LAN-ROOM-1 ";
    private static readonly string Client = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "client");
    private static readonly string Settings = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher.ini");
    private static readonly string ServerFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "server.txt");
    private readonly ComboBox address = new ComboBox();
    private readonly Button join = new Button();
    private readonly Button search = new Button();
    private readonly Label status = new Label();
    private readonly Label nickLabel = new Label();
    private readonly TextBox nickname = new TextBox();
    private readonly string profile;

    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length == 2 && args[0] == "--host")
        {
            string host;
            string savedProfile, savedNickname;
            LoadSettings(out savedProfile, out savedNickname);
            if (TryHost(args[1], out host))
                StartGame(host, savedProfile, savedNickname);
            else
                MessageBox.Show("\u8bf7\u8f93\u5165\u6709\u6548\u7684 IPv4 \u5730\u5740\u3002");
            return;
        }
        Application.Run(new LanLauncher());
    }

    private LanLauncher()
    {
        string savedNickname;
        LoadSettings(out profile, out savedNickname);
        Text = "Mini Battlegrounds LAN";
        ClientSize = new Size(460, 185);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        status.SetBounds(15, 15, 430, 40);
        nickLabel.SetBounds(15, 60, 55, 26);
        nickLabel.Text = "\u6635\u79f0";
        nickname.SetBounds(75, 60, 370, 26);
        nickname.MaxLength = 16;
        nickname.Text = savedNickname;
        address.SetBounds(15, 95, 430, 26);
        search.SetBounds(15, 133, 110, 28);
        search.Text = "\u641c\u7d22\u5c40\u57df\u7f51";
        join.SetBounds(320, 133, 125, 28);
        join.Text = "\u52a0\u5165\u623f\u95f4";
        Controls.AddRange(new Control[] { status, nickLabel, nickname, address, search, join });
        AcceptButton = join;
        if (File.Exists(ServerFile))
        {
            var configured = File.ReadAllText(ServerFile).Trim();
            if (configured.Length > 0)
                address.Text = configured;
        }
        Shown += async (sender, args) => await FindAndShow(true);
        search.Click += async (sender, args) => await FindAndShow(false);
        join.Click += (sender, args) => JoinSelected();
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
        foreach (var room in rooms)
            address.Items.Add(room);
        if (manualAddress.Length > 0)
            address.Text = manualAddress;
        else if (rooms.Count > 0)
            address.SelectedIndex = 0;
        search.Enabled = true;
        status.Text = rooms.Count > 0
            ? "\u9009\u62e9\u623f\u95f4\uff0c\u6216\u7c98\u8d34\u623f\u4e3b IP / \u9080\u8bf7\u94fe\u63a5\u3002"
            : "\u672a\u641c\u5230\u623f\u95f4\u3002\u8bf7\u8f93\u5165\u623f\u4e3b IP \u6216\u7c98\u8d34\u9080\u8bf7\u94fe\u63a5\u3002";
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
        nickname.Text = nickname.Text.Trim();
        SaveSettings();
        if (StartGame(host, profile, nickname.Text))
            Close();
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

    private static bool StartGame(string host, string profile, string nickname)
    {
        var executable = Path.Combine(Client, "CrazyShooter.exe");
        if (!File.Exists(executable))
        {
            MessageBox.Show("\u8bf7\u5148\u5c06 ZIP \u5168\u90e8\u89e3\u538b\uff0c\u4fdd\u7559 client \u76ee\u5f55\u3002", "Mini Battlegrounds");
            return false;
        }
        var arguments = "-screen-fullscreen 0 -lan-host=" + host + " -lan-profile=" + profile;
        var safeNickname = (nickname ?? "").Replace("\"", "").Trim();
        if (safeNickname.Length > 0)
            arguments += " -lan-name=\"" + safeNickname + "\"";
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

    private static void LoadSettings(out string profile, out string nickname)
    {
        profile = null;
        nickname = null;
        try
        {
            if (File.Exists(Settings))
            {
                foreach (var line in File.ReadAllLines(Settings, Encoding.UTF8))
                {
                    var split = line.IndexOf('=');
                    if (split <= 0)
                        continue;
                    var key = line.Substring(0, split).Trim();
                    var value = line.Substring(split + 1).Trim();
                    if (key == "profile")
                        profile = value;
                    else if (key == "nickname")
                        nickname = value;
                }
            }
        }
        catch (IOException)
        {
        }
        if (string.IsNullOrWhiteSpace(profile))
            profile = DefaultProfile();
        if (string.IsNullOrWhiteSpace(nickname))
            nickname = "\u73a9\u5bb6" + new Random().Next(1000, 10000);
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

    private void SaveSettings()
    {
        try
        {
            File.WriteAllText(Settings,
                "profile=" + profile + Environment.NewLine +
                "nickname=" + nickname.Text.Trim() + Environment.NewLine,
                new UTF8Encoding(true));
        }
        catch (IOException)
        {
        }
    }
}
