using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;

// Runs the actual patched TcpPeer receive loop, with no Unity scene or mocks.
internal static class TcpFinTest
{
    private static int Main(string[] args)
    {
        string managed = Path.GetFullPath(args[0]);
        AppDomain.CurrentDomain.AssemblyResolve += (sender, request) => {
            string path = Path.Combine(managed, new AssemblyName(request.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        var assembly = Assembly.LoadFrom(Path.Combine(managed, "Assembly-CSharp.dll"));
        var type = assembly.GetType("CS.TcpPeer", true);
        object peer = Activator.CreateInstance(type, true);
        var socketField = type.GetField("mSocket", BindingFlags.Instance | BindingFlags.NonPublic);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Connect(listener.LocalEndpoint);
        var server = listener.AcceptSocket();
        socketField.SetValue(peer, socket);
        Exception receiveError = null;
        var receive = type.GetMethod("StartReceive", BindingFlags.Instance | BindingFlags.NonPublic);
        var thread = new Thread(() => {
            try { receive.Invoke(peer, null); }
            catch (Exception error) { receiveError = error; }
        });
        thread.IsBackground = true;
        thread.Start();
        server.Send(new byte[] { 4, 0, 1, 0 });
        server.Shutdown(SocketShutdown.Send); // FIN, not RST or process termination.
        bool returned = thread.Join(1000);
        bool cleared = socketField.GetValue(peer) == null;
        var buffer = type.GetField("mBuffer", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(peer);
        int bytes = (int)buffer.GetType().GetMethod("get_Size").Invoke(buffer, null);
        Console.WriteLine("receive_returned=" + returned + " socket_cleared=" + cleared + " queued_bytes=" + bytes);
        type.GetMethod("Close").Invoke(peer, null);
        thread.Join(1000);
        server.Close(); listener.Stop();
        if (receiveError != null) { Console.Error.WriteLine(receiveError); return 2; }
        if (!returned || !cleared || bytes != 4) { Console.WriteLine("FAIL graceful EOF cleanup / final packet preservation"); return 1; }
        Console.WriteLine("PASS graceful EOF cleanup / final packet preservation"); return 0;
    }
}
