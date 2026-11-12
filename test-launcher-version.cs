using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

[assembly: AssemblyVersion("0.11.1.10")]
[assembly: AssemblyFileVersion("0.11.1.10")]

// LocalVersion reads the executing process's version. Give this fixture the
// original four-part release version and execute the real launcher's methods.
internal static class LauncherVersionTests
{
    private static int Main(string[] args)
    {
        var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        var type = assembly.GetType("LanLauncher", true);
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        string local = (string)type.GetMethod("LocalVersion", flags).Invoke(null, null);
        string actual = FileVersionInfo.GetVersionInfo(Application.ExecutablePath).FileVersion;
        bool sameIsNewer = (bool)type.GetMethod("IsNewer", flags).Invoke(null, new object[] { actual, local });
        bool nextIsNewer = (bool)type.GetMethod("IsNewer", flags).Invoke(null, new object[] { "0.11.1.11", local });
        Console.WriteLine("file=" + actual + " local=" + local + " same_is_newer=" + sameIsNewer + " next_is_newer=" + nextIsNewer);
        if (local != actual || sameIsNewer || !nextIsNewer)
        { Console.WriteLine("FAIL installed version loses revision or repeats update"); return 1; }
        Console.WriteLine("PASS full installed revision, no same-version update, next revision detected"); return 0;
    }
}
