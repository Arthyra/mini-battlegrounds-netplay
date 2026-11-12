using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.Emit;
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
        var builder = AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName("FinProbe"), AssemblyBuilderAccess.Run)
            .DefineDynamicModule("FinProbe").DefineType("FinPeer", TypeAttributes.Public, type);
        foreach (string name in new[] { "OnDisconnect", "OnReceive" })
        {
            var original = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var counter = builder.DefineField(name + "Count", typeof(int), FieldAttributes.Public);
            var method = builder.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Virtual,
                typeof(void), name == "OnReceive" ? new[] { typeof(byte[]) } : Type.EmptyTypes);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Dup); il.Emit(OpCodes.Ldfld, counter);
            il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stfld, counter); il.Emit(OpCodes.Ret);
            builder.DefineMethodOverride(method, original);
        }
        var probeType = builder.CreateType();
        object peer = Activator.CreateInstance(probeType);
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
        bool truncated = args.Length > 1 && args[1] == "truncated";
        server.Send(truncated ? new byte[] { 4 } : new byte[] { 4, 0, 1, 0 });
        server.Shutdown(SocketShutdown.Send); // FIN, not RST or process termination.
        bool returned = thread.Join(1000);
        bool cleared = socketField.GetValue(peer) == null;
        var buffer = type.GetField("mBuffer", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(peer);
        int bytes = (int)buffer.GetType().GetMethod("get_Size").Invoke(buffer, null);
        int earlyCallbacks = (int)probeType.GetField("OnDisconnectCount").GetValue(peer);
        type.GetMethod("Update").Invoke(peer, new object[] { 0f });
        int callbacks = (int)probeType.GetField("OnDisconnectCount").GetValue(peer);
        int packets = (int)probeType.GetField("OnReceiveCount").GetValue(peer);
        Console.WriteLine("receive_returned=" + returned + " socket_cleared=" + cleared + " queued_bytes=" + bytes +
            " early_callbacks=" + earlyCallbacks + " dispatched_packets=" + packets + " final_callbacks=" + callbacks);
        type.GetMethod("Close").Invoke(peer, null);
        thread.Join(1000);
        server.Close(); listener.Stop();
        if (receiveError != null) { Console.Error.WriteLine(receiveError); return 2; }
        if (!returned || !cleared || bytes != (truncated ? 1 : 4) || earlyCallbacks != 0 || packets != (truncated ? 0 : 1) || callbacks != 1)
        { Console.WriteLine("FAIL graceful EOF cleanup / final packet preservation"); return 1; }
        Console.WriteLine("PASS graceful EOF cleanup / final packet preservation"); return 0;
    }
}
