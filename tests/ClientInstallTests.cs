using System;
using System.IO;

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
