using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;

internal sealed class FakeSdk : IIpc
{
    internal object[] Devices;
    internal bool Fail;
    public Task<Dictionary<string, object>> Request(string pipe, string method, object parameters, int timeout = 3500)
    {
        if (Fail) throw new IOException("fixture error");
        if (pipe != "devicespace_sdk_ipc" || method != "get_device_list") throw new Exception("Unexpected SDK request");
        return Task.FromResult(Json.Fields("data", Devices));
    }
}

internal static class ClientInstallTests
{
    private static string Resolve(params string[] paths)
    {
        return Backend.ResolveClientInstall(paths);
    }

    private static void ExpectError(params string[] paths)
    {
        try { Resolve(paths); }
        catch (IOException) { return; }
        throw new Exception("Expected a clear error instead of an arbitrary installation.");
    }

    private static string CreateInstall(string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, "resources"));
        Directory.CreateDirectory(Path.Combine(directory, "app_process"));
        File.WriteAllText(Path.Combine(directory, "resources", "app.asar"), "fixture");
        File.WriteAllText(Path.Combine(directory, "app_process", "screenshare.exe"), "fixture");
        return Path.Combine(directory, "O+Connect.exe");
    }

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "--live")
            {
                string detected = Backend.FindClientInstall();
                Console.WriteLine("Running client installation detected: " + detected);
                Console.WriteLine("Client version: " + Backend.InstalledVersion(Path.Combine(detected, "resources", "app.asar")));
                return 0;
            }
            string root = Path.Combine(Path.GetTempPath(), "oppo-install-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                if (!Backend.UseClientSdk("17.100.10") || !Backend.UseClientSdk("17.100.10.0") || !Backend.UseClientSdk("17.100.11") || !Backend.UseClientSdk("17.101.0") || !Backend.UseClientSdk("18.0.0") || Backend.UseClientSdk("17.100.9") || Backend.UseClientSdk("16.2.20") || Backend.UseClientSdk("unknown") || Backend.UseClientSdk("17.100.10-beta")) throw new Exception("Client version routing changed unexpectedly.");
                string config = Path.Combine(root, "config.json");
                for (int location = 0; location < 4; location++) {
                    File.WriteAllText(config, Json.Encode(Json.Fields("shareScreenDeviceMap", Json.Fields("phone", Json.Fields("screenLocation", location)))));
                    if (Backend.SdkDirection(config, "phone") != new[] { "right", "left", "top", "bottom" }[location]) throw new Exception("Incorrect saved SDK direction.");
                    if (Backend.SdkDirection(config, "other") != null) throw new Exception("Another device's direction must not be used.");
                }
                File.WriteAllText(config, "{}");
                if (Backend.SdkDirection(config, "phone") != null || Backend.SdkDirection(config + ".missing", "phone") != null) throw new Exception("Missing direction must remain unknown.");
                var sdk = new FakeSdk { Devices = new object[] { Json.Fields("device_type", 8, "is_connected", true, "device_id", "test", "device_name", "Test phone") } };
                if (Json.Text(Backend.QuerySdkConnection(sdk).GetAwaiter().GetResult()["phase"]) != "connected") throw new Exception("Connected SDK phone not detected.");
                sdk.Devices = new object[0];
                if (Json.Text(Backend.QuerySdkConnection(sdk).GetAwaiter().GetResult()["phase"]) != "disconnected") throw new Exception("Empty SDK phone list was not disconnected.");
                sdk.Fail = true;
                if (Json.Text(Backend.QuerySdkConnection(sdk).GetAwaiter().GetResult()["phase"]) != "unavailable") throw new Exception("SDK failure must not look like a disconnection.");
                string executable = CreateInstall(Path.Combine(root, "non-default drive", "互联客户端"));
                if (!String.Equals(Resolve(executable), Path.GetDirectoryName(executable), StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Non-default client installation was ignored.");
                if (!String.Equals(Resolve(executable, executable.ToUpperInvariant()), Path.GetDirectoryName(executable), StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Electron child processes must resolve to one installation.");
                string incomplete = CreateInstall(Path.Combine(root, "incomplete"));
                File.Delete(Path.Combine(Path.GetDirectoryName(incomplete), "app_process", "screenshare.exe"));
                if (Resolve(null, "", incomplete, executable) != Path.GetDirectoryName(executable))
                    throw new Exception("An unreadable or incomplete candidate hid a valid installation.");
                ExpectError();
                ExpectError(null, "", incomplete);
                string second = CreateInstall(Path.Combine(root, "second installation"));
                ExpectError(executable, second);
                File.Delete(Path.Combine(Path.GetDirectoryName(executable), "resources", "app.asar"));
                ExpectError(executable);
                Console.WriteLine("PASS: custom path, Unicode/spaces, duplicate processes, unreadable paths, missing files, no client and multiple installations.");
            }
            finally { Directory.Delete(root, true); }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL: " + error.Message); return 1; }
    }
}
