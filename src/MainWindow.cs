using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace VideoSync
{
    enum Role { None, Host, Client }

    class MainWindow : Window
    {
        // Timing (milliseconds)
        const double PlayLeadMs = 500;    // starts are scheduled this far ahead so every screen starts together
        const double LoopLeadMs = 300;
        const double AheadTolMs = 20;     // ahead by more than this: hold the frame briefly
        const double TrimTargetMs = 250;  // catch-up seeks aim to land this far ahead, then hold
        const double BehindTolMs = 40;    // behind by more than this: seek ahead, then hold
        const double SettleMs = 400;      // ignore drift readings right after a start or seek
        const int DriftWindow = 5;        // median filter length (readings are 100 ms apart)
        const double SeekStepMs = 5000;

        readonly StartupOptions opts;
        readonly Settings settings;

        Role role = Role.None;
        SyncServer server;
        SyncClient client;
        HostDiscovery discovery;
        readonly List<string> discovered = new List<string>();
        string connStatus = "";

        PlayState state;
        long seq;

        MediaElement media;
        string loadedPath, openedPath;
        int recoveries;
        bool mediaReady, isPlaying;
        double durationMs, settleUntil, lastDrift;
        readonly List<double> driftSamples = new List<double>();
        bool parked, parkSeeked;          // paused, waiting to start at host time parkAt
        double parkAt;
        double holdLatencyMs, seekStartLatencyMs, catchUpAheadMs; // learned per PC, see Learn()
        enum LearnKind { None, HoldStart, SeekStart, CatchUp }
        LearnKind learn;
        string syncStatus = "idle";
        string fileWarning = "";
        readonly DispatcherTimer syncTimer, startTimer, uiTimer;
        int uiTicks;

        RadioButton hostRadio, clientRadio;
        StackPanel hostBar, clientBar;
        ComboBox hostBox;
        TextBlock folderText, overlay, statusText, timeText, hostAddrText, infoText;
        Border infoBadge;
        CheckBox allowWallpaperBox;
        string notice = "";
        double noticeUntil;
        bool infoPinned;
        double hostStartedAt, clientStartedAt, clientLastConnectedAt;
        string hostAddress = "";
        const string HostAddressFile = "host-address.txt";
        Border overlayBorder;
        Button playBtn, stopBtn;
        Slider seekSlider, volSlider;
        CheckBox loopBox, muteBox;
        ListBox clientList;
        FrameworkElement topBar, bottomBar, sidePanel;
        Grid stage;
        bool seeking, fullscreen;
        WindowState prevWindowState;

        public MainWindow(StartupOptions opts, Settings settings)
        {
            this.opts = opts;
            this.settings = settings;
            Title = "VideoSync";
            Width = 1100;
            Height = 700;
            Background = Brushes.Black;
            BuildUi();

            syncTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(100) };
            syncTimer.Tick += (s, e) => { HostEndCheck(); Enforce(false); };
            startTimer = new DispatcherTimer(DispatcherPriority.Send);
            startTimer.Tick += (s, e) => { startTimer.Stop(); Enforce(false); };
            uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            uiTimer.Tick += (s, e) => UpdateUi();

            Loaded += OnLoaded;
            Closing += (s, e) => StopNetwork();
            PreviewKeyDown += OnKey;
        }

        // ---------------------------------------------------------------- UI construction

        void BuildUi()
        {
            var barBrush = new SolidColorBrush(Color.FromRgb(243, 243, 243));
            var root = new DockPanel();

            // Top bar
            var top = new WrapPanel { Background = barBrush };
            hostRadio = new RadioButton { Content = "Host (controls playback)", GroupName = "role", Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            clientRadio = new RadioButton { Content = "Client (follows host)", GroupName = "role", Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
            hostRadio.Checked += (s, e) => SetRole(Role.Host);
            clientRadio.Checked += (s, e) => SetRole(Role.Client);
            top.Children.Add(Group(new TextBlock { Text = "Role:", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }, hostRadio, clientRadio));

            loopBox = new CheckBox { Content = "Loop", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            loopBox.Click += (s, e) => HostSetLoop(loopBox.IsChecked == true);
            hostAddrText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, Margin = new Thickness(16, 0, 4, 0) };
            hostBar = Group(Btn("Open video...", (s, e) => HostBrowse()), loopBox,
                Btn("Sync desktop background", (s, e) => HostSyncWallpaper()),
                hostAddrText, Btn("Copy address", (s, e) => CopyHostAddress()));
            top.Children.Add(hostBar);

            hostBox = new ComboBox { IsEditable = true, Width = 230, Margin = new Thickness(4, 0, 4, 0) };
            hostBox.SelectionChanged += (s, e) => { if (hostBox.SelectedItem != null) Connect((string)hostBox.SelectedItem); };
            folderText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 4, 0) };
            clientBar = Group(
                Label("Host:"), hostBox, Btn("Connect", (s, e) => Connect(hostBox.Text)),
                Label("   Media folder:"), folderText, Btn("Browse...", (s, e) => BrowseFolder()));
            allowWallpaperBox = new CheckBox { Content = "Allow wallpaper sync", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0) };
            allowWallpaperBox.Click += (s, e) => settings.Set("allowWallpaper", allowWallpaperBox.IsChecked == true ? "1" : "0");
            clientBar.Children.Add(allowWallpaperBox);
            clientBar.Children.Add(Btn("Restore my wallpaper", (s, e) => RestoreWallpaper()));
            top.Children.Add(clientBar);

            volSlider = new Slider { Minimum = 0, Maximum = 1, Width = 90, VerticalAlignment = VerticalAlignment.Center, Focusable = false };
            volSlider.ValueChanged += (s, e) => { media.Volume = volSlider.Value; settings.Set("volume", volSlider.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)); };
            muteBox = new CheckBox { Content = "Mute", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            muteBox.Click += (s, e) => media.IsMuted = muteBox.IsChecked == true;
            top.Children.Add(Group(Label("Volume"), volSlider, muteBox, Btn("Fullscreen (F11)", (s, e) => ToggleFullscreen())));
            topBar = top;
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);

            // Bottom bar: transport + status
            var bottom = new DockPanel { Background = barBrush };
            statusText = new TextBlock { Margin = new Thickness(8, 0, 8, 6), Foreground = Brushes.DimGray, TextTrimming = TextTrimming.CharacterEllipsis };
            DockPanel.SetDock(statusText, Dock.Bottom);
            bottom.Children.Add(statusText);
            playBtn = Btn("Play", (s, e) => HostPlayPause());
            playBtn.MinWidth = 70;
            stopBtn = Btn("Stop", (s, e) => HostStop());
            var transport = Group(playBtn, stopBtn);
            DockPanel.SetDock(transport, Dock.Left);
            bottom.Children.Add(transport);
            timeText = new TextBlock { Text = "0:00 / 0:00", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 12, 0), FontFamily = new FontFamily("Consolas") };
            DockPanel.SetDock(timeText, Dock.Right);
            bottom.Children.Add(timeText);
            seekSlider = new Slider { IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center, Focusable = false, Margin = new Thickness(6, 0, 6, 0) };
            seekSlider.PreviewMouseLeftButtonDown += (s, e) => { if (role == Role.Host) seeking = true; };
            seekSlider.PreviewMouseLeftButtonUp += (s, e) =>
            {
                if (!seeking) return;
                seeking = false;
                HostSeek(seekSlider.Value);
            };
            bottom.Children.Add(seekSlider);
            bottomBar = bottom;
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(bottom);

            // Right panel (host): connected screens
            var side = new DockPanel { Width = 280, Background = barBrush };
            var hdr = new TextBlock { Text = "Connected screens", FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 8, 8, 4) };
            DockPanel.SetDock(hdr, Dock.Top);
            side.Children.Add(hdr);
            clientList = new ListBox { Margin = new Thickness(8, 0, 8, 8), FontFamily = new FontFamily("Consolas"), Focusable = false };
            side.Children.Add(clientList);
            sidePanel = side;
            DockPanel.SetDock(side, Dock.Right);
            root.Children.Add(side);

            // Stage
            stage = new Grid { Background = Brushes.Black };
            media = new MediaElement
            {
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Manual,
                ScrubbingEnabled = true,
                Stretch = Stretch.Uniform,
            };
            media.MediaOpened += OnMediaOpened;
            media.MediaEnded += (s, e) => { media.Pause(); isPlaying = false; };
            media.MediaFailed += OnMediaFailed;
            stage.Children.Add(media);
            overlay = new TextBlock { Foreground = Brushes.White, FontSize = 20, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
            overlayBorder = new Border
            {
                Child = overlay,
                Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)),
                Padding = new Thickness(24, 16, 24, 16),
                CornerRadius = new CornerRadius(8),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 800,
            };
            stage.Children.Add(overlayBorder);
            // Host address badge: visible in fullscreen too, so the address is easy to read off any host screen.
            infoText = new TextBlock { Foreground = Brushes.White, FontSize = 22 };
            infoBadge = new Border
            {
                Child = infoText,
                Background = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)),
                Padding = new Thickness(16, 10, 16, 10),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(20),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };
            stage.Children.Add(infoBadge);
            stage.MouseLeftButtonDown += (s, e) => { if (e.ClickCount == 2) ToggleFullscreen(); };
            root.Children.Add(stage);

            Content = root;
        }

        static StackPanel Group(params UIElement[] items)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6, 8, 6) };
            foreach (var i in items) p.Children.Add(i);
            return p;
        }

        static Button Btn(string text, RoutedEventHandler click)
        {
            var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(2, 0, 2, 0) };
            b.Click += click;
            return b;
        }

        static TextBlock Label(string text)
        {
            return new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        }

        void ShowOverlay(string text)
        {
            overlay.Text = text;
            overlayBorder.Visibility = Visibility.Visible;
        }

        void HideOverlay() { overlayBorder.Visibility = Visibility.Collapsed; }

        string MediaFolder
        {
            get { return settings.Get("folder", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)); }
        }

        // ---------------------------------------------------------------- startup / roles

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (opts.Folder != null) settings.Set("folder", Path.GetFullPath(opts.Folder));
            folderText.Text = MediaFolder;
            folderText.ToolTip = MediaFolder;
            volSlider.Value = double.Parse(settings.Get("volume", "0.8"), System.Globalization.CultureInfo.InvariantCulture);
            if (opts.Mute) { muteBox.IsChecked = true; media.IsMuted = true; }
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            holdLatencyMs = double.Parse(settings.Get("holdLatency", "5"), ci);
            seekStartLatencyMs = double.Parse(settings.Get("seekStartLatency", "200"), ci);
            catchUpAheadMs = double.Parse(settings.Get("catchUpAhead", "1000"), ci);
            hostBox.Text = opts.HostAddress ?? ReadHostAddressFile() ?? settings.Get("host", "");
            if (opts.NoWallpaper) settings.Set("allowWallpaper", "0");
            allowWallpaperBox.IsChecked = settings.Get("allowWallpaper", "1") == "1";

            syncTimer.Start();
            uiTimer.Start();

            var r = opts.Role;
            if (r == Role.None)
            {
                var saved = settings.Get("role", "");
                r = saved == "Host" ? Role.Host : saved == "Client" ? Role.Client : Role.None;
            }
            if (r == Role.Host) hostRadio.IsChecked = true;
            else if (r == Role.Client) clientRadio.IsChecked = true;
            else
            {
                SetRoleUi();
                ShowOverlay("Choose a role above.\n\nHost: the PC that controls playback.\nClient: every PC that should follow it.\n\nEach PC needs its own copy of the video file.");
            }

            if (role == Role.Host)
            {
                loopBox.IsChecked = opts.Loop;
                if (opts.File != null) HostOpen(opts.File);
                if (opts.Play) HostPlayPause();
            }
            if (opts.Fullscreen) ToggleFullscreen();
        }

        void SetRole(Role r)
        {
            if (r == role) return;
            StopNetwork();
            role = r;
            state = null;
            UnloadMedia();
            settings.Set("role", r.ToString());
            Log.Write("Role: " + r);

            if (r == Role.Host)
            {
                server = new SyncServer();
                server.CurrentState = () => { var s = state; return s == null ? null : s.Serialize(); };
                try
                {
                    server.Start();
                    hostStartedAt = Clock.NowMs;
                    var addrs = SyncServer.LocalAddresses();
                    hostAddress = addrs.Count > 0 ? addrs[0] : Environment.MachineName;
                    hostAddrText.Text = "This PC's address: " + hostAddress;
                    hostAddrText.ToolTip = addrs.Count > 1 ? "All addresses: " + string.Join(", ", addrs.ToArray()) : null;
                    WriteHostAddressFile();
                    ShowOverlay("Open a video to start.\n\nClients on the network will find this PC automatically.\nThis PC's address: " + hostAddress);
                }
                catch (Exception ex)
                {
                    server = null;
                    ShowOverlay("Could not start hosting on port " + Proto.TcpPort + ":\n" + ex.Message +
                                "\n\nIs another VideoSync host already running on this PC?");
                }
            }
            else if (r == Role.Client)
            {
                discovery = new HostDiscovery();
                discovery.HostSeen += (name, ip) => Dispatcher.BeginInvoke(new Action(() => OnHostSeen(name, ip)));
                try { discovery.Start(); }
                catch (Exception ex) { Log.Write("Discovery unavailable: " + ex.Message); }
                if (hostBox.Text.Trim() != "") Connect(hostBox.Text);
                else ShowOverlay("Searching for a host on the network...\n\nIf nothing is found, type the address shown on the host's screen\ninto the Host box above (press Esc to see it) and press Connect.");
            }
            SetRoleUi();
        }

        void SetRoleUi()
        {
            bool host = role == Role.Host;
            hostBar.Visibility = host ? Visibility.Visible : Visibility.Collapsed;
            clientBar.Visibility = role == Role.Client ? Visibility.Visible : Visibility.Collapsed;
            sidePanel.Visibility = host && !fullscreen ? Visibility.Visible : Visibility.Collapsed;
            playBtn.IsEnabled = host;
            stopBtn.IsEnabled = host;
            seekSlider.IsEnabled = host;
            Title = "VideoSync - " + (host ? "Host" : role == Role.Client ? "Client" : "choose a role");
        }

        void StopNetwork()
        {
            if (server != null) { server.Stop(); server = null; }
            if (client != null) { client.Stop(); client = null; }
            if (discovery != null) { discovery.Stop(); discovery = null; }
        }

        void OnHostSeen(string name, string ip)
        {
            var label = name + " (" + ip + ")";
            if (!discovered.Contains(label))
            {
                discovered.Add(label);
                hostBox.Items.Add(label);
            }
            if (role != Role.Client) return;
            // First host found with nothing configured: connect to it automatically.
            bool idle = client == null && hostBox.Text.Trim() == "";
            // Configured address unreachable for a while (stale file/setting, host got a new IP): use the one we can see.
            bool stuck = client != null && !client.Connected && client.HostName != ip &&
                         Clock.NowMs - Math.Max(clientStartedAt, clientLastConnectedAt) > 6000;
            if (idle || stuck)
            {
                if (stuck) Log.Write("Host " + client.HostName + " unreachable; switching to discovered host " + label);
                hostBox.Text = label;
                Connect(label);
            }
        }

        /// <summary>host-address.txt next to the exe: written by the host, read by clients copied from the same folder.</summary>
        static string HostAddressPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, HostAddressFile); }
        }

        void WriteHostAddressFile()
        {
            try
            {
                File.WriteAllText(HostAddressPath, hostAddress + Environment.NewLine +
                    "# Written by the VideoSync host (" + Environment.MachineName + "). Clients started from a" + Environment.NewLine +
                    "# folder containing this file connect to the address on the first line." + Environment.NewLine);
            }
            catch (Exception ex) { Log.Write("Could not write " + HostAddressPath + ": " + ex.Message); }
        }

        static string ReadHostAddressFile()
        {
            try
            {
                if (!File.Exists(HostAddressPath)) return null;
                foreach (var line in File.ReadAllLines(HostAddressPath))
                {
                    var t = line.Trim();
                    if (t != "" && !t.StartsWith("#")) return t;
                }
            }
            catch { }
            return null;
        }

        void Notify(string text)
        {
            notice = text;
            noticeUntil = Clock.NowMs + 8000;
        }

        void HostSyncWallpaper()
        {
            if (role != Role.Host || server == null) return;
            Notify("Reading desktop background...");
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string msg, result;
                try { msg = Wallpaper.BuildMessage(); }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        Notify("");
                        MessageBox.Show(this, ex.Message, "Sync desktop background", MessageBoxButton.OK, MessageBoxImage.Information);
                    }));
                    return;
                }
                var srv = server;
                if (srv == null) return;
                int n = srv.Snapshot().Length;
                srv.SendWallpaper(msg);
                result = n == 0
                    ? "Desktop background ready: screens that connect will receive it."
                    : "Desktop background sent to " + n + " screen(s), and to any that connect later.";
                Log.Write(result + " (" + msg.Length / 1024 + " KB)");
                Dispatcher.BeginInvoke(new Action(() => Notify(result)));
            });
        }

        void OnWallpaperReceived(SyncClient c, string[] p)
        {
            if (allowWallpaperBox.IsChecked != true)
            {
                c.SendWallpaperResult("wallpaper sync is turned off on this PC");
                return;
            }
            Notify("Updating desktop background...");
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string result;
                try { result = Wallpaper.Apply(p, settings); }
                catch (Exception ex)
                {
                    result = "wallpaper failed: " + ex.Message;
                    Log.Write(result);
                }
                c.SendWallpaperResult(result);
                Dispatcher.BeginInvoke(new Action(() => Notify("Desktop background: " + result)));
            });
        }

        void RestoreWallpaper()
        {
            try { MessageBox.Show(this, Wallpaper.Restore(settings), "VideoSync", MessageBoxButton.OK, MessageBoxImage.Information); }
            catch (Exception ex) { MessageBox.Show(this, "Could not restore: " + ex.Message, "VideoSync", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        void CopyHostAddress()
        {
            try
            {
                Clipboard.SetText(hostAddress);
                hostAddrText.Text = "Copied: " + hostAddress;
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                t.Tick += (s, e) => { t.Stop(); hostAddrText.Text = "This PC's address: " + hostAddress; };
                t.Start();
            }
            catch (Exception ex) { MessageBox.Show(this, "Could not copy: " + ex.Message + "\n\nThe address is " + hostAddress, "VideoSync"); }
        }

        void Connect(string text)
        {
            if (role != Role.Client) return;
            text = (text ?? "").Trim();
            var addr = text;
            int a = text.LastIndexOf('('), b = text.LastIndexOf(')');
            if (a >= 0 && b > a) addr = text.Substring(a + 1, b - a - 1).Trim();
            if (addr == "") return;
            if (client != null && client.HostName == addr) return;
            if (client != null) client.Stop();
            settings.Set("host", text);

            var c = new SyncClient(addr);
            client = c;
            clientStartedAt = Clock.NowMs;
            c.StateReceived += s => Dispatcher.BeginInvoke(new Action(() => { if (client == c) ApplyState(s); }));
            c.WallpaperReceived += p => Dispatcher.BeginInvoke(new Action(() => { if (client == c) OnWallpaperReceived(c, p); }));
            c.StatusChanged += msg => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (client != c) return;
                connStatus = msg;
                if (state == null) ShowOverlay(msg);
            }));
            c.Start();
        }

        void BrowseFolder()
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "Folder that contains this PC's copies of the videos";
                dlg.SelectedPath = MediaFolder;
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                settings.Set("folder", dlg.SelectedPath);
                folderText.Text = dlg.SelectedPath;
                folderText.ToolTip = dlg.SelectedPath;
                if (state != null) ApplyState(state);
            }
        }

        // ---------------------------------------------------------------- sync engine

        double HostNow()
        {
            if (role == Role.Client && client != null) return client.HostNow;
            return Clock.NowMs;
        }

        string ResolvePath(PlayState s)
        {
            if (string.IsNullOrEmpty(s.FileName)) return null;
            if (role == Role.Host) return File.Exists(s.FilePath) ? s.FilePath : null;
            var candidates = new[]
            {
                Path.Combine(MediaFolder, s.FileName),
                s.FilePath,
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, s.FileName),
            };
            foreach (var c in candidates)
            {
                try { if (File.Exists(c)) return Path.GetFullPath(c); }
                catch { }
            }
            return null;
        }

        /// <summary>Adopt a new timeline from the host (or, on the host, from local controls).</summary>
        void ApplyState(PlayState s)
        {
            state = s;
            loopBox.IsChecked = s.Loop;
            var path = ResolvePath(s);
            if (path == null)
            {
                UnloadMedia();
                if (string.IsNullOrEmpty(s.FileName))
                {
                    ShowOverlay(role == Role.Host ? "Open a video to start." : "Connected. Waiting for the host to open a video...");
                    syncStatus = "no video";
                }
                else
                {
                    ShowOverlay("Video not found on this PC:\n" + s.FileName + "\n\nCopy it into the media folder:\n" + MediaFolder);
                    syncStatus = "missing file";
                }
                return;
            }

            if (!string.Equals(path, loadedPath, StringComparison.OrdinalIgnoreCase))
            {
                fileWarning = "";
                try
                {
                    if (s.FileSize > 0 && new FileInfo(path).Length != s.FileSize)
                        fileWarning = "Warning: this copy of " + s.FileName + " differs in size from the host's.";
                }
                catch { }
                LoadMedia(path); // OnMediaOpened will align it
                return;
            }
            Enforce(true);
        }

        void LoadMedia(string path)
        {
            mediaReady = false;
            isPlaying = false;
            loadedPath = path;
            syncStatus = "loading";
            ShowOverlay("Loading " + Path.GetFileName(path) + "...");
            media.Source = null; // force a fresh open even when reloading the same file
            media.Source = new Uri(path);
            media.Pause(); // opens the media and shows the first frame
            Log.Write("Loading " + path);
        }

        void UnloadMedia()
        {
            startTimer.Stop();
            media.Stop();
            media.Source = null;
            loadedPath = null;
            mediaReady = false;
            isPlaying = false;
            durationMs = 0;
            lastDrift = 0;
            parked = false;
            driftSamples.Clear();
        }

        void OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            Log.Write("Media failed: " + e.ErrorException.Message);
            var path = loadedPath;
            bool wasWorking = path != null && path == openedPath;
            mediaReady = false;
            isPlaying = false;
            parked = false;
            startTimer.Stop();
            if (wasWorking && recoveries < 5)
            {
                // Media Foundation occasionally fails transiently (e.g. around end-of-stream): reload and resync.
                recoveries++;
                syncStatus = "recovering";
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                t.Tick += (s, a) =>
                {
                    t.Stop();
                    if (loadedPath != path) return;
                    loadedPath = null;
                    LoadMedia(path);
                };
                t.Start();
                return;
            }
            ShowOverlay("Cannot play this file:\n" + e.ErrorException.Message +
                        "\n\nWindows Media Foundation must support the format (MP4/H.264 works everywhere).");
            syncStatus = "playback error";
        }

        void OnMediaOpened(object sender, RoutedEventArgs e)
        {
            openedPath = loadedPath;
            durationMs = media.NaturalDuration.HasTimeSpan ? media.NaturalDuration.TimeSpan.TotalMilliseconds : 0;
            seekSlider.Maximum = Math.Max(1, durationMs);
            HideOverlay();
            // MediaElement drops seeks until the media has played once, so prime it with a short muted play.
            var path = loadedPath;
            media.IsMuted = true;
            media.Play();
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            t.Tick += (s, a) =>
            {
                t.Stop();
                media.IsMuted = muteBox.IsChecked == true;
                if (loadedPath != path) return;
                media.Pause();
                mediaReady = true;
                if (recoveries > 0) Log.Write("Recovered playback of " + path);
                // Forgive old failures after a minute of healthy playback.
                var forgive = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
                forgive.Tick += (s2, a2) => { forgive.Stop(); if (loadedPath == path && mediaReady) recoveries = 0; };
                forgive.Start();
                Enforce(true);
            };
            t.Start();
        }

        /// <summary>
        /// Make the local player match the shared timeline. Called on every state change (hard)
        /// and every 100 ms (soft).
        ///
        /// The strategy follows measured MediaElement behavior:
        ///   * steady playback is very stable, so corrections are rare;
        ///   * Pause then Play (no seek between) resumes within a few ms, so being AHEAD is fixed by
        ///     holding the current frame until the timeline catches up;
        ///   * a seek while playing stalls for an unpredictable 70-600 ms, so being BEHIND is fixed by
        ///     seeking well past the timeline and then holding (as above) to trim the excess;
        ///   * Play after a paused seek starts ~200 ms late, consistently, so scheduled starts fire
        ///     early by a separately learned amount;
        ///   * any SpeedRatio change stalls 150-250 ms, so playback speed is never touched.
        /// </summary>
        void Enforce(bool hard)
        {
            startTimer.Stop();
            if (state == null || !mediaReady) return;
            if (role == Role.Client && (client == null || !client.HasClock)) return;
            double now = HostNow();

            if (!state.Playing)
            {
                parked = false;
                if (isPlaying) { media.Pause(); isPlaying = false; }
                // Only seek if really off: a paused seek makes the next Play start ~200 ms late.
                if (Math.Abs(media.Position.TotalMilliseconds - state.PosMs) > 40) Seek(state.PosMs);
                lastDrift = 0;
                syncStatus = "paused";
                return;
            }

            // Scheduled start: park on the start frame and fire at the agreed host time.
            if (now < state.RefHostMs - LatencyFor(parkSeeked || Math.Abs(media.Position.TotalMilliseconds - state.PosMs) > 40))
            {
                Park(state.PosMs, state.RefHostMs, false);
                syncStatus = "starting";
                return;
            }

            // Holding a frame (trim) or parked for a start: resume when the timeline reaches it.
            if (parked)
            {
                if (now < parkAt - LatencyFor(parkSeeked)) { ArmStart(now); return; }
                StartPlayback();
                return;
            }

            double expected = state.ExpectedPos(now);
            if (durationMs > 0 && expected >= durationMs - 100)
            {
                syncStatus = "ended"; // let it run out; the host decides what comes next
                return;
            }

            if (!isPlaying)
            {
                // Joining mid-playback (or after a load): start rolling, then catch up while playing.
                bool far = Math.Abs(media.Position.TotalMilliseconds - expected) > 40;
                StartPlayback();
                if (far) CatchUp(expected);
                return;
            }

            if (hard) { CatchUp(expected); return; }
            if (Clock.NowMs < settleUntil) return;

            double pos = media.Position.TotalMilliseconds;
            if (pos == 0 && expected > 1000) return; // MediaElement briefly reports 0 right after Play()
            driftSamples.Add(pos - expected);
            if (driftSamples.Count > DriftWindow) driftSamples.RemoveAt(0);
            if (driftSamples.Count < DriftWindow) return;
            var sorted = new List<double>(driftSamples);
            sorted.Sort();
            double drift = sorted[sorted.Count / 2];
            lastDrift = drift;
            if (Debug) Log.Write(string.Format("dbg {0} drift {1:0} (raw {2:0}) learn {3} holdLat {4:0} seekLat {5:0} catchUp {6:0}",
                role, drift, driftSamples[driftSamples.Count - 1], learn, holdLatencyMs, seekStartLatencyMs, catchUpAheadMs));

            Learn(drift);

            if (drift > AheadTolMs)
            {
                // Ahead: hold this frame until the timeline reaches it.
                media.Pause();
                isPlaying = false;
                double held = media.Position.TotalMilliseconds;
                parked = true;
                parkSeeked = false;
                parkAt = HostNow() + (held - expected);
                ArmStart(HostNow());
                syncStatus = "in sync";
            }
            else if (drift < -BehindTolMs) CatchUp(expected);
            else syncStatus = "in sync";
        }

        /// <summary>Learn this PC's latencies from the first settled reading after a start or catch-up.</summary>
        void Learn(double drift)
        {
            if (learn == LearnKind.None) return;
            var kind = learn;
            learn = LearnKind.None;
            if (kind == LearnKind.CatchUp)
            {
                // Aim to land ~TrimTargetMs ahead: enough margin to never land behind, short hold after.
                if (drift < 0) catchUpAheadMs = Math.Min(5000, catchUpAheadMs * 1.5);
                else catchUpAheadMs = Math.Max(150, Math.Min(5000, catchUpAheadMs - (drift - TrimTargetMs) * 0.5));
            }
            else if (Math.Abs(drift) < 300)
            {
                if (kind == LearnKind.HoldStart) holdLatencyMs = Clamp(holdLatencyMs - drift * 0.5, 0, 150);
                else seekStartLatencyMs = Clamp(seekStartLatencyMs - drift * 0.7, 0, 600);
            }
            else return;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            settings.Set("holdLatency", holdLatencyMs.ToString("0", ci));
            settings.Set("seekStartLatency", seekStartLatencyMs.ToString("0", ci));
            settings.Set("catchUpAhead", catchUpAheadMs.ToString("0", ci));
        }

        static double Clamp(double v, double lo, double hi) { return Math.Max(lo, Math.Min(hi, v)); }

        double LatencyFor(bool afterSeek) { return afterSeek ? seekStartLatencyMs : holdLatencyMs; }

        /// <summary>Behind (or far off) while playing: seek past the timeline; the hold logic trims the excess.</summary>
        void CatchUp(double expected)
        {
            Seek(expected + catchUpAheadMs);
            settleUntil = Clock.NowMs + SettleMs;
            learn = LearnKind.CatchUp;
            syncStatus = "resyncing";
        }

        /// <summary>Pause on frame <paramref name="pos"/> and start playing when host time reaches <paramref name="at"/>.</summary>
        void Park(double pos, double at, bool forceSeek)
        {
            if (isPlaying) { media.Pause(); isPlaying = false; }
            if (!parked) parkSeeked = false;
            if (forceSeek || Math.Abs(media.Position.TotalMilliseconds - pos) > 40)
            {
                Seek(pos);
                parkSeeked = true;
            }
            parked = true;
            parkAt = at;
            ArmStart(HostNow());
        }

        void ArmStart(double now)
        {
            startTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, parkAt - LatencyFor(parkSeeked) - now));
            startTimer.Start();
        }

        void StartPlayback()
        {
            media.Play();
            isPlaying = true;
            driftSamples.Clear();
            settleUntil = Clock.NowMs + SettleMs;
            learn = !parked ? LearnKind.None : parkSeeked ? LearnKind.SeekStart : LearnKind.HoldStart;
            parked = false;
            parkSeeked = false;
            syncStatus = "playing";
        }

        static readonly bool Debug = Environment.GetEnvironmentVariable("VIDEOSYNC_DEBUG") == "1";

        void Seek(double ms)
        {
            if (Debug) Log.Write(string.Format("dbg {0} seek to {1:0} (was {2:0})", role, ms, media.Position.TotalMilliseconds));
            media.Position = TimeSpan.FromMilliseconds(Math.Max(0, ms));
            driftSamples.Clear();
        }

        // ---------------------------------------------------------------- host controls

        PlayState NextState()
        {
            var s = state.Clone();
            s.Seq = ++seq;
            return s;
        }

        void Publish(PlayState s)
        {
            ApplyState(s);
            if (server != null) server.Broadcast(s.Serialize());
        }

        bool HasVideo { get { return role == Role.Host && state != null && !string.IsNullOrEmpty(state.FileName); } }

        void HostBrowse()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Open video",
                Filter = "Video files|*.mp4;*.m4v;*.mov;*.wmv;*.avi;*.mkv;*.mpg;*.mpeg;*.ts|All files|*.*",
            };
            if (dlg.ShowDialog(this) == true) HostOpen(dlg.FileName);
        }

        void HostOpen(string path)
        {
            if (role != Role.Host) return;
            FileInfo fi;
            try { fi = new FileInfo(path); if (!fi.Exists) throw new FileNotFoundException(path); }
            catch (Exception ex) { ShowOverlay("Cannot open " + path + ":\n" + ex.Message); return; }
            Publish(new PlayState
            {
                Seq = ++seq,
                FilePath = fi.FullName,
                FileName = fi.Name,
                FileSize = fi.Length,
                Playing = false,
                PosMs = 0,
                RefHostMs = Clock.NowMs,
                Loop = loopBox.IsChecked == true,
            });
        }

        void HostPlayPause()
        {
            if (!HasVideo) return;
            double now = Clock.NowMs;
            var s = NextState();
            if (state.Playing)
            {
                s.Playing = false;
                s.PosMs = state.ExpectedPos(now);
            }
            else
            {
                if (durationMs > 0 && s.PosMs >= durationMs - 100) s.PosMs = 0;
                s.Playing = true;
                s.RefHostMs = now + PlayLeadMs;
            }
            Publish(s);
        }

        void HostStop()
        {
            if (!HasVideo) return;
            var s = NextState();
            s.Playing = false;
            s.PosMs = 0;
            Publish(s);
        }

        void HostSeek(double ms)
        {
            if (!HasVideo) return;
            var s = NextState();
            s.PosMs = Math.Max(0, durationMs > 0 ? Math.Min(ms, durationMs - 200) : ms);
            s.RefHostMs = Clock.NowMs + (s.Playing ? PlayLeadMs : 0);
            Publish(s);
        }

        void HostSetLoop(bool loop)
        {
            if (!HasVideo) return;
            var s = NextState();
            s.Loop = loop;
            Publish(s);
        }

        /// <summary>Host decides what happens when the video reaches its end: loop or stop.</summary>
        void HostEndCheck()
        {
            if (!HasVideo || !state.Playing || durationMs <= 0) return;
            double now = Clock.NowMs;
            if (now < state.RefHostMs || state.ExpectedPos(now) < durationMs) return;
            var s = NextState();
            if (s.Loop)
            {
                s.PosMs = 0;
                s.RefHostMs = now + LoopLeadMs;
            }
            else
            {
                s.Playing = false;
                s.PosMs = durationMs;
            }
            Publish(s);
        }

        // ---------------------------------------------------------------- periodic UI / keys / fullscreen

        void UpdateUi()
        {
            uiTicks++;
            double pos = state == null ? 0 : Math.Min(state.ExpectedPos(HostNow()), durationMs > 0 ? durationMs : double.MaxValue);
            timeText.Text = Fmt(pos) + " / " + Fmt(durationMs);
            if (!seeking) seekSlider.Value = Math.Min(pos, seekSlider.Maximum);
            playBtn.Content = state != null && state.Playing ? "Pause" : "Play";

            string drift = mediaReady && state != null && state.Playing ? string.Format("drift {0:+0;-0;0} ms", lastDrift) : "";
            if (role == Role.Host)
            {
                var clients = server == null ? new ClientInfo[0] : server.Snapshot();
                statusText.Text = string.Format("Hosting on port {0}  |  {1} client(s)  |  {2}  {3}  {4}",
                    Proto.TcpPort, clients.Length, syncStatus, drift, fileWarning);
                if (uiTicks % 4 == 0)
                {
                    clientList.Items.Clear();
                    foreach (var c in clients)
                        clientList.Items.Add(string.Format("{0,-14} {1,6:+0;-0;0} ms  {2}", Trim(c.Name, 14), c.DriftMs, c.Status) +
                                             (c.Wallpaper != "" ? "\n" + new string(' ', 15) + c.Wallpaper : ""));
                    if (clients.Length == 0) clientList.Items.Add("(none yet)");
                }
                // Show the address while no screens are connected, for 20 s after start, or when pinned with I.
                bool showInfo = infoPinned || clients.Length == 0 || Clock.NowMs < hostStartedAt + 20000;
                infoText.Text = "Host address:  " + hostAddress + "\n" + clients.Length + " screen(s) connected" +
                                (infoPinned ? "   (press I to hide)" : "");
                infoBadge.Visibility = showInfo && hostAddress != "" ? Visibility.Visible : Visibility.Collapsed;
                if (uiTicks % 40 == 0 && clients.Length > 0)
                {
                    var parts = new List<string>();
                    foreach (var c in clients) parts.Add(string.Format("{0}: {1:+0;-0;0} ms {2}", c.Name, c.DriftMs, c.Status));
                    Log.Write("Host " + syncStatus + " " + drift + " | " + string.Join(" | ", parts.ToArray()));
                }
            }
            else if (role == Role.Client)
            {
                string clock = client != null && client.HasClock
                    ? string.Format("clock offset {0:0.0} ms, rtt {1:0.0} ms", client.OffsetMs, client.RttMs) : "";
                statusText.Text = string.Join("  |  ", new[] { connStatus, clock, syncStatus, drift, fileWarning })
                    .Replace("  |    |  ", "  |  ").Trim(' ', '|');
                if (uiTicks % 4 == 0 && client != null) client.Report(lastDrift, syncStatus);
                if (client != null && client.Connected) clientLastConnectedAt = Clock.NowMs;
            }
            else statusText.Text = "Choose Host or Client.";
            if (role != Role.Host) infoBadge.Visibility = Visibility.Collapsed;
            if (notice != "" && Clock.NowMs < noticeUntil) statusText.Text = notice + "   |   " + statusText.Text;
        }

        static string Trim(string s, int n) { return s.Length <= n ? s : s.Substring(0, n - 1) + "~"; }

        static string Fmt(double ms)
        {
            var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F11) { ToggleFullscreen(); e.Handled = true; return; }
            if (e.Key == Key.Escape && fullscreen) { ToggleFullscreen(); e.Handled = true; return; }
            if (Keyboard.FocusedElement is TextBox || role != Role.Host) return;
            if (e.Key == Key.I) { infoPinned = !infoPinned; e.Handled = true; }
            else if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { CopyHostAddress(); e.Handled = true; }
            else if (e.Key == Key.Space) { HostPlayPause(); e.Handled = true; }
            else if (e.Key == Key.Left && state != null) { HostSeek(state.ExpectedPos(Clock.NowMs) - SeekStepMs); e.Handled = true; }
            else if (e.Key == Key.Right && state != null) { HostSeek(state.ExpectedPos(Clock.NowMs) + SeekStepMs); e.Handled = true; }
        }

        void ToggleFullscreen()
        {
            fullscreen = !fullscreen;
            var vis = fullscreen ? Visibility.Collapsed : Visibility.Visible;
            topBar.Visibility = vis;
            bottomBar.Visibility = vis;
            if (fullscreen)
            {
                prevWindowState = WindowState;
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                WindowState = WindowState.Normal; // re-maximize so the window covers the taskbar
                WindowState = WindowState.Maximized;
                stage.Cursor = Cursors.None;
            }
            else
            {
                WindowStyle = WindowStyle.SingleBorderWindow;
                ResizeMode = ResizeMode.CanResize;
                WindowState = prevWindowState;
                stage.Cursor = null;
            }
            SetRoleUi();
        }
    }
}
