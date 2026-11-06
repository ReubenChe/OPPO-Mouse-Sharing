using System;
using System.IO;

internal static class UpdaterTests
{
    private static object Fixture(string tag, string url, string digest, int size, bool prerelease = false)
    {
        return Json.Fields("tag_name", tag, "draft", false, "prerelease", prerelease, "assets", new object[] {
            Json.Fields("name", "OPPO-Mouse-sharing-" + tag + ".exe", "browser_download_url", url, "digest", digest, "size", size)
        });
    }
    private static void Reject(object value)
    {
        try { Updater.ParseRelease(value); } catch (IOException) { return; }
        throw new Exception("Invalid release was accepted.");
    }
    private static int Main(string[] args)
    {
        try {
            string tag = "v1.4.5", url = "https://github.com/ReubenChe/OPPO-Mouse-Sharing/releases/download/v1.4.5/OPPO-Mouse-sharing-v1.4.5.exe";
            string digest = "sha256:" + new String('a', 64);
            var release = Updater.ParseRelease(Fixture(tag, url, digest, 1024));
            if (release.Version != new Version(1, 4, 5) || release.Digest != digest || release.Url != url) throw new Exception("Valid release was not parsed.");
            Reject(Fixture(tag, "https://example.com/update.exe", digest, 1024));
            Reject(Fixture(tag, url, "sha256:bad", 1024));
            Reject(Fixture(tag, url, digest, 0));
            Reject(Fixture(tag, url, digest, 1024, true));
            Reject(Fixture("v1.4.5-beta", url, digest, 1024));
            Reject(Json.Fields("tag_name", tag, "assets", new object[0]));
            string html = "<a href=\"/ReubenChe/OPPO-Mouse-Sharing/releases/download/v1.4.5/OPPO-Mouse-sharing-v1.4.5.exe\">file</a>" +
                "<clipboard-copy aria-label=\"Copy to clipboard digest for OPPO-Mouse-sharing-v1.4.5.exe\" value=\"" + digest + "\"></clipboard-copy>";
            var fallback = Updater.ParseReleasePage(tag, html);
            if (fallback.Version != new Version(1, 4, 5) || fallback.Digest != digest || fallback.Url != url)
                throw new Exception("Rate-limit fallback did not preserve asset validation.");
            try { Updater.ParseReleasePage(tag, html.Replace(digest, "sha256:bad")); throw new Exception("Missing digest was accepted."); } catch (IOException) { }
            string directory = Path.GetDirectoryName(args[0]);
            string target = Path.Combine(directory, "target.exe");
            File.WriteAllText(target, "old executable");
            if (Updater.Apply(new[] { "--apply-update", Int32.MaxValue.ToString(), target, args[0] }) != 0)
                throw new Exception("Installation failed.");
            string marker = Path.Combine(directory, "started.txt");
            for (int i = 0; i < 50 && !File.Exists(marker); i++) System.Threading.Thread.Sleep(100);
            if (!File.Exists(marker) || new FileInfo(target).Length != new FileInfo(args[0]).Length)
                throw new Exception("Replacement was not installed and started.");
            if (args.Length > 1 && args[1] == "--live") {
                var live = Updater.CheckReleaseLive();
                if (live.Digest == null || live.Url == null) throw new Exception("Live release is missing verified metadata.");
                Console.WriteLine("PASS: live GitHub release " + live.Tag + " and SHA-256 metadata retrieved.");
            }
            Console.WriteLine("PASS: release version, asset source, digest, size and prerelease validation.");
            Console.WriteLine("PASS: replacement installed and launched.");
            return 0;
        } catch (Exception e) { Console.Error.WriteLine("FAIL: " + e); return 1; }
    }
}
