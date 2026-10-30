using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("OPPO 键鼠共享助手")]
[assembly: AssemblyDescription("键鼠共享助手与 C# 原生后端")]
[assembly: AssemblyProduct("OPPO Mouse Sharing")]
[assembly: AssemblyVersion("1.3.1.0")]
[assembly: AssemblyFileVersion("1.3.1.0")]

internal static class Launcher
{
    private static readonly string[] Files = {
        "助手.ps1", "界面.ps1", "main.xaml", "close.xaml", "settings.xaml", "app.ico"
    };
    private static readonly string AppRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OPPO-Mouse-sharing");
    private static readonly string PowerShell = Path.Combine(
        Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && (args[0] == "--backend" || args[0] == "--connection-watch" || args[0] == "--connection-once" || args[0].StartsWith("--fixture-")))
        {
            try
            {
                int parent = args.Length > 1 ? Json.Number(args[1]) : 0;
                if (args[0] == "--backend") return Backend.Share(parent).GetAwaiter().GetResult();
                if (args[0] == "--connection-watch") return Backend.Watch(parent).GetAwaiter().GetResult();
                if (args[0] == "--connection-once") { Console.OutputEncoding = new UTF8Encoding(false); Console.WriteLine(Json.Encode(Backend.QueryConnection(new PipeHttp()).GetAwaiter().GetResult())); return 0; }
                return Backend.Fixture(args[0]);
            }
            catch { return 1; }
        }
        bool selfTest = args.Contains("--self-test");
        bool startInTray = args.Contains("--startup");
        Mutex instance = null;
        bool ownsInstance = false;
        try
        {
            Directory.CreateDirectory(AppRoot);
            if (!selfTest)
            {
                instance = new Mutex(false, @"Local\OppoMouseSharingExe");
                try { ownsInstance = instance.WaitOne(0); }
                catch (AbandonedMutexException) { ownsInstance = true; }
                if (!ownsInstance)
                {
                    if (!startInTray) MessageBox.Show("助手已经打开，请双击右下角托盘图标恢复窗口。", "键鼠共享助手");
                    return 0;
                }
            }
            string runtime = PrepareRuntime();
            string data = Path.Combine(AppRoot, selfTest ? "validation-data" : "data");
            Directory.CreateDirectory(data);
            if (selfTest) return SelfTest(runtime, data);

            // 只迁移偏好设置，不复制运行状态、地址或进程标识。
            string settings = Path.Combine(data, "settings.json");
            string previous = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "settings.json");
            if (!File.Exists(settings) && File.Exists(previous) && new FileInfo(previous).Length < 65536)
                File.Copy(previous, settings, false);

            string arguments = "-NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File "
                + Quote(Path.Combine(runtime, "助手.ps1"));
            if (args.Contains("--start-sharing")) arguments += " -StartSharing";
            if (startInTray) arguments += " -StartInTray";
            ProcessStartInfo info = StartInfo(PowerShell, arguments, runtime, data);
            using (Process process = Process.Start(info))
            {
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("助手启动失败。请检查 OPPO 互联安装及本地日志。");
            }
            return 0;
        }
        catch (Exception error)
        {
            try { File.AppendAllText(Path.Combine(AppRoot, "launcher-error.log"),
                DateTime.Now.ToString("s") + " " + error + Environment.NewLine, Encoding.UTF8); }
            catch { }
            if (!selfTest) MessageBox.Show(error.Message, "键鼠共享助手", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            if (ownsInstance) instance.ReleaseMutex();
            if (instance != null) instance.Dispose();
        }
    }

    private static string PrepareRuntime()
    {
        using (Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
        {
            if (payload == null) throw new InvalidOperationException("程序资源缺失，请重新复制完整 EXE。");
            string hash;
            using (SHA256 sha = SHA256.Create())
                hash = BitConverter.ToString(sha.ComputeHash(payload)).Replace("-", "").ToLowerInvariant().Substring(0, 16);
            payload.Position = 0;
            string runtimeRoot = Path.Combine(AppRoot, "runtime");
            Directory.CreateDirectory(runtimeRoot);
            string target = Path.Combine(runtimeRoot, hash);
            if (File.Exists(Path.Combine(target, "complete")) && Files.All(name => File.Exists(Path.Combine(target, name))))
                return target;

            // 压缩包只包含明确列出的文件，不包含子目录。
            string stage = Path.Combine(runtimeRoot, hash + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            using (ZipArchive archive = new ZipArchive(payload, ZipArchiveMode.Read, true))
            {
                foreach (string name in Files)
                {
                    ZipArchiveEntry entry = archive.GetEntry(name);
                    if (entry == null) throw new InvalidOperationException("程序资源不完整：" + name);
                    using (Stream input = entry.Open())
                    using (FileStream output = File.Create(Path.Combine(stage, name)))
                        input.CopyTo(output);
                }
            }
            File.WriteAllText(Path.Combine(stage, "complete"), hash);
            if (Directory.Exists(target))
            {
                // 之前解压失败时，不得覆盖正在运行的版本。
                return stage;
            }
            try { Directory.Move(stage, target); }
            catch (IOException)
            {
                if (File.Exists(Path.Combine(target, "complete")) && Files.All(name => File.Exists(Path.Combine(target, name))))
                    return target;
                throw;
            }
            return target;
        }
    }

    private static string Quote(string value) { return "\"" + value + "\""; }

    private static ProcessStartInfo StartInfo(string file, string arguments, string runtime, string data)
    {
        ProcessStartInfo info = new ProcessStartInfo(file, arguments);
        info.WorkingDirectory = runtime;
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.WindowStyle = ProcessWindowStyle.Hidden;
        info.EnvironmentVariables["OPPO_MOUSE_SHARING_DATA"] = data;
        info.EnvironmentVariables["OPPO_MOUSE_SHARING_EXE"] = Application.ExecutablePath;
        return info;
    }

    private static int SelfTest(string runtime, string data)
    {
        StringBuilder log = new StringBuilder();
        log.AppendLine("Runtime: " + runtime);
        RunCheck(PowerShell, "-NoProfile -NonInteractive -STA -ExecutionPolicy Bypass -File "
            + Quote(Path.Combine(runtime, "助手.ps1")) + " -ValidateOnly", runtime, data, log);
        File.WriteAllText(Path.Combine(AppRoot, "self-test.log"), log.ToString(), Encoding.UTF8);
        return 0;
    }

    private static void RunCheck(string file, string arguments, string runtime, string data, StringBuilder log)
    {
        ProcessStartInfo info = StartInfo(file, arguments, runtime, data);
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        using (Process process = Process.Start(info))
        {
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            log.Append(output).Append(error);
            if (process.ExitCode != 0)
            {
                File.WriteAllText(Path.Combine(AppRoot, "self-test.log"), log.ToString(), Encoding.UTF8);
                throw new InvalidOperationException("离线自检失败：" + Path.GetFileName(file));
            }
        }
    }
}
