using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal static class EcsAcceptanceRunner
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static bool interrupted;
    private static string Command(string file, string args)
    {
        var start = new ProcessStartInfo(file, args) { UseShellExecute=false, CreateNoWindow=true,
            RedirectStandardOutput=true, RedirectStandardError=true };
        using(var process=Process.Start(start))
        {
            var output=process.StandardOutput.ReadToEnd();
            var error=process.StandardError.ReadToEnd();
            process.WaitForExit();
            if(process.ExitCode!=0) return output+error;
            return output;
        }
    }
    private static void Record(string path, object value)
    {
        File.AppendAllText(path, Json.Serialize(value)+Environment.NewLine, new UTF8Encoding(false));
    }
    private static Dictionary<string,object> Verify(string root)
    {
        var manifest=(Dictionary<string,object>)Json.DeserializeObject(File.ReadAllText(Path.Combine(root,"acceptance-build.json")));
        foreach(Dictionary<string,object> file in (object[])manifest["files"])
        {
            var path=Path.GetFullPath(Path.Combine(root,(string)file["path"]));
            if(!path.StartsWith(root.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid build path");
            using(var sha=SHA256.Create())
            using(var stream=File.OpenRead(path))
            {
                var actual=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();
                if(actual!=(string)file["sha256"])throw new InvalidDataException("Build hash mismatch: "+file["path"]);
            }
        }
        return manifest;
    }
    public static int Main(string[] args)
    {
        Console.OutputEncoding=Encoding.UTF8;
        try
        {
            var root=AppDomain.CurrentDomain.BaseDirectory;
            var manifest=Verify(root);
            if(args.Length==1&&args[0]=="--verify") {Console.WriteLine("PASS build hashes "+manifest["version"]);return 0;}
            if(args.Length!=1||(args[0]!="pc1"&&args[0]!="pc2"))
            {Console.WriteLine("Usage: ECS-Acceptance.exe pc1 | pc2 | --verify");return 2;}
            var player=args[0];var run=player+"-"+DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var evidence=Path.Combine(root,"evidence",run);Directory.CreateDirectory(evidence);
            File.Copy(Path.Combine(root,"acceptance-build.json"),Path.Combine(evidence,"acceptance-build.json"));
            Record(Path.Combine(evidence,"machine.json"),new { utc=DateTime.UtcNow.ToString("o"),computer=Environment.MachineName,player=player,build=manifest["version"],commit=manifest["commit"],clockStatus=Command("w32tm.exe","/query /status") });
            var client=Path.Combine(root,"client");
            var arguments="-screen-fullscreen 0 -screen-width 1280 -screen-height 720 -lan-host=198.51.100.10 -lan-profile=v0111-"+player+" -ecs-test-login-port=43220 -ecs-diagnostics=\""+Path.Combine(evidence,"frames-connect.log")+"\" -logFile \""+Path.Combine(evidence,"unity.log")+"\"";
            Console.CancelKeyPress+=(sender,eventArgs)=>{eventArgs.Cancel=true;interrupted=true;};
            using(var game=Process.Start(new ProcessStartInfo(Path.Combine(client,"CrazyShooter.exe"),arguments){WorkingDirectory=client,UseShellExecute=false}))
            {
                File.WriteAllText(Path.Combine(evidence,"client.pid"),game.Id.ToString());
                Console.WriteLine("Started "+player+" on isolated ECS. Both players ready; room owner starts.");
                Console.WriteLine("Evidence: "+evidence);
                while(!game.HasExited&&!interrupted)
                {
                    var rows=new List<string>();
                    foreach(var line in Command("netstat.exe","-ano -p tcp").Split('\n'))
                    {
                        var row=line.Trim();
                        if(row.EndsWith(" "+game.Id,StringComparison.Ordinal)&&
                            (row.Contains(":43220 ")||row.Contains(":43223 ")||row.Contains(":43234 ")))rows.Add(row);
                    }
                    Record(Path.Combine(evidence,"connections.jsonl"),new {utc=DateTime.UtcNow.ToString("o"),connections=rows});
                    Thread.Sleep(2000);game.Refresh();
                }
            }
            var archive=Path.Combine(root,"ECS-evidence-"+run+".zip");
            ZipFile.CreateFromDirectory(evidence,archive,CompressionLevel.Optimal,false);
            Console.WriteLine("Evidence ZIP: "+archive);
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error.GetType().Name+": "+error.Message);return 1;}
    }
}
