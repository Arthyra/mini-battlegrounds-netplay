using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;

// Prints the discovery endpoint list the launcher computes, so multi-NIC
// broadcast coverage can be sanity-checked against the real adapters.
internal static class DiscoveryEndpointsTest
{
    private static void Main(string[] args)
    {
        var launcher = Assembly.LoadFrom(Path(args[0]));
        var type = launcher.GetTypes().First(t => t.GetMethod("DiscoveryEndpoints", BindingFlags.NonPublic | BindingFlags.Static) != null);
        var method = type.GetMethod("DiscoveryEndpoints", BindingFlags.NonPublic | BindingFlags.Static);
        var endpoints = (List<IPEndPoint>)method.Invoke(null, null);
        foreach (var endpoint in endpoints)
            Console.WriteLine(endpoint);
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
            Console.WriteLine("up-nic: " + nic.Name + " " + nic.NetworkInterfaceType);
    }

    private static string Path(string relative)
    {
        return System.IO.Path.GetFullPath(relative);
    }
}
