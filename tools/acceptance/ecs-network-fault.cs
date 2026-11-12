using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

internal static class EcsNetworkFault
{
    private static readonly string Root=AppDomain.CurrentDomain.BaseDirectory;
    private static void Cleanup(string group)
    {
        if(!Regex.IsMatch(group,@"^MBG-v0111-[0-9a-f]{32}$"))throw new ArgumentException("Invalid cleanup group");
        dynamic policy=Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2"));
        foreach(string suffix in new[]{"-out","-in"})policy.Rules.Remove(group+suffix);
        foreach(dynamic rule in policy.Rules)
            if((string)rule.Name==group+"-out"||(string)rule.Name==group+"-in")throw new IOException("Temporary firewall rule still exists");
        dynamic service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));service.Connect();
        try{service.GetFolder("\\").DeleteTask(group,0);}catch(COMException){}
    }
    public static int Main(string[] args)
    {
        Console.OutputEncoding=Encoding.UTF8;
        try
        {
            if(args.Length==2&&args[0]=="--cleanup"){Cleanup(args[1]);return 0;}
            int seconds=25;
            if(args.Length<1||args.Length>2||(args[0]!="tcp"&&args[0]!="udp")||(args.Length==2&&(!int.TryParse(args[1],out seconds)||seconds<10||seconds>60)))
            {Console.WriteLine("Usage: ECS-NetworkFault.exe tcp|udp [10..60 seconds]");return 2;}
            var principal=new WindowsPrincipal(WindowsIdentity.GetCurrent());
            if(!principal.IsInRole(WindowsBuiltInRole.Administrator))throw new UnauthorizedAccessException("Run this EXE from an administrator terminal.");
            var game=Path.Combine(Root,"client","CrazyShooter.exe");if(!File.Exists(game))throw new FileNotFoundException("Place this EXE beside acceptance-build.json and the client directory.");
            int protocol=args[0]=="tcp"?6:17;int port=protocol==6?43234:43235;
            string group="MBG-v0111-"+Guid.NewGuid().ToString("N");
            string evidenceRoot=Path.Combine(Root,"evidence");Directory.CreateDirectory(evidenceRoot);
            string evidence=Directory.GetDirectories(evidenceRoot).OrderByDescending(Directory.GetCreationTimeUtc).FirstOrDefault()??evidenceRoot;
            string log=Path.Combine(evidence,group+".jsonl");var json=new JavaScriptSerializer();
            Action<string> record=e=>File.AppendAllText(log,json.Serialize(new {utc=DateTime.UtcNow.ToString("o"),eventName=e,group=group,protocol=args[0],port=port,seconds=seconds})+Environment.NewLine);
            // Register a SYSTEM cleanup task before adding any rule. If this
            // process is killed, Task Scheduler still removes this run's rules.
            dynamic service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));service.Connect();
            dynamic task=service.NewTask(0);task.RegistrationInfo.Description="Remove temporary Mini Battlegrounds acceptance firewall rules";
            task.Principal.UserId="SYSTEM";task.Principal.LogonType=5;task.Principal.RunLevel=1;
            task.Settings.Enabled=true;task.Settings.StartWhenAvailable=true;task.Settings.ExecutionTimeLimit="PT1M";
            dynamic trigger=task.Triggers.Create(1);trigger.StartBoundary=DateTime.Now.AddSeconds(seconds+15).ToString("s");trigger.Enabled=true;
            // The elevated watchdog executes only Windows-owned binaries, never
            // an EXE or script in the user's writable extraction directory.
            string system=Environment.GetFolderPath(Environment.SpecialFolder.System);
            foreach(string suffix in new[]{"-out","-in"})
            {
                dynamic action=task.Actions.Create(0);action.Path=Path.Combine(system,"netsh.exe");
                action.Arguments="advfirewall firewall delete rule name=\""+group+suffix+"\"";
            }
            dynamic removeTask=task.Actions.Create(0);removeTask.Path=Path.Combine(system,"schtasks.exe");
            removeTask.Arguments="/Delete /TN \""+group+"\" /F";
            service.GetFolder("\\").RegisterTaskDefinition(group,task,6,"SYSTEM",null,5,null);
            try
            {
                dynamic policy=Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2"));
                foreach(int direction in new[]{2,1})
                {
                    dynamic rule=Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule"));
                    rule.Name=group+(direction==2?"-out":"-in");rule.Grouping=group;rule.ApplicationName=game;
                    rule.Protocol=protocol;rule.RemoteAddresses="198.51.100.10";rule.RemotePorts=port.ToString();
                    rule.Direction=direction;rule.Profiles=int.MaxValue;rule.Action=0;rule.Enabled=true;policy.Rules.Add(rule);
                }
                record("block_started");Console.WriteLine(args[0]+" "+port+" blocked for "+seconds+" seconds. Keep both games open.");
                Thread.Sleep(seconds*1000);
            }
            finally{Cleanup(group);record("block_removed");}
            Console.WriteLine("Temporary rules removed. Evidence: "+log);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error.GetType().Name+": "+error.Message);return 1;}
    }
}
