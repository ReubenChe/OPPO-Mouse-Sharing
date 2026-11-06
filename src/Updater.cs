using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

internal static class Updater
{
    private const string Api = "https://api.github.com/repos/ReubenChe/OPPO-Mouse-Sharing/releases/latest";
    private const string DownloadPrefix = "https://github.com/ReubenChe/OPPO-Mouse-Sharing/releases/download/";
    private const long MaxBytes = 200L * 1024 * 1024;
    internal sealed class Release
    {
        internal Version Version;
        internal string Tag, Url, Digest, Notes;
        internal long Size;
    }
    private sealed class TimedClient : WebClient
    {
        internal Uri LastUri;
        protected override WebRequest GetWebRequest(Uri address)
        {
            var request = base.GetWebRequest(address);
            request.Timeout = 30000;
            var http = request as HttpWebRequest;
            if (http != null) http.ReadWriteTimeout = 30000;
            return request;
        }
        protected override WebResponse GetWebResponse(WebRequest request)
        {
            var response = base.GetWebResponse(request);
            LastUri = response.ResponseUri;
            return response;
        }
    }
    internal static Release ParseReleasePage(string tag, string html)
    {
        Version version;
        if (!Regex.IsMatch(tag ?? "", @"^v\d+\.\d+\.\d+$") || !Version.TryParse(tag.Substring(1), out version))
            throw new IOException("发布版本号格式无效。");
        string name = "OPPO-Mouse-sharing-" + tag + ".exe";
        string path = "/ReubenChe/OPPO-Mouse-Sharing/releases/download/" + tag + "/" + name;
        var links = Regex.Matches(html, "href=\"" + Regex.Escape(path) + "\"", RegexOptions.CultureInvariant);
        var digestTags = Regex.Matches(html, "<clipboard-copy\\b[^>]*>", RegexOptions.CultureInvariant);
        string digest = null;
        int matches = 0;
        foreach (Match item in digestTags)
        {
            if (!item.Value.Contains("aria-label=\"Copy to clipboard digest for " + name + "\"")) continue;
            var value = Regex.Match(item.Value, "\\bvalue=\"(sha256:[0-9a-fA-F]{64})\"", RegexOptions.CultureInvariant);
            if (value.Success) { digest = value.Groups[1].Value; matches++; }
        }
        if (links.Count != 1 || matches != 1) throw new IOException("发布页面没有符合要求的更新文件或 SHA-256 校验信息。请打开项目发布页面手动下载。");
        return new Release { Tag = tag, Version = version, Url = "https://github.com" + path, Digest = digest, Size = 0, Notes = "发布说明请查看项目的 GitHub Releases 页面。" };
    }
    internal static string ParseReleaseNotes(string html)
    {
        const string unavailable = "发布说明请查看项目的 GitHub Releases 页面。";
        if (String.IsNullOrEmpty(html)) return unavailable;
        var opening = Regex.Match(html, @"<div\b(?=[^>]*\bdata-test-selector\s*=\s*['""]body-content['""])[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!opening.Success) return unavailable;
        int start = opening.Index + opening.Length, depth = 1, end = -1;
        foreach (Match tag in Regex.Matches(html.Substring(start), @"</?div\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            if (tag.Value.StartsWith("</", StringComparison.Ordinal)) depth--;
            else if (!tag.Value.EndsWith("/>", StringComparison.Ordinal)) depth++;
            if (depth == 0) { end = start + tag.Index; break; }
        }
        if (end < 0) return unavailable;
        string content = html.Substring(start, end - start);
        content = Regex.Replace(content, @"<(script|style|svg)\b[^>]*>[\s\S]*?</\1\s*>", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        content = Regex.Replace(content, @"<li\b[^>]*>", "\n• ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        content = Regex.Replace(content, @"<br\s*/?>|</(?:p|div|li|h[1-6]|ul|ol|pre|blockquote)\s*>", "\n", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        content = Regex.Replace(content, @"<[^>]*>", "", RegexOptions.CultureInvariant);
        content = WebUtility.HtmlDecode(content).Replace('\u00a0', ' ');
        content = Regex.Replace(content, @"[ \t]*\r?\n[ \t]*", "\n", RegexOptions.CultureInvariant);
        content = Regex.Replace(content, @"\n{3,}", "\n\n", RegexOptions.CultureInvariant).Trim();
        return content.Length == 0 ? "暂无更新说明。" : content;
    }
    private static Release GetLatestViaPage(TimedClient client)
    {
        string html = client.DownloadString("https://github.com/ReubenChe/OPPO-Mouse-Sharing/releases/latest");
        var location = client.LastUri;
        if (location == null || location.Scheme != "https" || location.Host != "github.com") throw new IOException("无法确认最新正式版的来源。");
        var match = Regex.Match(location.AbsolutePath, @"^/ReubenChe/OPPO-Mouse-Sharing/releases/tag/(v\d+\.\d+\.\d+)$", RegexOptions.CultureInvariant);
        if (!match.Success) throw new IOException("无法确认最新正式版的版本号。");
        string tag = match.Groups[1].Value;
        var release = ParseReleasePage(tag, client.DownloadString("https://github.com/ReubenChe/OPPO-Mouse-Sharing/releases/expanded_assets/" + tag));
        release.Notes = ParseReleaseNotes(html);
        return release;
    }
    private static Release GetLatest(TimedClient client)
    {
        try { return ParseRelease(Json.Decode(client.DownloadString(Api))); }
        catch (WebException error)
        {
            var response = error.Response as HttpWebResponse;
            if (response == null || (response.StatusCode != HttpStatusCode.Forbidden && (int)response.StatusCode != 429)) throw;
            response.Dispose();
            return GetLatestViaPage(client);
        }
    }
    internal static Release CheckReleaseLive()
    {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
        using (var client = new TimedClient()) {
            client.Encoding = Encoding.UTF8;
            client.Headers.Add("User-Agent", "OPPO-Mouse-sharing-updater");
            client.Headers.Add("Accept", "application/vnd.github+json");
            return GetLatest(client);
        }
    }
    internal static Release CheckReleasePageLive()
    {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
        using (var client = new TimedClient()) {
            client.Encoding = Encoding.UTF8;
            client.Headers.Add("User-Agent", "OPPO-Mouse-sharing-updater");
            return GetLatestViaPage(client);
        }
    }
    internal static Release ParseRelease(object raw)
    {
        var data = Json.Map(raw);
        string tag = Json.Text(Json.Get(data, "tag_name"));
        if (!Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$")) throw new IOException("发布版本号格式无效。");
        Version version;
        if (!Version.TryParse(tag.Substring(1), out version)) throw new IOException("发布版本号无效。");
        if (Json.Get(data, "draft") is bool && (bool)Json.Get(data, "draft") || Json.Get(data, "prerelease") is bool && (bool)Json.Get(data, "prerelease"))
            throw new IOException("更新不是正式版本。");
        var result = new Release { Tag = tag, Version = version, Notes = Json.Text(Json.Get(data, "body")) };
        string expected = "OPPO-Mouse-sharing-" + tag + ".exe";
        int matches = 0;
        foreach (object item in Json.List(Json.Get(data, "assets")))
        {
            if (Json.Text(Json.Get(item, "name")) != expected) continue;
            matches++;
            result.Url = Json.Text(Json.Get(item, "browser_download_url"));
            result.Digest = Json.Text(Json.Get(item, "digest"));
            long size;
            if (!Int64.TryParse(Json.Text(Json.Get(item, "size")), out size)) size = 0;
            result.Size = size;
        }
        string expectedUrl = DownloadPrefix + tag + "/" + expected;
        if (matches != 1 || result.Url != expectedUrl || !Regex.IsMatch(result.Digest ?? "", @"^sha256:[0-9a-fA-F]{64}$") || result.Size <= 0 || result.Size > MaxBytes)
            throw new IOException("该版本没有符合要求的更新文件或 SHA-256 校验信息。请打开项目发布页面手动下载。");
        return result;
    }
    private static string Hash(string path)
    {
        using (var stream = File.OpenRead(path))
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
    private static string Quote(string value) { return "\"" + value + "\""; }
    private static void Error(string message) { UpdateDialog.Show("软件更新", message, false); }
    internal static int Check(string[] args)
    {
        return Check(args, null);
    }
    internal static int Check(string[] args, Func<Release> getRelease)
    {
        string folder = null;
        string marker = args.Length == 4 || args.Length == 5 ? args[1] : null;
        try
        {
            if (args.Length != 4 && !(args.Length == 5 && args[4] == "--background")) throw new IOException("更新参数无效。");
            bool background = args.Length == 5;
            int parent = Int32.Parse(args[2]);
            string target = Path.GetFullPath(args[3]);
            marker = Path.GetFullPath(marker);
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            using (var client = new TimedClient())
            {
                client.Encoding = Encoding.UTF8;
                client.Headers.Add("User-Agent", "OPPO-Mouse-sharing-updater");
                client.Headers.Add("Accept", "application/vnd.github+json");
                var release = getRelease == null ? GetLatest(client) : getRelease();
                Version current = Assembly.GetExecutingAssembly().GetName().Version;
                if (release.Version.CompareTo(new Version(current.Major, current.Minor, current.Build)) <= 0)
                {
                    Json.Write(marker + ".result", Json.Fields("status", "current", "message", "当前已是最新版本（v" + current.ToString(3) + "）。")); return 0;
                }
                if (background) { Json.Write(marker + ".result", Json.Fields("status", "available", "message", "发现新版本 " + release.Tag)); return 0; }
                string notes = release.Notes.Length > 1200 ? release.Notes.Substring(0, 1200) + "…" : release.Notes;
                string question = "发现新版本 " + release.Tag + "（当前 v" + current.ToString(3) + "）\n\n" + notes;
                if (!UpdateDialog.Show("发现新版本", question, true)) { Json.Write(marker + ".result", Json.Fields("status", "cancelled", "message", "已取消更新。")); return 0; }
                folder = Path.Combine(Path.GetTempPath(), "oppo-update-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                string download = Path.Combine(folder, "download.exe");
                client.DownloadFile(release.Url, download);
                long length = new FileInfo(download).Length;
                if (length <= 0 || length > MaxBytes || (release.Size > 0 && length != release.Size) || !String.Equals(Hash(download), release.Digest.Substring(7), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("下载文件的大小或 SHA-256 校验不匹配，已取消更新。");
                string helper = Path.Combine(folder, "updater.exe");
                File.Copy(Application.ExecutablePath, helper);
                var info = new ProcessStartInfo(helper, "--apply-update " + parent + " " + Quote(target) + " " + Quote(download));
                info.UseShellExecute = false; info.CreateNoWindow = true;
                Process.Start(info).Dispose();
                File.WriteAllText(marker, "ready", Encoding.UTF8);
                folder = null; // 更新进程接管临时目录。
                return 0;
            }
        }
        catch (Exception e) {
            try { if (marker != null) Json.Write(marker + ".result", Json.Fields("status", "error", "message", "检查或下载更新失败：" + e.Message)); } catch { }
            return 1;
        }
        finally { if (folder != null) { try { Directory.Delete(folder, true); } catch { } } }
    }
    internal static int Apply(string[] args)
    {
        string prepared = null, backup = null;
        try
        {
            if (args.Length != 4) throw new IOException("更新参数无效。");
            int parent = Int32.Parse(args[1]);
            string target = Path.GetFullPath(args[2]), download = Path.GetFullPath(args[3]);
            try { using (var process = Process.GetProcessById(parent)) { if (!process.WaitForExit(90000)) throw new IOException("助手未能及时退出，更新已取消。"); } }
            catch (ArgumentException) { }
            prepared = target + ".update-" + Guid.NewGuid().ToString("N");
            backup = target + ".backup-" + Guid.NewGuid().ToString("N");
            File.Copy(download, prepared);
            File.Replace(prepared, target, backup);
            prepared = null;
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch { File.Replace(backup, target, null); backup = null; throw; }
            try { File.Delete(backup); backup = null; } catch { }
            return 0;
        }
        catch (Exception e) { Error("安装更新失败：" + e.Message + (backup == null ? "" : "\n旧版本备份：" + backup)); return 1; }
        finally { if (prepared != null) { try { File.Delete(prepared); } catch { } } }
    }
}
