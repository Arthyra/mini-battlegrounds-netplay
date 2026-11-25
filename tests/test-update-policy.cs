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
            const string setting = "MBG_UPDATE_MANIFEST_URLS";
            const string preferred = "https://updates.example.invalid/latest.json";
            const string fallback = "https://fallback.example.invalid/latest.json";
            var liveEndpoints = Environment.GetEnvironmentVariable(setting);

            Environment.SetEnvironmentVariable(setting, null);
            Check(((string[])Call("ReadUpdateUrls")).Length == 0, "fresh source build has no update endpoint");
            Environment.SetEnvironmentVariable(setting, " " + preferred + " ; ; " + fallback + " ");
            Check(((string[])Call("ReadUpdateUrls")).SequenceEqual(new[] { preferred, fallback }),
                "configured HTTPS mirrors retain their order");
            Environment.SetEnvironmentVariable(setting, preferred + ";http://localhost/latest.json");
            try { Call("ReadUpdateUrls"); throw new Exception("HTTP mirror configured"); }
            catch (TargetInvocationException e)
            {
                Check(e.InnerException is InvalidDataException, "HTTP mirror configuration rejected before network request");
            }
            Environment.SetEnvironmentVariable(setting, liveEndpoints);

            var request = (HttpWebRequest)Call("UpdateRequest", preferred, 30);
            Check(!request.AllowAutoRedirect, "redirects cannot downgrade transport");
            try { Call("UpdateRequest", "http://localhost/latest.json", 30); throw new Exception("HTTP accepted"); }
            catch (TargetInvocationException e)
            {
                Check(e.InnerException is InvalidDataException, "HTTP rejected before network request");
            }

            var synthetic = Activator.CreateInstance(type.Assembly.GetTypes()
                .First(t => t.GetField("Signature") != null && t.GetField("Url") != null), true);
            var fields = synthetic.GetType();
            fields.GetField("Version").SetValue(synthetic, "1.0.0");
            fields.GetField("Sha256").SetValue(synthetic, new string('a', 64));
            fields.GetField("Url").SetValue(synthetic, "https://updates.example.invalid/package.zip");
            fields.GetField("Signature").SetValue(synthetic, Convert.ToBase64String(new byte[256]));
            Check(!(bool)Call("VerifyUpdateManifest", synthetic), "forged signature rejected");
            fields.GetField("Url").SetValue(synthetic, "http://updates.example.invalid/package.zip");
            Check(!(bool)Call("VerifyUpdateManifest", synthetic), "HTTP package URL rejected");

            if (live)
            {
                var mirrors = (string[])Call("ReadUpdateUrls");
                if (mirrors.Length == 0)
                    throw new InvalidOperationException("Set " + setting + " for an authorized mirror before using --live.");
                var info = Call("FetchUpdate", (object)mirrors);
                Check((bool)Call("VerifyUpdateManifest", info), "configured mirror RSA-SHA256 manifest signature accepted");
                fields.GetField("Signature").SetValue(info, "AAAA");
                Check(!(bool)Call("VerifyUpdateManifest", info), "forged signature rejected (live)");
            }
            else
            {
                Console.WriteLine("SKIP live mirror (explicit configuration and --live required)");
            }
            Check(type.GetMethod("StripZoneMarkers", BindingFlags.NonPublic | BindingFlags.Static) == null, "startup-wide Zone.Identifier removal removed");
            Console.WriteLine(passed + " passed"); return 0;
        }
        catch(Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
