using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
internal static class UpdatePolicyTests
{
    static Type type;
    static int passed;
    static object Call(string name, params object[] args) { return type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, args); }
    static void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
    public static int Main(string[] args)
    {
        try
        {
            bool live = Array.IndexOf(args, "--live") >= 0;
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            type = Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("Launcher");
            if(type == null) type = Array.Find(Assembly.LoadFrom(Path.GetFullPath(args[0])).GetTypes(), t => t.GetMethod("VerifyUpdateManifest", BindingFlags.NonPublic | BindingFlags.Static) != null);
            var urls = (string[])type.GetField("UpdateUrls", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            Check(Array.TrueForAll(urls, u => new Uri(u).Scheme == "https"), "all manifest endpoints HTTPS");
            var request = (HttpWebRequest)Call("UpdateRequest", "https://198.51.100.10/mbg-update/latest.json", 30);
            Check(!request.AllowAutoRedirect, "redirects cannot downgrade transport");
            try { Call("UpdateRequest", "http://127.0.0.1:1/latest.json", 30); throw new Exception("HTTP accepted"); }
            catch(TargetInvocationException e) { Check(e.InnerException is InvalidDataException, "HTTP rejected before network request"); }

            // Local rejects on a synthetic manifest: these hold regardless of
            // what the production mirror currently serves.
            var synthetic = Activator.CreateInstance(type.Assembly.GetTypes().First(t => t.GetField("Signature") != null && t.GetField("Url") != null));
            var fields = synthetic.GetType();
            fields.GetField("Signature").SetValue(synthetic, "AAAA");
            Check(!(bool)Call("VerifyUpdateManifest", synthetic), "forged signature rejected");
            fields.GetField("Url").SetValue(synthetic, "http://198.51.100.10:8080/update/client.zip");
            Check(!(bool)Call("VerifyUpdateManifest", synthetic), "HTTP package URL rejected");

            // The live fetches assert against the PRODUCTION mirror and are
            // opt-in: a deployment, cleanup or outage there must not fail CI.
            if (live)
            {
                var info = Call("FetchUpdateFrom", "https://198.51.100.10/mbg-update/latest.json");
                Check((bool)Call("VerifyUpdateManifest", info), "real ECS RSA-SHA256 manifest signature accepted");
                fields.GetField("Signature").SetValue(info, "AAAA");
                Check(!(bool)Call("VerifyUpdateManifest", info), "forged signature rejected (live)");
                info = Call("FetchUpdateFrom", "https://198.51.100.10:8445/mbg-update/latest.json");
                fields.GetField("Url").SetValue(info, "http://198.51.100.10:8080/update/client.zip");
                Check(!(bool)Call("VerifyUpdateManifest", info), "HTTP package URL rejected (live)");
            }
            else
            {
                Console.WriteLine("SKIP real-mirror fetches (pass --live to include them)");
            }
            Check(type.GetMethod("StripZoneMarkers", BindingFlags.NonPublic | BindingFlags.Static) == null, "startup-wide Zone.Identifier removal removed");
            Console.WriteLine(passed + " passed"); return 0;
        }
        catch(Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
