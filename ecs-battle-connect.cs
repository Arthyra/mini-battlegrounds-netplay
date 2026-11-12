using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;

// Only the socket's asynchronous operation runs outside Unity. State changes,
// proxy adoption, Send and OnDisconnect are performed by Pump on Main.Update.
public static class EcsBattleConnect
{
    private sealed class Attempt
    {
        public object Peer;
        public Socket Socket;
        public IAsyncResult Result;
        public Exception Error;
        public readonly Stopwatch Timer = Stopwatch.StartNew();
        public readonly List<byte[]> Packets = new List<byte[]>();
    }

    private static Attempt pending;
    private static Attempt awaitingJoin;

    private static FieldInfo Field(object peer, string name)
    {
        for (Type type = peer.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            if (field != null) return field;
        }
        throw new MissingFieldException(name);
    }

    private static MethodInfo Method(object peer, string name)
    {
        for (Type type = peer.GetType(); type != null; type = type.BaseType)
        {
            var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (method != null) return method;
        }
        throw new MissingMethodException(name);
    }

    public static bool Begin(object peer, string host, int port)
    {
        if (pending != null) Cancel(pending.Peer);
        if (awaitingJoin != null) Cancel(awaitingJoin.Peer);
        var attempt = new Attempt { Peer = peer };
        pending = attempt;
        EcsDiagnostics.Trace("connect.begin", port);
        try
        {
            var address = IPAddress.Parse(host);
            attempt.Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            attempt.Result = attempt.Socket.BeginConnect(new IPEndPoint(address, port), null, null);
        }
        catch (Exception error) { attempt.Error = error; }
        // Callers may now queue their join packet, while Update keeps running.
        return true;
    }

    public static bool IsPending(object peer)
    {
        return pending != null && Object.ReferenceEquals(pending.Peer, peer);
    }

    public static bool QueueSend(object peer, byte[] data)
    {
        var attempt = pending;
        if (attempt == null || !Object.ReferenceEquals(attempt.Peer, peer)) return false;
        if (attempt.Packets.Count >= 16 || data.Length > 65535)
            attempt.Error = new InvalidOperationException("Too many pending battle packets");
        else attempt.Packets.Add((byte[])data.Clone());
        return true;
    }

    public static void Cancel(object peer)
    {
        var previous = pending;
        if (previous != null && Object.ReferenceEquals(previous.Peer, peer) &&
            Object.ReferenceEquals(Interlocked.CompareExchange(ref pending, null, previous), previous))
        {
            if (previous.Socket != null) previous.Socket.Close();
            EcsDiagnostics.Trace("connect.cancel", 0);
        }
        previous = awaitingJoin;
        if (previous != null && Object.ReferenceEquals(previous.Peer, peer) &&
            Object.ReferenceEquals(Interlocked.CompareExchange(ref awaitingJoin, null, previous), previous))
            previous.Socket.Close();
    }

    public static void JoinReceived(object peer)
    {
        var attempt = awaitingJoin;
        if (attempt != null && Object.ReferenceEquals(attempt.Peer, peer))
            Interlocked.CompareExchange(ref awaitingJoin, null, attempt);
    }

    public static void Pump()
    {
        var joined = awaitingJoin;
        if (joined != null && joined.Timer.ElapsedMilliseconds >= 8000 &&
            Object.ReferenceEquals(Interlocked.CompareExchange(ref awaitingJoin, null, joined), joined))
        {
            EcsDiagnostics.Trace("join.timeout_ms", (int)joined.Timer.ElapsedMilliseconds);
            Method(joined.Peer, "Close").Invoke(joined.Peer, null);
            Method(joined.Peer, "OnDisconnect").Invoke(joined.Peer, null);
        }
        var attempt = pending;
        if (attempt == null) return;
        if (attempt.Error == null && !attempt.Result.IsCompleted && attempt.Timer.ElapsedMilliseconds < 2000)
            return;
        // Close may run on a receive thread. It must not clear a replacement
        // attempt between identity check and assignment.
        if (!Object.ReferenceEquals(Interlocked.CompareExchange(ref pending, null, attempt), attempt)) return;
        try
        {
            if (attempt.Error != null) throw attempt.Error;
            if (!attempt.Result.IsCompleted) throw new SocketException((int)SocketError.TimedOut);
            attempt.Socket.EndConnect(attempt.Result);
            attempt.Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            attempt.Socket.NoDelay = true;
            attempt.Socket.ReceiveTimeout = (int)Field(attempt.Peer, "SocketReceiveTimeout").GetValue(null);
            attempt.Socket.SendTimeout = (int)Field(attempt.Peer, "SocketSendTimeout").GetValue(null);
            attempt.Socket.LingerState = new LingerOption(true,
                (int)Field(attempt.Peer, "SocketLingerTime").GetValue(null));
            Field(attempt.Peer, "mSocket").SetValue(attempt.Peer, attempt.Socket);
            var receive = Method(attempt.Peer, "StartReceive");
            var thread = new Thread((ThreadStart)Delegate.CreateDelegate(typeof(ThreadStart), attempt.Peer, receive));
            thread.IsBackground = true;
            Field(attempt.Peer, "mThread").SetValue(attempt.Peer, thread);
            thread.Start();
            var send = Method(attempt.Peer, "Send");
            foreach (var packet in attempt.Packets) send.Invoke(attempt.Peer, new object[] { packet });
            EcsDiagnostics.Trace("connect.complete_ms", (int)attempt.Timer.ElapsedMilliseconds);
            attempt.Timer.Reset();
            attempt.Timer.Start();
            awaitingJoin = attempt;
        }
        catch (Exception error)
        {
            if (attempt.Socket != null) attempt.Socket.Close();
            Cancel(attempt.Peer);
            EcsDiagnostics.Trace("connect.failed." + error.GetType().Name, (int)attempt.Timer.ElapsedMilliseconds);
            Method(attempt.Peer, "OnDisconnect").Invoke(attempt.Peer, null);
        }
    }
}
