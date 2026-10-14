using System;
using System.Windows;

namespace VideoSync
{
    class StartupOptions
    {
        public Role Role = Role.None;
        public string HostAddress, Folder, File;
        public bool Fullscreen, Play, Loop, Mute, NoWallpaper;

        public static StartupOptions Parse(string[] args)
        {
            var o = new StartupOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                string next = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null;
                switch (a)
                {
                    case "--host": o.Role = Role.Host; break;
                    case "--client": o.Role = Role.Client; if (next != null) { o.HostAddress = next; i++; } break;
                    case "--folder": if (next != null) { o.Folder = next; i++; } break;
                    case "--file": if (next != null) { o.File = next; i++; } break;
                    case "--play": o.Play = true; break;
                    case "--loop": o.Loop = true; break;
                    case "--fullscreen": o.Fullscreen = true; break;
                    case "--mute": o.Mute = true; break;
                    case "--no-wallpaper": o.NoWallpaper = true; break;
                }
            }
            return o;
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Log.Write("Starting VideoSync " + string.Join(" ", args));
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (s, e) =>
            {
                Log.Write("Unhandled: " + e.Exception);
                MessageBox.Show(e.Exception.Message, "VideoSync error", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            app.Run(new MainWindow(StartupOptions.Parse(args), Settings.Load()));
        }
    }
}
