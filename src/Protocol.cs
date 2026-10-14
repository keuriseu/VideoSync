using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace VideoSync
{
    /// <summary>Monotonic, high-resolution process clock in milliseconds.</summary>
    static class Clock
    {
        static readonly Stopwatch sw = Stopwatch.StartNew();
        public static double NowMs { get { return sw.Elapsed.TotalMilliseconds; } }
    }

    static class Proto
    {
        public const int TcpPort = 47800;   // host <-> client control channel
        public const int UdpPort = 47801;   // host discovery beacons
        public const string Magic = "VIDEOSYNC1";

        public static string F(double d) { return d.ToString("R", CultureInfo.InvariantCulture); }
        public static double D(string s) { return double.Parse(s, CultureInfo.InvariantCulture); }
        public static string Join(params string[] parts) { return string.Join("\t", parts); }
        public static string Clean(string s) { return (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '); }
    }

    /// <summary>
    /// The shared playback timeline. When Playing, the video is at PosMs at host-clock
    /// time RefHostMs and advances in real time from there. RefHostMs may be in the
    /// future, which schedules a synchronized start on every screen.
    /// </summary>
    class PlayState
    {
        public long Seq;
        public bool Playing;
        public double PosMs;
        public double RefHostMs;
        public bool Loop;
        public long FileSize;
        public string FileName = "";
        public string FilePath = "";

        public double ExpectedPos(double hostNow)
        {
            if (!Playing) return PosMs;
            return PosMs + Math.Max(0, hostNow - RefHostMs);
        }

        public PlayState Clone() { return (PlayState)MemberwiseClone(); }

        public string Serialize()
        {
            return Proto.Join("STATE", Seq.ToString(CultureInfo.InvariantCulture), Playing ? "1" : "0",
                Proto.F(PosMs), Proto.F(RefHostMs), Loop ? "1" : "0",
                FileSize.ToString(CultureInfo.InvariantCulture), Proto.Clean(FileName), Proto.Clean(FilePath));
        }

        public static PlayState Parse(string[] p)
        {
            if (p.Length < 9) return null;
            var s = new PlayState();
            s.Seq = long.Parse(p[1], CultureInfo.InvariantCulture);
            s.Playing = p[2] == "1";
            s.PosMs = Proto.D(p[3]);
            s.RefHostMs = Proto.D(p[4]);
            s.Loop = p[5] == "1";
            s.FileSize = long.Parse(p[6], CultureInfo.InvariantCulture);
            s.FileName = p[7];
            s.FilePath = p[8];
            return s;
        }
    }

    /// <summary>Newline-delimited UTF-8 text messages over a TCP connection.</summary>
    class LineConnection
    {
        readonly TcpClient tcp;
        readonly StreamReader reader;
        readonly StreamWriter writer;
        readonly object sendLock = new object();
        int closed;

        public readonly string Remote;
        public event Action<LineConnection, string> LineReceived;
        public event Action<LineConnection> Closed;

        public LineConnection(TcpClient client)
        {
            tcp = client;
            tcp.NoDelay = true;
            tcp.SendTimeout = 3000;
            var stream = tcp.GetStream();
            var enc = new UTF8Encoding(false);
            reader = new StreamReader(stream, enc);
            writer = new StreamWriter(stream, enc);
            writer.AutoFlush = true;
            Remote = tcp.Client.RemoteEndPoint.ToString();
        }

        public void Start()
        {
            var t = new Thread(ReadLoop);
            t.IsBackground = true;
            t.Start();
        }

        void ReadLoop()
        {
            try
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var h = LineReceived;
                    if (h != null) h(this, line);
                }
            }
            catch (Exception ex) { Log.Write("Connection " + Remote + " read error: " + ex.Message); }
            Close();
        }

        public bool Send(string line)
        {
            try
            {
                lock (sendLock) writer.Write(line + "\n");
                return true;
            }
            catch
            {
                Close();
                return false;
            }
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref closed, 1) == 1) return;
            try { tcp.Close(); } catch { }
            var h = Closed;
            if (h != null) h(this);
        }
    }

    static class Log
    {
        static readonly object lk = new object();
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VideoSync");

        public static void Write(string msg)
        {
            try
            {
                lock (lk)
                {
                    Directory.CreateDirectory(Dir);
                    File.AppendAllText(Path.Combine(Dir, "videosync.log"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
                }
            }
            catch { }
        }
    }

    /// <summary>Tiny key=value settings file in %APPDATA%\VideoSync.</summary>
    class Settings
    {
        readonly Dictionary<string, string> values = new Dictionary<string, string>();
        readonly string path = Path.Combine(Log.Dir, "settings.ini");

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (File.Exists(s.path))
                    foreach (var line in File.ReadAllLines(s.path))
                    {
                        int i = line.IndexOf('=');
                        if (i > 0) s.values[line.Substring(0, i)] = line.Substring(i + 1);
                    }
            }
            catch { }
            return s;
        }

        public string Get(string key, string def)
        {
            string v;
            lock (values) return values.TryGetValue(key, out v) ? v : def;
        }

        public void Set(string key, string value)
        {
            lock (values)
            {
                values[key] = Proto.Clean(value);
                try
                {
                    Directory.CreateDirectory(Log.Dir);
                    var lines = new List<string>();
                    foreach (var kv in values) lines.Add(kv.Key + "=" + kv.Value);
                    File.WriteAllLines(path, lines.ToArray());
                }
                catch { }
            }
        }
    }
}
