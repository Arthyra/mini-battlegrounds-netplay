using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

// IL/JIT sanity gate for patched game assemblies. Every non-abstract method
// body is prepared (JIT-compiled in the metadata loader); anything that
// fails to prepare is an InvalidProgramException waiting to crash the game.
//
// Exit codes:
//   0 = every method prepared, or only failures matching a tolerate pattern
//   1 = load failure, type failure, or any untolerated method failure
//
// Usage: ilcheck.exe Assembly.dll [tolerate-substring ...]
// The game has pre-existing methods the runtime cannot prepare (for example
// CS.Battle::RemoveUnit resolving List<CS.Unit>.Remove); list such
// substrings as tolerate arguments to keep the gate on new damage only.
internal static class IlCheck
{
    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("usage: ilcheck.exe Assembly.dll [tolerate-substring ...]");
            return 1;
        }
        var tolerate = new List<string>();
        for (var i = 1; i < args.Length; i++)
            tolerate.Add(args[i]);
        var probeDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(args[0]));
        AppDomain.CurrentDomain.AssemblyResolve += (sender, eventArgs) =>
        {
            var name = new AssemblyName(eventArgs.Name).Name + ".dll";
            var candidate = System.IO.Path.Combine(probeDir, name);
            return System.IO.File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };
        Assembly assembly;
        try
        {
            assembly = Assembly.Load(System.IO.File.ReadAllBytes(args[0]));
        }
        catch (Exception error)
        {
            Console.WriteLine("LOAD FAIL " + error.GetType().FullName + " " + error.Message);
            return 1;
        }
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (Exception error)
        {
            Console.WriteLine("TYPES FAIL " + error.GetType().FullName + " " + error.Message);
            var rt = error as ReflectionTypeLoadException;
            if (rt != null)
                foreach (var le in rt.LoaderExceptions)
                    Console.WriteLine("  LOAD " + le.GetType().FullName + " " + le.Message);
            return 1;
        }
        var checkedCount = 0;
        var toleratedCount = 0;
        var untolerated = 0;
        foreach (var type in types)
        {
            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static |
                                          BindingFlags.Public | BindingFlags.NonPublic |
                                          BindingFlags.DeclaredOnly);
            }
            catch (Exception error)
            {
                Console.WriteLine("TYPES FAIL " + type.FullName + " " + error.Message);
                untolerated++;
                continue;
            }
            foreach (var method in methods)
            {
                if (method.ContainsGenericParameters || method.GetMethodBody() == null)
                    continue;
                try
                {
                    RuntimeHelpers.PrepareMethod(method.MethodHandle);
                    checkedCount++;
                }
                catch (Exception error)
                {
                    var description = type.FullName + "::" + method.Name + " : " +
                                      error.GetType().Name + " " + error.Message;
                    var tolerated = false;
                    foreach (var pattern in tolerate)
                        if (description.Contains(pattern))
                            tolerated = true;
                    Console.WriteLine((tolerated ? "TOLERATED " : "FAIL ") + description);
                    if (tolerated)
                        toleratedCount++;
                    else
                        untolerated++;
                }
            }
        }
        Console.WriteLine("checked=" + checkedCount + " tolerated=" + toleratedCount +
                          " failed=" + untolerated);
        return untolerated > 0 ? 1 : 0;
    }
}
