using System;
using System.IO;
using System.Reflection;
internal static class UpdaterInstallTests
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode, SetLastError=true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint creation,uint attributes,IntPtr template);
    private static void WriteMark(string path)
    {
        using(var handle=CreateFile(path,0x40000000,0,IntPtr.Zero,2,0,IntPtr.Zero))
        using(var stream=new FileStream(handle,FileAccess.Write))
        using(var writer=new StreamWriter(stream)){writer.Write("[ZoneTransfer]\r\nZoneId=3\r\n");}
    }
    private static string ReadMark(string path)
    {
        using(var handle=CreateFile(path,0x80000000,1,IntPtr.Zero,3,0,IntPtr.Zero))
        using(var stream=new FileStream(handle,FileAccess.Read))
        using(var reader=new StreamReader(stream)){return reader.ReadToEnd();}
    }
    private static MethodInfo install;
    private static string runRoot;
    private static int passed;
    private static void Check(bool value, string name) { if(!value)throw new Exception(name);Console.WriteLine("PASS "+name);passed++; }
    private static string[] Fixture(string name)
    {
        var root=Path.Combine(runRoot,name);var source=Path.Combine(root,"source");var target=Path.Combine(root,"install");
        Directory.CreateDirectory(source);Directory.CreateDirectory(target);
        foreach(var file in new[]{"a.txt","b.txt"}) { File.WriteAllText(Path.Combine(source,file),"new-"+file);File.WriteAllText(Path.Combine(target,file),"old-"+file); }
        return new[]{source,target};
    }
    private static void Apply(string[] paths, Action<int> progress)
    { install.Invoke(null,new object[]{paths[0],paths[1],new Action<string>(Console.WriteLine),progress}); }
    public static int Main(string[] args)
    {
        try
        {
            install=Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("Updater").GetMethod("InstallFiles",BindingFlags.NonPublic|BindingFlags.Static);
            runRoot=Path.GetFullPath(args[1]);Directory.CreateDirectory(runRoot);
            var locked=Fixture("locked");
            using(var handle=File.Open(Path.Combine(locked[1],"b.txt"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            {
                try{Apply(locked,p=>{});throw new Exception("lock ignored");}
                catch(TargetInvocationException e){Check(e.InnerException is IOException,"occupied file rejected before replacement");}
                Check(File.ReadAllText(Path.Combine(locked[1],"a.txt"))=="old-a.txt","old installation intact when target occupied");
            }
            var rollback=Fixture("rollback");
            try{Apply(rollback,p=>{throw new IOException("injected post-first-file interruption");});throw new Exception("fault ignored");}
            catch(TargetInvocationException e){Check(e.InnerException is IOException,"partial-install fault surfaced");}
            Check(File.ReadAllText(Path.Combine(rollback[1],"a.txt"))=="old-a.txt"&&File.ReadAllText(Path.Combine(rollback[1],"b.txt"))=="old-b.txt","replaced files rolled back after partial failure");
            var success=Fixture("success");
            var sentinel=Path.Combine(success[1],"unrelated.exe:Zone.Identifier");File.WriteAllText(Path.Combine(success[1],"unrelated.exe"),"sentinel");WriteMark(sentinel);
            Apply(success,p=>{});
            Check(File.ReadAllText(Path.Combine(success[1],"a.txt"))=="new-a.txt"&&File.ReadAllText(Path.Combine(success[1],"b.txt"))=="new-b.txt","valid file replacement succeeds");
            Check(ReadMark(sentinel).Contains("ZoneId=3"),"unrelated Windows download mark preserved");
            Check(Directory.GetDirectories(success[1],"update-backup-*").Length==0,"completed install removes its rollback directory");
            Console.WriteLine(passed+" passed");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
