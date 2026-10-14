using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace VideoSync
{
    class ClientInfo
    {
        public LineConnection Conn;
        public string Name = "?";
        public double DriftMs;
        public string Status = "connecting";
        public string Wallpaper = "";
    }

    /// <summary>Host side: accepts clients, answers clock pings, pushes playback state, sends discovery beacons.</summary>
    class SyncServer
    {
        TcpListener listener;
        volatile bool running;
        readonly ManualResetEvent stopEvent = new ManualResetEvent(false);
        readonly List<ClientInfo> clients = new List<ClientInfo>();
        readonly object lk = new object();

        /// <summary>Returns the current serialized state (or null), sent to every newly connected client.</summary>
        public Func<string> CurrentState;

        volatile string lastWallpaper;

        /// <summary>Sends a WALLPAPER message to every client now, and to any client that connects later.</summary>
        public void SendWallpaper(string line)
        {
            lastWallpaper = line;
            foreach (var c in Snapshot())
            {
                var ci = c;
                ci.Wallpaper = "sending wallpaper...";
                // Large message: send off the UI thread, one client at a time per thread.
                ThreadPool.QueueUserWorkItem(_ => ci.Conn.Send(line));
            }
        }

        public void Start()
        {
            listener = new TcpListener(IPAddress.Any, Proto.TcpPort);
            listener.Start();
            running = true;
            StartThread(AcceptLoop);
            StartThread(BeaconLoop);
            Log.Write("Host listening on TCP " + Proto.TcpPort);
        }

        public void Stop()
        {
            running = false;
            stopEvent.Set();
            try { listener.Stop(); } catch { }
            foreach (var c in Snapshot()) c.Conn.Close();
        }

        public ClientInfo[] Snapshot()
        {
            lock (lk) return clients.ToArray();
        }

        public void Broadcast(string line)
        {
            foreach (var c in Snapshot()) c.Conn.Send(line);
        }

        static void StartThread(ThreadStart fn)
        {
            var t = new Thread(fn);
            t.IsBackground = true;
            t.Start();
        }

        void AcceptLoop()
        {
            while (running)
            {
                try
                {
                    var conn = new LineConnection(listener.AcceptTcpClient());
                    var ci = new ClientInfo { Conn = conn, Name = conn.Remote };
                    conn.LineReceived += (c, line) => OnLine(ci, line);
                    conn.Closed += c =>
                    {
                        lock (lk) clients.Remove(ci);
                        Log.Write("Client disconnected: " + ci.Name);
                    };
                    lock (lk) clients.Add(ci);
                    conn.Start();
                    conn.Send(Proto.Join("HELLO", Proto.Magic));
                    var st = CurrentState == null ? null : CurrentState();
                    if (st != null) conn.Send(st);
                    var wp = lastWallpaper;
                    if (wp != null) ThreadPool.QueueUserWorkItem(_ => conn.Send(wp));
                    Log.Write("Client connected: " + conn.Remote);
                }
                catch (Exception ex)
                {
                    if (!running) break;
                    Log.Write("Accept error: " + ex.Message);
                }
            }
        }

        void OnLine(ClientInfo ci, string line)
        {
            var p = line.Split('\t');
            try
            {
                switch (p[0])
                {
                    case "PING":
                        // Reply immediately with our clock so the client can estimate offset (NTP style).
                        ci.Conn.Send(Proto.Join("PONG", p[1], Proto.F(Clock.NowMs)));
                        break;
                    case "HI":
                        ci.Name = p[1];
                        break;
                    case "REPORT":
                        ci.DriftMs = Proto.D(p[1]);
                        ci.Status = p.Length > 2 ? p[2] : "";
                        break;
                    case "WPRESULT":
                        ci.Wallpaper = p.Length > 1 ? p[1] : "";
                        Log.Write("Wallpaper on " + ci.Name + ": " + ci.Wallpaper);
                        break;
                }
            }
            catch (Exception ex) { Log.Write("Bad message from " + ci.Name + ": " + ex.Message); }
        }

        void BeaconLoop()
        {
            var msg = Encoding.UTF8.GetBytes(Proto.Join(Proto.Magic, Proto.Clean(Environment.MachineName),
                Proto.TcpPort.ToString()));
            while (running)
            {
                // Directed broadcast on every IPv4 interface (255.255.255.255 alone only leaves via one NIC).
                foreach (var t in BroadcastTargets())
                    SendBeacon(msg, new IPEndPoint(t.Item1, 0), new IPEndPoint(t.Item2, Proto.UdpPort));
                SendBeacon(msg, new IPEndPoint(IPAddress.Any, 0), new IPEndPoint(IPAddress.Broadcast, Proto.UdpPort));
                SendBeacon(msg, new IPEndPoint(IPAddress.Any, 0), new IPEndPoint(IPAddress.Loopback, Proto.UdpPort));
                stopEvent.WaitOne(1000);
            }
        }

        static void SendBeacon(byte[] msg, IPEndPoint from, IPEndPoint to)
        {
            try
            {
                using (var u = new UdpClient(from))
                {
                    u.EnableBroadcast = true;
                    u.Send(msg, msg.Length, to);
                }
            }
            catch { }
        }

        /// <summary>This PC's IPv4 addresses, real LAN adapters (those with a default gateway) first.</summary>
        public static List<string> LocalAddresses()
        {
            var primary = new List<string>();
            var other = new List<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    var props = ni.GetIPProperties();
                    bool hasGateway = false;
                    foreach (var g in props.GatewayAddresses)
                        if (g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)) hasGateway = true;
                    foreach (var ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        var s = ua.Address.ToString();
                        if (s.StartsWith("169.254.")) continue; // no DHCP lease: unusable
                        (hasGateway ? primary : other).Add(s);
                    }
                }
            }
            catch { }
            primary.AddRange(other);
            return primary;
        }

        static List<Tuple<IPAddress, IPAddress>> BroadcastTargets()
        {
            var list = new List<Tuple<IPAddress, IPAddress>>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                        var a = ua.Address.GetAddressBytes();
                        var m = ua.IPv4Mask.GetAddressBytes();
                        var b = new byte[4];
                        for (int i = 0; i < 4; i++) b[i] = (byte)(a[i] | ~m[i]);
                        list.Add(Tuple.Create(ua.Address, new IPAddress(b)));
                    }
                }
            }
            catch { }
            return list;
        }
    }

    /// <summary>Client side: keeps a connection to the host, estimates the host clock, receives state.</summary>
    class SyncClient
    {
        readonly string host;
        volatile bool running;
        volatile LineConnection conn;
        readonly List<double[]> samples = new List<double[]>(); // {rtt, offset}
        double offsetMs, rttMs;
        bool hasClock;

        public volatile bool Connected;
        public readonly string HostName;
        public event Action<PlayState> StateReceived;
        public event Action<string> StatusChanged;
        public event Action<string[]> WallpaperReceived;

        public SyncClient(string host)
        {
            this.host = host;
            HostName = host;
        }

        public bool HasClock { get { lock (samples) return hasClock; } }
        public double OffsetMs { get { lock (samples) return offsetMs; } }
        public double RttMs { get { lock (samples) return rttMs; } }

        /// <summary>Current time on the host's clock.</summary>
        public double HostNow { get { return Clock.NowMs + OffsetMs; } }

        public void Start()
        {
            running = true;
            var t = new Thread(Run);
            t.IsBackground = true;
            t.Start();
        }

        public void Stop()
        {
            running = false;
            var c = conn;
            if (c != null) c.Close();
        }

        public void Report(double driftMs, string status)
        {
            var c = conn;
            if (c != null) c.Send(Proto.Join("REPORT", Proto.F(Math.Round(driftMs, 1)), Proto.Clean(status)));
        }

        public void SendWallpaperResult(string text)
        {
            var c = conn;
            if (c != null) c.Send(Proto.Join("WPRESULT", Proto.Clean(text)));
        }

        void Status(string s)
        {
            var h = StatusChanged;
            if (h != null) h(s);
        }

        void Run()
        {
            while (running)
            {
                Status("Connecting to " + host + "...");
                try
                {
                    var tcp = new TcpClient();
                    var ar = tcp.BeginConnect(host, Proto.TcpPort, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(3000))
                    {
                        tcp.Close();
                        throw new TimeoutException("timed out");
                    }
                    tcp.EndConnect(ar);

                    var c = new LineConnection(tcp);
                    var closed = new ManualResetEvent(false);
                    c.LineReceived += OnLine;
                    c.Closed += x => closed.Set();
                    lock (samples) { samples.Clear(); hasClock = false; }
                    conn = c;
                    c.Start();
                    c.Send(Proto.Join("HI", Proto.Clean(Environment.MachineName)));
                    Connected = true;
                    Status("Connected to " + host);
                    Log.Write("Connected to host " + host);

                    // Burst of pings for a fast initial clock estimate, then one per second to track drift.
                    int n = 0;
                    while (running && !closed.WaitOne(n < 8 ? 50 : 1000))
                    {
                        c.Send(Proto.Join("PING", Proto.F(Clock.NowMs)));
                        n++;
                    }
                    if (running) Log.Write("Lost connection to host " + host);
                }
                catch (Exception ex)
                {
                    Status("Cannot reach " + host + " (" + ex.Message + "), retrying...");
                }
                Connected = false;
                conn = null;
                for (int i = 0; i < 20 && running; i++) Thread.Sleep(100);
            }
        }

        void OnLine(LineConnection c, string line)
        {
            var p = line.Split('\t');
            try
            {
                switch (p[0])
                {
                    case "PONG":
                    {
                        double t0 = Proto.D(p[1]), hostT = Proto.D(p[2]), t3 = Clock.NowMs;
                        double rtt = t3 - t0;
                        double off = hostT - (t0 + t3) / 2;
                        lock (samples)
                        {
                            samples.Add(new[] { rtt, off });
                            if (samples.Count > 15) samples.RemoveAt(0);
                            // Use the sample with the smallest round trip: least queuing noise.
                            var best = samples[0];
                            foreach (var s in samples) if (s[0] < best[0]) best = s;
                            rttMs = best[0];
                            offsetMs = best[1];
                            hasClock = true;
                        }
                        break;
                    }
                    case "WALLPAPER":
                    {
                        var h = WallpaperReceived;
                        if (p.Length >= 5 && h != null) h(p);
                        break;
                    }
                    case "STATE":
                    {
                        var s = PlayState.Parse(p);
                        var h = StateReceived;
                        if (s != null && h != null) h(s);
                        break;
                    }
                }
            }
            catch (Exception ex) { Log.Write("Bad message from host: " + ex.Message); }
        }
    }

    /// <summary>Listens for host beacons on the LAN.</summary>
    class HostDiscovery
    {
        UdpClient udp;
        volatile bool running;
        public event Action<string, string> HostSeen; // machine name, IP

        public void Start()
        {
            udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, Proto.UdpPort));
            running = true;
            var t = new Thread(Loop);
            t.IsBackground = true;
            t.Start();
        }

        public void Stop()
        {
            running = false;
            try { udp.Close(); } catch { }
        }

        void Loop()
        {
            var ep = new IPEndPoint(IPAddress.Any, 0);
            while (running)
            {
                try
                {
                    var p = Encoding.UTF8.GetString(udp.Receive(ref ep)).Split('\t');
                    if (p.Length >= 2 && p[0] == Proto.Magic)
                    {
                        var h = HostSeen;
                        if (h != null) h(p[1], ep.Address.ToString());
                    }
                }
                catch
                {
                    if (!running) break;
                    Thread.Sleep(200);
                }
            }
        }
    }
}
