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
    [STAThread]
    private static int Main(string[] args)
    {
        try {
            var confirmation = UpdateDialog.Create("发现新版本", "测试更新说明", true);
            if (!confirmation.AllowsTransparency || ((System.Windows.Controls.Border)confirmation.Content).CornerRadius.TopLeft != 12)
                throw new Exception("Update dialog must have transparent rounded corners.");
            if (((System.Windows.Controls.Button)confirmation.FindName("ConfirmChoice")).Content.ToString() != "下载并安装" ||
                ((System.Windows.Controls.Button)confirmation.FindName("CancelChoice")).Visibility != System.Windows.Visibility.Visible)
                throw new Exception("Update confirmation controls are incorrect.");
            var message = UpdateDialog.Create("软件更新", "测试错误信息", false);
            if (((System.Windows.Controls.Button)message.FindName("CancelChoice")).Visibility != System.Windows.Visibility.Collapsed ||
                ((System.Windows.Controls.TextBlock)message.FindName("DialogMessage")).Text != "测试错误信息")
                throw new Exception("Update message controls are incorrect.");
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
            string notes = Updater.ParseReleaseNotes("<div data-test-selector=\"body-content\"><h2>更新内容</h2><div><ul><li>修复 &amp; 改进</li><li>支持 <strong>中文</strong></li></ul></div></div><p>不属于正文</p>");
            if (!notes.Contains("更新内容") || !notes.Contains("• 修复 & 改进") || !notes.Contains("• 支持 中文") || notes.Contains("不属于正文"))
                throw new Exception("Release page notes were not extracted correctly: " + notes);
            if (Updater.ParseReleaseNotes("<div data-test-selector='body-content'></div>") != "暂无更新说明。") throw new Exception("Empty release notes were not handled.");
            if (!Updater.ParseReleaseNotes("<p>changed layout</p>").Contains("GitHub Releases")) throw new Exception("Missing release notes must provide a fallback.");
            string directory = Path.GetDirectoryName(args[0]);
            string checkMarker = Path.Combine(directory, "background-check");
            var future = new Updater.Release { Version = new Version(99, 0, 0), Tag = "v99.0.0" };
            if (Updater.Check(new[] { "--check-update", checkMarker, "0", args[0], "--background" }, () => future) != 0 ||
                Json.Text(Json.Get(Json.Read(checkMarker + ".result"), "status")) != "available")
                throw new Exception("Background checking did not report an available version without a dialog.");
            var current = new Updater.Release { Version = new Version(0, 0, 0), Tag = "v0.0.0" };
            if (Updater.Check(new[] { "--check-update", checkMarker, "0", args[0], "--background" }, () => current) != 0 ||
                Json.Text(Json.Get(Json.Read(checkMarker + ".result"), "status")) != "current")
                throw new Exception("Background checking did not report a current version.");
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
                var page = Updater.CheckReleasePageLive();
                if (page.Notes.Contains("请查看项目的 GitHub Releases 页面")) throw new Exception("Live release notes were not extracted from the release page.");
                Console.WriteLine("PASS: live release page notes retrieved for " + page.Tag + ".");
            }
            Console.WriteLine("PASS: release version, asset source, digest, size and prerelease validation.");
            Console.WriteLine("PASS: replacement installed and launched.");
            return 0;
        } catch (Exception e) { Console.Error.WriteLine("FAIL: " + e); return 1; }
    }
}
