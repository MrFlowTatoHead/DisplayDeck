// SwitchAgent.exe - tiny LAN HTTP agent so a phone can trigger display-mode
// switches. Compiled with /target:winexe (no console window); logs to agent.log.
//
// It deliberately uses a raw TcpListener instead of HttpListener so it can run
// as a normal user with no URL-ACL reservation. Reads agent.config (same folder):
//   port=5990
//   bind=<optional listen address, default all interfaces>
//   token=<shared secret; required on every request except /ping>
//   mode.<id>.slot / label / subtitle / icon / profile / audio
// Mode entries are re-read automatically whenever agent.config changes (the
// capture tool edits them); port/bind changes need a restart.
//
// Endpoints (token via X-Auth-Token header or ?token= query):
//   GET  /ping            -> "pong" (no token; connectivity test)
//   GET  /status          -> {ok, mode, busy, modes[], displays[]}
//   POST /switch/<id>     -> runs switch.ps1 -Mode <id> synchronously, then
//                            re-detects the mode; {ok, exitCode, mode, displays[], log[]}
//   GET  /log             -> {ok, lines[]} tail of switch.log
//   POST /steam           -> runs WindowMover.exe steam: moves Steam to the primary
//                            monitor and maximizes it; {ok, message, screen}
//                            (?dryrun=1 reports without moving anything)
//
// The active mode is whichever mode's saved profile matches the live layout
// (MonitorProfile.exe current), or "unknown". A switch request returns only
// after the whole display+audio sequence finishes, so the phone shows the
// real outcome. Only one switch runs at a time (409 while busy).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SwitchAgentApp
{
    class ModeDef
    {
        public string Id;
        public int Slot = 99;
        public string Label;
        public string Subtitle = "";
        public string Icon = "display";
        public string Profile;
        public string Audio = "";
    }

    class Program
    {
        static string BaseDir;
        static string AgentLog;
        static string SwitchLog;
        static string ConfigPath;
        static string Token = "";
        static int Port = 5990;
        static string Bind = "";
        static List<ModeDef> Modes = new List<ModeDef>();
        static DateTime ConfigStamp = DateTime.MinValue;
        static object ConfigLock = new object();
        static object LogLock = new object();
        static int Switching = 0;
        static Mutex SingleInstance;

        static void Main(string[] args)
        {
            BaseDir = AppDomain.CurrentDomain.BaseDirectory;
            AgentLog = Path.Combine(BaseDir, "agent.log");
            SwitchLog = Path.Combine(BaseDir, "switch.log");
            ConfigPath = Path.Combine(BaseDir, "agent.config");
            IPAddress addr;
            try
            {
                ConfigStamp = File.GetLastWriteTimeUtc(ConfigPath);
                LoadConfig(true);
                addr = Bind.Length > 0 ? IPAddress.Parse(Bind) : IPAddress.Any;
            }
            catch (Exception ex)
            {
                Log("FATAL: " + ex);
                return;
            }

            // The scheduled-task watchdog launches this every few minutes; a copy that
            // finds the agent already running exits quietly.
            bool createdNew;
            SingleInstance = new Mutex(true, "Local\\DisplaySwitchAgent-" + Port, out createdNew);
            if (!createdNew) return;

            Log("Agent started (pid " + Process.GetCurrentProcess().Id + ", launched by " + ParentProcessName()
                + "), listening on " + addr + ":" + Port + " with " + Modes.Count + " mode(s)");

            // A listener failure (network change, sleep/wake) restarts the listener rather
            // than ending the agent.
            while (true)
            {
                TcpListener listener = null;
                try
                {
                    listener = new TcpListener(addr, Port);
                    listener.Start();
                    while (true)
                    {
                        TcpClient client = listener.AcceptTcpClient();
                        Thread t = new Thread(delegate() { HandleClientSafe(client); });
                        t.IsBackground = true;
                        t.Start();
                    }
                }
                catch (Exception ex)
                {
                    Log("Listener error, restarting it in 5 s: " + ex.Message);
                    try { if (listener != null) listener.Stop(); }
                    catch { }
                    Thread.Sleep(5000);
                }
            }
        }

        static string ParentProcessName()
        {
            try
            {
                string q = "SELECT ParentProcessId FROM Win32_Process WHERE ProcessId=" + Process.GetCurrentProcess().Id;
                using (System.Management.ManagementObjectSearcher s = new System.Management.ManagementObjectSearcher(q))
                {
                    foreach (System.Management.ManagementObject o in s.Get())
                    {
                        int ppid = Convert.ToInt32(o["ParentProcessId"]);
                        try { return Process.GetProcessById(ppid).ProcessName; }
                        catch { return "pid " + ppid; }
                    }
                }
            }
            catch { }
            return "unknown";
        }

        static void LoadConfig(bool startup)
        {
            if (!File.Exists(ConfigPath)) throw new Exception("Missing config file: " + ConfigPath);
            string token = "";
            Dictionary<string, ModeDef> modes = new Dictionary<string, ModeDef>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in File.ReadAllLines(ConfigPath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                if (key.Equals("token", StringComparison.OrdinalIgnoreCase)) { token = val; continue; }
                if (key.Equals("port", StringComparison.OrdinalIgnoreCase))
                {
                    if (startup) int.TryParse(val, out Port);
                    continue;
                }
                if (key.Equals("bind", StringComparison.OrdinalIgnoreCase))
                {
                    if (startup) Bind = val;
                    continue;
                }
                string[] parts = key.Split('.');
                if (parts.Length != 3 || !parts[0].Equals("mode", StringComparison.OrdinalIgnoreCase)) continue;
                string id = parts[1].ToLowerInvariant();
                if (!Regex.IsMatch(id, "^[a-z0-9_-]{1,32}$")) continue;
                ModeDef m;
                if (!modes.TryGetValue(id, out m))
                {
                    m = new ModeDef();
                    m.Id = id;
                    m.Label = id;
                    m.Profile = id;
                    modes[id] = m;
                }
                string field = parts[2].ToLowerInvariant();
                if (field == "slot") { int.TryParse(val, out m.Slot); }
                else if (field == "label" && val.Length > 0) m.Label = val;
                else if (field == "subtitle") m.Subtitle = val;
                else if (field == "icon" && val.Length > 0) m.Icon = val;
                else if (field == "profile" && Regex.IsMatch(val, "^[A-Za-z0-9_-]{1,40}$")) m.Profile = val;
                else if (field == "audio") m.Audio = val;
            }
            if (token.Length < 8) throw new Exception("agent.config must contain a token of at least 8 characters.");
            List<ModeDef> list = new List<ModeDef>(modes.Values);
            list.Sort(delegate(ModeDef a, ModeDef b)
            {
                int c = a.Slot.CompareTo(b.Slot);
                return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
            });
            Token = token;
            Modes = list;
        }

        static void RefreshConfigIfChanged()
        {
            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(ConfigPath); }
            catch { return; }
            if (stamp == ConfigStamp) return;
            lock (ConfigLock)
            {
                if (stamp == ConfigStamp) return;
                try
                {
                    LoadConfig(false);
                    ConfigStamp = stamp;
                    Log("Reloaded agent.config: " + Modes.Count + " mode(s)");
                }
                catch (Exception ex)
                {
                    Log("agent.config reload failed, keeping previous settings: " + ex.Message);
                }
            }
        }

        static bool IsReady(ModeDef m)
        {
            return File.Exists(Path.Combine(Path.Combine(BaseDir, "profiles"), m.Profile + ".mon"));
        }

        static void Log(string msg)
        {
            lock (LogLock)
            {
                try
                {
                    File.AppendAllText(AgentLog,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
                }
                catch { }
            }
        }

        static void HandleClientSafe(TcpClient client)
        {
            try { HandleClient(client); }
            catch (Exception ex) { Log("Client error: " + ex.Message); }
        }

        static void HandleClient(TcpClient client)
        {
            string remote = "?";
            try { remote = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString(); }
            catch { }
            using (client)
            using (NetworkStream ns = client.GetStream())
            {
                client.ReceiveTimeout = 10000;
                client.SendTimeout = 30000;

                byte[] buf = new byte[16384];
                int len = 0;
                int headerEnd = -1;
                while (headerEnd < 0 && len < buf.Length)
                {
                    int n = ns.Read(buf, len, buf.Length - len);
                    if (n <= 0) return;
                    len += n;
                    headerEnd = FindHeaderEnd(buf, len);
                }
                if (headerEnd < 0)
                {
                    Respond(ns, 400, "Bad Request", "text/plain", "request too large");
                    return;
                }

                string head = Encoding.ASCII.GetString(buf, 0, headerEnd);
                string[] lines = head.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                string[] reqLine = lines[0].Split(' ');
                if (reqLine.Length < 2)
                {
                    Respond(ns, 400, "Bad Request", "text/plain", "malformed request");
                    return;
                }
                string method = reqLine[0].ToUpperInvariant();
                string rawUrl = reqLine[1];
                Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < lines.Length; i++)
                {
                    int c = lines[i].IndexOf(':');
                    if (c > 0) headers[lines[i].Substring(0, c).Trim()] = lines[i].Substring(c + 1).Trim();
                }
                // Any request body is irrelevant to this API and is ignored.

                string path = rawUrl;
                string query = "";
                int q = rawUrl.IndexOf('?');
                if (q >= 0) { path = rawUrl.Substring(0, q); query = rawUrl.Substring(q + 1); }
                if (path.Length > 1) path = path.TrimEnd('/');

                RefreshConfigIfChanged();
                Route(ns, remote, method, path, query, headers);
            }
        }

        static int FindHeaderEnd(byte[] buf, int len)
        {
            for (int i = 3; i < len; i++)
                if (buf[i] == '\n' && buf[i - 1] == '\r' && buf[i - 2] == '\n' && buf[i - 3] == '\r')
                    return i + 1;
            return -1;
        }

        static bool Authorized(Dictionary<string, string> headers, string query)
        {
            string t;
            if (headers.TryGetValue("X-Auth-Token", out t) && t == Token) return true;
            foreach (string kv in query.Split('&'))
            {
                int eq = kv.IndexOf('=');
                if (eq > 0 && kv.Substring(0, eq) == "token"
                    && Uri.UnescapeDataString(kv.Substring(eq + 1)) == Token) return true;
            }
            return false;
        }

        static void Route(NetworkStream ns, string remote, string method, string path,
            string query, Dictionary<string, string> headers)
        {
            if (path == "/ping")
            {
                Respond(ns, 200, "OK", "text/plain", "pong");
                return;
            }
            if (!Authorized(headers, query))
            {
                Log(remote + "  " + method + " " + path + "  -> 401");
                RespondError(ns, 401, "Unauthorized", "bad or missing token");
                return;
            }
            if (method == "GET" && path == "/status") { DoStatus(ns); return; }
            if (method == "GET" && path == "/log") { DoLog(ns); return; }
            if (method == "POST" && path.StartsWith("/switch/"))
            {
                DoSwitch(ns, remote, path.Substring("/switch/".Length));
                return;
            }
            if (method == "POST" && path == "/steam")
            {
                DoSteam(ns, remote, ("&" + query + "&").Contains("&dryrun=1&"));
                return;
            }
            RespondError(ns, 404, "Not Found", "unknown endpoint");
        }

        static string DetectMode(List<ModeDef> modes, out List<string> displays)
        {
            displays = new List<string>();
            string output;
            int code;
            RunProc(Path.Combine(BaseDir, "MonitorProfile.exe"), "current", 15000, out output, out code);
            string matched = null;
            foreach (string raw in output.Split('\n'))
            {
                string line = raw.Trim().TrimEnd('\r');
                if (line.StartsWith("MATCH ")) matched = line.Substring(6).Trim();
                else if (line.StartsWith("- ")) displays.Add(line.Substring(2));
            }
            if (matched != null)
                foreach (ModeDef m in modes)
                    if (m.Profile.Equals(matched, StringComparison.OrdinalIgnoreCase)) return m.Id;
            return "unknown";
        }

        static void DoStatus(NetworkStream ns)
        {
            List<ModeDef> modes = Modes;
            List<string> displays;
            string mode = DetectMode(modes, out displays);
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"mode\":\"").Append(Esc(mode)).Append('"');
            sb.Append(",\"busy\":").Append(Thread.VolatileRead(ref Switching) != 0 ? "true" : "false");
            sb.Append(",\"modes\":[");
            for (int i = 0; i < modes.Count; i++)
            {
                ModeDef m = modes[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":\"").Append(Esc(m.Id)).Append('"');
                sb.Append(",\"label\":\"").Append(Esc(m.Label)).Append('"');
                sb.Append(",\"subtitle\":\"").Append(Esc(m.Subtitle)).Append('"');
                sb.Append(",\"icon\":\"").Append(Esc(m.Icon)).Append('"');
                sb.Append(",\"slot\":").Append(m.Slot.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"ready\":").Append(IsReady(m) ? "true" : "false");
                sb.Append('}');
            }
            sb.Append("],\"displays\":");
            AppendArr(sb, displays);
            sb.Append('}');
            Respond(ns, 200, "OK", "application/json", sb.ToString());
        }

        static void DoLog(NetworkStream ns)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"lines\":");
            AppendArr(sb, Tail(SwitchLog, 60));
            sb.Append('}');
            Respond(ns, 200, "OK", "application/json", sb.ToString());
        }

        static void DoSwitch(NetworkStream ns, string remote, string modeId)
        {
            List<ModeDef> modes = Modes;
            ModeDef target = null;
            foreach (ModeDef m in modes)
                if (m.Id.Equals(modeId, StringComparison.OrdinalIgnoreCase)) target = m;
            if (target == null)
            {
                RespondError(ns, 404, "Not Found", "unknown mode");
                return;
            }
            if (!IsReady(target))
            {
                RespondError(ns, 409, "Conflict", "'" + target.Label
                    + "' has no saved layout yet. Run Capture-Display-Mode on the PC to set it up.");
                return;
            }
            if (Interlocked.CompareExchange(ref Switching, 1, 0) != 0)
            {
                RespondError(ns, 409, "Conflict", "a switch is already in progress");
                return;
            }
            try
            {
                Log(remote + "  switch -> " + target.Id);
                string psExe = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell\\v1.0\\powershell.exe");
                string psArgs = "-NoProfile -ExecutionPolicy Bypass -File \""
                    + Path.Combine(BaseDir, "switch.ps1") + "\" -Mode " + target.Id;
                string output;
                int code;
                RunProc(psExe, psArgs, 120000, out output, out code);

                List<string> displays;
                string mode = DetectMode(modes, out displays);
                StringBuilder sb = new StringBuilder();
                sb.Append("{\"ok\":").Append(code == 0 ? "true" : "false");
                sb.Append(",\"exitCode\":").Append(code.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"mode\":\"").Append(Esc(mode)).Append('"');
                sb.Append(",\"displays\":");
                AppendArr(sb, displays);
                sb.Append(",\"log\":");
                AppendArr(sb, Tail(SwitchLog, 15));
                sb.Append('}');
                Log(remote + "  switch -> " + target.Id + " finished: exit " + code + ", mode now '" + mode + "'");
                Respond(ns, 200, "OK", "application/json", sb.ToString());
            }
            finally
            {
                Interlocked.Exchange(ref Switching, 0);
            }
        }

        static void DoSteam(NetworkStream ns, string remote, bool dryRun)
        {
            if (Interlocked.CompareExchange(ref Switching, 1, 0) != 0)
            {
                RespondError(ns, 409, "Conflict", "a display switch is in progress - try again in a moment");
                return;
            }
            try
            {
                Log(remote + "  bring Steam" + (dryRun ? " (dry run)" : ""));
                string output;
                int code;
                RunProc(Path.Combine(BaseDir, "WindowMover.exe"), dryRun ? "steam --dry-run" : "steam",
                    70000, out output, out code);

                string result = "WindowMover gave no result (exit " + code + ").";
                List<string> logLines = new List<string>();
                logLines.Add("---- Bring Steam requested" + (dryRun ? " (dry run)" : "") + " ----");
                foreach (string raw in output.Split('\n'))
                {
                    string line = raw.TrimEnd('\r');
                    if (line.Length == 0) continue;
                    if (line.StartsWith("RESULT: ")) result = line.Substring(8);
                    logLines.Add(line);
                }
                if (!dryRun) AppendSwitchLog(logLines);

                List<string> displays;
                DetectMode(Modes, out displays);
                string screen = "";
                foreach (string d in displays)
                    if (d.Contains("[PRIMARY]")) screen = d.Split(new string[] { "  " }, StringSplitOptions.None)[0];

                StringBuilder sb = new StringBuilder();
                sb.Append("{\"ok\":").Append(code == 0 ? "true" : "false");
                sb.Append(",\"exitCode\":").Append(code.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"message\":\"").Append(Esc(result)).Append('"');
                sb.Append(",\"screen\":\"").Append(Esc(screen)).Append('"');
                sb.Append('}');
                Log(remote + "  bring Steam finished: exit " + code + " - " + result);
                Respond(ns, 200, "OK", "application/json", sb.ToString());
            }
            finally
            {
                Interlocked.Exchange(ref Switching, 0);
            }
        }

        static void AppendSwitchLog(List<string> lines)
        {
            string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  ";
            StringBuilder sb = new StringBuilder();
            foreach (string l in lines) sb.Append(stamp).Append(l).Append(Environment.NewLine);
            lock (LogLock)
            {
                try { File.AppendAllText(SwitchLog, sb.ToString()); }
                catch { }
            }
        }

        static void RunProc(string file, string args, int timeoutMs, out string output, out int exitCode)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = file;
            psi.Arguments = args;
            psi.WorkingDirectory = BaseDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                Task<string> tOut = p.StandardOutput.ReadToEndAsync();
                Task<string> tErr = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    p.WaitForExit();
                    output = tOut.Result + tErr.Result + "\n(process timed out)";
                    exitCode = -1;
                    return;
                }
                p.WaitForExit();
                output = tOut.Result + tErr.Result;
                exitCode = p.ExitCode;
            }
        }

        static List<string> Tail(string file, int n)
        {
            try
            {
                if (!File.Exists(file)) return new List<string>();
                string[] all = File.ReadAllLines(file);
                int start = all.Length > n ? all.Length - n : 0;
                List<string> result = new List<string>();
                for (int i = start; i < all.Length; i++) result.Add(all[i]);
                return result;
            }
            catch { return new List<string>(); }
        }

        static void AppendArr(StringBuilder sb, List<string> items)
        {
            sb.Append('[');
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(Esc(items[i])).Append('"');
            }
            sb.Append(']');
        }

        static string Esc(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        static void RespondError(NetworkStream ns, int code, string text, string message)
        {
            Respond(ns, code, text, "application/json",
                "{\"ok\":false,\"error\":\"" + Esc(message) + "\"}");
        }

        static void Respond(NetworkStream ns, int code, string text, string ctype, string body)
        {
            byte[] payload = Encoding.UTF8.GetBytes(body);
            string head = "HTTP/1.1 " + code + " " + text + "\r\n"
                + "Content-Type: " + ctype + "; charset=utf-8\r\n"
                + "Content-Length: " + payload.Length + "\r\n"
                + "Connection: close\r\n\r\n";
            byte[] h = Encoding.ASCII.GetBytes(head);
            ns.Write(h, 0, h.Length);
            ns.Write(payload, 0, payload.Length);
            ns.Flush();
        }
    }
}
