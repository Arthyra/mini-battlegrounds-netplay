using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
internal class TestPeer
{
    private Socket mSocket;
    private Thread mThread;
    private static int SocketReceiveTimeout=1000, SocketSendTimeout=1000, SocketLingerTime=0;
    public int Disconnects, CallbackThread;
    public Socket Adopted { get { return mSocket; } }
    private void StartReceive() { }
    public void Send(byte[] bytes) { mSocket.Send(bytes); }
    public void OnDisconnect() { Disconnects++;CallbackThread=Thread.CurrentThread.ManagedThreadId; }
}
internal sealed class NotCompleted : IAsyncResult
{
    public object AsyncState { get { return null; } }
    public WaitHandle AsyncWaitHandle { get { throw new NotSupportedException(); } }
    public bool CompletedSynchronously { get { return false; } }
    public bool IsCompleted { get { return false; } }
}
internal static class BattleConnectTests
{
    private static int passed;
    private static void Check(bool value,string name){if(!value)throw new Exception(name);Console.WriteLine("PASS "+name);passed++;}
    private static void Pump(TestPeer peer){var limit=Stopwatch.StartNew();while(EcsBattleConnect.IsPending(peer)&&limit.ElapsedMilliseconds<3000){EcsBattleConnect.Pump();Thread.Sleep(1);}}
    public static int Main()
    {
        try
        {
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;
            var peer=new TestPeer();var watch=Stopwatch.StartNew();EcsBattleConnect.Begin(peer,"127.0.0.1",port);
            Check(watch.ElapsedMilliseconds<100,"Begin returns without synchronous socket connect");
            Check(peer.Adopted==null,"proxy socket remains untouched until main-thread Pump");
            Check(EcsBattleConnect.QueueSend(peer,new byte[]{1,2,3}),"join packet queued during connection");
            using(var accepted=listener.AcceptSocket())
            {
                Pump(peer);var data=new byte[3];accepted.ReceiveTimeout=1000;int received=accepted.Receive(data);
                Check(peer.Adopted!=null&&peer.Adopted.Connected&&received==3&&data[2]==3,"main-thread adoption and queued-send completion");
                peer.Adopted.Close();
            }
            var old=new TestPeer();var current=new TestPeer();EcsBattleConnect.Begin(old,"127.0.0.1",port);EcsBattleConnect.Begin(current,"127.0.0.1",port);EcsBattleConnect.Cancel(old);
            Check(EcsBattleConnect.IsPending(current),"stale cancellation cannot cancel replacement attempt");EcsBattleConnect.Cancel(current);
            Check(!EcsBattleConnect.IsPending(current),"current cancellation releases pending attempt");
            var stalled=new TestPeer();EcsBattleConnect.Begin(stalled,"127.0.0.1",port);
            var field=typeof(EcsBattleConnect).GetField("pending",BindingFlags.NonPublic|BindingFlags.Static);var attempt=field.GetValue(null);
            attempt.GetType().GetField("Result").SetValue(attempt,new NotCompleted());
            watch.Restart();EcsBattleConnect.Pump();Check(watch.ElapsedMilliseconds<50&&EcsBattleConnect.IsPending(stalled),"incomplete handshake leaves frame immediately");
            Thread.Sleep(2100);EcsBattleConnect.Pump();
            Check(stalled.Disconnects==1&&stalled.CallbackThread==Thread.CurrentThread.ManagedThreadId&&!EcsBattleConnect.IsPending(stalled),"deadline reports failure once on caller thread");
            listener.Stop();var refused=new TestPeer();EcsBattleConnect.Begin(refused,"127.0.0.1",port);Pump(refused);
            Check(refused.Disconnects==1&&refused.Adopted==null,"refused connection cleans up without adopting socket");
            Console.WriteLine(passed+" passed");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
