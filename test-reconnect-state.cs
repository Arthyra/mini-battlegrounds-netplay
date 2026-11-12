using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Executes candidate reconnect IL outside Unity. The sole runtime substitution
// is Unity's native realtime clock -> fixed 1000f; lifecycle methods are intact.
internal static class ReconnectStateTests
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int failures;
    private static void Check(string name, Action check)
    {
        try { check(); Console.WriteLine("PASS " + name); }
        catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error.GetBaseException()); }
    }
    private static void Require(bool condition) { if (!condition) throw new Exception("State assertion failed"); }
    private static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception error) { Console.WriteLine("Fixture error: " + error.GetType().FullName + " " + error.Message); return 2; }
    }
    private static int Run(string[] args)
    {
        string managed = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
        var resolver = new DefaultAssemblyResolver(); resolver.AddSearchDirectory(managed);
        using (var source = AssemblyDefinition.ReadAssembly(Path.Combine(managed, "Assembly-CSharp.dll"), new ReaderParameters { AssemblyResolver = resolver }))
        {
            foreach (var type in source.MainModule.Types)
                foreach (var method in type.Methods.Where(m => m.HasBody))
                    foreach (var instruction in method.Body.Instructions)
                    {
                        var target = instruction.Operand as MethodReference;
                        if (instruction.OpCode == OpCodes.Call && target != null && target.DeclaringType.FullName == "UnityEngine.Time" && target.Name == "get_realtimeSinceStartup")
                        { instruction.OpCode = OpCodes.Ldc_R4; instruction.Operand = 1000f; }
                    }
            source.Write(output);
        }
        AppDomain.CurrentDomain.AssemblyResolve += (sender, request) => {
            string path = Path.Combine(managed, new AssemblyName(request.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        Console.WriteLine("Loading clock-substituted fixture");
        var assembly = Assembly.LoadFile(output);
        var battle = assembly.GetType("CS.BattleTcpServerProxy", true);
        var managerType = assembly.GetType("CS.ServerProxyManager", true);
        var manager = managerType.GetMethod("get_Instance").Invoke(null, null);
        var current = managerType.GetField("mBattleTcpServerProxy", BindingFlags.Instance | BindingFlags.NonPublic);
        Action<string, object> set = (name, value) => battle.GetField(name, Static).SetValue(null, value);
        Func<string, object> get = name => battle.GetField(name, Static).GetValue(null);
        Check("heartbeat after teardown tolerates absent proxy", () => {
            current.SetValue(manager, null); set("mLanTcpActive", true); set("mLanLastTcpRecv", 0f);
            battle.GetMethod("TickLanTcpHeartbeat").Invoke(null, null);
            Require(!(bool)get("mLanTcpActive"));
        });
        Check("failed asynchronous attempt preserves accumulated retry count", () => {
            var peer = Activator.CreateInstance(battle, true); current.SetValue(manager, peer);
            set("mLanReconnectPending", false); set("mLanReconnectGivingUp", false); set("mLanReconnectInProgress", false);
            set("mLanReconnectAttempts", 7);
            battle.GetMethod("OnDisconnect", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Invoke(peer, null);
            Require((int)get("mLanReconnectAttempts") == 7 && (bool)get("mLanReconnectPending"));
        });
        Check("retry cap stops before opening another connection", () => {
            set("mLanReconnectPending", true); set("mLanReconnectAttempts", 20); set("mLanReconnectRetryAt", 0f);
            battle.GetMethod("TickLanReconnect").Invoke(null, null);
            Require((int)get("mLanReconnectAttempts") == 20 && !(bool)get("mLanReconnectPending") && (bool)get("mLanReconnectGivingUp"));
        });
        return failures == 0 ? 0 : 1;
    }
}
