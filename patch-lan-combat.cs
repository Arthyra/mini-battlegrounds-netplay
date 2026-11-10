using System;
using System.Linq;
using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class PatchCombat
{
    private const int ReportHit = 201;
    private const int AutoRevive = 202;
    private const int MeleeSwing = 203;

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
        report.Parameters.Add(new ParameterDefinition("targetId", ParameterAttributes.None, module.TypeSystem.UInt32));
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
            handler.Create(OpCodes.Ldloc, target), handler.Create(OpCodes.Callvirt, Method(unit, "GetID")),
            handler.Create(OpCodes.Call, report));

        var vehicle = module.GetType("CS.Vehicle");
        var vehicleCast = collision.Body.Instructions.Last(i => i.OpCode == OpCodes.Isinst &&
            ((TypeReference)i.Operand).FullName == vehicle.FullName);
        var vehicleStore = vehicleCast.Next;
        var vehicleTarget = (VariableDefinition)vehicleStore.Operand;
        if (vehicleStore.OpCode != OpCodes.Stloc_S || vehicleTarget.VariableType.FullName != vehicle.FullName)
            throw new InvalidOperationException("Unexpected vehicle collision branch.");
        Before(handler, vehicleStore.Next, handler.Create(OpCodes.Ldarg_0), handler.Create(OpCodes.Ldarg_1),
            handler.Create(OpCodes.Ldloc, vehicleTarget), handler.Create(OpCodes.Callvirt, Method(vehicle, "GetID")),
            handler.Create(OpCodes.Call, report));
    }

    private static void InstallReviveBar(ModuleDefinition module, TypeDefinition unit)
    {
        var knockdown = Method(unit, "Knockdown");
        var il = knockdown.Body.GetILProcessor();
        var ret = knockdown.Body.Instructions.Single(i => i.OpCode == OpCodes.Ret);
        // The unit is its own revive target. This advances the native countdown
        // without entering the teammate-rescue casting state.
        Before(il, ret, il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldarg_0),
            il.Create(OpCodes.Call, Method(unit, "RevivedBegin", unit.FullName)));

        var bar = module.GetType("CS.GUIEntityReviveBar");
        var refresh = Method(bar, "RefreshProgress");
        var duration = refresh.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldsfld &&
            ((FieldReference)i.Operand).Name == "REVIVE_TIME");
        duration.OpCode = OpCodes.Ldc_R4;
        duration.Operand = 3.0f;

        // The native teammate timer can stop advancing while the local unit is down.
        var startedAt = new FieldDefinition("mLanStartedAt", FieldAttributes.Private, module.TypeSystem.Single);
        bar.Fields.Add(startedAt);
        var engine = module.AssemblyResolver.Resolve(module.AssemblyReferences.Single(a => a.Name == "UnityEngine"));
        var clock = module.ImportReference(Method(engine.MainModule.GetType("UnityEngine.Time"), "get_realtimeSinceStartup"));
        var progressIl = refresh.Body.GetILProcessor();
        var elapsed = refresh.Body.Variables[0];
        // Reuse the original fill and countdown rendering, with elapsed time from the local clock.
        while (refresh.Body.Instructions[0] != duration)
            progressIl.Remove(refresh.Body.Instructions[0]);
        Before(progressIl, duration, progressIl.Create(OpCodes.Call, clock),
            progressIl.Create(OpCodes.Ldarg_0), progressIl.Create(OpCodes.Ldfld, startedAt),
            progressIl.Create(OpCodes.Sub), progressIl.Create(OpCodes.Stloc, elapsed));

        var start = Method(bar, "Start", unit.FullName);
        var setText = start.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
            .First(m => m.Name == "set_text");
        start.Body.Instructions.Clear();
        var startBar = start.Body.GetILProcessor();
        startBar.Emit(OpCodes.Ldarg_0);
        startBar.Emit(OpCodes.Call, clock);
        startBar.Emit(OpCodes.Stfld, startedAt);
        startBar.Emit(OpCodes.Ldarg_0);
        startBar.Emit(OpCodes.Ldarg_1);
        startBar.Emit(OpCodes.Stfld, bar.Fields.Single(f => f.Name == "mUnit"));
        startBar.Emit(OpCodes.Ldarg_0);
        startBar.Emit(OpCodes.Ldfld, bar.Fields.Single(f => f.Name == "mName"));
        startBar.Emit(OpCodes.Ldstr, "自动复活");
        startBar.Emit(OpCodes.Callvirt, setText);
        startBar.Emit(OpCodes.Ldarg_0);
        startBar.Emit(OpCodes.Call, refresh);
        startBar.Emit(OpCodes.Ret);
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

        foreach (var respawn in new[] { true, false })
        {
            var revive = new MethodDefinition(respawn ? "HandleLanAutoRevive" : "HandleLanPickup", MethodAttributes.Private, module.TypeSystem.Void);
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
            var selected = new VariableDefinition(module.TypeSystem.UInt32);
            revive.Body.Variables.Add(selected);
            if (respawn)
            {
                il.Emit(OpCodes.Ldloc, target);
                il.Emit(OpCodes.Callvirt, Method(unit, "RevivedEnd"));
                il.Emit(OpCodes.Ldloc, target);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Callvirt, readFloat);
                il.Emit(OpCodes.Callvirt, Method(unit, "SetHealth", "System.Single"));
            }
            else
            {
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Callvirt, readUInt);
                il.Emit(OpCodes.Stloc, selected);
            }
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
            il.Emit(OpCodes.Call, Method(unit, "HandleEquipmentsChanged"));
            il.Emit(OpCodes.Ldloc, target);
            il.Emit(respawn ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
            if (respawn)
                il.Emit(OpCodes.Ldc_I4_0);
            else
                il.Emit(OpCodes.Ldloc, selected);
            il.Emit(OpCodes.Callvirt, Method(unit, "SwitchWeapon", "System.Boolean", "System.UInt32"));
            il.Append(ret);

            Dispatch(dispatcher, respawn ? AutoRevive : 204, revive);
        }
    }

    private static void InstallMeleeSwing(ModuleDefinition module, TypeDefinition battle, TypeDefinition unit)
    {
        var dispatcher = Method(battle, "HandleMessages", "CS.NetMessage", "System.IO.BinaryReader");
        var reader = dispatcher.Parameters[1].ParameterType;
        var readId = Method(battle, "HandleMeleeHitUnit", reader.FullName)
            .Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().First(m => m.Name == "ReadUInt32");
        var swing = new MethodDefinition("HandleLanMeleeSwing", MethodAttributes.Private, module.TypeSystem.Void);
        swing.Parameters.Add(new ParameterDefinition("reader", ParameterAttributes.None, reader));
        battle.Methods.Add(swing);

        var il = swing.Body.GetILProcessor();
        var target = new VariableDefinition(unit);
        swing.Body.Variables.Add(target);
        var ret = il.Create(OpCodes.Ret);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, readId);
        il.Emit(OpCodes.Call, Method(battle, "GetUnit", "System.UInt32"));
        il.Emit(OpCodes.Stloc, target);
        il.Emit(OpCodes.Ldloc, target);
        il.Emit(OpCodes.Brfalse, ret);
        il.Emit(OpCodes.Ldloc, target);
        il.Emit(OpCodes.Callvirt, Method(unit, "GetVisual"));
        il.Emit(OpCodes.Callvirt, Method(module.GetType("CS.UnitVisual"), "HandleMelee"));
        il.Append(ret);

        var handler = dispatcher.Body.GetILProcessor();
        var original = dispatcher.Body.Instructions[0];
        Before(handler, original, handler.Create(OpCodes.Ldarg_1), handler.Create(OpCodes.Ldc_I4, MeleeSwing),
            handler.Create(OpCodes.Bne_Un, original), handler.Create(OpCodes.Ldarg_0),
            handler.Create(OpCodes.Ldarg_2), handler.Create(OpCodes.Call, swing), handler.Create(OpCodes.Ret));
    }

    private static void InstallSniperScope(ModuleDefinition module, TypeDefinition unit)
    {
        var local = module.GetType("CS.LocalController");
        var item = module.GetType("CS.Item");
        var engine = module.AssemblyResolver.Resolve(module.AssemblyReferences.Single(a => a.Name == "UnityEngine")).MainModule;
        var button = module.ImportReference(Method(engine.GetType("UnityEngine.Input"), "GetMouseButton", "System.Int32"));
        var unitField = local.Fields.Single(f => f.Name == "mUnit");
        var bonus = new MethodDefinition("GetLanScopeSightBonus", MethodAttributes.Private, module.TypeSystem.Single);
        local.Methods.Add(bonus);
        var il = bonus.Body.GetILProcessor();
        var normal = il.Create(OpCodes.Ldc_R4, 0f);
        var weapon = new VariableDefinition(item);
        bonus.Body.Variables.Add(weapon);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, Method(local, "IsMuted"));
        il.Emit(OpCodes.Brtrue, normal);
        foreach (var state in new[] { "IsDown", "IsMounted" })
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, unitField);
            il.Emit(OpCodes.Callvirt, Method(unit, state));
            il.Emit(OpCodes.Brtrue, normal);
        }
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Call, button);
        il.Emit(OpCodes.Brfalse, normal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, unitField);
        il.Emit(OpCodes.Callvirt, Method(unit, "GetWeapon"));
        il.Emit(OpCodes.Isinst, module.GetType("CS.ItemGun"));
        il.Emit(OpCodes.Stloc, weapon);
        il.Emit(OpCodes.Ldloc, weapon);
        il.Emit(OpCodes.Brfalse, normal);
        // APK GunData GSD_SNIPE_SIGHT values, added to the Windows camera distance.
        foreach (var pair in new[] { new[] { 12, 160 }, new[] { 17, 80 }, new[] { 18, 160 } })
        {
            var next = il.Create(OpCodes.Nop);
            il.Emit(OpCodes.Ldloc, weapon);
            il.Emit(OpCodes.Callvirt, Method(item, "GetKind"));
            il.Emit(OpCodes.Ldc_I4, pair[0]);
            il.Emit(OpCodes.Bne_Un, next);
            il.Emit(OpCodes.Ldc_R4, (float)pair[1]);
            il.Emit(OpCodes.Ret);
            il.Append(next);
        }
        il.Append(normal);
        il.Emit(OpCodes.Ret);

        var scopeState = new FieldDefinition("mLanScopeActive", FieldAttributes.Private, module.TypeSystem.Boolean);
        local.Fields.Add(scopeState);
        var send = new MethodDefinition("SendLanScopeState", MethodAttributes.Private, module.TypeSystem.Void);
        send.Parameters.Add(new ParameterDefinition("active", ParameterAttributes.None, module.TypeSystem.Boolean));
        local.Methods.Add(send);
        il = send.Body.GetILProcessor();
        var ret = il.Create(OpCodes.Ret);
        var packet = module.GetType("CS.NetPacket");
        var proxy = module.GetType("CS.ServerProxyManager");
        var data = new VariableDefinition(packet);
        send.Body.Variables.Add(data);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, scopeState);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Beq, ret);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, scopeState);
        il.Emit(OpCodes.Newobj, Method(packet, ".ctor"));
        il.Emit(OpCodes.Stloc, data);
        il.Emit(OpCodes.Ldloc, data);
        il.Emit(OpCodes.Ldc_I4, 206);
        il.Emit(OpCodes.Callvirt, Method(packet, "Push", "System.UInt16"));
        il.Emit(OpCodes.Ldloc, data);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, Method(packet, "Push", "System.Boolean"));
        il.Emit(OpCodes.Ldloc, data);
        il.Emit(OpCodes.Callvirt, Method(packet, "UpdateHeader"));
        il.Emit(OpCodes.Call, Method(proxy, "get_Instance"));
        il.Emit(OpCodes.Callvirt, Method(proxy, "GetBattleTcpServerProxy"));
        il.Emit(OpCodes.Ldloc, data);
        il.Emit(OpCodes.Callvirt, Method(module.GetType("CS.NetBuffer"), "get_Base"));
        il.Emit(OpCodes.Callvirt, Method(module.GetType("CS.TcpPeer"), "Send", "System.Byte[]"));
        il.Append(ret);

        var scope = new MethodDefinition("UpdateLanSniperScope", MethodAttributes.Private, module.TypeSystem.Void);
        scope.Parameters.Add(new ParameterDefinition("delta", ParameterAttributes.None, module.TypeSystem.Single));
        local.Methods.Add(scope);
        il = scope.Body.GetILProcessor();
        var sight = new VariableDefinition(module.TypeSystem.Single);
        var vector = engine.GetType("UnityEngine.Vector3");
        var pos = new VariableDefinition(module.ImportReference(vector));
        scope.Body.Variables.Add(sight);
        scope.Body.Variables.Add(pos);
        var controller = module.GetType("CS.CameraController");
        var transform = engine.GetType("UnityEngine.Transform");
        var move = module.ImportReference(Method(engine.GetType("UnityEngine.Mathf"), "MoveTowards",
            "System.Single", "System.Single", "System.Single"));
        var y = module.ImportReference(vector.Fields.Single(f => f.Name == "y"));
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, bonus);
        il.Emit(OpCodes.Stloc, sight);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, sight);
        il.Emit(OpCodes.Ldc_R4, 0f);
        il.Emit(OpCodes.Cgt);
        il.Emit(OpCodes.Call, send);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, local.Fields.Single(f => f.Name == "mCameraController"));
        il.Emit(OpCodes.Callvirt, Method(controller, "GetCamera"));
        il.Emit(OpCodes.Callvirt, module.ImportReference(Method(engine.GetType("UnityEngine.Component"), "get_transform")));
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Callvirt, module.ImportReference(Method(transform, "get_localPosition")));
        il.Emit(OpCodes.Stloc, pos);
        il.Emit(OpCodes.Ldloca, pos);
        il.Emit(OpCodes.Ldloca, pos);
        il.Emit(OpCodes.Ldfld, y);
        il.Emit(OpCodes.Ldc_R4, 180f);
        il.Emit(OpCodes.Ldloc, sight);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_R4, 440f);
        il.Emit(OpCodes.Ldloc, sight);
        il.Emit(OpCodes.Ldc_R4, 0f);
        var speed = il.Create(OpCodes.Mul);
        il.Emit(OpCodes.Bgt, speed);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldc_R4, 220f);
        il.Append(speed);
        il.Emit(OpCodes.Call, move);
        il.Emit(OpCodes.Stfld, y);
        il.Emit(OpCodes.Ldloc, pos);
        il.Emit(OpCodes.Callvirt, module.ImportReference(Method(transform, "set_localPosition", vector.FullName)));
        il.Emit(OpCodes.Ret);

        var update = Method(local, "UpdateCamera", "System.Single");
        var patch = update.Body.GetILProcessor();
        Before(patch, update.Body.Instructions.Last(),
            patch.Create(OpCodes.Ldarg_0), patch.Create(OpCodes.Ldarg_1), patch.Create(OpCodes.Call, scope));
        var operate = Method(local, "RequestOperate");
        patch = operate.Body.GetILProcessor();
        Before(patch, operate.Body.Instructions[0], patch.Create(OpCodes.Ldarg_0),
            patch.Create(OpCodes.Ldarg_0), patch.Create(OpCodes.Call, bonus),
            patch.Create(OpCodes.Ldc_R4, 0f), patch.Create(OpCodes.Cgt), patch.Create(OpCodes.Call, send));

        var bullet = module.GetType("CS.Bullet");
        var visual = module.GetType("CS.BulletVisual");
        var load = Method(visual, "Load");
        var effect = load.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldstr &&
            (string)i.Operand == "CS_Effect_Missle_Pistol");
        // Scoped bullets carry a flag in the ID, so every client loads the same original prefab.
        patch = load.Body.GetILProcessor();
        var ordinary = patch.Create(OpCodes.Ldstr, "CS_Effect_Missle_Pistol");
        var getAsset = effect.Next;
        effect.OpCode = OpCodes.Ldarg_0;
        effect.Operand = null;
        Before(patch, getAsset, patch.Create(OpCodes.Ldfld, visual.Fields.Single(f => f.Name == "mBullet")),
            patch.Create(OpCodes.Callvirt, Method(bullet, "GetID")), patch.Create(OpCodes.Ldc_I4, int.MinValue),
            patch.Create(OpCodes.And), patch.Create(OpCodes.Brfalse, ordinary),
            patch.Create(OpCodes.Ldstr, "CS_Effect_Missle_SniperRifle_98K"), patch.Create(OpCodes.Br, getAsset), ordinary);
    }


    private static void InstallRespawnMode(ModuleDefinition module)
    {
        var player = module.GetType("CS.LocalPlayer");
        foreach (var name in new[] { ".ctor", "Reset" })
        {
            var preferred = Method(player, name).Body.Instructions.Single(i =>
                i.OpCode == OpCodes.Stfld && ((FieldReference)i.Operand).Name == "mPreferedBattleMode");
            preferred.Previous.OpCode = OpCodes.Ldc_I4_4;
        }

        var utility = module.GetType("CS.Utility");
        foreach (var entry in new[] { new { Name = "GetBattleModeName", Text = 10990 },
                                      new { Name = "GetBattleModeDescription", Text = 10991 } })
        {
            var method = Method(utility, entry.Name, "CS.BattleMode");
            var il = method.Body.GetILProcessor();
            var first = method.Body.Instructions[0];
            Before(il, first, il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldc_I4_4),
                il.Create(OpCodes.Bne_Un, first), il.Create(OpCodes.Ldc_I4, entry.Text), il.Create(OpCodes.Ret));
        }

        var bar = module.GetType("CS.GUIEntityBattleBar");
        var setup = Method(bar, "Initialise");
        var modes = setup.Body.Instructions.Single(i => i.OpCode == OpCodes.Newarr &&
            ((TypeReference)i.Operand).FullName == "CS.BattleMode");
        modes.Previous.OpCode = OpCodes.Ldc_I4_1;
        modes.Next.Next.Next.OpCode = OpCodes.Ldc_I4_4;
        var setupIl = setup.Body.GetILProcessor();
        var extraMode = modes.Next.Next.Next.Next.Next;
        for (int i = 0; i < 4; i++)
        {
            var next = extraMode.Next;
            setupIl.Remove(extraMode);
            extraMode = next;
        }
        // The Windows menu prefab contains one prebuilt button per old mode.
        var menu = setup.Body.Instructions.Single(i => i.OpCode == OpCodes.Stfld &&
            ((FieldReference)i.Operand).Name == "mModeMenu");
        var transform = (MethodReference)menu.Previous.Previous.Previous.Previous.Operand;
        var find = (MethodReference)menu.Previous.Previous.Operand;
        var gameObject = (MethodReference)menu.Previous.Operand;
        var setActive = Method(bar, "ShowModeMenu", "System.Boolean").Body.Instructions
            .Single(i => i.OpCode == OpCodes.Callvirt).Operand as MethodReference;
        Before(setupIl, menu.Next, setupIl.Create(OpCodes.Ldarg_0),
            setupIl.Create(OpCodes.Ldfld, (FieldReference)menu.Operand),
            setupIl.Create(OpCodes.Callvirt, transform), setupIl.Create(OpCodes.Ldstr, "MenuItem_1"),
            setupIl.Create(OpCodes.Callvirt, find), setupIl.Create(OpCodes.Callvirt, gameObject),
            setupIl.Create(OpCodes.Ldc_I4_0), setupIl.Create(OpCodes.Callvirt, setActive));
    }

    private static void InstallInfiniteAmmo(ModuleDefinition module)
    {
        var reload = Method(module.GetType("CS.OperatorGun"), "Reloaded", "System.IO.BinaryReader");
        var il = reload.Body.GetILProcessor();
        var takeAmmo = reload.Body.Instructions.Single(i => i.OpCode == OpCodes.Callvirt &&
            ((MethodReference)i.Operand).Name == "ConsumeItem");
        var magazine = takeAmmo.Next.Next;
        var subtract = reload.Body.Instructions[3];
        while (subtract != magazine)
        {
            var next = subtract.Next;
            il.Remove(subtract);
            subtract = next;
        }

        var entry = module.GetType("CS.GUIEntityWeaponBar").NestedTypes.Single(t => t.Name == "Slot")
            .NestedTypes.Single(t => t.Name == "Entry");
        var update = Method(entry, "Update", "System.Single");
        il = update.Body.GetILProcessor();
        var reserved = update.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldfld &&
            ((FieldReference)i.Operand).Name == "mAmmoReserved");
        reserved.Next.OpCode = OpCodes.Ldstr;
        reserved.Next.Operand = "INF";
        il.Remove(reserved.Next.Next); // constrained
        il.Remove(reserved.Next.Next); // ToString
    }

    private static void InstallRoundControls(ModuleDefinition module)
    {
        var local = module.GetType("CS.LocalController");
        var switchWeapon = Method(local, "SwitchWeapon", "CS.ArmType");
        var permitted = switchWeapon.Body.Instructions.Single(i => i.OpCode == OpCodes.Callvirt &&
            ((MethodReference)i.Operand).Name == "IsWeaponSwitchable").Next;
        var il = switchWeapon.Body.GetILProcessor();
        Before(il, permitted.Next, il.Create(OpCodes.Ldarg_1), il.Create(OpCodes.Ldc_I4_5),
            il.Create(OpCodes.Beq, (Instruction)permitted.Operand));

        var sendScope = Method(local, "SendLanScopeState", "System.Boolean");
        var wheel = new MethodDefinition("UpdateLanWeaponWheel", MethodAttributes.Private, module.TypeSystem.Void);
        local.Methods.Add(wheel);
        il = wheel.Body.GetILProcessor();
        var engine = module.AssemblyResolver.Resolve(module.AssemblyReferences.Single(a => a.Name == "UnityEngine")).MainModule;
        var vector = engine.GetType("UnityEngine.Vector2");
        var delta = new VariableDefinition(module.TypeSystem.Single);
        wheel.Body.Variables.Add(delta);
        var packet = module.GetType("CS.NetPacket");
        var data = new VariableDefinition(packet);
        wheel.Body.Variables.Add(data);
        var ret = il.Create(OpCodes.Ret);
        il.Emit(OpCodes.Call, module.ImportReference(Method(engine.GetType("UnityEngine.Input"), "get_mouseScrollDelta")));
        il.Emit(OpCodes.Ldfld, module.ImportReference(vector.Fields.Single(f => f.Name == "y")));
        il.Emit(OpCodes.Stloc, delta);
        il.Emit(OpCodes.Ldloc, delta);
        il.Emit(OpCodes.Ldc_R4, 0f);
        il.Emit(OpCodes.Beq, ret);

        // Wheel and scope use the same bool-sized battle packet format.
        foreach (var i in sendScope.Body.Instructions.SkipWhile(i => i.OpCode != OpCodes.Newobj).TakeWhile(i => i.OpCode != OpCodes.Ret))
        {
            if (i.OpCode == OpCodes.Ldarg_1)
            {
                il.Emit(OpCodes.Ldloc, delta);
                il.Emit(OpCodes.Ldc_R4, 0f);
                il.Emit(OpCodes.Clt);
            }
            else if (i.OpCode == OpCodes.Stloc || i.OpCode == OpCodes.Ldloc)
                il.Append(il.Create(i.OpCode, data));
            else if (i.OpCode == OpCodes.Ldc_I4)
                il.Emit(OpCodes.Ldc_I4, 208);
            else
                il.Append(il.Create(i.OpCode, (MethodReference)i.Operand));
        }
        il.Append(ret);

        var input = Method(local, "UpdateInput", "System.Single");
        il = input.Body.GetILProcessor();
        var keyOne = input.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldc_I4_S &&
            (sbyte)i.Operand == 49 && i.Next.Operand.ToString().Contains("::GetKeyDown("));
        Before(il, keyOne, il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Call, wheel));
    }

    private static void InstallScopePose(ModuleDefinition module, TypeDefinition battle, TypeDefinition unit)
    {
        var visual = module.GetType("CS.UnitVisual");
        var entity = module.GetType("CS.UnitEntity");
        var engine = module.AssemblyResolver.Resolve(module.AssemblyReferences.Single(a => a.Name == "UnityEngine")).MainModule;
        var animator = engine.GetType("UnityEngine.Animator");
        var controller = engine.GetType("UnityEngine.RuntimeAnimatorController");
        var overrideType = engine.GetType("UnityEngine.AnimatorOverrideController");
        var clip = engine.GetType("UnityEngine.AnimationClip");
        var setController = module.ImportReference(Method(animator, "set_runtimeAnimatorController", controller.FullName));
        var getController = module.ImportReference(Method(animator, "get_runtimeAnimatorController"));
        var setOverrideBase = module.ImportReference(Method(overrideType, "set_runtimeAnimatorController", controller.FullName));
        var getOverrideBase = module.ImportReference(Method(overrideType, "get_runtimeAnimatorController"));
        var setClip = module.ImportReference(Method(overrideType, "set_Item", "System.String", clip.FullName));
        var animatorField = entity.Fields.Single(f => f.Name == "mAnimator");
        var cached = new FieldDefinition("mLanScopeController", FieldAttributes.Private, module.ImportReference(overrideType));
        entity.Fields.Add(cached);
        var apply = new MethodDefinition("SetLanScopePose", MethodAttributes.Public, module.TypeSystem.Void);
        apply.Parameters.Add(new ParameterDefinition("scoped", ParameterAttributes.None, module.TypeSystem.Boolean));
        entity.Methods.Add(apply);
        var il = apply.Body.GetILProcessor();
        var ret = il.Create(OpCodes.Ret);
        var enable = il.Create(OpCodes.Ldarg_0);
        var assign = il.Create(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, animatorField);
        il.Emit(OpCodes.Brfalse, ret);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Brtrue, enable);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, cached);
        il.Emit(OpCodes.Brfalse, ret);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, animatorField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, cached);
        il.Emit(OpCodes.Callvirt, getOverrideBase);
        il.Emit(OpCodes.Callvirt, setController);
        il.Emit(OpCodes.Br, ret);
        il.Append(enable);
        il.Emit(OpCodes.Ldfld, cached);
        il.Emit(OpCodes.Brtrue, assign);

        var bundles = module.GetType("CS.AssetBundleManager");
        var unitBundle = module.GetType("CS.AssetBundleNames").Fields.Single(f => f.Name == "Unit");
        var loadClip = new GenericInstanceMethod(Method(module.GetType("CS.AssetBundleLoader"), "GetAsset", "System.String"));
        loadClip.GenericArguments.Add(module.ImportReference(clip));
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Newobj, module.ImportReference(Method(overrideType, ".ctor")));
        il.Emit(OpCodes.Stfld, cached);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, cached);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, animatorField);
        il.Emit(OpCodes.Callvirt, getController);
        il.Emit(OpCodes.Callvirt, setOverrideBase);
        foreach (var action in new[] { "Hold", "Fire" })
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, cached);
            il.Emit(OpCodes.Ldstr, action + "_Rifle");
            il.Emit(OpCodes.Call, Method(bundles, "get_Instance"));
            il.Emit(OpCodes.Ldsfld, unitBundle);
            il.Emit(OpCodes.Callvirt, Method(bundles, "Retrieve", "System.String"));
            il.Emit(OpCodes.Ldstr, (action + "_Rifle_Snipe").ToLowerInvariant());
            il.Emit(OpCodes.Callvirt, loadClip);
            il.Emit(OpCodes.Callvirt, setClip);
        }

        il.Append(assign);
        il.Emit(OpCodes.Ldfld, animatorField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, cached);
        il.Emit(OpCodes.Callvirt, setController);
        il.Append(ret);

        var packet = new MethodDefinition("HandleLanScopePose", MethodAttributes.Private, module.TypeSystem.Void);
        packet.Parameters.Add(new ParameterDefinition("reader", ParameterAttributes.None, module.ImportReference(typeof(BinaryReader))));
        battle.Methods.Add(packet);
        il = packet.Body.GetILProcessor();
        var target = new VariableDefinition(unit);
        packet.Body.Variables.Add(target);
        ret = il.Create(OpCodes.Ret);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, module.ImportReference(typeof(BinaryReader).GetMethod("ReadUInt32")));
        il.Emit(OpCodes.Call, Method(battle, "GetUnit", "System.UInt32"));
        il.Emit(OpCodes.Stloc, target);
        il.Emit(OpCodes.Ldloc, target);
        il.Emit(OpCodes.Brfalse, ret);
        il.Emit(OpCodes.Ldloc, target);
        il.Emit(OpCodes.Callvirt, Method(unit, "GetVisual"));
        il.Emit(OpCodes.Callvirt, Method(visual, "GetEntity"));
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, module.ImportReference(typeof(BinaryReader).GetMethod("ReadBoolean")));
        il.Emit(OpCodes.Callvirt, apply);
        il.Append(ret);
        Dispatch(Method(battle, "HandleMessages", "CS.NetMessage", "System.IO.BinaryReader"), 207, packet);
    }

    private static void InstallEliminationNotices(ModuleDefinition module, TypeDefinition battle, TypeDefinition unit)
    {
        var down = Method(unit, "IsDown");
        var dead = Method(unit, "IsDead");
        var damage = Method(battle, "HandleUnitDamaged", unit.FullName, unit.FullName, "System.Single", "System.Boolean");
        var code = damage.Body.Instructions;
        var downNotice = code.Single(i => i.OpCode == OpCodes.Callvirt && ((MethodReference)i.Operand).Name == "HandleUnitDown");
        var getKiller = code.Single(i => i.OpCode == OpCodes.Callvirt && ((MethodReference)i.Operand).Name == "CalcKiller");
        downNotice.Next.Operand = getKiller.Previous.Previous;
        var setKiller = code.Single(i => i.OpCode == OpCodes.Callvirt && ((MethodReference)i.Operand).Name == "SetKiller");
        var il = damage.Body.GetILProcessor();
        // Award the native kill and retain the killer, while keeping the target in the
        // LAN knockdown state so the survival mode never removes them from the round.
        Before(il, setKiller.Next, il.Create(OpCodes.Ldarg_2), il.Create(OpCodes.Callvirt, down),
            il.Create(OpCodes.Brtrue, code.Last()));

        foreach (var name in new[] {
            "HandleMeleeHitUnit", "HandleBulletHitUnit", "HandleBulletExplosionHitUnit",
            "HandleThrowableExplosionHitUnit", "HandleThrowableIncendiaryHitUnit",
            "HandleVehicleHitUnit", "HandleVehicleExplosionHitUnit"
        })
        {
            var hit = Method(battle, name, "System.IO.BinaryReader");
            il = hit.Body.GetILProcessor();
            var branch = hit.Body.Instructions.Single(i => i.Operand == down).Next;
            var deadCheck = (Instruction)branch.Operand;
            var deadBranch = hit.Body.Instructions.Single(i => i.Operand == dead).Next;
            var oldDownNotice = branch.Next;
            branch.OpCode = OpCodes.Brtrue;
            branch.Operand = deadBranch.Next;
            il.InsertAfter(branch, il.Create(OpCodes.Br, deadCheck));
            while (oldDownNotice != deadCheck)
            {
                var next = oldDownNotice.Next;
                il.Remove(oldDownNotice);
                oldDownNotice = next;
            }
        }

        var bullet = Method(battle, "HandleBulletHitUnit", "System.IO.BinaryReader");
        var effect = bullet.Body.Instructions.Single(i => i.OpCode == OpCodes.Callvirt &&
            ((MethodReference)i.Operand).Name == "HandleHitByBullet");
        // The first GetUnit reads the attacker, while the effect is owned by the target.
        var shooter = bullet.Body.Instructions.First(i => i.OpCode == OpCodes.Call &&
            ((MethodReference)i.Operand).Name == "GetUnit").Next;
        effect.Previous.Previous.Operand = shooter.Operand;
    }

    private static void InstallProfileName(ModuleDefinition module)
    {
        const string prefix = "-lan-profile=";
        var login = module.GetType("CS.GUIEntityLoginSteam");
        var request = Method(login, "RequestLogin");
        var name = new MethodDefinition("GetLanProfileName", MethodAttributes.Private | MethodAttributes.Static, module.TypeSystem.String);
        login.Methods.Add(name);
        var il = name.Body.GetILProcessor();
        var args = new VariableDefinition(new ArrayType(module.TypeSystem.String));
        var index = new VariableDefinition(module.TypeSystem.Int32);
        var arg = new VariableDefinition(module.TypeSystem.String);
        name.Body.Variables.Add(args);
        name.Body.Variables.Add(index);
        name.Body.Variables.Add(arg);
        var core = module.TypeSystem;
        var environment = new TypeReference("System", "Environment", module, core.CoreLibrary);
        var readArgs = new MethodReference("GetCommandLineArgs", args.VariableType, environment);
        var startsWith = new MethodReference("StartsWith", core.Boolean, core.String) { HasThis = true };
        startsWith.Parameters.Add(new ParameterDefinition(core.String));
        var substring = new MethodReference("Substring", core.String, core.String) { HasThis = true };
        substring.Parameters.Add(new ParameterDefinition(core.Int32));
        var loop = il.Create(OpCodes.Ldloc, index);
        var next = il.Create(OpCodes.Ldloc, index);
        var fallback = il.Create(OpCodes.Ldstr, "1");
        il.Emit(OpCodes.Call, readArgs);
        il.Emit(OpCodes.Stloc, args);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stloc, index);
        il.Append(loop);
        il.Emit(OpCodes.Ldloc, args);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Bge, fallback);
        il.Emit(OpCodes.Ldloc, args);
        il.Emit(OpCodes.Ldloc, index);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Stloc, arg);
        il.Emit(OpCodes.Ldloc, arg);
        il.Emit(OpCodes.Ldstr, prefix);
        il.Emit(OpCodes.Callvirt, startsWith);
        il.Emit(OpCodes.Brfalse, next);
        il.Emit(OpCodes.Ldloc, arg);
        il.Emit(OpCodes.Ldc_I4, prefix.Length);
        il.Emit(OpCodes.Callvirt, substring);
        il.Emit(OpCodes.Ret);
        il.Append(next);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, index);
        il.Emit(OpCodes.Br, loop);
        il.Append(fallback);
        il.Emit(OpCodes.Ret);
        var account = request.Body.Instructions[1];
        if (account.OpCode != OpCodes.Ldstr || (string)account.Operand != "1")
            throw new InvalidOperationException("Expected the local login patch first.");
        account.OpCode = OpCodes.Call;
        account.Operand = name;
    }

    private static void InstallVehiclePropImpact(ModuleDefinition module, TypeDefinition battle)
    {
        var handler = Method(battle, "HandleVehicleHitObstacle", "System.IO.BinaryReader");
        var destroyable = module.GetType("CS.Destroyable");
        var vehicle = module.GetType("CS.Vehicle");
        var takeDamage = handler.Body.Instructions.Single(i => i.OpCode == OpCodes.Callvirt &&
            ((MethodReference)i.Operand).Name == "TakeDamage");
        var group = handler.Body.Variables.Single(v => v.VariableType.FullName == destroyable.FullName);
        var car = handler.Body.Variables.Single(v => v.VariableType.FullName == vehicle.FullName);
        // Native obstacle death enables the prop's rigidbody. Reuse the native impulse
        // from the car's position so the detached prop actually tips or moves.
        var il = handler.Body.GetILProcessor();
        Before(il, takeDamage.Next, il.Create(OpCodes.Ldloc, group), il.Create(OpCodes.Ldloc, car),
            il.Create(OpCodes.Callvirt, Method(vehicle, "GetPosition")), il.Create(OpCodes.Ldc_R4, 5f),
            il.Create(OpCodes.Callvirt, Method(destroyable, "HitByExplosion", "UnityEngine.Vector3", "System.Single")));
    }

    private static void InstallItems(ModuleDefinition module, TypeDefinition battle, TypeDefinition unit)
    {
        var local = module.GetType("CS.LocalController");
        var input = Method(local, "UpdateInput", "System.Single");
        foreach (var key in new[] { 52, 53, 54 })
        {
            var press = input.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldc_I4_S &&
                (sbyte)i.Operand == key && i.Next.Operand.ToString().Contains("::GetKeyDown("));
            if (key == 54)
                press.Operand = (sbyte)0;
            else
                press.Next.Next.Next.Next.OpCode = key == 52 ? OpCodes.Ldc_I4_4 : OpCodes.Ldc_I4_5;
        }

        // Arm types 4 and 5 retain their native item behavior but use keys 4 and 5.
        var slot = module.GetType("CS.GUIEntityWeaponBar").NestedTypes.Single(t => t.Name == "Slot");
        var refresh = slot.Methods.Single(m => m.Name == "Refresh");
        var label = refresh.Body.Instructions.First(i => i.OpCode == OpCodes.Ldc_I4_1);
        var labelIl = refresh.Body.GetILProcessor();
        Before(labelIl, label, labelIl.Create(OpCodes.Dup), labelIl.Create(OpCodes.Ldc_I4_4),
            labelIl.Create(OpCodes.Bge, label.Next.Next));

        var lootType = module.GetType("CS.Loot");
        var loot = Method(local, "Loot", lootType.FullName);
        var request = Method(local, "RequestLoot", "System.UInt32", "System.UInt16", "CS.ItemSlotType",
            "System.UInt16", "System.UInt16");
        // Keep the native mute and distance checks, but let the server replace an occupied gun slot.
        var lootIl = loot.Body.GetILProcessor();
        var single = loot.Body.Instructions.First(i => i.OpCode == OpCodes.Ble &&
            i.Previous.Previous.Operand is MethodReference && ((MethodReference)i.Previous.Previous.Operand).Name == "GetNumSlots");
        var originalSingle = (Instruction)single.Operand;
        var pickup = lootIl.Create(OpCodes.Ldarg_0);
        Before(lootIl, originalSingle, pickup, lootIl.Create(OpCodes.Ldarg_1),
            lootIl.Create(OpCodes.Callvirt, Method(lootType, "GetID")),
            lootIl.Create(OpCodes.Ldc_I4_0), lootIl.Create(OpCodes.Ldc_I4_0),
            lootIl.Create(OpCodes.Ldc_I4_0), lootIl.Create(OpCodes.Ldc_I4_0),
            lootIl.Create(OpCodes.Call, request), lootIl.Create(OpCodes.Ret));
        single.Operand = pickup;

        var dispatcher = Method(battle, "HandleMessages", "CS.NetMessage", "System.IO.BinaryReader");
        var reader = dispatcher.Parameters[1].ParameterType;
        var readUInt = Method(battle, "HandleLootSpawn", reader.FullName).Body.Instructions
            .Select(i => i.Operand).OfType<MethodReference>().First(m => m.Name == "ReadUInt32");
        var nativeLoot = unit.Methods.Single(m => m.Name == "Loot" && m.Parameters.Count == 6);
        var gui = nativeLoot.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().ToArray();
        var remove = new MethodDefinition("HandleLanRemoveLoot", MethodAttributes.Private, module.TypeSystem.Void);
        remove.Parameters.Add(new ParameterDefinition("reader", ParameterAttributes.None, reader));
        battle.Methods.Add(remove);
        var il = remove.Body.GetILProcessor();
        var id = new VariableDefinition(module.TypeSystem.UInt32);
        var target = new VariableDefinition(lootType);
        var entity = new VariableDefinition(module.GetType("CS.GUIEntityLoot"));
        remove.Body.Variables.Add(id);
        remove.Body.Variables.Add(target);
        remove.Body.Variables.Add(entity);
        var ret = il.Create(OpCodes.Ret);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, readUInt);
        il.Emit(OpCodes.Stloc, id);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, id);
        il.Emit(OpCodes.Call, Method(battle, "GetLoot", "System.UInt32"));
        il.Emit(OpCodes.Stloc, target);
        il.Emit(OpCodes.Ldloc, target);
        il.Emit(OpCodes.Brfalse, ret);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, id);
        il.Emit(OpCodes.Call, Method(battle, "RemoveLoot", "System.UInt32"));
        il.Emit(OpCodes.Call, gui.First(m => m.DeclaringType.Name == "GUISystem" && m.Name == "get_Instance"));
        il.Emit(OpCodes.Callvirt, gui.OfType<GenericInstanceMethod>().Single(m => m.GenericArguments[0].Name == "GUIEntityLoot"));
        il.Emit(OpCodes.Stloc, entity);
        il.Emit(OpCodes.Ldloc, entity);
        il.Emit(OpCodes.Brfalse, ret);
        il.Emit(OpCodes.Ldloc, entity);
        il.Emit(OpCodes.Ldloc, target);
        il.Emit(OpCodes.Callvirt, gui.Single(m => m.Name == "HandleRemoveLoot"));
        il.Append(ret);
        Dispatch(dispatcher, 205, remove);
    }

    private static void Dispatch(MethodDefinition dispatcher, int message, MethodDefinition handler)
    {
        var il = dispatcher.Body.GetILProcessor();
        var original = dispatcher.Body.Instructions[0];
        Before(il, original, il.Create(OpCodes.Ldarg_1), il.Create(OpCodes.Ldc_I4, message),
            il.Create(OpCodes.Bne_Un, original), il.Create(OpCodes.Ldarg_0),
            il.Create(OpCodes.Ldarg_2), il.Create(OpCodes.Call, handler), il.Create(OpCodes.Ret));
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
                InstallReviveBar(module, unit);
                InstallMeleeSwing(module, battle, unit);
                InstallSniperScope(module, unit);
                InstallScopePose(module, battle, unit);
                InstallRoundControls(module);
                InstallInfiniteAmmo(module);
                InstallRespawnMode(module);
                InstallItems(module, battle, unit);
                InstallVehiclePropImpact(module, battle);
                InstallEliminationNotices(module, battle, unit);
                InstallProfileName(module);
                module.Write(args[1]);
            }
        }
    }
}
