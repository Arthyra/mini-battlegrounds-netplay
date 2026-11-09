using System;
using System.Linq;
using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class PatchCombat
{
    private const int ReportHit = 201;
    private const int AutoRevive = 202;

    private static MethodDefinition Method(TypeDefinition type, string name, params string[] parameters)
    {
        return type.Methods.Single(m => m.Name == name &&
            m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameters));
    }

    private static void Before(ILProcessor il, Instruction anchor, params Instruction[] instructions)
    {
        foreach (var instruction in instructions)
            il.InsertBefore(anchor, instruction);
    }

    private static void InstallHitReport(ModuleDefinition module, TypeDefinition battle, TypeDefinition unit)
    {
        var bullet = module.GetType("CS.Bullet");
        var packet = module.GetType("CS.NetPacket");
        var proxy = module.GetType("CS.ServerProxyManager");
        var report = new MethodDefinition("ReportLanBulletHit", MethodAttributes.Private, module.TypeSystem.Void);
        report.Parameters.Add(new ParameterDefinition("bullet", ParameterAttributes.None, bullet));
        report.Parameters.Add(new ParameterDefinition("target", ParameterAttributes.None, unit));
        battle.Methods.Add(report);
        var il = report.Body.GetILProcessor();
        var ret = il.Create(OpCodes.Ret);
        var networkPacket = new VariableDefinition(packet);
        report.Body.Variables.Add(networkPacket);
        report.Body.InitLocals = true;
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, Method(bullet, "GetShooter"));
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, Method(battle, "GetLocalUnit"));
        il.Emit(OpCodes.Bne_Un, ret);

        il.Emit(OpCodes.Newobj, Method(packet, ".ctor"));
        il.Emit(OpCodes.Stloc, networkPacket);
        il.Emit(OpCodes.Ldloc, networkPacket);
        il.Emit(OpCodes.Ldc_I4, ReportHit);
        il.Emit(OpCodes.Callvirt, Method(packet, "Push", "System.UInt16"));
        il.Emit(OpCodes.Ldloc, networkPacket);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, Method(bullet, "GetID"));
        il.Emit(OpCodes.Callvirt, Method(packet, "Push", "System.UInt32"));
        il.Emit(OpCodes.Ldloc, networkPacket);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, Method(unit, "GetID"));
        il.Emit(OpCodes.Callvirt, Method(packet, "Push", "System.UInt32"));
        il.Emit(OpCodes.Ldloc, networkPacket);
        il.Emit(OpCodes.Callvirt, Method(packet, "UpdateHeader"));
        il.Emit(OpCodes.Call, Method(proxy, "get_Instance"));
        il.Emit(OpCodes.Callvirt, Method(proxy, "GetBattleTcpServerProxy"));
        il.Emit(OpCodes.Ldloc, networkPacket);
        il.Emit(OpCodes.Callvirt, Method(module.GetType("CS.NetBuffer"), "get_Base"));
        il.Emit(OpCodes.Callvirt, Method(module.GetType("CS.TcpPeer"), "Send", "System.Byte[]"));
        il.Append(ret);

        var collision = Method(battle, "UpdateBulletCollision", bullet.FullName);
        var unitCast = collision.Body.Instructions.Last(i => i.OpCode == OpCodes.Isinst &&
            ((TypeReference)i.Operand).FullName == unit.FullName);
        var store = unitCast.Next;
        var target = (VariableDefinition)store.Operand;
        if (store.OpCode != OpCodes.Stloc_S || target.VariableType.FullName != unit.FullName ||
            store.Next.OpCode != OpCodes.Br)
            throw new InvalidOperationException("Unexpected player collision branch.");
        var handler = collision.Body.GetILProcessor();
        Before(handler, store.Next, handler.Create(OpCodes.Ldarg_0), handler.Create(OpCodes.Ldarg_1),
            handler.Create(OpCodes.Ldloc, target), handler.Create(OpCodes.Call, report));
    }

    private static void InstallRevive(ModuleDefinition module, TypeDefinition battle, TypeDefinition unit)
    {
        var damage = Method(unit, "TakeDamage", "System.Single", "System.Single", "System.Boolean");
        var teamCheck = damage.Body.Instructions.Single(i => i.OpCode == OpCodes.Callvirt &&
            ((MethodReference)i.Operand).Name == "GetTeamAlives");
        var compare = teamCheck.Next.Next.Next;
        var deathBranch = compare.Next;
        if (compare.OpCode != OpCodes.Ldc_I4_1 || deathBranch.OpCode != OpCodes.Ble)
            throw new InvalidOperationException("Unexpected knockdown condition.");
        // The following comparison now sees team count > -1 for every living player.
        compare.OpCode = OpCodes.Ldc_I4_M1;

        var dispatcher = Method(battle, "HandleMessages", "CS.NetMessage", "System.IO.BinaryReader");
        var readerType = dispatcher.Parameters[1].ParameterType;
        var originalHit = Method(battle, "HandleBulletHitUnit", readerType.FullName);
        var readUInt = originalHit.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
            .First(m => m.Name == "ReadUInt32");
        var readFloat = originalHit.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
            .First(m => m.Name == "ReadSingle");

        var revive = new MethodDefinition("HandleLanAutoRevive", MethodAttributes.Private, module.TypeSystem.Void);
        revive.Parameters.Add(new ParameterDefinition("reader", ParameterAttributes.None, readerType));
        battle.Methods.Add(revive);
        var il = revive.Body.GetILProcessor();
        var target = new VariableDefinition(unit);
        revive.Body.Variables.Add(target);
        revive.Body.InitLocals = true;
        var ret = il.Create(OpCodes.Ret);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, readUInt);
        il.Emit(OpCodes.Call, Method(battle, "GetUnit", "System.UInt32"));
        il.Emit(OpCodes.Stloc, target);
        il.Emit(OpCodes.Ldloc, target);
        il.Emit(OpCodes.Brfalse, ret);
        il.Emit(OpCodes.Ldloc, target);
        il.Emit(OpCodes.Callvirt, Method(unit, "RevivedEnd"));
        il.Emit(OpCodes.Ldloc, target);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, readFloat);
        il.Emit(OpCodes.Callvirt, Method(unit, "SetHealth", "System.Single"));
        il.Emit(OpCodes.Ldloc, target);
        il.Emit(OpCodes.Callvirt, Method(unit, "ClearItems"));
        il.Emit(OpCodes.Ldloc, target);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, Method(unit, "ReadItems", readerType.FullName));
        il.Emit(OpCodes.Ldloc, target);
        il.Emit(OpCodes.Call, Method(unit, "HandleStorageChanged"));
        il.Emit(OpCodes.Ldloc, target);
        il.Emit(OpCodes.Call, Method(unit, "HandleWeaponsChanged"));
        il.Emit(OpCodes.Ldloc, target);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Callvirt, Method(unit, "SwitchWeapon", "System.Boolean", "System.UInt32"));
        il.Append(ret);

        var handler = dispatcher.Body.GetILProcessor();
        var original = dispatcher.Body.Instructions[0];
        Before(handler, original, handler.Create(OpCodes.Ldarg_1), handler.Create(OpCodes.Ldc_I4, AutoRevive),
            handler.Create(OpCodes.Bne_Un, original), handler.Create(OpCodes.Ldarg_0),
            handler.Create(OpCodes.Ldarg_2), handler.Create(OpCodes.Call, revive), handler.Create(OpCodes.Ret));
    }

    public static void Main(string[] args)
    {
        if (args.Length != 2)
            throw new ArgumentException("Pass the input and output Assembly-CSharp.dll paths.");
        using (var resolver = new DefaultAssemblyResolver())
        {
            resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0])));
            using (var module = ModuleDefinition.ReadModule(args[0], new ReaderParameters { AssemblyResolver = resolver }))
            {
                var battle = module.GetType("CS.Battle");
                var unit = module.GetType("CS.Unit");
                InstallHitReport(module, battle, unit);
                InstallRevive(module, battle, unit);
                module.Write(args[1]);
            }
        }
    }
}
