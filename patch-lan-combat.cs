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

    private static void Trace(ILProcessor il, MethodReference trace, string tag, int id)
    {
        il.Emit(OpCodes.Ldstr, tag);
        il.Emit(OpCodes.Ldc_I4, id);
        il.Emit(OpCodes.Call, trace);
    }

    private static void InsertTrace(ILProcessor il, Instruction anchor, MethodReference trace, string tag, int id)
    {
        il.InsertBefore(anchor, il.Create(OpCodes.Ldstr, tag));
        il.InsertBefore(anchor, il.Create(OpCodes.Ldc_I4, id));
        il.InsertBefore(anchor, il.Create(OpCodes.Call, trace));
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
        var motionField = local.Fields.Single(f => f.Name == "mMotion");
        var vectorZero = module.ImportReference(Method(engine.GetType("UnityEngine.Vector3"), "get_zero"));
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
        // Scoped sniping is stationary, matching the APK sniper mode.
        var settled = il.Create(OpCodes.Nop);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Brfalse, settled);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, Method(local, "StopMove"));
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, vectorZero);
        il.Emit(OpCodes.Stfld, motionField);
        il.Append(settled);
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

        // Block WASD translation while the shot is scoped. Mouse look and fire
        // remain available, so the player can still aim and shoot on the spot.
        var movement = Method(local, "Move", "System.Single");
        var movementIl = movement.Body.GetILProcessor();
        var movementStart = movement.Body.Instructions[0];
        Before(movementIl, movementStart, movementIl.Create(OpCodes.Ldarg_0),
            movementIl.Create(OpCodes.Ldfld, scopeState), movementIl.Create(OpCodes.Brfalse, movementStart),
            movementIl.Create(OpCodes.Ret));

        // The camera backs off for the scope, which pushes the character out of
        // the default shadow distance. Keep the character shadow visible while
        // scoped and restore the original setting afterwards.
        var quality = engine.GetType("UnityEngine.QualitySettings");
        var getShadow = module.ImportReference(Method(quality, "get_shadowDistance"));
        var setShadow = module.ImportReference(Method(quality, "set_shadowDistance", "System.Single"));
        var shadowSaved = new FieldDefinition("mLanShadowSaved", FieldAttributes.Private, module.TypeSystem.Boolean);
        var shadowDistance = new FieldDefinition("mLanShadowDistance", FieldAttributes.Private, module.TypeSystem.Single);
        local.Fields.Add(shadowSaved);
        local.Fields.Add(shadowDistance);
        var shadow = new MethodDefinition("LanUpdateScopeShadow", MethodAttributes.Private, module.TypeSystem.Void);
        shadow.Parameters.Add(new ParameterDefinition("sight", ParameterAttributes.None, module.TypeSystem.Single));
        local.Methods.Add(shadow);
        var s = shadow.Body.GetILProcessor();
        var shadowRet = s.Create(OpCodes.Ret);
        var restoreShadow = s.Create(OpCodes.Nop);
        var applyShadow = s.Create(OpCodes.Nop);
        s.Emit(OpCodes.Ldarg_1);
        s.Emit(OpCodes.Ldc_R4, 0f);
        s.Emit(OpCodes.Ble, restoreShadow);
        s.Emit(OpCodes.Ldarg_0);
        s.Emit(OpCodes.Ldfld, shadowSaved);
        s.Emit(OpCodes.Brtrue, applyShadow);
        s.Emit(OpCodes.Ldarg_0);
        s.Emit(OpCodes.Call, getShadow);
        s.Emit(OpCodes.Stfld, shadowDistance);
        s.Emit(OpCodes.Ldarg_0);
        s.Emit(OpCodes.Ldc_I4_1);
        s.Emit(OpCodes.Stfld, shadowSaved);
        s.Append(applyShadow);
        s.Emit(OpCodes.Ldc_R4, 600f);
        s.Emit(OpCodes.Call, setShadow);
        s.Emit(OpCodes.Ret);
        s.Append(restoreShadow);
        s.Emit(OpCodes.Ldarg_0);
        s.Emit(OpCodes.Ldfld, shadowSaved);
        s.Emit(OpCodes.Brfalse, shadowRet);
        s.Emit(OpCodes.Ldarg_0);
        s.Emit(OpCodes.Ldfld, shadowDistance);
        s.Emit(OpCodes.Call, setShadow);
        s.Emit(OpCodes.Ldarg_0);
        s.Emit(OpCodes.Ldc_I4_0);
        s.Emit(OpCodes.Stfld, shadowSaved);
        s.Append(shadowRet);

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
        il.Emit(OpCodes.Ldloc, sight);
        il.Emit(OpCodes.Call, shadow);
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

        // Q sends a small "last weapon" request; the server owns the previous
        // slot so every client sees the same switch.
        var proxy = module.GetType("CS.ServerProxyManager");
        var last = new MethodDefinition("SendLanLastWeapon", MethodAttributes.Private, module.TypeSystem.Void);
        local.Methods.Add(last);
        var lastIl = last.Body.GetILProcessor();
        var lastData = new VariableDefinition(packet);
        last.Body.Variables.Add(lastData);
        lastIl.Emit(OpCodes.Newobj, Method(packet, ".ctor"));
        lastIl.Emit(OpCodes.Stloc, lastData);
        lastIl.Emit(OpCodes.Ldloc, lastData);
        lastIl.Emit(OpCodes.Ldc_I4, 212);
        lastIl.Emit(OpCodes.Callvirt, Method(packet, "Push", "System.UInt16"));
        lastIl.Emit(OpCodes.Ldloc, lastData);
        lastIl.Emit(OpCodes.Callvirt, Method(packet, "UpdateHeader"));
        lastIl.Emit(OpCodes.Call, Method(proxy, "get_Instance"));
        lastIl.Emit(OpCodes.Callvirt, Method(proxy, "GetBattleTcpServerProxy"));
        lastIl.Emit(OpCodes.Ldloc, lastData);
        lastIl.Emit(OpCodes.Callvirt, Method(module.GetType("CS.NetBuffer"), "get_Base"));
        lastIl.Emit(OpCodes.Callvirt, Method(module.GetType("CS.TcpPeer"), "Send", "System.Byte[]"));
        lastIl.Emit(OpCodes.Ret);

        var input = Method(local, "UpdateInput", "System.Single");
        il = input.Body.GetILProcessor();
        var keyOne = input.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldc_I4_S &&
            (sbyte)i.Operand == 49 && i.Next.Operand.ToString().Contains("::GetKeyDown("));
        Before(il, keyOne, il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Call, wheel));
        var muted = input.Body.Instructions.First(i => i.OpCode == OpCodes.Call &&
            ((MethodReference)i.Operand).Name == "IsMuted");
        var muteStart = muted.Previous;
        var skipLast = il.Create(OpCodes.Nop);
        var qStart = il.Create(OpCodes.Ldc_I4_S, (sbyte)113);
        Before(il, muteStart, qStart,
            il.Create(OpCodes.Call, module.ImportReference(Method(engine.GetType("UnityEngine.Input"), "GetKeyDown", "UnityEngine.KeyCode"))),
            il.Create(OpCodes.Brfalse, skipLast), il.Create(OpCodes.Ldarg_0),
            il.Create(OpCodes.Call, last), skipLast);
        // The chat-focus path branches straight to the IsMuted check; retarget
        // that branch to the new Q check so Q is reachable in normal play.
        foreach (var branch in input.Body.Instructions)
            if (branch.Operand == muteStart)
                branch.Operand = qStart;
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
        var setClip = module.ImportReference(Method(overrideType, "set_Item", "System.String", clip.FullName));
        var animatorField = entity.Fields.Single(f => f.Name == "mAnimator");
        var cached = new FieldDefinition("mLanScopeController", FieldAttributes.Private, module.ImportReference(overrideType));
        entity.Fields.Add(cached);

        // Wrap every unit animator once at creation. Swapping the whole runtime
        // controller on scope exit reset the state machine and left the weapon
        // dangling from the hand, so only the two rifle clips are overridden.
        var wrap = new MethodDefinition("LanWrapAnimator", MethodAttributes.Public, module.TypeSystem.Void);
        entity.Methods.Add(wrap);
        var w = wrap.Body.GetILProcessor();
        var wrapped = w.Create(OpCodes.Ret);
        var current = new VariableDefinition(module.ImportReference(controller));
        wrap.Body.Variables.Add(current);
        w.Emit(OpCodes.Ldarg_0);
        w.Emit(OpCodes.Ldfld, animatorField);
        w.Emit(OpCodes.Brfalse, wrapped);
        w.Emit(OpCodes.Ldarg_0);
        w.Emit(OpCodes.Ldfld, animatorField);
        w.Emit(OpCodes.Callvirt, getController);
        w.Emit(OpCodes.Stloc, current);
        w.Emit(OpCodes.Ldloc, current);
        w.Emit(OpCodes.Brfalse, wrapped);
        w.Emit(OpCodes.Ldarg_0);
        w.Emit(OpCodes.Ldfld, cached);
        w.Emit(OpCodes.Ldloc, current);
        w.Emit(OpCodes.Ceq);
        w.Emit(OpCodes.Brtrue, wrapped);
        w.Emit(OpCodes.Ldarg_0);
        w.Emit(OpCodes.Newobj, module.ImportReference(Method(overrideType, ".ctor")));
        w.Emit(OpCodes.Stfld, cached);
        w.Emit(OpCodes.Ldarg_0);
        w.Emit(OpCodes.Ldfld, cached);
        w.Emit(OpCodes.Ldloc, current);
        w.Emit(OpCodes.Callvirt, setOverrideBase);
        w.Emit(OpCodes.Ldarg_0);
        w.Emit(OpCodes.Ldfld, animatorField);
        w.Emit(OpCodes.Ldarg_0);
        w.Emit(OpCodes.Ldfld, cached);
        w.Emit(OpCodes.Callvirt, setController);
        w.Append(wrapped);

        var applyUnit = Method(entity, "ApplyUnit");
        var applyIl = applyUnit.Body.GetILProcessor();
        var originalController = applyUnit.Body.Instructions.Single(i => i.OpCode == OpCodes.Callvirt &&
            ((MethodReference)i.Operand).Name == "set_runtimeAnimatorController");
        Before(applyIl, originalController.Next, applyIl.Create(OpCodes.Ldarg_0), applyIl.Create(OpCodes.Call, wrap));

        var bundles = module.GetType("CS.AssetBundleManager");
        var unitBundle = module.GetType("CS.AssetBundleNames").Fields.Single(f => f.Name == "Unit");
        var standard = module.GetType("CS.AssetBundleLoaderStandard");
        var bundleType = engine.GetType("UnityEngine.AssetBundle");
        var assetBundle = standard.Fields.Single(f => f.Name == "mAssetBundle");
        var clipRef = module.ImportReference(clip);
        var loadAsset = module.ImportReference(bundleType.Methods.Single(m => m.Name == "LoadAsset" &&
            !m.HasGenericParameters && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.String"));
        // AssetBundle.LoadAsset(name, Type) is non-generic. The generic
        // GetAsset<T> call resolved to a Boolean load in the Mono 2.0 runtime
        // and threw FormatException on the clip name.
        var load = new MethodDefinition("LanLoadClip", MethodAttributes.Private | MethodAttributes.Static, clipRef);
        load.Parameters.Add(new ParameterDefinition("name", ParameterAttributes.None, module.TypeSystem.String));
        entity.Methods.Add(load);
        var l = load.Body.GetILProcessor();
        var noLoader = l.Create(OpCodes.Ldnull);
        var loaded = l.Create(OpCodes.Ret);
        var standardValue = new VariableDefinition(module.ImportReference(standard));
        load.Body.Variables.Add(standardValue);
        l.Emit(OpCodes.Call, Method(bundles, "get_Instance"));
        l.Emit(OpCodes.Ldsfld, unitBundle);
        l.Emit(OpCodes.Callvirt, Method(bundles, "Retrieve", "System.String"));
        l.Emit(OpCodes.Isinst, standard);
        l.Emit(OpCodes.Stloc, standardValue);
        l.Emit(OpCodes.Ldloc, standardValue);
        l.Emit(OpCodes.Brfalse, noLoader);
        l.Emit(OpCodes.Ldloc, standardValue);
        l.Emit(OpCodes.Ldfld, assetBundle);
        l.Emit(OpCodes.Ldarg_0);
        l.Emit(OpCodes.Callvirt, loadAsset);
        l.Emit(OpCodes.Isinst, clipRef);
        l.Emit(OpCodes.Ret);
        l.Append(noLoader);
        l.Append(loaded);

        var apply = new MethodDefinition("SetLanScopePose", MethodAttributes.Public, module.TypeSystem.Void);
        apply.Parameters.Add(new ParameterDefinition("scoped", ParameterAttributes.None, module.TypeSystem.Boolean));
        entity.Methods.Add(apply);
        var il = apply.Body.GetILProcessor();
        var ret = il.Create(OpCodes.Ret);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, wrap);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, cached);
        il.Emit(OpCodes.Brfalse, ret);
        foreach (var action in new[] { "Hold", "Fire" })
        {
            var normal = il.Create(OpCodes.Ldstr, action + "_Rifle");
            var after = il.Create(OpCodes.Nop);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, cached);
            il.Emit(OpCodes.Ldstr, action + "_Rifle");
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Brfalse, normal);
            il.Emit(OpCodes.Ldstr, action + "_Rifle_Snipe");
            il.Emit(OpCodes.Br, after);
            il.Append(normal);
            il.Append(after);
            il.Emit(OpCodes.Call, load);
            il.Emit(OpCodes.Callvirt, setClip);
        }

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

    private static void InstallLanRoom(ModuleDefinition module, ModuleDefinition ui)
    {
        var room = ui.GetType("LanRoomUI");
        var request = module.GetType("CS.GUIEntityLogin").Methods.Single(m => m.Name == "RequestLogin" &&
            m.HasBody && m.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "127.0.0.1"));
        var loginHost = request.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldstr &&
            (string)i.Operand == "127.0.0.1");
        var requestIl = request.Body.GetILProcessor();
        requestIl.InsertAfter(loginHost,
            requestIl.Create(OpCodes.Call, module.ImportReference(Method(room, "GetLoginHost", "System.String"))));
        var diagnostics = ui.GetType("EcsDiagnostics");
        var loginPort = request.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldc_I4 && (int)i.Operand == 43120);
        requestIl.InsertAfter(loginPort,
            requestIl.Create(OpCodes.Call, module.ImportReference(Method(diagnostics, "LoginPort", "System.Int32"))));
        var managerConnect = Method(module.GetType("CS.ServerProxyManager"), "ConnectToBattleTcpServer", "System.String", "System.Int32");
        var peerConnect = managerConnect.Body.Instructions.Single(i =>
            i.OpCode == OpCodes.Callvirt && ((MethodReference)i.Operand).Name == "Connect");
        peerConnect.OpCode = OpCodes.Call;
        var asyncConnect = ui.GetType("EcsBattleConnect");
        peerConnect.Operand = module.ImportReference(Method(asyncConnect, "Begin", "System.Object", "System.String", "System.Int32"));
        var tcpPeer = module.GetType("CS.TcpPeer");
        var tcpSend = Method(tcpPeer, "Send", "System.Byte[]");
        var sendIl = tcpSend.Body.GetILProcessor();
        var sendFirst = tcpSend.Body.Instructions[0];
        Before(sendIl, sendFirst, sendIl.Create(OpCodes.Ldarg_0), sendIl.Create(OpCodes.Ldarg_1),
            sendIl.Create(OpCodes.Call, module.ImportReference(Method(asyncConnect, "QueueSend", "System.Object", "System.Byte[]"))),
            sendIl.Create(OpCodes.Brfalse, sendFirst), sendIl.Create(OpCodes.Ret));
        var tcpUpdate = Method(tcpPeer, "Update", "System.Single");
        var updateIl = tcpUpdate.Body.GetILProcessor();
        var updateFirst = tcpUpdate.Body.Instructions[0];
        Before(updateIl, updateFirst, updateIl.Create(OpCodes.Ldarg_0),
            updateIl.Create(OpCodes.Call, module.ImportReference(Method(asyncConnect, "IsPending", "System.Object"))),
            updateIl.Create(OpCodes.Brfalse, updateFirst), updateIl.Create(OpCodes.Ret));
        var tcpClose = Method(tcpPeer, "Close");
        Before(tcpClose.Body.GetILProcessor(), tcpClose.Body.Instructions[0],
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Call, module.ImportReference(Method(asyncConnect, "Cancel", "System.Object"))));
        var mainUpdate = Method(module.GetType("Main"), "Update");
        mainUpdate.Body.GetILProcessor().InsertBefore(mainUpdate.Body.Instructions[0],
            mainUpdate.Body.GetILProcessor().Create(OpCodes.Call, module.ImportReference(Method(diagnostics, "Frame"))));
        mainUpdate.Body.GetILProcessor().InsertBefore(mainUpdate.Body.Instructions[0],
            mainUpdate.Body.GetILProcessor().Create(OpCodes.Call, module.ImportReference(Method(asyncConnect, "Pump"))));

        var world = module.GetType("CS.WorldServerProxy");
        var receive = Method(world, "OnReceive", "System.Byte[]");
        var il = receive.Body.GetILProcessor();
        var code = receive.Body.Instructions;
        var message = code.Single(i => i.OpCode == OpCodes.Stloc_3).Next;
        Before(il, message, il.Create(OpCodes.Ldloc_3), il.Create(OpCodes.Ldc_I4, 210),
            il.Create(OpCodes.Bne_Un, message), il.Create(OpCodes.Ldloc_1),
            il.Create(OpCodes.Call, module.ImportReference(Method(room, "Receive", "System.IO.BinaryReader"))),
            il.Create(OpCodes.Ret));
        var login = Method(world, "HandleLogin", "System.IO.BinaryReader");
        il = login.Body.GetILProcessor();
        il.InsertBefore(login.Body.Instructions[0],
            il.Create(OpCodes.Call, module.ImportReference(Method(room, "ResetState"))));

        var bar = module.GetType("CS.GUIEntityBattleBar");
        var toggle = Method(bar, "HandleButtonBattleClicked");
        toggle.Body = new MethodBody(toggle);
        il = toggle.Body.GetILProcessor();
        il.Emit(OpCodes.Ldc_I4_4);
        il.Emit(OpCodes.Call, Method(bar, "RequestPlayerBattle", "CS.BattleMode"));
        il.Emit(OpCodes.Ret);

        var start = new MethodDefinition("RequestLanRoomStart",
            MethodAttributes.Private | MethodAttributes.Static, module.TypeSystem.Void);
        bar.Methods.Add(start);
        il = start.Body.GetILProcessor();
        var packet = module.GetType("CS.NetPacket");
        var proxy = module.GetType("CS.ServerProxyManager");
        var data = new VariableDefinition(packet);
        start.Body.Variables.Add(data);
        il.Emit(OpCodes.Newobj, Method(packet, ".ctor"));
        il.Emit(OpCodes.Stloc, data);
        il.Emit(OpCodes.Ldloc, data);
        il.Emit(OpCodes.Ldc_I4, 211);
        il.Emit(OpCodes.Callvirt, Method(packet, "Push", "System.UInt16"));
        il.Emit(OpCodes.Ldloc, data);
        il.Emit(OpCodes.Callvirt, Method(packet, "UpdateHeader"));
        il.Emit(OpCodes.Call, Method(proxy, "get_Instance"));
        il.Emit(OpCodes.Callvirt, Method(proxy, "GetWorldServerProxy"));
        il.Emit(OpCodes.Ldloc, data);
        il.Emit(OpCodes.Callvirt, Method(module.GetType("CS.NetBuffer"), "get_Base"));
        il.Emit(OpCodes.Callvirt, Method(module.GetType("CS.TcpPeer"), "Send", "System.Byte[]"));
        il.Emit(OpCodes.Ret);

        var setup = Method(bar, "Initialise");
        il = setup.Body.GetILProcessor();
        var engine = module.AssemblyResolver.Resolve(module.AssemblyReferences.Single(a => a.Name == "UnityEngine"));
        var action = module.ImportReference(engine.MainModule.GetType("UnityEngine.Events.UnityAction"));
        var makeAction = new MethodReference(".ctor", module.TypeSystem.Void, action) { HasThis = true };
        makeAction.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
        makeAction.Parameters.Add(new ParameterDefinition(module.TypeSystem.IntPtr));
        Before(il, setup.Body.Instructions.Last(),
            il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, bar.Fields.Single(f => f.Name == "mButtonBattle")),
            il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, bar.Fields.Single(f => f.Name == "mButtonBattleText")),
            il.Create(OpCodes.Ldnull), il.Create(OpCodes.Ldftn, start), il.Create(OpCodes.Newobj, makeAction),
            il.Create(OpCodes.Call, module.ImportReference(Method(room, "Attach",
                "UnityEngine.UI.Button", "UnityEngine.UI.Text", "UnityEngine.Events.UnityAction"))));
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
        var concat = module.ImportReference(typeof(string).GetMethod("Concat", new[] { typeof(string), typeof(string) }));
        var loop = il.Create(OpCodes.Ldloc, index);
        var next = il.Create(OpCodes.Ldloc, index);
        var fallback = il.Create(OpCodes.Ldstr, "pc-");
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
        il.Emit(OpCodes.Ldstr, "|lan-v4");
        il.Emit(OpCodes.Call, concat);
        il.Emit(OpCodes.Ret);
        il.Append(next);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, index);
        il.Emit(OpCodes.Br, loop);
        il.Append(fallback);
        il.Emit(OpCodes.Call, module.ImportReference(typeof(Environment).GetProperty("MachineName").GetGetMethod()));
        il.Emit(OpCodes.Callvirt, module.ImportReference(typeof(string).GetMethod("ToLowerInvariant")));
        il.Emit(OpCodes.Call, concat);
        il.Emit(OpCodes.Ldstr, "|lan-v4");
        il.Emit(OpCodes.Call, concat);
        il.Emit(OpCodes.Ret);
        var account = request.Body.Instructions[1];
        if (account.OpCode != OpCodes.Ldstr || (string)account.Operand != "1")
            throw new InvalidOperationException("Expected the local login patch first.");
        account.OpCode = OpCodes.Call;
        account.Operand = name;
    }

    private static void InstallNickname(ModuleDefinition module)
    {
        const string prefix = "-lan-name=";
        var world = module.GetType("CS.WorldServerProxy");
        var core = module.TypeSystem;
        var args = new VariableDefinition(new ArrayType(core.String));
        var index = new VariableDefinition(core.Int32);
        var arg = new VariableDefinition(core.String);
        var environment = new TypeReference("System", "Environment", module, core.CoreLibrary);
        var readArgs = new MethodReference("GetCommandLineArgs", args.VariableType, environment);
        var startsWith = new MethodReference("StartsWith", core.Boolean, core.String) { HasThis = true };
        startsWith.Parameters.Add(new ParameterDefinition(core.String));
        var substring = new MethodReference("Substring", core.String, core.String) { HasThis = true };
        substring.Parameters.Add(new ParameterDefinition(core.Int32));
        var get = new MethodDefinition("GetLanNickname", MethodAttributes.Private | MethodAttributes.Static, core.String);
        world.Methods.Add(get);
        get.Body.Variables.Add(args);
        get.Body.Variables.Add(index);
        get.Body.Variables.Add(arg);
        var il = get.Body.GetILProcessor();
        var loop = il.Create(OpCodes.Ldloc, index);
        var next = il.Create(OpCodes.Ldloc, index);
        var none = il.Create(OpCodes.Ldnull);
        il.Emit(OpCodes.Call, readArgs);
        il.Emit(OpCodes.Stloc, args);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stloc, index);
        il.Append(loop);
        il.Emit(OpCodes.Ldloc, args);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Bge, none);
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
        il.Append(none);
        il.Emit(OpCodes.Ret);

        var packet = module.GetType("CS.NetPacket");
        var proxy = module.GetType("CS.ServerProxyManager");
        var nick = new VariableDefinition(core.String);
        var data = new VariableDefinition(packet);
        var send = new MethodDefinition("SendLanNickname", MethodAttributes.Private, core.Void);
        world.Methods.Add(send);
        send.Body.Variables.Add(nick);
        send.Body.Variables.Add(data);
        il = send.Body.GetILProcessor();
        var ret = il.Create(OpCodes.Ret);
        il.Emit(OpCodes.Call, get);
        il.Emit(OpCodes.Stloc, nick);
        il.Emit(OpCodes.Ldloc, nick);
        il.Emit(OpCodes.Brfalse, ret);
        il.Emit(OpCodes.Ldloc, nick);
        il.Emit(OpCodes.Callvirt, module.ImportReference(typeof(string).GetProperty("Length").GetGetMethod()));
        il.Emit(OpCodes.Brfalse, ret);
        il.Emit(OpCodes.Newobj, Method(packet, ".ctor"));
        il.Emit(OpCodes.Stloc, data);
        il.Emit(OpCodes.Ldloc, data);
        il.Emit(OpCodes.Ldc_I4, 213);
        il.Emit(OpCodes.Callvirt, Method(packet, "Push", "System.UInt16"));
        il.Emit(OpCodes.Ldloc, data);
        il.Emit(OpCodes.Ldloc, nick);
        il.Emit(OpCodes.Callvirt, Method(packet, "Push", "System.String"));
        il.Emit(OpCodes.Ldloc, data);
        il.Emit(OpCodes.Callvirt, Method(packet, "UpdateHeader"));
        il.Emit(OpCodes.Call, Method(proxy, "get_Instance"));
        il.Emit(OpCodes.Callvirt, Method(proxy, "GetWorldServerProxy"));
        il.Emit(OpCodes.Ldloc, data);
        il.Emit(OpCodes.Callvirt, Method(module.GetType("CS.NetBuffer"), "get_Base"));
        il.Emit(OpCodes.Callvirt, Method(module.GetType("CS.TcpPeer"), "Send", "System.Byte[]"));
        il.Append(ret);

        // LanRoomUI calls this through reflection after the player edits the
        // name in the lobby; the server saves it and re-sends profile 117.
        var sendValue = new MethodDefinition("SendLanNicknameValue",
            MethodAttributes.Public | MethodAttributes.Static, core.Void);
        sendValue.Parameters.Add(new ParameterDefinition("name", ParameterAttributes.None, core.String));
        world.Methods.Add(sendValue);
        var valueData = new VariableDefinition(packet);
        sendValue.Body.Variables.Add(valueData);
        var valueIl = sendValue.Body.GetILProcessor();
        var valueRet = valueIl.Create(OpCodes.Ret);
        valueIl.Emit(OpCodes.Ldarg_0);
        valueIl.Emit(OpCodes.Brfalse, valueRet);
        valueIl.Emit(OpCodes.Ldarg_0);
        valueIl.Emit(OpCodes.Callvirt, module.ImportReference(typeof(string).GetProperty("Length").GetGetMethod()));
        valueIl.Emit(OpCodes.Brfalse, valueRet);
        valueIl.Emit(OpCodes.Newobj, Method(packet, ".ctor"));
        valueIl.Emit(OpCodes.Stloc, valueData);
        valueIl.Emit(OpCodes.Ldloc, valueData);
        valueIl.Emit(OpCodes.Ldc_I4, 213);
        valueIl.Emit(OpCodes.Callvirt, Method(packet, "Push", "System.UInt16"));
        valueIl.Emit(OpCodes.Ldloc, valueData);
        valueIl.Emit(OpCodes.Ldarg_0);
        valueIl.Emit(OpCodes.Callvirt, Method(packet, "Push", "System.String"));
        valueIl.Emit(OpCodes.Ldloc, valueData);
        valueIl.Emit(OpCodes.Callvirt, Method(packet, "UpdateHeader"));
        valueIl.Emit(OpCodes.Call, Method(proxy, "get_Instance"));
        valueIl.Emit(OpCodes.Callvirt, Method(proxy, "GetWorldServerProxy"));
        valueIl.Emit(OpCodes.Ldloc, valueData);
        valueIl.Emit(OpCodes.Callvirt, Method(module.GetType("CS.NetBuffer"), "get_Base"));
        valueIl.Emit(OpCodes.Callvirt, Method(module.GetType("CS.TcpPeer"), "Send", "System.Byte[]"));
        valueIl.Append(valueRet);

        var login = Method(world, "HandleLogin", "System.IO.BinaryReader");
        var ping = login.Body.Instructions.Single(i => i.OpCode == OpCodes.Call &&
            ((MethodReference)i.Operand).Name == "SetPingEnabled");
        Before(login.Body.GetILProcessor(), ping.Next,
            login.Body.GetILProcessor().Create(OpCodes.Ldarg_0),
            login.Body.GetILProcessor().Create(OpCodes.Call, send));

        // Re-sent profile 117 updates LocalPlayer, but the hall's top bar does
        // not redraw itself. Refresh it so a rename is visible immediately.
        var gui = module.GetType("CS.GUISystem");
        var bar = module.GetType("CS.GUIEntitySystemBar");
        var getEntity = new GenericInstanceMethod(gui.Methods.Single(m => m.Name == "GetEntity" &&
            m.HasGenericParameters && m.Parameters.Count == 0));
        getEntity.GenericArguments.Add(module.ImportReference(bar));
        var refresh = module.ImportReference(Method(bar, "RefreshPlayer"));
        var getPlayer = Method(world, "HandleGetPlayer", "System.IO.BinaryReader");
        var playerIl = getPlayer.Body.GetILProcessor();
        var setInfo = getPlayer.Body.Instructions.Single(i => i.OpCode == OpCodes.Callvirt &&
            ((MethodReference)i.Operand).Name == "SetPlayerInfo");
        var barValue = new VariableDefinition(module.ImportReference(bar));
        getPlayer.Body.Variables.Add(barValue);
        var skipRefresh = playerIl.Create(OpCodes.Nop);
        Before(playerIl, setInfo.Next, playerIl.Create(OpCodes.Call, Method(gui, "get_Instance")),
            playerIl.Create(OpCodes.Callvirt, getEntity), playerIl.Create(OpCodes.Stloc, barValue),
            playerIl.Create(OpCodes.Ldloc, barValue), playerIl.Create(OpCodes.Brfalse, skipRefresh),
            playerIl.Create(OpCodes.Ldloc, barValue), playerIl.Create(OpCodes.Callvirt, refresh),
            skipRefresh);
    }

    private static void InstallUnitLeave(ModuleDefinition module)
    {
        var battle = module.GetType("CS.Battle");
        var unit = module.GetType("CS.Unit");
        var core = module.TypeSystem;
        var units = battle.Fields.Single(f => f.Name == "mUnits");
        var getUnit = battle.Methods.Single(m => m.Name == "GetUnit");
        var destroy = unit.Methods.Single(m => m.Name == "Destroy" && m.Parameters.Count == 0);

        var remove = new MethodReference("Remove", core.Boolean, units.FieldType) { HasThis = true };
        remove.Parameters.Add(new ParameterDefinition(unit));

        var method = new MethodDefinition("RemoveUnit", MethodAttributes.Public, core.Void);
        method.Parameters.Add(new ParameterDefinition("id", ParameterAttributes.None, core.UInt32));
        battle.Methods.Add(method);
        var unitValue = new VariableDefinition(unit);
        method.Body.Variables.Add(unitValue);
        var il = method.Body.GetILProcessor();
        var ret = il.Create(OpCodes.Ret);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, getUnit);
        il.Emit(OpCodes.Stloc, unitValue);
        il.Emit(OpCodes.Ldloc, unitValue);
        il.Emit(OpCodes.Brfalse, ret);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, units);
        il.Emit(OpCodes.Ldloc, unitValue);
        il.Emit(OpCodes.Callvirt, remove);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldloc, unitValue);
        il.Emit(OpCodes.Callvirt, destroy);
        il.Append(ret);

        // The server sends 214 when a battle connection drops. Remove that unit
        // instead of leaving the other clients with a frozen character.
        var handle = battle.Methods.Single(m => m.Name == "HandleMessages");
        var instructions = handle.Body.GetILProcessor();
        var original = handle.Body.Instructions[0];
        var readId = module.ImportReference(typeof(System.IO.BinaryReader).GetMethod("ReadUInt32"));
        instructions.InsertBefore(original, instructions.Create(OpCodes.Ldarg_1));
        instructions.InsertBefore(original, instructions.Create(OpCodes.Ldc_I4, 214));
        instructions.InsertBefore(original, instructions.Create(OpCodes.Bne_Un, original));
        instructions.InsertBefore(original, instructions.Create(OpCodes.Ldarg_0));
        instructions.InsertBefore(original, instructions.Create(OpCodes.Ldarg_2));
        instructions.InsertBefore(original, instructions.Create(OpCodes.Callvirt, readId));
        instructions.InsertBefore(original, instructions.Create(OpCodes.Call, method));
        instructions.InsertBefore(original, instructions.Create(OpCodes.Ret));
    }

    private static void InstallReconnect(ModuleDefinition module, ModuleDefinition uiModule)
    {
        var battleProxy = module.GetType("CS.BattleTcpServerProxy");
        var local = module.GetType("CS.LocalPlayer");
        var info = module.GetType("CS.BattleServerInfo");
        var packet = module.GetType("CS.NetPacket");
        var manager = module.GetType("CS.ServerProxyManager");
        var duid = module.GetType("CS.DUID");
        var buffer = module.GetType("CS.NetBuffer");
        var peer = module.GetType("CS.TcpPeer");
        var core = module.TypeSystem;

        // Debug trace: one line per probe with a wall-clock timestamp. Written
        // to the client working directory as mbg-reconnect.log.
        var trace = new MethodDefinition("LanTrace", MethodAttributes.Public | MethodAttributes.Static, core.Void);
        trace.Parameters.Add(new ParameterDefinition("tag", ParameterAttributes.None, core.String));
        trace.Parameters.Add(new ParameterDefinition("id", ParameterAttributes.None, core.Int32));
        battleProxy.Methods.Add(trace);
        var traceIl = trace.Body.GetILProcessor();
        traceIl.Emit(OpCodes.Ldarg_0);
        traceIl.Emit(OpCodes.Ldarg_1);
        traceIl.Emit(OpCodes.Call, module.ImportReference(Method(uiModule.GetType("EcsDiagnostics"), "Trace", "System.String", "System.Int32")));
        traceIl.Emit(OpCodes.Ret);

        // Operand references lifted from the original OnDisconnect giving-up
        // path so the reconnect tick can exit the same way (StageLogin + 10107).
        var disconnectMethod = battleProxy.Methods.Single(m => m.Name == "OnDisconnect");
        var exitOperands = disconnectMethod.Body.Instructions
            .Where(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference)
            .Select(i => (MethodReference)i.Operand).ToList();
        var setCurrentStageLogin = exitOperands.First(m => m.Name == "SetCurrentStage");

        var reconnect = new MethodDefinition("ReconnectLanBattle", MethodAttributes.Public, core.Boolean);
        battleProxy.Methods.Add(reconnect);
        var serverInfo = new VariableDefinition(info);
        var request = new VariableDefinition(packet);
        var attempts = new VariableDefinition(core.Int32);
        reconnect.Body.Variables.Add(serverInfo);
        reconnect.Body.Variables.Add(request);
        reconnect.Body.Variables.Add(attempts);
        var il = reconnect.Body.GetILProcessor();
        var fail = il.Create(OpCodes.Ldc_I4_0);
        var loop = il.Create(OpCodes.Nop);
        var next = il.Create(OpCodes.Nop);

        il.Emit(OpCodes.Call, Method(local, "get_Instance"));
        il.Emit(OpCodes.Callvirt, Method(local, "GetBattleServerInfo"));
        il.Emit(OpCodes.Stloc, serverInfo);
        il.Emit(OpCodes.Ldloc, serverInfo);
        il.Emit(OpCodes.Brfalse, fail);
        il.Emit(OpCodes.Ldloc, serverInfo);
        il.Emit(OpCodes.Ldfld, info.Fields.Single(f => f.Name == "mAddress"));
        il.Emit(OpCodes.Brfalse, fail);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, attempts);
        il.Append(loop);
        il.Emit(OpCodes.Call, Method(manager, "get_Instance"));
        il.Emit(OpCodes.Ldloc, serverInfo);
        il.Emit(OpCodes.Ldfld, info.Fields.Single(f => f.Name == "mAddress"));
        il.Emit(OpCodes.Ldloc, serverInfo);
        il.Emit(OpCodes.Ldfld, info.Fields.Single(f => f.Name == "mTcpPort"));
        il.Emit(OpCodes.Callvirt, Method(manager, "ConnectToBattleTcpServer", "System.String", "System.Int32"));
        il.Emit(OpCodes.Brfalse, next);
        Trace(il, trace, "rc", 15);

        il.Emit(OpCodes.Newobj, Method(packet, ".ctor"));
        il.Emit(OpCodes.Stloc, request);
        il.Emit(OpCodes.Ldloc, request);
        il.Emit(OpCodes.Ldc_I4, 34);
        il.Emit(OpCodes.Callvirt, Method(packet, "Push", "System.UInt16"));
        il.Emit(OpCodes.Ldloc, request);
        il.Emit(OpCodes.Call, Method(local, "get_Instance"));
        il.Emit(OpCodes.Callvirt, Method(local, "GetBattleID"));
        il.Emit(OpCodes.Callvirt, Method(packet, "Push", "System.UInt32"));
        il.Emit(OpCodes.Ldloc, request);
        il.Emit(OpCodes.Call, Method(local, "get_Instance"));
        il.Emit(OpCodes.Callvirt, Method(local, "GetDUID"));
        il.Emit(OpCodes.Callvirt, Method(duid, "GetMain"));
        il.Emit(OpCodes.Callvirt, Method(packet, "Push", "System.UInt16"));
        il.Emit(OpCodes.Ldloc, request);
        il.Emit(OpCodes.Call, Method(local, "get_Instance"));
        il.Emit(OpCodes.Callvirt, Method(local, "GetDUID"));
        il.Emit(OpCodes.Callvirt, Method(duid, "GetSub"));
        il.Emit(OpCodes.Callvirt, Method(packet, "Push", "System.UInt32"));
        il.Emit(OpCodes.Ldloc, request);
        il.Emit(OpCodes.Callvirt, Method(packet, "UpdateHeader"));
        il.Emit(OpCodes.Call, Method(manager, "get_Instance"));
        il.Emit(OpCodes.Callvirt, Method(manager, "GetBattleTcpServerProxy"));
        il.Emit(OpCodes.Ldloc, request);
        il.Emit(OpCodes.Callvirt, Method(buffer, "get_Base"));
        il.Emit(OpCodes.Callvirt, Method(peer, "Send", "System.Byte[]"));
        Trace(il, trace, "rc", 16);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);

        il.Append(next);
        il.Emit(OpCodes.Ldloc, attempts);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Stloc, attempts);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Blt, loop);
        il.Append(fail);
        Trace(il, trace, "rc", 14);
        il.Emit(OpCodes.Ret);

        // The disconnect callback may run on Unity's main thread. Keep it
        // non-blocking: retry at most once per UpdateInput tick.
        var pending = new FieldDefinition("mLanReconnectPending",
            FieldAttributes.Private | FieldAttributes.Static, core.Boolean);
        var givingUp = new FieldDefinition("mLanReconnectGivingUp",
            FieldAttributes.Private | FieldAttributes.Static, core.Boolean);
        var attemptsMade = new FieldDefinition("mLanReconnectAttempts",
            FieldAttributes.Private | FieldAttributes.Static, core.Int32);
        var retryAt = new FieldDefinition("mLanReconnectRetryAt",
            FieldAttributes.Private | FieldAttributes.Static, core.Single);
        battleProxy.Fields.Add(pending);
        battleProxy.Fields.Add(givingUp);
        battleProxy.Fields.Add(attemptsMade);
        battleProxy.Fields.Add(retryAt);
        var inProgress = new FieldDefinition("mLanReconnectInProgress",
            FieldAttributes.Private | FieldAttributes.Static, core.Boolean);
        battleProxy.Fields.Add(inProgress);
        // Set by the (receive-thread) world disconnect handler; consumed by
        // TickLanReconnect on the main thread, which owns stage transitions.
        var worldDrop = new FieldDefinition("mLanWorldDropPending",
            FieldAttributes.Private | FieldAttributes.Static, core.Boolean);
        battleProxy.Fields.Add(worldDrop);
        var loginRetryAt = new FieldDefinition("mLanLoginRetryAt",
            FieldAttributes.Private | FieldAttributes.Static, core.Single);
        battleProxy.Fields.Add(loginRetryAt);

        var engine = module.AssemblyResolver.Resolve(
            module.AssemblyReferences.Single(a => a.Name == "UnityEngine")).MainModule;
        var realtime = module.ImportReference(
            Method(engine.GetType("UnityEngine.Time"), "get_realtimeSinceStartup"));
        var tick = new MethodDefinition("TickLanReconnect",
            MethodAttributes.Public | MethodAttributes.Static, core.Void);
        battleProxy.Methods.Add(tick);
        var tickIl = tick.Body.GetILProcessor();
        var done = tickIl.Create(OpCodes.Ret);
        var failed = tickIl.Create(OpCodes.Nop);
        var later = tickIl.Create(OpCodes.Nop);
        // A dead world connection must not leave a stage from its receive
        // thread (Unity teardown off the main thread crashes the player).
        // Consume the pending drop here on the main thread instead; the
        // server is gone, so skip straight back to the login stage.
        var worldEntry = tickIl.Create(OpCodes.Nop);
        var enteredTimes = module.GetType("CS.StageLogin").Fields.Single(f => f.Name == "EnteredTimes");
        tickIl.Emit(OpCodes.Ldsfld, worldDrop);
        tickIl.Emit(OpCodes.Brfalse, worldEntry);
        tickIl.Emit(OpCodes.Ldc_I4_0);
        tickIl.Emit(OpCodes.Stsfld, worldDrop);
        tickIl.Emit(OpCodes.Ldc_I4_0);
        tickIl.Emit(OpCodes.Stsfld, pending);
        tickIl.Emit(OpCodes.Ldc_I4_0);
        tickIl.Emit(OpCodes.Stsfld, givingUp);
        tickIl.Emit(OpCodes.Call, Method(manager, "get_Instance"));
        tickIl.Emit(OpCodes.Callvirt, Method(manager, "DisconnectAll"));
        tickIl.Emit(OpCodes.Ldc_I4_0);
        tickIl.Emit(OpCodes.Stsfld, enteredTimes);
        tickIl.Emit(OpCodes.Call, Method(module.GetType("CS.StageManager"), "get_Instance"));
        tickIl.Emit(OpCodes.Callvirt, setCurrentStageLogin);
        tickIl.Emit(OpCodes.Pop);
        tickIl.Emit(OpCodes.Call, Method(module.GetType("CS.MessageboxManager"), "get_Instance"));
        tickIl.Emit(OpCodes.Call, Method(module.GetType("CS.TextDataManager"), "get_Instance"));
        tickIl.Emit(OpCodes.Ldc_I4, 10108);
        tickIl.Emit(OpCodes.Ldc_I4_1);
        tickIl.Emit(OpCodes.Callvirt, Method(module.GetType("CS.TextDataManager"), "GetData", "System.UInt32", "System.Boolean"));
        tickIl.Emit(OpCodes.Ldc_I4_0);
        tickIl.Emit(OpCodes.Callvirt, Method(module.GetType("CS.MessageboxManager"), "Show", "System.String", "CS.Messagebox/Buttons"));
        tickIl.Emit(OpCodes.Pop);
        tickIl.Emit(OpCodes.Ret);
        tickIl.Append(worldEntry);
        // The LAN auto-login fires only on the first stage entry
        // (StageLogin.EnteredTimes == 1), so a client pushed back to the
        // login stage after a server death spins forever. When the login
        // stage has no live connection, re-enter it with a reset counter
        // so the auto-login arms again; one attempt per 3s.
        var retryEnd = tickIl.Create(OpCodes.Nop);
        var retryFire = tickIl.Create(OpCodes.Nop);
        var retrySocket = tickIl.Create(OpCodes.Nop);
        var retryStage = tickIl.Create(OpCodes.Nop);
        // isinst + dup: both the jump and fall-through paths must leave an
        // empty stack, so every skipped value is popped explicitly.
        tickIl.Emit(OpCodes.Call, Method(module.GetType("CS.StageManager"), "get_Instance"));
        tickIl.Emit(OpCodes.Callvirt, Method(module.GetType("CS.StageManager"), "GetCurrentStage"));
        tickIl.Emit(OpCodes.Isinst, module.GetType("CS.StageLogin"));
        tickIl.Emit(OpCodes.Dup);
        tickIl.Emit(OpCodes.Brtrue, retryStage);
        tickIl.Emit(OpCodes.Pop);
        tickIl.Emit(OpCodes.Br, retryEnd);
        tickIl.Append(retryStage);
        tickIl.Emit(OpCodes.Pop);
        tickIl.Emit(OpCodes.Call, Method(manager, "get_Instance"));
        tickIl.Emit(OpCodes.Callvirt, Method(manager, "GetLoginServerProxy"));
        tickIl.Emit(OpCodes.Dup);
        tickIl.Emit(OpCodes.Brtrue, retrySocket);
        tickIl.Emit(OpCodes.Pop);
        tickIl.Emit(OpCodes.Br, retryFire);
        tickIl.Append(retrySocket);
        tickIl.Emit(OpCodes.Callvirt, Method(peer, "GetSocket"));
        tickIl.Emit(OpCodes.Brfalse, retryFire);
        tickIl.Emit(OpCodes.Br, retryEnd);
        tickIl.Append(retryFire);
        tickIl.Emit(OpCodes.Call, realtime);
        tickIl.Emit(OpCodes.Ldsfld, loginRetryAt);
        tickIl.Emit(OpCodes.Blt, retryEnd);
        tickIl.Emit(OpCodes.Call, realtime);
        tickIl.Emit(OpCodes.Ldc_R4, 3f);
        tickIl.Emit(OpCodes.Add);
        tickIl.Emit(OpCodes.Stsfld, loginRetryAt);
        tickIl.Emit(OpCodes.Ldc_I4_0);
        tickIl.Emit(OpCodes.Stsfld, enteredTimes);
        tickIl.Emit(OpCodes.Call, Method(module.GetType("CS.StageManager"), "get_Instance"));
        tickIl.Emit(OpCodes.Callvirt, setCurrentStageLogin);
        tickIl.Emit(OpCodes.Pop);
        tickIl.Append(retryEnd);
        tickIl.Emit(OpCodes.Ldsfld, pending);
        tickIl.Emit(OpCodes.Brfalse, done);
        tickIl.Emit(OpCodes.Call, realtime);
        tickIl.Emit(OpCodes.Ldsfld, retryAt);
        tickIl.Emit(OpCodes.Blt, done);
        Trace(tickIl, trace, "tick", 10);
        tickIl.Emit(OpCodes.Ldsfld, attemptsMade);
        tickIl.Emit(OpCodes.Ldc_I4_S, (sbyte)20);
        tickIl.Emit(OpCodes.Bge, failed);
        tickIl.Emit(OpCodes.Ldsfld, attemptsMade);
        tickIl.Emit(OpCodes.Ldc_I4_1);
        tickIl.Emit(OpCodes.Add);
        tickIl.Emit(OpCodes.Stsfld, attemptsMade);
        tickIl.Emit(OpCodes.Ldc_I4_1);
        tickIl.Emit(OpCodes.Stsfld, inProgress);
        tickIl.Emit(OpCodes.Call, realtime);
        tickIl.Emit(OpCodes.Ldc_R4, 1.5f);
        tickIl.Emit(OpCodes.Add);
        tickIl.Emit(OpCodes.Stsfld, retryAt);
        tickIl.Emit(OpCodes.Call, Method(manager, "get_Instance"));
        tickIl.Emit(OpCodes.Callvirt, Method(manager, "GetBattleTcpServerProxy"));
        tickIl.Emit(OpCodes.Callvirt, reconnect);
        tickIl.Emit(OpCodes.Ldc_I4_0);
        tickIl.Emit(OpCodes.Stsfld, inProgress);
        tickIl.Emit(OpCodes.Brfalse, failed);
        Trace(tickIl, trace, "tick", 11);
        tickIl.Emit(OpCodes.Ldc_I4_0);
        tickIl.Emit(OpCodes.Stsfld, pending);
        tickIl.Emit(OpCodes.Br, done);
        tickIl.Append(failed);
        Trace(tickIl, trace, "tick", 12);
        tickIl.Emit(OpCodes.Ldsfld, attemptsMade);
        tickIl.Emit(OpCodes.Ldc_I4_S, (sbyte)20);
        tickIl.Emit(OpCodes.Blt, later);
        Trace(tickIl, trace, "tick", 13);
        tickIl.Emit(OpCodes.Ldc_I4_0);
        tickIl.Emit(OpCodes.Stsfld, pending);
        tickIl.Emit(OpCodes.Ldc_I4_1);
        tickIl.Emit(OpCodes.Stsfld, givingUp);
        tickIl.Emit(OpCodes.Call, Method(manager, "get_Instance"));
        tickIl.Emit(OpCodes.Callvirt, Method(manager, "DisconnectAll"));
        // Mirror the original giving-up exit: only pull the client back to the
        // login stage when a battle scene is actually still running, so an
        // already-ended round (result screen) is not interrupted.
        var stageBattleExit = tickIl.Create(OpCodes.Nop);
        var stageType = module.GetType("CS.StageBattle");
        var battleType = module.GetType("CS.Battle");
        var exitStage = new VariableDefinition(stageType);
        var exitBattle = new VariableDefinition(battleType);
        tick.Body.Variables.Add(exitStage);
        tick.Body.Variables.Add(exitBattle);
        tickIl.Emit(OpCodes.Call, Method(module.GetType("CS.StageManager"), "get_Instance"));
        tickIl.Emit(OpCodes.Callvirt, Method(module.GetType("CS.StageManager"), "GetCurrentStage"));
        tickIl.Emit(OpCodes.Isinst, module.GetType("CS.StageBattle"));
        tickIl.Emit(OpCodes.Stloc, exitStage);
        tickIl.Emit(OpCodes.Ldloc, exitStage);
        tickIl.Emit(OpCodes.Brfalse, stageBattleExit);
        tickIl.Emit(OpCodes.Ldloc, exitStage);
        tickIl.Emit(OpCodes.Callvirt, Method(stageType, "GetBattle"));
        tickIl.Emit(OpCodes.Stloc, exitBattle);
        tickIl.Emit(OpCodes.Ldloc, exitBattle);
        tickIl.Emit(OpCodes.Brfalse, stageBattleExit);
        tickIl.Emit(OpCodes.Ldloc, exitBattle);
        tickIl.Emit(OpCodes.Callvirt, Method(battleType, "GetState"));
        tickIl.Emit(OpCodes.Ldc_I4_4);
        tickIl.Emit(OpCodes.Bge, stageBattleExit);
        tickIl.Emit(OpCodes.Call, Method(manager, "get_Instance"));
        tickIl.Emit(OpCodes.Callvirt, Method(manager, "DisconnectAll"));
        tickIl.Emit(OpCodes.Ldc_I4_0);
        tickIl.Emit(OpCodes.Stsfld, enteredTimes);
        tickIl.Emit(OpCodes.Call, Method(module.GetType("CS.StageManager"), "get_Instance"));
        tickIl.Emit(OpCodes.Callvirt, setCurrentStageLogin);
        tickIl.Emit(OpCodes.Pop);
        tickIl.Emit(OpCodes.Call, Method(module.GetType("CS.MessageboxManager"), "get_Instance"));
        tickIl.Emit(OpCodes.Call, Method(module.GetType("CS.TextDataManager"), "get_Instance"));
        tickIl.Emit(OpCodes.Ldc_I4, 10107);
        tickIl.Emit(OpCodes.Ldc_I4_1);
        tickIl.Emit(OpCodes.Callvirt, Method(module.GetType("CS.TextDataManager"), "GetData", "System.UInt32", "System.Boolean"));
        tickIl.Emit(OpCodes.Ldc_I4_0);
        tickIl.Emit(OpCodes.Callvirt, Method(module.GetType("CS.MessageboxManager"), "Show", "System.String", "CS.Messagebox/Buttons"));
        tickIl.Emit(OpCodes.Pop);
        tickIl.Append(stageBattleExit);
        tickIl.Emit(OpCodes.Br, done);
        tickIl.Append(later);
        tickIl.Emit(OpCodes.Call, realtime);
        tickIl.Emit(OpCodes.Ldc_R4, 1.5f);
        tickIl.Emit(OpCodes.Add);
        tickIl.Emit(OpCodes.Stsfld, retryAt);
        tickIl.Append(done);

        var traceRef = (MethodReference)trace;

        var basePeer = module.GetType("CS.TcpPeer");
        if (basePeer != null)
        {
            // Receive == 0 means graceful EOF. The original loop polls and
            // receives zero forever, leaving login/world sockets in CLOSE_WAIT.
            // Close only the socket here; Update drains the final buffered frame
            // before invoking the existing main-thread disconnect callback.
            var receiveLoop = Method(basePeer, "StartReceive");
            var received = receiveLoop.Body.Instructions.Single(i =>
                i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference &&
                ((MethodReference)i.Operand).Name == "Receive");
            var eofBranch = receiveLoop.Body.Instructions.SkipWhile(i => i != received)
                .First(i => i.OpCode == OpCodes.Ble || i.OpCode == OpCodes.Ble_S);
            var positiveRead = eofBranch.Next;
            eofBranch.OpCode = OpCodes.Bgt;
            eofBranch.Operand = positiveRead;
            var receiveIl = receiveLoop.Body.GetILProcessor();
            receiveIl.InsertBefore(positiveRead, receiveIl.Create(OpCodes.Ldarg_0));
            receiveIl.InsertBefore(positiveRead, receiveIl.Create(OpCodes.Call, Method(basePeer, "Close")));
            receiveIl.InsertBefore(positiveRead, receiveIl.Create(OpCodes.Leave,
                receiveLoop.Body.Instructions.Last(i => i.OpCode == OpCodes.Ret)));

            foreach (var name in new[] { "StartReceive", "DispatchRecvBuffer" })
            {
                var m = basePeer.Methods.FirstOrDefault(x => x.Name == name && x.Parameters.Count == 0);
                if (m == null) continue;
                var catchHandler = m.Body.ExceptionHandlers.FirstOrDefault(h => h.HandlerType == ExceptionHandlerType.Catch);
                if (catchHandler == null) continue;
                var close = m.Body.Instructions.SkipWhile(i => i != catchHandler.HandlerStart).FirstOrDefault(i =>
                    (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
                    i.Operand != null && ((MethodReference)i.Operand).Name == "Close");
                if (close == null) continue;
                var mi = m.Body.GetILProcessor();
                // Trace which native close path fired (receive-thread exception
                // vs dispatch exception) before closing the socket.
                mi.InsertBefore(close, mi.Create(OpCodes.Ldstr, name == "StartReceive" ? "sr" : "dr"));
                mi.InsertBefore(close, mi.Create(OpCodes.Ldc_I4, name == "StartReceive" ? 40 : 41));
                mi.InsertBefore(close, mi.Create(OpCodes.Call, trace));
                // The original DispatchRecvBuffer catch already calls
                // OnDisconnect; only add the call where it is missing.
                var afterClose = close.Next;
                var alreadyNotified = afterClose != null && afterClose.OpCode == OpCodes.Ldarg_0 &&
                    afterClose.Next != null && afterClose.Next.OpCode == OpCodes.Callvirt &&
                    afterClose.Next.Operand is MethodReference &&
                    ((MethodReference)afterClose.Next.Operand).Name == "OnDisconnect";
                if (!alreadyNotified)
                {
                    mi.InsertAfter(close, mi.Create(OpCodes.Ldarg_0));
                    mi.InsertAfter(close.Next, mi.Create(OpCodes.Callvirt, Method(basePeer, "OnDisconnect")));
                }
            }

            // Trace send failures: a send error closes the socket from the
            // client side and must show up in the trace.
            var send = basePeer.Methods.FirstOrDefault(x => x.Name == "Send" && x.Parameters.Count == 1);
            if (send != null)
            {
                var close = send.Body.Instructions.FirstOrDefault(i =>
                    (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
                    i.Operand != null && ((MethodReference)i.Operand).Name == "Close");
                if (close != null)
                {
                    var si = send.Body.GetILProcessor();
                    si.InsertBefore(close, si.Create(OpCodes.Ldstr, "sf"));
                    si.InsertBefore(close, si.Create(OpCodes.Ldc_I4, 50));
                    si.InsertBefore(close, si.Create(OpCodes.Call, trace));
                }
            }

            // Trace the Update-path disconnect (socket null with drained
            // buffer), gated to battle proxies only so login/world traffic
            // does not pollute the trace.
            var update = basePeer.Methods.FirstOrDefault(x => x.Name == "Update" && x.Parameters.Count == 1);
            if (update != null)
            {
                // Dispatch has already delivered every complete frame. At EOF
                // an incomplete trailing frame can never be completed; discard
                // that tail rather than keeping a dead peer alive forever.
                var sizeAtEof = update.Body.Instructions.Single(i =>
                    i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference &&
                    ((MethodReference)i.Operand).DeclaringType.FullName == "CS.NetBuffer" &&
                    ((MethodReference)i.Operand).Name == "get_Size");
                if (sizeAtEof.Next.OpCode != OpCodes.Ldc_I4_0 || sizeAtEof.Next.Next.OpCode != OpCodes.Bgt)
                    throw new InvalidOperationException("Unexpected TCP EOF buffer guard");
                sizeAtEof.Operand = Method(module.GetType("CS.NetBuffer"), "Clear");
                sizeAtEof.Next.OpCode = OpCodes.Nop;
                sizeAtEof.Next.Next.OpCode = OpCodes.Nop;
                sizeAtEof.Next.Next.Operand = null;
                var call = update.Body.Instructions.FirstOrDefault(i =>
                    i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference &&
                    ((MethodReference)i.Operand).Name == "OnDisconnect");
                if (call != null)
                {
                    var ui = update.Body.GetILProcessor();
                    var skip = ui.Create(OpCodes.Nop);
                    // Each instruction lands directly before the OnDisconnect
                    // call, so the final sequence is
                    // ldarg.0/isinst/brfalse/trace/skip.
                    ui.InsertBefore(call, ui.Create(OpCodes.Ldarg_0));
                    ui.InsertBefore(call, ui.Create(OpCodes.Isinst, battleProxy));
                    ui.InsertBefore(call, ui.Create(OpCodes.Brfalse, skip));
                    ui.InsertBefore(call, ui.Create(OpCodes.Ldstr, "up"));
                    ui.InsertBefore(call, ui.Create(OpCodes.Ldc_I4, 60));
                    ui.InsertBefore(call, ui.Create(OpCodes.Call, trace));
                    ui.InsertBefore(call, skip);
                }
            }
        }

        var shadowBattle = module.GetType("CS.Battle");
        var shadowLoaded = shadowBattle == null ? null : shadowBattle.Methods.FirstOrDefault(m => m.Name == "HandleBattleLoaded");
        var qualityType = engine.GetType("UnityEngine.QualitySettings");
        var setShadowDistance = module.ImportReference(Method(qualityType, "set_shadowDistance", "System.Single"));
        if (shadowLoaded != null)
        {
            var si = shadowLoaded.Body.GetILProcessor();
            var shadowFirst = shadowLoaded.Body.Instructions[0];
            // A single cascade stretched over 300m rendered distant tree
            // shadows as blotchy artifacts that players read as a texture defect.
            // Four cascades, VeryHigh resolution and CloseFit projection keep
            // shadows fine-grained at 150m instead of losing them entirely.
            si.InsertBefore(shadowFirst, si.Create(OpCodes.Ldc_R4, 150f));
            si.InsertBefore(shadowFirst, si.Create(OpCodes.Call, setShadowDistance));
            si.InsertBefore(shadowFirst, si.Create(OpCodes.Ldc_I4_4));
            si.InsertBefore(shadowFirst, si.Create(OpCodes.Call, module.ImportReference(Method(qualityType, "set_shadowCascades", "System.Int32"))));
            si.InsertBefore(shadowFirst, si.Create(OpCodes.Ldc_I4_3));
            si.InsertBefore(shadowFirst, si.Create(OpCodes.Call, module.ImportReference(Method(qualityType, "set_shadowResolution", "UnityEngine.ShadowResolution"))));
            si.InsertBefore(shadowFirst, si.Create(OpCodes.Ldc_I4_0));
            si.InsertBefore(shadowFirst, si.Create(OpCodes.Call, module.ImportReference(Method(qualityType, "set_shadowProjection", "UnityEngine.ShadowProjection"))));
        }

        var lastTcpRecv = new FieldDefinition("mLanLastTcpRecv",
            FieldAttributes.Private | FieldAttributes.Static, core.Single);
        var tcpActive = new FieldDefinition("mLanTcpActive",
            FieldAttributes.Private | FieldAttributes.Static, core.Boolean);
        battleProxy.Fields.Add(lastTcpRecv);
        battleProxy.Fields.Add(tcpActive);

        var heartbeat = new MethodDefinition("TickLanTcpHeartbeat",
            MethodAttributes.Public | MethodAttributes.Static, core.Void);
        battleProxy.Methods.Add(heartbeat);
        var hi = heartbeat.Body.GetILProcessor();
        var hbDone = hi.Create(OpCodes.Ret);
        var heartbeatPeer = new VariableDefinition(battleProxy);
        heartbeat.Body.Variables.Add(heartbeatPeer);
        heartbeat.Body.InitLocals = true;
        hi.Emit(OpCodes.Ldsfld, tcpActive);
        hi.Emit(OpCodes.Brfalse, hbDone);
        hi.Emit(OpCodes.Call, realtime);
        hi.Emit(OpCodes.Ldsfld, lastTcpRecv);
        hi.Emit(OpCodes.Sub);
        hi.Emit(OpCodes.Ldc_R4, 8f);
        hi.Emit(OpCodes.Ble, hbDone);
        hi.Emit(OpCodes.Ldc_I4_0);
        hi.Emit(OpCodes.Stsfld, tcpActive);
        Trace(hi, trace, "hb", 20);
        hi.Emit(OpCodes.Call, Method(manager, "get_Instance"));
        hi.Emit(OpCodes.Callvirt, Method(manager, "GetBattleTcpServerProxy"));
        hi.Emit(OpCodes.Stloc, heartbeatPeer);
        hi.Emit(OpCodes.Ldloc, heartbeatPeer);
        hi.Emit(OpCodes.Brfalse, hbDone);
        hi.Emit(OpCodes.Ldloc, heartbeatPeer);
        hi.Emit(OpCodes.Callvirt, Method(battleProxy, "OnDisconnect"));
        hi.Append(hbDone);

        var recvHeartbeat = battleProxy.Methods.FirstOrDefault(m => m.Name == "OnReceive" && m.Parameters.Count == 1);
        if (recvHeartbeat != null)
        {
            var ri = recvHeartbeat.Body.GetILProcessor();
            var rf = recvHeartbeat.Body.Instructions[0];
            ri.InsertBefore(rf, ri.Create(OpCodes.Call, realtime));
            ri.InsertBefore(rf, ri.Create(OpCodes.Stsfld, lastTcpRecv));
            ri.InsertBefore(rf, ri.Create(OpCodes.Ldc_I4_1));
            ri.InsertBefore(rf, ri.Create(OpCodes.Stsfld, tcpActive));
            // Trace every received battle packet with its message id so the
            // trace shows whether ping echoes and snapshot bursts arrive.
            var msgIdStore = recvHeartbeat.Body.Instructions.FirstOrDefault(i => i.OpCode == OpCodes.Stloc_3);
            if (msgIdStore != null)
            {
                ri.InsertAfter(msgIdStore, ri.Create(OpCodes.Ldstr, "rx"));
                ri.InsertAfter(msgIdStore.Next, ri.Create(OpCodes.Ldloc_3));
                ri.InsertAfter(msgIdStore.Next.Next, ri.Create(OpCodes.Call, trace));
            }
        }

        // Trace the join-battle response: entry and the stage re-entry, which
        // is the reconnect path most likely to misbehave.
        var joinBattle = battleProxy.Methods.FirstOrDefault(m => m.Name == "HandleJoinBattle");
        if (joinBattle != null)
        {
            var ji = joinBattle.Body.GetILProcessor();
            var joinEntry = joinBattle.Body.Instructions[0];
            ji.InsertBefore(joinEntry, ji.Create(OpCodes.Ldarg_0));
            ji.InsertBefore(joinEntry, ji.Create(OpCodes.Call, module.ImportReference(
                Method(uiModule.GetType("EcsBattleConnect"), "JoinReceived", "System.Object"))));
            InsertTrace(ji, joinBattle.Body.Instructions[0], trace, "jb", 30);
            var stageSwitch = joinBattle.Body.Instructions.FirstOrDefault(i =>
                (i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Call) &&
                i.Operand is MethodReference && ((MethodReference)i.Operand).Name == "SetCurrentStage");
            if (stageSwitch != null && stageSwitch.Next != null)
            {
                ji.InsertBefore(stageSwitch.Next, ji.Create(OpCodes.Ldc_I4_0));
                ji.InsertBefore(stageSwitch.Next.Next, ji.Create(OpCodes.Stsfld, attemptsMade));
                InsertTrace(ji, stageSwitch.Next, trace, "jb", 31);
            }
        }

        // Reconnect must be driven by a component that keeps updating after
        // the battle input component is disabled. Main.Update is always alive.
        var main = module.GetType("Main");
        var mainUpdate = main == null ? null : main.Methods.FirstOrDefault(m => m.Name == "Update");
        if (mainUpdate != null)
        {
            mainUpdate.Body.GetILProcessor().InsertBefore(
                mainUpdate.Body.Instructions[0], mainUpdate.Body.GetILProcessor().Create(OpCodes.Call, tick));
            mainUpdate.Body.GetILProcessor().InsertBefore(
                mainUpdate.Body.Instructions[0], mainUpdate.Body.GetILProcessor().Create(OpCodes.Call, heartbeat));
        }
        else
        {
            var input = Method(module.GetType("CS.LocalController"), "UpdateInput", "System.Single");
            input.Body.GetILProcessor().InsertBefore(input.Body.Instructions[0], input.Body.GetILProcessor().Create(OpCodes.Call, tick));
        }

        // Bypass the original DisconnectAll path while recovery is active.
        // Insert at entry with an empty evaluation stack, not immediately
        // before the original callvirt (which already has an instance pushed).
        var disconnect = battleProxy.Methods.Single(m => m.Name == "OnDisconnect");
        var onDisconnect = disconnect.Body.GetILProcessor();
        var first = disconnect.Body.Instructions[0];
        var alreadyQueued = onDisconnect.Create(OpCodes.Ret);
        var givingUpExit = onDisconnect.Create(OpCodes.Ldstr, "od");
        InsertTrace(onDisconnect, first, trace, "od", 0);
        // Ignore disconnects from a replaced/stale proxy. ConnectToBattleTcpServer
        // swaps this instance; its old callback must not re-arm pending after a
        // successful reconnect.
        onDisconnect.InsertBefore(first, onDisconnect.Create(OpCodes.Ldarg_0));
        onDisconnect.InsertBefore(first, onDisconnect.Create(OpCodes.Call, Method(manager, "get_Instance")));
        onDisconnect.InsertBefore(first, onDisconnect.Create(OpCodes.Callvirt, Method(manager, "GetBattleTcpServerProxy")));
        onDisconnect.InsertBefore(first, onDisconnect.Create(OpCodes.Bne_Un, alreadyQueued));
        onDisconnect.InsertBefore(first, onDisconnect.Create(OpCodes.Ldc_I4_0));
        onDisconnect.InsertBefore(first, onDisconnect.Create(OpCodes.Stsfld, tcpActive));
        onDisconnect.InsertBefore(first, onDisconnect.Create(OpCodes.Ldsfld, inProgress));
        onDisconnect.InsertBefore(first, onDisconnect.Create(OpCodes.Brtrue, alreadyQueued));
        onDisconnect.InsertBefore(first, onDisconnect.Create(OpCodes.Ldsfld, givingUp));
        onDisconnect.InsertBefore(first, onDisconnect.Create(OpCodes.Brtrue, givingUpExit));
        onDisconnect.InsertBefore(first, onDisconnect.Create(OpCodes.Ldsfld, pending));
        onDisconnect.InsertBefore(first, onDisconnect.Create(OpCodes.Brtrue, alreadyQueued));
        // Run the base TcpPeer cleanup even while recovery is pending. The
        // high-level DisconnectAll/StageLogin path remains skipped, but the
        // old socket must be reset or ConnectToBattleTcpServer can keep failing.
        onDisconnect.InsertBefore(first, onDisconnect.Create(OpCodes.Ldarg_0));
        onDisconnect.InsertBefore(first, onDisconnect.Create(OpCodes.Call, Method(peer, "OnDisconnect")));
        onDisconnect.InsertBefore(first, onDisconnect.Create(OpCodes.Ldc_I4_1));
        onDisconnect.InsertBefore(first, onDisconnect.Create(OpCodes.Stsfld, pending));
        // Preserve the failure count across asynchronous attempts. Reset it
        // only after a real join response or a fresh lobby login.
        // The main-thread tick owns retry timing. Receive-thread callbacks must
        // not read UnityEngine.Time or touch a newer attempt's deadline.
        onDisconnect.InsertBefore(first, onDisconnect.Create(OpCodes.Ldstr, "od"));
        onDisconnect.InsertBefore(first, onDisconnect.Create(OpCodes.Ldc_I4, 4));
        onDisconnect.InsertBefore(first, onDisconnect.Create(OpCodes.Call, trace));
        onDisconnect.InsertBefore(first, alreadyQueued);
        // The tick performs the giving-up exit on the main thread; a late
        // disconnect on the receive thread only runs socket cleanup, or it
        // would leave a stage off the main thread and crash.
        onDisconnect.InsertBefore(first, givingUpExit);
        onDisconnect.InsertBefore(first, onDisconnect.Create(OpCodes.Ldc_I4, 5));
        onDisconnect.InsertBefore(first, onDisconnect.Create(OpCodes.Call, trace));
        onDisconnect.InsertBefore(first, onDisconnect.Create(OpCodes.Ldarg_0));
        onDisconnect.InsertBefore(first, onDisconnect.Create(OpCodes.Call, Method(peer, "OnDisconnect")));
        onDisconnect.InsertBefore(first, onDisconnect.Create(OpCodes.Ret));

        // Permit normal reconnect behavior after a new lobby login.
        var login = Method(module.GetType("CS.WorldServerProxy"),
            "HandleLogin", "System.IO.BinaryReader");
        var loginIl = login.Body.GetILProcessor();
        var originalLogin = login.Body.Instructions[0];
        loginIl.InsertBefore(originalLogin, loginIl.Create(OpCodes.Ldc_I4_0));
        loginIl.InsertBefore(originalLogin, loginIl.Create(OpCodes.Stsfld, givingUp));
        loginIl.InsertBefore(originalLogin, loginIl.Create(OpCodes.Ldc_I4_0));
        loginIl.InsertBefore(originalLogin, loginIl.Create(OpCodes.Stsfld, pending));
        loginIl.InsertBefore(originalLogin, loginIl.Create(OpCodes.Ldc_I4_0));
        loginIl.InsertBefore(originalLogin, loginIl.Create(OpCodes.Stsfld, attemptsMade));

        // A world drop during a battle leaves the stage from the receive
        // thread in the original handler, and Unity teardown off the main
        // thread crashes the player (observed when the whole server dies).
        // Rewrite the handler to close sockets and flag the drop only;
        // TickLanReconnect consumes the flag on the main thread.
        var world = module.GetType("CS.WorldServerProxy");
        var worldDisconnect = world.Methods.Single(m => m.Name == "OnDisconnect");
        worldDisconnect.Body.ExceptionHandlers.Clear();
        worldDisconnect.Body.Variables.Clear();
        worldDisconnect.Body.Instructions.Clear();
        var worldIl = worldDisconnect.Body.GetILProcessor();
        var dropStart = worldIl.Create(OpCodes.Nop);
        var dropEnd = worldIl.Create(OpCodes.Nop);
        worldIl.Emit(OpCodes.Ldarg_0);
        worldIl.Emit(OpCodes.Call, Method(peer, "OnDisconnect"));
        worldIl.Emit(OpCodes.Call, Method(module.GetType("CS.StageManager"), "get_Instance"));
        worldIl.Emit(OpCodes.Callvirt, Method(module.GetType("CS.StageManager"), "GetCurrentStage"));
        worldIl.Emit(OpCodes.Isinst, module.GetType("CS.StageHall"));
        worldIl.Emit(OpCodes.Brtrue, dropStart);
        worldIl.Emit(OpCodes.Call, Method(module.GetType("CS.StageManager"), "get_Instance"));
        worldIl.Emit(OpCodes.Callvirt, Method(module.GetType("CS.StageManager"), "GetCurrentStage"));
        worldIl.Emit(OpCodes.Isinst, module.GetType("CS.StageBattle"));
        worldIl.Emit(OpCodes.Brfalse, dropEnd);
        worldIl.Append(dropStart);
        worldIl.Emit(OpCodes.Call, Method(manager, "get_Instance"));
        worldIl.Emit(OpCodes.Callvirt, Method(manager, "DisconnectAll"));
        worldIl.Emit(OpCodes.Ldc_I4_1);
        worldIl.Emit(OpCodes.Stsfld, worldDrop);
        worldIl.Append(dropEnd);
        worldIl.Emit(OpCodes.Ret);

        // The reconnect path re-enters the battle stage, which re-runs
        // CameraController.SetupHighlighting while the persistent Main Camera
        // already carries a HighlightingRenderer: AddComponent returns null
        // and the null receiver NREs every frame. Skip setup when present.
        var camera = module.GetType("CS.CameraController");
        var setupHighlighting = Method(camera, "SetupHighlighting");
        var addComponent = setupHighlighting.Body.Instructions.FirstOrDefault(i =>
            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
            i.Operand is GenericInstanceMethod &&
            ((GenericInstanceMethod)i.Operand).Name == "AddComponent");
        if (addComponent != null)
        {
            var highlightType = module.ImportReference(((GenericInstanceMethod)addComponent.Operand).GenericArguments[0]);
            var cameraIl = setupHighlighting.Body.GetILProcessor();
            var entry = setupHighlighting.Body.Instructions[0];
            var resume = cameraIl.Create(OpCodes.Nop);
            var engineComponent = engine.GetType("UnityEngine.Component");
            var getComponent = module.ImportReference(engineComponent.Methods.First(m =>
                m.Name == "GetComponent" && m.Parameters.Count == 1 &&
                m.Parameters[0].ParameterType.FullName == "System.Type"));
            var typeFromHandle = module.ImportReference(typeof(Type).GetMethod("GetTypeFromHandle"));
            cameraIl.InsertBefore(entry, cameraIl.Create(OpCodes.Ldarg_0));
            cameraIl.InsertBefore(entry, cameraIl.Create(OpCodes.Ldfld, camera.Fields.Single(f => f.Name == "mCamera")));
            cameraIl.InsertBefore(entry, cameraIl.Create(OpCodes.Ldtoken, highlightType));
            cameraIl.InsertBefore(entry, cameraIl.Create(OpCodes.Call, typeFromHandle));
            cameraIl.InsertBefore(entry, cameraIl.Create(OpCodes.Callvirt, getComponent));
            cameraIl.InsertBefore(entry, cameraIl.Create(OpCodes.Brfalse, resume));
            cameraIl.InsertBefore(entry, cameraIl.Create(OpCodes.Ret));
            cameraIl.InsertBefore(entry, resume);
        }

    }

    private static void InstallScoreboard(ModuleDefinition module)
    {
        var world = module.GetType("CS.WorldServerProxy");
        var core = module.TypeSystem;
        var field = new FieldDefinition("LanScoreText",
            FieldAttributes.Public | FieldAttributes.Static, core.String);
        world.Fields.Add(field);

        var battle = module.GetType("CS.Battle");
        var disconnectedField = world.Fields.FirstOrDefault(f => f.Name == "LanBattleClosed");
        if (disconnectedField == null)
        {
            disconnectedField = new FieldDefinition("LanBattleClosed",
                FieldAttributes.Public | FieldAttributes.Static, core.Boolean);
            world.Fields.Add(disconnectedField);
        }
        foreach (var name in new[] { "HandleBattleClose", "HandleBattleBegin" })
        {
            var method = battle.Methods.FirstOrDefault(m => m.Name == name);
            if (method == null)
                continue;
            var b = method.Body.GetILProcessor();
            b.InsertBefore(method.Body.Instructions[0],
                b.Create(name == "HandleBattleClose" ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0));
            b.InsertBefore(method.Body.Instructions[1], b.Create(OpCodes.Stsfld, disconnectedField));
        }
        var handle = battle.Methods.Single(m => m.Name == "HandleMessages");
        var il = handle.Body.GetILProcessor();
        var original = handle.Body.Instructions[0];
        var data = new VariableDefinition(new ArrayType(core.Byte));
        handle.Body.Variables.Add(data);
        var readBytes = module.ImportReference(typeof(System.IO.BinaryReader).GetMethod("ReadBytes", new[] { typeof(int) }));
        var baseStream = module.ImportReference(typeof(System.IO.BinaryReader).GetProperty("BaseStream").GetGetMethod());
        var length = module.ImportReference(typeof(System.IO.Stream).GetProperty("Length").GetGetMethod());
        var position = module.ImportReference(typeof(System.IO.Stream).GetProperty("Position").GetGetMethod());
        var utf8 = module.ImportReference(typeof(System.Text.Encoding).GetProperty("UTF8").GetGetMethod());
        var getString = module.ImportReference(typeof(System.Text.Encoding).GetMethod("GetString", new[] { typeof(byte[]) }));

        il.InsertBefore(original, il.Create(OpCodes.Ldarg_1));
        il.InsertBefore(original, il.Create(OpCodes.Ldc_I4, 216));
        il.InsertBefore(original, il.Create(OpCodes.Bne_Un, original));
        il.InsertBefore(original, il.Create(OpCodes.Ldarg_2));
        il.InsertBefore(original, il.Create(OpCodes.Ldarg_2));
        il.InsertBefore(original, il.Create(OpCodes.Callvirt, baseStream));
        il.InsertBefore(original, il.Create(OpCodes.Callvirt, length));
        il.InsertBefore(original, il.Create(OpCodes.Conv_I4));
        il.InsertBefore(original, il.Create(OpCodes.Ldarg_2));
        il.InsertBefore(original, il.Create(OpCodes.Callvirt, baseStream));
        il.InsertBefore(original, il.Create(OpCodes.Callvirt, position));
        il.InsertBefore(original, il.Create(OpCodes.Conv_I4));
        il.InsertBefore(original, il.Create(OpCodes.Sub));
        il.InsertBefore(original, il.Create(OpCodes.Callvirt, readBytes));
        il.InsertBefore(original, il.Create(OpCodes.Stloc, data));
        il.InsertBefore(original, il.Create(OpCodes.Call, utf8));
        il.InsertBefore(original, il.Create(OpCodes.Ldloc, data));
        il.InsertBefore(original, il.Create(OpCodes.Callvirt, getString));
        il.InsertBefore(original, il.Create(OpCodes.Stsfld, field));
        il.InsertBefore(original, il.Create(OpCodes.Ret));
    }
    private static void InstallHideTips(ModuleDefinition module)
    {
        var tip = module.GetType("CS.GUIEntityBattleTip");
        if (tip == null)
            return;
        foreach (var name in new[] { "Initialise", "Update", "Pop", "AdjustItems" })
        {
            var method = tip.Methods.FirstOrDefault(m => m.Name == name);
            if (method == null)
                continue;
            var il = method.Body.GetILProcessor();
            method.Body.ExceptionHandlers.Clear();
            method.Body.Instructions.Clear();
            method.Body.Variables.Clear();
            il.Emit(OpCodes.Ret);
        }
    }

    private static void InstallUdpFallback(ModuleDefinition module)
    {
        var battle = module.GetType("CS.Battle");
        var loaded = battle.Methods.FirstOrDefault(m => m.Name == "HandleBattleLoaded");
        var manager = module.GetType("CS.ServerProxyManager");
        var gui = module.GetType("CS.GUISystem");
        var messageType = module.GetType("CS.GUIEntityBattleMessage");
        if (loaded == null || manager == null || gui == null || messageType == null)
            return;
        var connectDef = manager.Methods.FirstOrDefault(m => m.Name == "ConnectToBattleUdpServer" &&
            m.Parameters.Count == 2);
        var getEntityDef = gui.Methods.FirstOrDefault(m => m.Name == "GetEntity" && m.HasGenericParameters);
        var handlerDef = messageType.Methods.FirstOrDefault(m => m.Name == "HandleSystemMessage" &&
            m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.String");
        if (connectDef == null || getEntityDef == null || handlerDef == null)
            return;
        var connect = loaded.Body.Instructions.FirstOrDefault(i =>
            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand == connectDef);
        if (connect == null)
            return;
        var helper = new MethodDefinition("ConnectLanBattleUdp", MethodAttributes.Public, module.TypeSystem.Boolean);
        helper.Parameters.Add(new ParameterDefinition("address", ParameterAttributes.None, module.TypeSystem.String));
        helper.Parameters.Add(new ParameterDefinition("port", ParameterAttributes.None, module.TypeSystem.Int32));
        manager.Methods.Add(helper);
        var attempt = new VariableDefinition(module.TypeSystem.Int32);
        helper.Body.Variables.Add(attempt);
        var hi = helper.Body.GetILProcessor();
        var loop = hi.Create(OpCodes.Nop);
        var success = hi.Create(OpCodes.Ldc_I4_1);
        var fail = hi.Create(OpCodes.Ldc_I4_0);
        hi.Emit(OpCodes.Ldc_I4_0);
        hi.Emit(OpCodes.Stloc, attempt);
        hi.Append(loop);
        hi.Emit(OpCodes.Ldarg_0);
        hi.Emit(OpCodes.Ldarg_1);
        hi.Emit(OpCodes.Ldarg_2);
        hi.Emit(OpCodes.Callvirt, module.ImportReference(connectDef));
        hi.Emit(OpCodes.Brtrue, success);
        hi.Emit(OpCodes.Ldc_I4, 300);
        hi.Emit(OpCodes.Call, module.ImportReference(typeof(System.Threading.Thread).GetMethod("Sleep", new[] { typeof(int) })));
        hi.Emit(OpCodes.Ldloc, attempt);
        hi.Emit(OpCodes.Ldc_I4_1);
        hi.Emit(OpCodes.Add);
        hi.Emit(OpCodes.Dup);
        hi.Emit(OpCodes.Stloc, attempt);
        hi.Emit(OpCodes.Ldc_I4_3);
        hi.Emit(OpCodes.Blt, loop);
        hi.Append(fail);
        hi.Emit(OpCodes.Ret);
        hi.Append(success);
        hi.Emit(OpCodes.Ret);
        connect.OpCode = OpCodes.Callvirt;
        connect.Operand = helper;

        var getEntity = new GenericInstanceMethod(module.ImportReference(getEntityDef));
        getEntity.GenericArguments.Add(messageType);
        var il = loaded.Body.GetILProcessor();
        var warning = il.Create(OpCodes.Nop);
        il.Append(warning);
        il.Append(il.Create(OpCodes.Call, module.ImportReference(gui.Methods.Single(m => m.Name == "get_Instance"))));
        il.Append(il.Create(OpCodes.Callvirt, getEntity));
        il.Append(il.Create(OpCodes.Ldstr, "UDP 43135 未连接，移动同步可能失效；请检查防火墙/UDP 端口。"));
        il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(handlerDef)));
        il.Append(il.Create(OpCodes.Ret));
        var branch = loaded.Body.Instructions.SkipWhile(i => i != connect).FirstOrDefault(i => i.OpCode == OpCodes.Brfalse);
        if (branch != null)
            branch.Operand = warning;
    }

    private static void InstallUdpStats(ModuleDefinition module)
    {
        var tcp = module.GetType("CS.TcpServerProxy");
        var battle = module.GetType("CS.Battle");
        var udp = module.GetType("CS.BattleUdpServerProxy");
        var manager = module.GetType("CS.ServerProxyManager");
        var packetType = module.GetType("CS.BattleUdpPacket");
        var packet = module.GetType("CS.NetPacket");
        var buffer = module.GetType("CS.NetBuffer");
        var peer = module.GetType("CS.TcpPeer");
        if (tcp == null || udp == null || manager == null || packetType == null)
            return;
        var rtt = new FieldDefinition("LanUdpRtt", FieldAttributes.Public | FieldAttributes.Static, module.TypeSystem.Single);
        var sent = new FieldDefinition("LanUdpSent", FieldAttributes.Public | FieldAttributes.Static, module.TypeSystem.Int32);
        var recv = new FieldDefinition("LanUdpRecv", FieldAttributes.Public | FieldAttributes.Static, module.TypeSystem.Int32);
        var loss = new FieldDefinition("LanUdpLoss", FieldAttributes.Public | FieldAttributes.Static, module.TypeSystem.Int32);
        var pingEnabled = new FieldDefinition("LanUdpPingEnabled", FieldAttributes.Public | FieldAttributes.Static, module.TypeSystem.Boolean);
        tcp.Fields.Add(rtt);
        tcp.Fields.Add(sent);
        tcp.Fields.Add(recv);
        tcp.Fields.Add(loss);
        tcp.Fields.Add(pingEnabled);
        var loaded = battle.Methods.FirstOrDefault(m => m.Name == "HandleBattleLoaded");
        if (loaded != null)
        {
            var loadedFirst = loaded.Body.Instructions[0];
            loaded.Body.GetILProcessor().InsertBefore(loadedFirst, loaded.Body.GetILProcessor().Create(OpCodes.Ldc_I4_1));
            loaded.Body.GetILProcessor().InsertBefore(loadedFirst, loaded.Body.GetILProcessor().Create(OpCodes.Stsfld, pingEnabled));
        }
        var engine = module.AssemblyResolver.Resolve(module.AssemblyReferences.Single(a => a.Name == "UnityEngine")).MainModule;
        var getTime = module.ImportReference(Method(engine.GetType("UnityEngine.Time"), "get_time"));

        var ack = new MethodDefinition("LanUdpAck", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
        ack.Parameters.Add(new ParameterDefinition("data", ParameterAttributes.None, new ArrayType(module.TypeSystem.Byte)));
        tcp.Methods.Add(ack);
        var ai = ack.Body.GetILProcessor();
        var ackRet = ai.Create(OpCodes.Ret);
        ai.Emit(OpCodes.Ldarg_0);
        ai.Emit(OpCodes.Ldc_I4_2);
        ai.Emit(OpCodes.Call, module.ImportReference(typeof(BitConverter).GetMethod("ToUInt16", new[] { typeof(byte[]), typeof(int) })));
        ai.Emit(OpCodes.Ldc_I4, 209);
        ai.Emit(OpCodes.Bne_Un, ackRet);
        ai.Emit(OpCodes.Ldarg_0);
        ai.Emit(OpCodes.Ldc_I4, 18);
        ai.Emit(OpCodes.Call, module.ImportReference(typeof(BitConverter).GetMethod("ToSingle", new[] { typeof(byte[]), typeof(int) })));
        ai.Emit(OpCodes.Stloc_0);
        ai.Emit(OpCodes.Call, getTime);
        ai.Emit(OpCodes.Ldloc_0);
        ai.Emit(OpCodes.Sub);
        ai.Emit(OpCodes.Stsfld, rtt);
        ai.Emit(OpCodes.Ldsfld, recv);
        ai.Emit(OpCodes.Ldc_I4_1);
        ai.Emit(OpCodes.Add);
        ai.Emit(OpCodes.Stsfld, recv);
        ai.Emit(OpCodes.Ldsfld, sent);
        ai.Emit(OpCodes.Ldsfld, recv);
        ai.Emit(OpCodes.Sub);
        ai.Emit(OpCodes.Ldc_I4, 100);
        ai.Emit(OpCodes.Mul);
        ai.Emit(OpCodes.Ldsfld, sent);
        ai.Emit(OpCodes.Div);
        ai.Emit(OpCodes.Stsfld, loss);
        ai.Append(ackRet);
        ack.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Single));

        var send = new MethodDefinition("LanUdpPing", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
        tcp.Methods.Add(send);
        var si = send.Body.GetILProcessor();
        var request = new VariableDefinition(packet);
        var pingRet = si.Create(OpCodes.Ret);
        send.Body.Variables.Add(request);
        var localPlayer = module.GetType("CS.LocalPlayer");
        si.Emit(OpCodes.Call, Method(localPlayer, "get_Instance"));
        si.Emit(OpCodes.Brfalse, pingRet);
        si.Emit(OpCodes.Call, Method(localPlayer, "get_Instance"));
        si.Emit(OpCodes.Callvirt, Method(localPlayer, "GetDUID"));
        si.Emit(OpCodes.Brfalse, pingRet);
        si.Emit(OpCodes.Call, Method(manager, "get_Instance"));
        si.Emit(OpCodes.Callvirt, Method(manager, "GetBattleUdpServerProxy"));
        si.Emit(OpCodes.Brfalse, pingRet);
        si.Emit(OpCodes.Ldc_I4, 210);
        si.Emit(OpCodes.Newobj, Method(packetType, ".ctor", "CS.NetMessage"));
        si.Emit(OpCodes.Stloc, request);
        si.Emit(OpCodes.Ldloc, request);
        si.Emit(OpCodes.Call, getTime);
        si.Emit(OpCodes.Callvirt, Method(packet, "Push", "System.Single"));
        si.Emit(OpCodes.Ldloc, request);
        si.Emit(OpCodes.Callvirt, Method(packet, "UpdateHeader"));
        si.Emit(OpCodes.Call, Method(manager, "get_Instance"));
        si.Emit(OpCodes.Callvirt, Method(manager, "GetBattleUdpServerProxy"));
        si.Emit(OpCodes.Ldloc, request);
        si.Emit(OpCodes.Callvirt, Method(buffer, "get_Base"));
        si.Emit(OpCodes.Callvirt, Method(peer, "Send", "System.Byte[]"));
        si.Emit(OpCodes.Ldsfld, sent);
        si.Emit(OpCodes.Ldc_I4_1);
        si.Emit(OpCodes.Add);
        si.Emit(OpCodes.Stsfld, sent);
        si.Append(pingRet);

        // Hook the existing 2-second TCP ping so a UDP probe is sent alongside it.
        var update = tcp.Methods.FirstOrDefault(m => m.Name == "UpdatePing" && m.Parameters.Count == 1);
        if (update != null)
        {
            var requestPing = update.Body.Instructions.FirstOrDefault(i =>
                (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
                i.Operand is MethodReference && ((MethodReference)i.Operand).Name == "RequestPing");
            if (requestPing != null)
                update.Body.GetILProcessor().InsertAfter(requestPing, update.Body.GetILProcessor().Create(OpCodes.Call, send));
        }
        var onReceive = udp.Methods.FirstOrDefault(m => m.Name == "OnReceive");
        if (onReceive != null)
        {
            var first = onReceive.Body.Instructions[0];
            onReceive.Body.GetILProcessor().InsertBefore(first, onReceive.Body.GetILProcessor().Create(OpCodes.Ldarg_1));
            onReceive.Body.GetILProcessor().InsertBefore(first, onReceive.Body.GetILProcessor().Create(OpCodes.Call, ack));
        }
        var getLatency = tcp.Methods.FirstOrDefault(m => m.Name == "GetLatency" && m.Parameters.Count == 0);
        if (getLatency != null)
        {
            var g = getLatency.Body.GetILProcessor();
            getLatency.Body.Instructions.Clear();
            getLatency.Body.Variables.Clear();
            g.Emit(OpCodes.Ldsfld, rtt);
            g.Emit(OpCodes.Ret);
        }
        var local = module.GetType("CS.LocalController");
        if (local != null)
        {
            var nextPing = new FieldDefinition("LanUdpNextPing", FieldAttributes.Public | FieldAttributes.Static, module.TypeSystem.Single);
            local.Fields.Add(nextPing);
            var tick = new MethodDefinition("LanUdpTick", MethodAttributes.Private | MethodAttributes.Static, module.TypeSystem.Void);
            local.Methods.Add(tick);
            var ti = tick.Body.GetILProcessor();
            var tickRet = ti.Create(OpCodes.Ret);
            ti.Emit(OpCodes.Ldsfld, pingEnabled);
            ti.Emit(OpCodes.Brfalse, tickRet);
            ti.Emit(OpCodes.Call, getTime);
            ti.Emit(OpCodes.Ldsfld, nextPing);
            ti.Emit(OpCodes.Blt, tickRet);
            ti.Emit(OpCodes.Call, getTime);
            ti.Emit(OpCodes.Ldc_R4, 2f);
            ti.Emit(OpCodes.Add);
            ti.Emit(OpCodes.Stsfld, nextPing);
            ti.Emit(OpCodes.Call, send);
            ti.Append(tickRet);
            var localUpdate = local.Methods.FirstOrDefault(m => m.Name == "Update");
            if (update != null)
            {
                var first = localUpdate.Body.Instructions[0];
                localUpdate.Body.GetILProcessor().InsertBefore(first, localUpdate.Body.GetILProcessor().Create(OpCodes.Call, tick));
            }
        }
        var getLoss = tcp.Methods.FirstOrDefault(m => m.Name == "GetPacketLoss" && m.Parameters.Count == 0);
        if (getLoss != null)
        {
            var g = getLoss.Body.GetILProcessor();
            getLoss.Body.Instructions.Clear();
            getLoss.Body.Variables.Clear();
            g.Emit(OpCodes.Ldsfld, loss);
            g.Emit(OpCodes.Ret);
        }
    }

    private static void InstallLanUdpWarning(ModuleDefinition module)
    {
        var world = module.GetType("CS.WorldServerProxy");
        var udp = module.GetType("CS.BattleUdpServerProxy");
        var battle = module.GetType("CS.Battle");
        var local = module.GetType("CS.LocalController");
        var system = module.GetType("CS.GUIEntitySystemMessage");
        var gui = module.GetType("CS.GUISystem");
        var defines = module.GetType("CS.Defines");
        var engine = module.AssemblyResolver.Resolve(module.AssemblyReferences.Single(a => a.Name == "UnityEngine")).MainModule;
        var getTime = module.ImportReference(Method(engine.GetType("UnityEngine.Time"), "get_time"));
        var udpOnReceive = udp == null ? null : udp.Methods.FirstOrDefault(m => m.Name == "OnReceive");
        var loaded = battle == null ? null : battle.Methods.FirstOrDefault(m => m.Name == "HandleBattleLoaded");
        var update = local == null ? null : local.Methods.FirstOrDefault(m => m.Name == "Update");
        var pop = system == null ? null : system.Methods.FirstOrDefault(m => m.Name == "Pop" && m.Parameters.Count == 3);
        var getEntityDef = gui == null ? null : gui.Methods.FirstOrDefault(m => m.Name == "GetEntity" && m.HasGenericParameters);
        if (world == null || udpOnReceive == null || loaded == null || update == null || pop == null || getEntityDef == null)
            return;
        var seen = new FieldDefinition("LanUdpSeen", FieldAttributes.Public | FieldAttributes.Static, module.TypeSystem.Boolean);
        var warnAt = new FieldDefinition("LanUdpWarnAt", FieldAttributes.Public | FieldAttributes.Static, module.TypeSystem.Single);
        world.Fields.Add(seen);
        world.Fields.Add(warnAt);
        var getEntity = new GenericInstanceMethod(module.ImportReference(getEntityDef));
        getEntity.GenericArguments.Add(system);

        var mark = new MethodDefinition("LanMarkUdpSeen", MethodAttributes.Private | MethodAttributes.Static, module.TypeSystem.Void);
        local.Methods.Add(mark);
        var mIl = mark.Body.GetILProcessor();
        mIl.Emit(OpCodes.Ldc_I4_1);
        mIl.Emit(OpCodes.Stsfld, seen);
        mIl.Emit(OpCodes.Ret);
        var onFirst = udpOnReceive.Body.Instructions[0];
        udpOnReceive.Body.GetILProcessor().InsertBefore(onFirst, udpOnReceive.Body.GetILProcessor().Create(OpCodes.Call, mark));

        var arm = new MethodDefinition("LanArmUdpWarning", MethodAttributes.Private | MethodAttributes.Static, module.TypeSystem.Void);
        local.Methods.Add(arm);
        var aIl = arm.Body.GetILProcessor();
        aIl.Emit(OpCodes.Call, getTime);
        aIl.Emit(OpCodes.Ldc_R4, 2f);
        aIl.Emit(OpCodes.Add);
        aIl.Emit(OpCodes.Stsfld, warnAt);
        aIl.Emit(OpCodes.Ret);
        var loadedFirst = loaded.Body.Instructions[0];
        loaded.Body.GetILProcessor().InsertBefore(loadedFirst, loaded.Body.GetILProcessor().Create(OpCodes.Call, arm));

        var check = new MethodDefinition("LanCheckUdp", MethodAttributes.Private | MethodAttributes.Static, module.TypeSystem.Void);
        local.Methods.Add(check);
        var cIl = check.Body.GetILProcessor();
        var ret = cIl.Create(OpCodes.Ret);
        cIl.Emit(OpCodes.Ldsfld, warnAt);
        cIl.Emit(OpCodes.Ldc_R4, 0f);
        cIl.Emit(OpCodes.Ble, ret);
        cIl.Emit(OpCodes.Ldsfld, seen);
        cIl.Emit(OpCodes.Brtrue, ret);
        cIl.Emit(OpCodes.Call, getTime);
        cIl.Emit(OpCodes.Ldsfld, warnAt);
        cIl.Emit(OpCodes.Blt, ret);
        cIl.Emit(OpCodes.Ldc_R4, 0f);
        cIl.Emit(OpCodes.Stsfld, warnAt);
        cIl.Emit(OpCodes.Call, module.ImportReference(gui.Methods.Single(m => m.Name == "get_Instance")));
        cIl.Emit(OpCodes.Callvirt, getEntity);
        cIl.Emit(OpCodes.Ldstr, "UDP 43135 未连接，移动同步可能失效；请检查防火墙/UDP 端口。");
        cIl.Emit(OpCodes.Ldsfld, defines.Fields.Single(f => f.Name == "SystemMessageWarning"));
        cIl.Emit(OpCodes.Ldc_I4_0);
        cIl.Emit(OpCodes.Callvirt, pop);
        cIl.Append(ret);
        var updateFirst = update.Body.Instructions[0];
        update.Body.GetILProcessor().InsertBefore(updateFirst, update.Body.GetILProcessor().Create(OpCodes.Call, check));
    }

    private static void InstallUdpChatPrompt(ModuleDefinition module)
    {
        var messageType = module.GetType("CS.GUIEntityBattleMessage");
        var handler = messageType.Methods.FirstOrDefault(m => m.Name == "HandleBattleChat" &&
            m.Parameters.Count == 3 && m.Parameters[2].ParameterType.FullName == "System.String");
        var system = messageType.Methods.FirstOrDefault(m => m.Name == "HandleSystemMessage" &&
            m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.String");
        if (handler == null || system == null)
            return;
        var il = handler.Body.GetILProcessor();
        var original = handler.Body.Instructions[0];
        var ret = il.Create(OpCodes.Ret);
        il.InsertBefore(original, il.Create(OpCodes.Ldarg_3));
        il.InsertBefore(original, il.Create(OpCodes.Ldstr, "UDP 43135"));
        il.InsertBefore(original, il.Create(OpCodes.Callvirt,
            module.ImportReference(typeof(string).GetMethod("StartsWith", new[] { typeof(string) }))));
        il.InsertBefore(original, il.Create(OpCodes.Brfalse, original));
        il.InsertBefore(original, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(original, il.Create(OpCodes.Ldarg_3));
        il.InsertBefore(original, il.Create(OpCodes.Callvirt, system));
        il.InsertBefore(original, il.Create(OpCodes.Ret));
    }

    private static void InstallPacketLoss(ModuleDefinition module)
    {
        var proxy = module.GetType("CS.TcpServerProxy");
        var recalc = proxy.Methods.FirstOrDefault(m => m.Name == "RecalcPacketLoss" && m.Parameters.Count == 0);
        var loss = proxy.Fields.FirstOrDefault(f => f.Name == "mPacketLoss");
        if (recalc == null || loss == null)
            return;
        var ret = recalc.Body.Instructions.FirstOrDefault(i => i.OpCode == OpCodes.Ret);
        if (ret == null)
            return;
        var il = recalc.Body.GetILProcessor();
        // mPacketLoss currently contains the number of lost samples. Convert it
        // to a percentage over the sampled window so the HUD does not display
        // 0..4 as if it were a percentage. The divisor (count - 1) is zero while
        // at most one ping is in flight, which killed the whole Update loop at
        // login, so skip the conversion until the window has two samples.
        il.InsertBefore(ret, il.Create(OpCodes.Ldloc_0));
        il.InsertBefore(ret, il.Create(OpCodes.Ldc_I4_1));
        il.InsertBefore(ret, il.Create(OpCodes.Ble, ret));
        il.InsertBefore(ret, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(ret, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(ret, il.Create(OpCodes.Ldfld, loss));
        il.InsertBefore(ret, il.Create(OpCodes.Ldc_I4, 100));
        il.InsertBefore(ret, il.Create(OpCodes.Mul));
        il.InsertBefore(ret, il.Create(OpCodes.Ldloc_0));
        il.InsertBefore(ret, il.Create(OpCodes.Ldc_I4_1));
        il.InsertBefore(ret, il.Create(OpCodes.Sub));
        il.InsertBefore(ret, il.Create(OpCodes.Div));
        il.InsertBefore(ret, il.Create(OpCodes.Stfld, loss));
    }

    private static void InstallStatusRate(ModuleDefinition module)
    {
        // The client already interpolates UnitVisual with Lerp/Slerp in
        // UnitVisual.UpdateInterpolation. The 10 Hz status target is the
        // remaining stair-step source; 20 Hz fits the server's 20 Hz tick.
        var local = module.GetType("CS.LocalController");
        var field = local.Fields.FirstOrDefault(f => f.Name == "StatusSyncFrequency");
        var cctor = local.Methods.FirstOrDefault(m => m.Name == ".cctor");
        if (field == null || cctor == null)
            return;
        var instructions = cctor.Body.Instructions;
        for (var i = 1; i < instructions.Count; i++)
        {
            if (instructions[i].OpCode == OpCodes.Stsfld && instructions[i].Operand == field &&
                instructions[i - 1].OpCode == OpCodes.Ldc_R4 &&
                Math.Abs((float)instructions[i - 1].Operand - 0.1f) < 0.0001f)
                instructions[i - 1].Operand = 0.05f;
        }
    }

    private static void InstallMountedStatus(ModuleDefinition module)
    {
        var local = module.GetType("CS.LocalController");
        var update = local.Methods.FirstOrDefault(m => m.Name == "UpdateSync" && m.Parameters.Count == 1);
        if (update == null)
            return;
        var instructions = update.Body.Instructions;
        for (var i = 0; i < instructions.Count; i++)
        {
            if (instructions[i].OpCode == OpCodes.Callvirt &&
                instructions[i].Operand is MethodReference &&
                ((MethodReference)instructions[i].Operand).Name == "IsMounted")
            {
                // Keep the Unit on the stack balanced and remove the mounted
                // skip so passengers also report yaw/aim.
                instructions[i].OpCode = OpCodes.Pop;
                instructions[i].Operand = null;
                if (i + 1 < instructions.Count && instructions[i + 1].OpCode == OpCodes.Brtrue)
                {
                    instructions[i + 1].OpCode = OpCodes.Nop;
                    instructions[i + 1].Operand = null;
                }
                break;
            }
        }
    }

    private static void InstallMountMovement(ModuleDefinition module)
    {
        var unit = module.GetType("CS.Unit");
        var mount = unit.Methods.Single(m => m.Name == "Mount");
        var calls = mount.Body.Instructions
            .Where(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                        && i.Operand is MethodReference)
            .Where(i => ((MethodReference)i.Operand).Name == "StopMove")
            .ToList();
        foreach (var call in calls)
        {
            // StopMove is an instance method: its receiver is already on the
            // stack. Remove only the call would leave an extra value and make
            // the method's final ret invalid IL. Pop consumes the receiver.
            call.OpCode = OpCodes.Pop;
            call.Operand = null;
        }
    }

    private static void InstallLanMoveResume(ModuleDefinition module)
    {
        // LocalController.UpdateInput only calls Move when the WASD state
        // differs from the cached mLanMotion vector. The scope gate and the
        // mounted skip suppress movement without touching that cache, so a
        // key held across scoping or a ride compares equal afterwards and
        // the input loop stays silent forever. Keep the cache zeroed while
        // movement is suppressed; the next state change then re-fires.
        var local = module.GetType("CS.LocalController");
        var input = Method(local, "UpdateInput", "System.Single");
        var scopeField = local.Fields.FirstOrDefault(f => f.Name == "mLanScopeActive");
        var motionField = local.Fields.Single(f => f.Name == "mMotion");
        var engine = module.AssemblyResolver.Resolve(
            module.AssemblyReferences.Single(a => a.Name == "UnityEngine")).MainModule;
        var zero = module.ImportReference(Method(engine.GetType("UnityEngine.Vector3"), "get_zero"));
        var il = input.Body.GetILProcessor();

        // Mounted: the IsMounted skip jumps past the whole movement block.
        // Zero the cache on that path so dismounting re-fires the detector.
        var mountedCall = input.Body.Instructions.First(i =>
            i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference &&
            ((MethodReference)i.Operand).Name == "IsMounted");
        var mountedSkip = (Instruction)mountedCall.Next.Operand;
        var resume = il.Create(OpCodes.Br, mountedSkip);
        var zeroBlock = new Instruction[]
        {
            il.Create(OpCodes.Ldarg_0),
            il.Create(OpCodes.Call, zero),
            il.Create(OpCodes.Stfld, motionField),
            resume,
        };
        foreach (var instruction in zeroBlock)
            il.InsertBefore(mountedSkip, instruction);
        mountedCall.Next.OpCode = OpCodes.Brtrue;
        mountedCall.Next.Operand = zeroBlock[0];

        if (scopeField != null)
        {
            // Scoped: Move is gated inside LocalController.Move, but the
            // caller still refreshes the cache afterwards. Undo that update
            // while the scope is active.
            var stores = input.Body.Instructions
                .Select((instruction, index) => new { instruction, index })
                .Where(pair => pair.instruction.OpCode == OpCodes.Stfld && pair.instruction.Operand == motionField)
                .Select(pair => pair.instruction).ToList();
            // UpdateInput caches the WASD vector exactly once, after the
            // Move/StopMove decision.
            var afterMove = stores[0];
            var next = afterMove.Next;
            foreach (var instruction in new[]
                     {
                         il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, scopeField),
                         il.Create(OpCodes.Brfalse, next), il.Create(OpCodes.Ldarg_0),
                         il.Create(OpCodes.Call, zero), il.Create(OpCodes.Stfld, motionField),
                     })
                il.InsertBefore(next, instruction);
        }
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
        if (args.Length != 3)
            throw new ArgumentException("Pass input Assembly-CSharp.dll, output Assembly-CSharp.dll and LanRoomUI.dll.");
        using (var resolver = new DefaultAssemblyResolver())
        {
            resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0])));
            using (var module = ModuleDefinition.ReadModule(args[0], new ReaderParameters { AssemblyResolver = resolver }))
            using (var roomUi = ModuleDefinition.ReadModule(args[2]))
            {
                var battle = module.GetType("CS.Battle");
                var unit = module.GetType("CS.Unit");
                Console.WriteLine("step hit"); InstallHitReport(module, battle, unit);
                Console.WriteLine("step revive"); InstallRevive(module, battle, unit);
                Console.WriteLine("step bar"); InstallReviveBar(module, unit);
                Console.WriteLine("step melee"); InstallMeleeSwing(module, battle, unit);
                Console.WriteLine("step scope"); InstallSniperScope(module, unit);
                Console.WriteLine("step pose"); InstallScopePose(module, battle, unit);
                Console.WriteLine("step controls"); InstallRoundControls(module);
                Console.WriteLine("step ammo"); InstallInfiniteAmmo(module);
                Console.WriteLine("step mode"); InstallRespawnMode(module);
                Console.WriteLine("step room"); InstallLanRoom(module, roomUi);
                Console.WriteLine("step items"); InstallItems(module, battle, unit);
                Console.WriteLine("step vehicle"); InstallVehiclePropImpact(module, battle);
                Console.WriteLine("step notices"); InstallEliminationNotices(module, battle, unit);
                Console.WriteLine("step profile"); InstallProfileName(module);
                Console.WriteLine("step nickname"); InstallNickname(module);
        InstallUnitLeave(module);
        InstallReconnect(module, roomUi);
                Console.WriteLine("step scoreboard"); InstallScoreboard(module);
                Console.WriteLine("step tips"); InstallHideTips(module);
                Console.WriteLine("step mountmove"); InstallMountMovement(module);
                Console.WriteLine("step mounted"); InstallMountedStatus(module);
                Console.WriteLine("step rate"); InstallStatusRate(module);
                Console.WriteLine("step loss"); InstallPacketLoss(module);
                Console.WriteLine("step chat"); InstallUdpChatPrompt(module);
                Console.WriteLine("step warn"); InstallLanUdpWarning(module);
                Console.WriteLine("step udpstats"); InstallUdpStats(module);
                Console.WriteLine("step udpfb"); InstallUdpFallback(module);
        Console.WriteLine("step moveresume"); InstallLanMoveResume(module);
                try
                {
                    module.Write(args[1]);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("WRITE " + ex.GetType().FullName);
                    Console.Error.WriteLine(ex.Message);
                    Console.Error.WriteLine(ex.StackTrace);
                    if (ex.InnerException != null)
                    {
                        Console.Error.WriteLine("INNER " + ex.InnerException.GetType().FullName);
                        Console.Error.WriteLine(ex.InnerException.Message);
                    }
                    throw;
                }
            }
        }
    }
}







