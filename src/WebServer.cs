using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

namespace VideoSync
{
    class WebClientInfo
    {
        public string Id, Name = "Browser", Status = "", Address = "";
        public double DriftMs, LastSeen;
    }

    /// <summary>
    /// Minimal HTTP server (host only) so phones and tablets can join from a web browser:
    ///   GET /                 the player page (src/web/client.html, embedded in the exe)
    ///   GET /events           Server-Sent Events stream of playback state (JSON)
    ///   GET /time?...         host clock for NTP-style sync; query also carries the page's status report
    ///   GET /video/{v}/{name} the current video, with HTTP Range support for seeking
    /// Built on TcpListener rather than HttpListener, which needs admin rights to listen on the LAN.
    /// </summary>
    class WebServer
    {
        public static readonly int Port = Proto.TcpPort + 2;

        TcpListener listener;
        volatile bool running;
        byte[] page;
        readonly List<Stream> eventStreams = new List<Stream>();
        readonly Dictionary<string, WebClientInfo> clients = new Dictionary<string, WebClientInfo>();
        readonly object lk = new object();
        readonly ManualResetEvent stopEvent = new ManualResetEvent(false);

        /// <summary>Returns the current playback state (immutable snapshot) or null.</summary>
        public Func<PlayState> CurrentState;

        public void Start()
        {
            page = LoadPage();
            listener = new TcpListener(IPAddress.Any, Port);
            listener.Start();
            running = true;
            StartThread(AcceptLoop);
            StartThread(HeartbeatLoop);
            Log.Write("Web server listening on port " + Port);
        }

        public void Stop()
        {
            running = false;
            stopEvent.Set();
            try { listener.Stop(); } catch { }
            lock (lk)
            {
                foreach (var s in eventStreams) try { s.Close(); } catch { }
                eventStreams.Clear();
            }
        }

        static void StartThread(ThreadStart fn)
        {
            var t = new Thread(fn);
            t.IsBackground = true;
            t.Start();
        }

        static byte[] LoadPage()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("client.html"))
            {
                if (s == null) return Encoding.UTF8.GetBytes("<h1>VideoSync: web client missing from this build</h1>");
                var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        /// <summary>Browsers that reported in the last few seconds.</summary>
        public WebClientInfo[] Snapshot()
        {
            var list = new List<WebClientInfo>();
            double now = Clock.NowMs;
            lock (lk)
                foreach (var c in clients.Values)
                    if (now - c.LastSeen < 8000) list.Add(c);
            return list.ToArray();
        }

        /// <summary>Push a new state to every connected browser.</summary>
        public void Broadcast(PlayState s)
        {
            SendEvent(Encoding.UTF8.GetBytes("data: " + StateJson(s) + "\n\n"));
        }

        void SendEvent(byte[] msg)
        {
            Stream[] streams;
            lock (lk) streams = eventStreams.ToArray();
            foreach (var st in streams)
            {
                var stream = st;
                // Off the caller's thread: a slow phone must never block the host UI.
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { lock (stream) { stream.Write(msg, 0, msg.Length); stream.Flush(); } }
                    catch
                    {
                        lock (lk) eventStreams.Remove(stream);
                        try { stream.Close(); } catch { }
                    }
                });
            }
        }

        void HeartbeatLoop()
        {
            // Comment lines keep idle event streams open through Wi-Fi power saving and NAT timeouts.
            var ping = Encoding.ASCII.GetBytes(": ping\n\n");
            while (running && !stopEvent.WaitOne(15000)) SendEvent(ping);
        }

        void AcceptLoop()
        {
            while (running)
            {
                try
                {
                    var c = listener.AcceptTcpClient();
                    var t = new Thread(() => Handle(c));
                    t.IsBackground = true;
                    t.Start();
                }
                catch
                {
                    if (!running) break;
                }
            }
        }

        void Handle(TcpClient c)
        {
            bool handedOff = false;
            try
            {
                c.NoDelay = true;
                c.SendTimeout = 15000; // a phone that stops reading (screen off, out of range) is dropped
                var ns = c.GetStream();
                var reader = new StreamReader(ns, Encoding.ASCII);
                string remote = ((IPEndPoint)c.Client.RemoteEndPoint).Address.ToString();
                while (running)
                {
                    string requestLine = reader.ReadLine();
                    if (requestLine == null) break;
                    if (requestLine == "") continue;
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    string line;
                    while (!string.IsNullOrEmpty(line = reader.ReadLine()))
                    {
                        int i = line.IndexOf(':');
                        if (i > 0) headers[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                    }
                    var parts = requestLine.Split(' ');
                    if (parts.Length < 2) break;
                    string method = parts[0], target = parts[1], path = target, query = "";
                    int q = target.IndexOf('?');
                    if (q >= 0) { path = target.Substring(0, q); query = target.Substring(q + 1); }
                    bool head = method == "HEAD";

                    if (method != "GET" && !head) Send(ns, 405, "text/plain", Ascii("Method not allowed"), false);
                    else if (path == "/time")
                    {
                        // Host time is read as late as possible, just before replying.
                        Report(query, remote);
                        Send(ns, 200, "application/json", Ascii("{\"host\":" + Proto.F(Clock.NowMs) + "}"), head);
                    }
                    else if (path == "/" || path == "/index.html") Send(ns, 200, "text/html; charset=utf-8", page, head);
                    else if (path == "/events") { StartEvents(ns); handedOff = true; return; }
                    else if (path.StartsWith("/video/")) ServeVideo(ns, headers, head);
                    else Send(ns, 404, "text/plain", Ascii("Not found"), head);

                    string conn;
                    if (headers.TryGetValue("Connection", out conn) && conn.Equals("close", StringComparison.OrdinalIgnoreCase)) break;
                }
            }
            catch { } // client went away mid-request (normal for video seeks)
            finally
            {
                if (!handedOff) try { c.Close(); } catch { }
            }
        }

        static byte[] Ascii(string s) { return Encoding.ASCII.GetBytes(s); }

        static string Reason(int code)
        {
            switch (code)
            {
                case 200: return "OK";
                case 206: return "Partial Content";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 416: return "Range Not Satisfiable";
                default: return "Status";
            }
        }

        static void WriteHeader(Stream ns, int code, string type, long length, string extra)
        {
            var h = new StringBuilder();
            h.Append("HTTP/1.1 ").Append(code).Append(' ').Append(Reason(code)).Append("\r\n");
            if (type != null) h.Append("Content-Type: ").Append(type).Append("\r\n");
            h.Append("Content-Length: ").Append(length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            h.Append("Cache-Control: no-store\r\nConnection: keep-alive\r\n");
            if (extra != null) h.Append(extra);
            h.Append("\r\n");
            var b = Ascii(h.ToString());
            ns.Write(b, 0, b.Length);
        }

        static void Send(Stream ns, int code, string type, byte[] body, bool head)
        {
            WriteHeader(ns, code, type, body == null ? 0 : body.Length, null);
            if (body != null && !head) ns.Write(body, 0, body.Length);
            ns.Flush();
        }

        void StartEvents(Stream ns)
        {
            var b = Ascii("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-store\r\n" +
                          "Connection: keep-alive\r\nX-Accel-Buffering: no\r\n\r\nretry: 2000\n\n");
            var first = Encoding.UTF8.GetBytes("data: " + StateJson(CurrentState == null ? null : CurrentState()) + "\n\n");
            lock (ns)
            {
                ns.Write(b, 0, b.Length);
                ns.Write(first, 0, first.Length);
                ns.Flush();
            }
            lock (lk) eventStreams.Add(ns);
        }

        void Report(string query, string remote)
        {
            var q = ParseQuery(query);
            string id;
            if (!q.TryGetValue("id", out id) || id == "") return;
            lock (lk)
            {
                WebClientInfo ci;
                if (!clients.TryGetValue(id, out ci))
                {
                    ci = new WebClientInfo { Id = id };
                    clients[id] = ci;
                    Log.Write("Browser joined: " + remote + " (" + (q.ContainsKey("name") ? q["name"] : id) + ")");
                }
                string v;
                if (q.TryGetValue("name", out v) && v != "") ci.Name = Proto.Clean(v);
                if (q.TryGetValue("status", out v)) ci.Status = Proto.Clean(v);
                double d;
                if (q.TryGetValue("drift", out v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) ci.DriftMs = d;
                ci.Address = remote;
                ci.LastSeen = Clock.NowMs;
            }
        }

        static Dictionary<string, string> ParseQuery(string query)
        {
            var d = new Dictionary<string, string>();
            foreach (var part in query.Split('&'))
            {
                int i = part.IndexOf('=');
                if (i <= 0) continue;
                try { d[part.Substring(0, i)] = Uri.UnescapeDataString(part.Substring(i + 1).Replace('+', ' ')); }
                catch { }
            }
            return d;
        }

        void ServeVideo(Stream ns, Dictionary<string, string> headers, bool head)
        {
            // Only ever the host's current video, whatever the URL says: never an arbitrary path.
            var st = CurrentState == null ? null : CurrentState();
            if (st == null || string.IsNullOrEmpty(st.FilePath) || !File.Exists(st.FilePath))
            {
                Send(ns, 404, "text/plain", Ascii("No video"), head);
                return;
            }
            using (var fs = new FileStream(st.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536))
            {
                long len = fs.Length, start = 0, end = len - 1;
                bool partial = false;
                string range;
                if (headers.TryGetValue("Range", out range) && range.StartsWith("bytes="))
                {
                    var r = range.Substring(6).Split(',')[0].Split('-');
                    if (r[0] == "") start = Math.Max(0, len - long.Parse(r[1], CultureInfo.InvariantCulture));
                    else
                    {
                        start = long.Parse(r[0], CultureInfo.InvariantCulture);
                        if (r.Length > 1 && r[1] != "") end = Math.Min(long.Parse(r[1], CultureInfo.InvariantCulture), len - 1);
                    }
                    if (start >= len)
                    {
                        WriteHeader(ns, 416, null, 0, "Content-Range: bytes */" + len + "\r\n");
                        ns.Flush();
                        return;
                    }
                    partial = true;
                }
                long count = end - start + 1;
                WriteHeader(ns, partial ? 206 : 200, MimeType(st.FilePath), count,
                    "Accept-Ranges: bytes\r\n" + (partial ? "Content-Range: bytes " + start + "-" + end + "/" + len + "\r\n" : ""));
                if (head) { ns.Flush(); return; }
                fs.Seek(start, SeekOrigin.Begin);
                var buf = new byte[65536];
                while (count > 0)
                {
                    int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, count));
                    if (n <= 0) break;
                    ns.Write(buf, 0, n);
                    count -= n;
                }
                ns.Flush();
            }
        }

        static string MimeType(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".webm": return "video/webm";
                case ".mov": return "video/quicktime";
                case ".mkv": return "video/x-matroska";
                case ".ogv": return "video/ogg";
                default: return "video/mp4";
            }
        }

        static string StateJson(PlayState s)
        {
            if (s == null) return "{\"playing\":false,\"pos\":0,\"ref\":0,\"loop\":false,\"name\":\"\",\"video\":\"\"}";
            string video = string.IsNullOrEmpty(s.FileName) ? "" :
                "/video/" + Wallpaper.Hash(Encoding.UTF8.GetBytes(s.FilePath + "|" + s.FileSize)) + "/" + Uri.EscapeDataString(s.FileName);
            return "{\"seq\":" + s.Seq.ToString(CultureInfo.InvariantCulture) +
                   ",\"playing\":" + (s.Playing ? "true" : "false") +
                   ",\"pos\":" + Proto.F(s.PosMs) +
                   ",\"ref\":" + Proto.F(s.RefHostMs) +
                   ",\"loop\":" + (s.Loop ? "true" : "false") +
                   ",\"name\":" + JsonString(s.FileName) +
                   ",\"video\":" + JsonString(video) + "}";
        }

        static string JsonString(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char ch in s ?? "")
            {
                if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
                else if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                else sb.Append(ch);
            }
            return sb.Append('"').ToString();
        }
    }
}
