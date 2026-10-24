using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static class Json
{
    internal static string Encode(object value) { return Serializer().Serialize(value); }
    internal static object Decode(string value) { return Serializer().DeserializeObject(value.TrimStart('\uFEFF')); }
    private static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 20 * 1024 * 1024 }; }
    internal static Dictionary<string, object> Map(object value)
    {
        if (value is string) value = Decode((string)value);
        return value as Dictionary<string, object> ?? new Dictionary<string, object>();
    }
    internal static object Get(object value, string key)
    {
        object found; return Map(value).TryGetValue(key, out found) ? found : null;
    }
    internal static string Text(object value) { return Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""; }
    internal static int Number(object value, int fallback = 0)
    {
        int result; return Int32.TryParse(Text(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : fallback;
    }
    internal static object[] List(object value) { return value as object[] ?? new object[0]; }
    internal static Dictionary<string, object> Fields(params object[] pairs)
    {
        var result = new Dictionary<string, object>();
        for (int i = 0; i < pairs.Length; i += 2) result[(string)pairs[i]] = pairs[i + 1];
        return result;
    }
    internal static Dictionary<string, object> Read(string file) { return Map(Decode(File.ReadAllText(file, Encoding.UTF8))); }
    internal static void Write(string file, object value)
    {
        string temporary = file + ".tmp";
        File.WriteAllText(temporary, Encode(value), new UTF8Encoding(false));
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(file)) File.Replace(temporary, file, null);
                else File.Move(temporary, file);
                return;
            }
            catch (IOException) { if (attempt >= 3) throw; Thread.Sleep(25); }
        }
    }
}

internal interface IIpc
{
    Task<Dictionary<string, object>> Request(string pipe, string route, object parameters, int timeout = 3500);
}

internal sealed class PipeHttp : IIpc
{
    public async Task<Dictionary<string, object>> Request(string pipe, string route, object parameters, int timeout = 3500)
    {
        using (var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            Task<Dictionary<string, object>> operation = Exchange(client, route, parameters, timeout);
            if (await Task.WhenAny(operation, Task.Delay(timeout)).ConfigureAwait(false) != operation)
            {
                client.Dispose();
                ObserveFault(operation);
                throw new TimeoutException("本机通信超时。");
            }
            return await operation.ConfigureAwait(false);
        }
    }
    private static void ObserveFault(Task operation)
    {
        operation.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
    }
    private static async Task<Dictionary<string, object>> Exchange(NamedPipeClientStream pipe, string route, object parameters, int timeout)
    {
        await Task.Run(() => pipe.Connect(timeout)).ConfigureAwait(false);
        string encoded = parameters == null ? "" : Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Encode(parameters)));
        byte[] request = Encoding.ASCII.GetBytes("GET " + route + "?scope=1&params=" + encoded + " HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
        await pipe.WriteAsync(request, 0, request.Length).ConfigureAwait(false);
        await pipe.FlushAsync().ConfigureAwait(false);
        return Json.Map(Json.Decode(await ReadResponse(pipe).ConfigureAwait(false)));
    }
    internal static async Task<string> ReadResponse(Stream stream)
    {
        var reader = new HttpBytes(stream);
        string status = await reader.Line().ConfigureAwait(false);
        string[] parts = status.Split(' ');
        if (parts.Length < 2 || parts[1] != "200") throw new IOException("本机通信返回异常 HTTP 状态。");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int headerBytes = status.Length;
        for (;;)
        {
            string line = await reader.Line().ConfigureAwait(false);
            headerBytes += line.Length;
            if (headerBytes > 65536) throw new IOException("本机通信响应头过大。");
            if (line.Length == 0) break;
            int colon = line.IndexOf(':');
            if (colon <= 0) throw new IOException("本机通信响应格式异常。");
            headers[line.Substring(0, colon)] = line.Substring(colon + 1).Trim();
        }
        using (var body = new MemoryStream())
        {
            string transfer, length;
            if (headers.TryGetValue("Transfer-Encoding", out transfer) && transfer.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                for (;;)
                {
                    string line = await reader.Line().ConfigureAwait(false);
                    int count;
                    if (!Int32.TryParse(line.Split(';')[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out count) || count < 0)
                        throw new IOException("本机通信分块格式异常。");
                    if (count == 0)
                    {
                        int trailers = 0;
                        while ((await reader.Line().ConfigureAwait(false)).Length != 0) if (++trailers > 100) throw new IOException("响应尾部过大。");
                        break;
                    }
                    if (body.Length + count > 16 * 1024 * 1024) throw new IOException("本机通信响应过大。");
                    await reader.Copy(body, count).ConfigureAwait(false);
                    if ((await reader.Line().ConfigureAwait(false)).Length != 0) throw new IOException("本机通信分块边界异常。");
                }
            }
            else if (headers.TryGetValue("Content-Length", out length))
            {
                int count;
                if (!Int32.TryParse(length, out count) || count < 0 || count > 16 * 1024 * 1024) throw new IOException("本机通信响应长度异常。");
                await reader.Copy(body, count).ConfigureAwait(false);
            }
            else
            {
                int next;
                while ((next = await reader.Byte().ConfigureAwait(false)) >= 0)
                {
                    if (body.Length >= 16 * 1024 * 1024) throw new IOException("本机通信响应过大。");
                    body.WriteByte((byte)next);
                }
            }
            return Encoding.UTF8.GetString(body.ToArray());
        }
    }
    private sealed class HttpBytes
    {
        private readonly Stream stream;
        private readonly byte[] buffer = new byte[4096];
        private int offset, count;
        internal HttpBytes(Stream stream) { this.stream = stream; }
        internal async Task<int> Byte()
        {
            if (offset == count) { count = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false); offset = 0; if (count == 0) return -1; }
            return buffer[offset++];
        }
        internal async Task<string> Line()
        {
            var result = new StringBuilder();
            for (;;)
            {
                int next = await Byte().ConfigureAwait(false);
                if (next < 0) throw new EndOfStreamException("本机通信响应提前结束。");
                if (next == 10) return result.ToString().TrimEnd('\r');
                result.Append((char)next);
                if (result.Length > 65536) throw new IOException("本机通信响应行过长。");
            }
        }
        internal async Task Copy(Stream output, int length)
        {
            while (length > 0)
            {
                if (offset == count) { count = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false); offset = 0; if (count == 0) throw new EndOfStreamException("本机通信响应提前结束。"); }
                int take = Math.Min(length, count - offset); output.Write(buffer, offset, take); offset += take; length -= take;
            }
        }
    }
}

// 所有共享状态转换都在后端事件循环中串行执行。
// 仅传输层可替换，便于测试复用同一套状态机。
internal sealed class SharingSession
{
    private readonly IIpc ipc;
    private readonly Action<Dictionary<string, object>> publish;
    internal readonly Dictionary<string, object> Parameters;
    internal readonly Dictionary<string, object> State;
    internal bool Sharing, Connecting, Stopped;
    internal string Failure;
    internal DateTime ConnectDeadline;
    internal SharingSession(IIpc ipc, Dictionary<string, object> parameters, Action<Dictionary<string, object>> publish, string direction)
    {
        this.ipc = ipc; Parameters = parameters; this.publish = publish;
        State = Json.Fields("phase", "starting", "message", "正在连接手机…", "phone", "", "crossings", 0, "direction", direction);
    }
    internal void Set(params object[] values)
    {
        for (int i = 0; i < values.Length; i += 2) State[(string)values[i]] = values[i + 1];
        State["updated"] = DateTime.UtcNow.ToString("o"); State["backendPid"] = Process.GetCurrentProcess().Id;
        publish(State);
    }
    internal void Stop(string failure = null) { if (Failure == null && failure != null) Failure = failure; Stopped = true; }
    internal async Task Send(string route, object value, int timeout = 3500)
    {
        var response = await ipc.Request("exipc", route, value, timeout).ConfigureAwait(false);
        object status = Json.Get(response, "status");
        if (status != null && Json.Number(status, -1) != 200) throw new IOException("协同模块未能完成请求。");
    }
    internal async Task Direction(string direction)
    {
        if (Stopped) return;
        if (direction != "left" && direction != "right") throw new ArgumentException("请选择左侧或右侧。");
        int location = direction == "left" ? 1 : 0, edge = direction == "left" ? 1 : 2;
        Set("phase", "changing", "message", "正在设置穿越方向…");
        await Send("/change_keymouse_cross_connect_switch", Json.Fields("crossConnectSwitch", 3)).ConfigureAwait(false);
        if (Stopped) return;
        if (Sharing) await Send("/change_screen_location", Json.Fields("mode", 0, "location", location,
            "remote_device_ip", Json.Get(Parameters, "remote_device_ip"), "remote_device_id", Json.Get(Parameters, "remote_device_id"))).ConfigureAwait(false);
        if (Stopped) return;
        await Send("/change_keymouse_cross_connect_switch", Json.Fields("crossConnectSwitch", edge)).ConfigureAwait(false);
        Parameters["screen_location"] = location;
        Set("phase", Sharing ? "ready" : "armed", "direction", direction,
            "message", direction == "left" ? "从电脑左边缘进入手机，从手机右边缘返回。" : "从电脑右边缘进入手机，从手机左边缘返回。");
    }
    internal async Task Event(Dictionary<string, object> item, string direction)
    {
        if (Stopped) return;
        string name = Json.Text(Json.Get(item, "event"));
        if (name.Length == 0 && Json.Get(item, "msg") is string)
        {
            Dictionary<string, object> nested;
            try { nested = Json.Map(Json.Decode((string)item["msg"])); } catch { return; }
            await Event(nested, direction).ConfigureAwait(false); return;
        }
        if (name == "onExSuccess")
        {
            Sharing = true; Connecting = false;
            await Task.Delay(150).ConfigureAwait(false);
            if (!Stopped) await Direction(direction).ConfigureAwait(false);
        }
        else if (name == "cursor_cross") Set("crossings", Json.Number(State["crossings"]) + 1);
        else if (name == "cross_edge" && !Sharing && !Connecting)
        {
            Connecting = true; ConnectDeadline = DateTime.UtcNow.AddSeconds(18);
            Set("phase", "starting", "message", "正在与手机建立共享连接…");
            await Send("/start_screen_sharing", Parameters).ConfigureAwait(false);
        }
        else if (name == "onExFailed") Stop("手机未能建立键鼠共享连接。");
        else if (name == "onExStopped") Stop();
        else if (name == "pipe_error") Stop("协同事件连接已关闭。");
    }
}

internal static class Backend
{
    internal static string FindClientInstall()
    {
        var processes = Process.GetProcessesByName("O+Connect");
        try
        {
            if (processes.Length == 0) throw new IOException("未检测到正在运行的 OPPO 互联客户端，请先打开客户端并连接手机。");
            var paths = new List<string>();
            int session;
            using (var current = Process.GetCurrentProcess()) session = current.SessionId;
            foreach (var process in processes)
            {
                try
                {
                    if (process.SessionId == session) paths.Add(process.MainModule.FileName);
                }
                // Electron 子进程可能已退出，或不允许查询；继续检查其他进程。
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }
                catch (NotSupportedException) { }
            }
            return ResolveClientInstall(paths);
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    internal static string ResolveClientInstall(IEnumerable<string> executablePaths)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string executable in executablePaths)
        {
            if (String.IsNullOrWhiteSpace(executable) || !Path.IsPathRooted(executable)) continue;
            string directory = Path.GetDirectoryName(executable);
            if (File.Exists(Path.Combine(directory, "resources", "app.asar")) &&
                File.Exists(Path.Combine(directory, "app_process", "screenshare.exe"))) directories.Add(directory);
        }
        if (directories.Count == 0)
            throw new IOException("无法从当前用户的 OPPO 互联进程读取完整安装目录，请确认客户端正常运行、权限一致，且 resources/app.asar 与 app_process/screenshare.exe 存在。");
        if (directories.Count > 1)
            throw new IOException("检测到多个不同安装目录的 OPPO 互联客户端，请仅保留一个客户端运行后重试。");
        return directories.Single();
    }
    internal static string Root { get { return Environment.GetEnvironmentVariable("OPPO_MOUSE_SHARING_DATA") ?? Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName); } }
    internal static bool Alive(int pid)
    {
        try { using (var process = Process.GetProcessById(pid)) return !process.HasExited; } catch { return false; }
    }
    internal static string Preference()
    {
        try { return Json.Text(Json.Get(Json.Read(Path.Combine(Root, "settings.json")), "direction")) == "left" ? "left" : "right"; } catch { return "right"; }
    }
    private static Dictionary<string, object> Configuration()
    {
        var config = Json.Read(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "O+Connect", "config.json"));
        string id = Json.Text(Json.Get(config, "CurrentConnectedDevice"));
        var phone = Json.List(Json.Get(config, "devicesList")).Select(Json.Map).FirstOrDefault(p => Json.Text(Json.Get(p, "deviceId")).Split(':')[0] == id);
        object local = Json.Get(config, "localDeviceInfo");
        if (id.Length == 0 || phone == null || Json.Text(Json.Get(phone, "ip")).Length == 0 || Json.Text(Json.Get(local, "oafDeviceId")).Length == 0)
            throw new IOException("请先在 OPPO 互联中连接手机。");
        Uri uri; IPAddress address;
        if (!Uri.TryCreate("tcp://" + Json.Text(Json.Get(phone, "ip")), UriKind.Absolute, out uri) || !IPAddress.TryParse(uri.Host.Trim('[', ']'), out address))
            throw new IOException("手机连接地址无效，请重新连接手机。");
        return Json.Fields("config", config, "phone", phone, "id", id, "host", address.ToString());
    }
    internal static string InstalledVersion(string file)
    {
        using (var stream = File.OpenRead(file))
        using (var reader = new BinaryReader(stream))
        {
            byte[] prefix = reader.ReadBytes(16);
            if (prefix.Length != 16) throw new IOException("安装包格式发生变化。");
            uint headerSize = BitConverter.ToUInt32(prefix, 4), jsonSize = BitConverter.ToUInt32(prefix, 12);
            if (jsonSize > 20 * 1024 * 1024 || 16L + jsonSize > stream.Length) throw new IOException("安装包格式发生变化。");
            var header = Json.Map(Json.Decode(Encoding.UTF8.GetString(reader.ReadBytes((int)jsonSize))));
            var entry = Json.Map(Json.Get(Json.Get(header, "files"), "package.json"));
            long offset; int size = Json.Number(Json.Get(entry, "size"), -1);
            if (!Int64.TryParse(Json.Text(Json.Get(entry, "offset")), out offset) || offset < 0 || size < 0 || size > 1024 * 1024)
                throw new IOException("安装包格式发生变化。");
            long position = 8L + headerSize + offset;
            if (position < 16 || position + size > stream.Length) throw new IOException("安装包格式发生变化。");
            stream.Position = position;
            return Json.Text(Json.Get(Json.Decode(Encoding.UTF8.GetString(reader.ReadBytes(size))), "version"));
        }
    }
    internal static string ConnectionMethod(Dictionary<string, object> device)
    {
        object types = Json.Get(device, "connect_types") ?? Json.Get(device, "connect_type");
        int flags = types is object[] ? ((object[])types).Aggregate(0, (sum, value) => sum | Json.Number(value)) : Json.Number(types);
        var names = new List<string>();
        if ((flags & 128) != 0) names.Add("USB");
        if ((flags & 18) != 0) names.Add("无线");
        if ((flags & 64) != 0) names.Add("远程");
        if (names.Count == 0 && (flags & 5) != 0) names.Add("蓝牙");
        return names.Count == 0 ? "连接方式未知" : String.Join(" / ", names) + "连接";
    }
    internal static async Task<Dictionary<string, object>> QueryConnection(IIpc ipc)
    {
        try
        {
            var response = await ipc.Request("dspipc", "/get_remote_device", null, 1000).ConfigureAwait(false);
            var device = Json.Map(Json.Get(response, "data"));
            if (Json.Number(Json.Get(response, "status"), -1) != 0 || Json.Text(Json.Get(device, "device_id")).Length == 0) return Json.Fields("phase", "disconnected");
            string phone = Json.Text(Json.Get(device, "device_name"));
            return Json.Fields("phase", "connected", "phone", phone.Length == 0 ? "手机" : phone, "method", ConnectionMethod(device));
        }
        catch { return Json.Fields("phase", "unavailable"); }
    }
    internal static async Task<int> Watch(int parent)
    {
        if (parent <= 0) return 1;
        var ipc = new PipeHttp();
        while (Alive(parent))
        {
            var state = await QueryConnection(ipc).ConfigureAwait(false);
            state["watcherPid"] = Process.GetCurrentProcess().Id; state["updated"] = DateTime.UtcNow.ToString("o");
            Json.Write(Path.Combine(Root, "connection.json"), state);
            await Task.Delay(2000).ConfigureAwait(false);
        }
        return 0;
    }
    internal static async Task<int> Share(int parent)
    {
        Directory.CreateDirectory(Root);
        var ipc = new PipeHttp();
        var parameters = new Dictionary<string, object>();
        var session = new SharingSession(ipc, parameters, state => Json.Write(Path.Combine(Root, "state.json"), state), Preference());
        Process native = null; NamedPipeClientStream events = null;
        var queue = new BlockingCollection<Dictionary<string, object>>();
        string stop = Path.Combine(Root, "stop.request");
        try
        {
            session.Set("phase", "starting");
            string install = FindClientInstall();
            string clientVersion = InstalledVersion(Path.Combine(install, "resources", "app.asar"));
            session.Set("clientVersion", clientVersion, "clientVersionVerified", clientVersion == "16.2.20");
            var existing = Process.GetProcessesByName("screenshare");
            bool occupied = existing.Length != 0; foreach (var process in existing) process.Dispose();
            if (occupied) throw new IOException("已有屏幕协同进程在运行，请先结束该会话。");
            var config = Configuration(); var phone = Json.Map(config["phone"]); var local = Json.Map(Json.Get(config["config"], "localDeviceInfo"));
            session.Set("phone", Json.Get(phone, "deviceName") ?? "已连接手机");
            var addressResponse = await ipc.Request("dspipc", "/get_channel_local_ip", null).ConfigureAwait(false);
            string localIp = Json.Text(Json.Get(Json.Get(addressResponse, "data"), "ip")); IPAddress parsed;
            if (!IPAddress.TryParse(localIp, out parsed)) throw new IOException("无法获取电脑互联地址，请确认客户端正在运行。");
            foreach (var entry in Json.Fields("mode", 0, "screen_location", Preference() == "left" ? 1 : 0, "crossConnectSwitch", 3,
                "remote_device_type", Json.Get(phone, "deviceType"), "remote_device_name", Json.Get(phone, "deviceName"),
                "remote_device_ip", config["host"], "remote_device_id", config["id"], "connection_type", Json.Get(phone, "connectType"),
                "local_device_type", 1, "local_device_id", Json.Get(local, "oafDeviceId"), "local_device_name", Json.Get(local, "deviceName"), "local_ip", localIp)) parameters[entry.Key] = entry.Value;
            native = Process.Start(new ProcessStartInfo(Path.Combine(install, "app_process", "screenshare.exe"), "0") {
                WorkingDirectory = Path.Combine(install, "app_process"), UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            bool ready = false;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(250).ConfigureAwait(false);
                if (File.Exists(stop) || native.HasExited || (parent > 0 && !Alive(parent))) { session.Stop(); break; }
                try { await session.Send("/change_keymouse_cross_connect_switch", Json.Fields("crossConnectSwitch", 3), 1000).ConfigureAwait(false); ready = true; break; } catch { }
            }
            if (!ready) { if (!session.Stopped) throw new IOException("协同模块没有就绪。"); return 0; }
            events = new NamedPipeClientStream(".", "exevent", PipeDirection.InOut, PipeOptions.Asynchronous);
            await Task.Run(() => events.Connect(3500)).ConfigureAwait(false);
            byte[] subscribe = Encoding.UTF8.GetBytes("ex_callback\r\n");
            await events.WriteAsync(subscribe, 0, subscribe.Length).ConfigureAwait(false);
            ReadEvents(events, queue, () => session.Stopped);
            await Task.Delay(250).ConfigureAwait(false);
            await session.Direction(Preference()).ConfigureAwait(false);
            DateTime nextConfigCheck = DateTime.UtcNow.AddSeconds(2);
            while (!session.Stopped)
            {
                if (File.Exists(stop) || (parent > 0 && !Alive(parent))) { session.Stop(); break; }
                if (native.HasExited) { session.Stop("协同模块已退出。"); break; }
                if (session.Connecting && !session.Sharing && DateTime.UtcNow > session.ConnectDeadline) { session.Stop("等待手机连接超时，请解锁手机后重试。"); break; }
                Dictionary<string, object> item;
                if (queue.TryTake(out item, 250)) await session.Event(item, Preference()).ConfigureAwait(false);
                string direction = Preference();
                if (!session.Stopped && !session.Connecting && direction != Json.Text(session.State["direction"])) await session.Direction(direction).ConfigureAwait(false);
                if (DateTime.UtcNow >= nextConfigCheck)
                {
                    nextConfigCheck = DateTime.UtcNow.AddSeconds(2);
                    if (Json.Text(Configuration()["id"]) != Json.Text(config["id"])) session.Stop("手机已断开或连接设备已变化。");
                }
            }
        }
        catch (FileNotFoundException) { session.Stop("没有找到客户端或所需文件，请检查安装目录。"); }
        catch (Exception error) { session.Stop(error is IOException || error is TimeoutException ? error.Message : "助手遇到异常，正在停止共享。"); }
        finally
        {
            session.Stopped = true;
            session.Set("phase", "stopping", "message", "正在停止共享…");
            if (native != null && !native.HasExited)
            {
                try { session.Send("/change_keymouse_cross_connect_switch", Json.Fields("crossConnectSwitch", 3), 1500).GetAwaiter().GetResult(); } catch { }
                try { session.Send("/stop_screen_sharing", Json.Fields("mode", 0, "reason", 1), 2000).GetAwaiter().GetResult(); } catch { }
                Thread.Sleep(150);
            }
            if (events != null) events.Dispose();
            if (native != null) { if (!native.HasExited) { native.Kill(); native.WaitForExit(1000); } native.Dispose(); }
            if (File.Exists(stop)) File.Delete(stop);
            session.Set("phase", session.Failure == null ? "stopped" : "error", "message", session.Failure ?? "已停止。", "nativePid", null);
        }
        return 0;
    }
    private static async void ReadEvents(Stream events, BlockingCollection<Dictionary<string, object>> queue, Func<bool> stopped)
    {
        try
        {
            byte[] bytes = new byte[4096]; char[] chars = new char[4096]; Decoder decoder = Encoding.UTF8.GetDecoder(); string buffer = "";
            while (!stopped())
            {
                int count = await events.ReadAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                if (count == 0) break;
                buffer += new string(chars, 0, decoder.GetChars(bytes, 0, count, chars, 0));
                if (buffer.Length > 1024 * 1024) throw new IOException("事件缓冲区过大。");
                int end;
                while ((end = buffer.IndexOf('\n')) >= 0)
                {
                    string line = buffer.Substring(0, end).Trim(); buffer = buffer.Substring(end + 1);
                    try { if (line.Length > 0) queue.Add(Json.Map(Json.Decode(line))); } catch (ArgumentException) { }
                }
                if (buffer.Trim().Length > 0)
                {
                    try { var item = Json.Map(Json.Decode(buffer)); buffer = ""; queue.Add(item); } catch (ArgumentException) { } catch (InvalidOperationException) { }
                }
            }
        }
        catch { }
        finally { if (!stopped()) queue.Add(Json.Fields("event", "pipe_error")); }
    }
    internal static int Fixture(string mode)
    {
        if (Environment.GetEnvironmentVariable("OPPO_MOUSE_SHARING_FIXTURE") != "1") return 2;
        DateTime until = DateTime.UtcNow.AddSeconds(20);
        if (mode == "--fixture-idle") { while (DateTime.UtcNow < until) Thread.Sleep(100); return 0; }
        string stop = Path.Combine(Root, "stop.request");
        Json.Write(Path.Combine(Root, "state.json"), Json.Fields("phase", "ready", "backendPid", Process.GetCurrentProcess().Id, "updated", DateTime.UtcNow.ToString("o")));
        while (!File.Exists(stop) && DateTime.UtcNow < until) Thread.Sleep(50);
        if (File.Exists(stop)) File.Delete(stop);
        Json.Write(Path.Combine(Root, "state.json"), Json.Fields("phase", "stopped", "backendPid", Process.GetCurrentProcess().Id, "updated", DateTime.UtcNow.ToString("o")));
        return 0;
    }
}
