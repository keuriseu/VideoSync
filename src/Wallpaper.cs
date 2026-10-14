using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace VideoSync
{
    /// <summary>Reads, sends and applies the Windows desktop background.</summary>
    static class Wallpaper
    {
        const int SPI_SETDESKWALLPAPER = 0x14;
        const int SPI_GETDESKWALLPAPER = 0x73;
        const int SPIF_UPDATEINIFILE = 0x01;
        const int SPIF_SENDCHANGE = 0x02;
        const string DesktopKey = @"Control Panel\Desktop";
        public const int MaxBytes = 25 * 1024 * 1024;

        /// <summary>Testing aid: receive and save the picture but don't change the desktop.</summary>
        public static readonly bool DryRun = Environment.GetEnvironmentVariable("VIDEOSYNC_WALLPAPER_DRYRUN") == "1";

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SystemParametersInfo(int action, int param, string vparam, int winIni);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SystemParametersInfo(int action, int param, StringBuilder vparam, int winIni);

        public static string CurrentPath()
        {
            var sb = new StringBuilder(1024);
            SystemParametersInfo(SPI_GETDESKWALLPAPER, sb.Capacity, sb, 0);
            return sb.ToString();
        }

        static void GetStyle(out string style, out string tile)
        {
            style = "10"; // Fill
            tile = "0";
            using (var k = Registry.CurrentUser.OpenSubKey(DesktopKey))
            {
                if (k == null) return;
                style = k.GetValue("WallpaperStyle") as string ?? style;
                tile = k.GetValue("TileWallpaper") as string ?? tile;
            }
        }

        /// <summary>Builds the WALLPAPER message for this PC's current background, or throws with a readable reason.</summary>
        public static string BuildMessage()
        {
            string path = CurrentPath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                throw new InvalidOperationException("This PC's desktop background isn't a picture (a solid color can't be synced).");
            byte[] data = File.ReadAllBytes(path);
            if (data.Length > RecompressAboveBytes) data = Recompress(data);
            if (data.Length > MaxBytes)
                throw new InvalidOperationException("The background picture is too large to send (" + data.Length / (1024 * 1024) + " MB).");
            string style, tile;
            GetStyle(out style, out tile);
            return Proto.Join("WALLPAPER", Hash(data), style, tile, Convert.ToBase64String(data));
        }

        const int RecompressAboveBytes = 8 * 1024 * 1024;
        const int MaxPixelWidth = 7680;

        /// <summary>Large wallpapers (often multi-MB PNGs) are re-encoded as high-quality JPEG for a quick transfer.</summary>
        static byte[] Recompress(byte[] data)
        {
            using (var input = new MemoryStream(data))
            {
                var frame = System.Windows.Media.Imaging.BitmapFrame.Create(input,
                    System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                System.Windows.Media.Imaging.BitmapSource src = frame;
                if (frame.PixelWidth > MaxPixelWidth)
                {
                    double scale = (double)MaxPixelWidth / frame.PixelWidth;
                    src = new System.Windows.Media.Imaging.TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(scale, scale));
                }
                var enc = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 92 };
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(src));
                using (var output = new MemoryStream())
                {
                    enc.Save(output);
                    Log.Write(string.Format("Wallpaper recompressed {0:0.0} MB -> {1:0.0} MB ({2}x{3})",
                        data.Length / 1048576.0, output.Length / 1048576.0, src.PixelWidth, src.PixelHeight));
                    return output.ToArray();
                }
            }
        }

        public static string Hash(byte[] d)
        {
            using (var sha = SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(d)).Replace("-", "").Substring(0, 16);
        }

        static string Ext(byte[] d)
        {
            if (d.Length > 3 && d[0] == 0x89 && d[1] == 0x50) return ".png";
            if (d.Length > 1 && d[0] == 'B' && d[1] == 'M') return ".bmp";
            return ".jpg";
        }

        /// <summary>Applies a received WALLPAPER message. Returns a short status for the host.</summary>
        public static string Apply(string[] p, Settings settings)
        {
            string hash = p[1], style = p[2], tile = p[3];
            if (!DryRun && settings.Get("wallpaperHash", "") == hash) return "wallpaper up to date";
            byte[] data = Convert.FromBase64String(p[4]);
            if (Hash(data) != hash) throw new InvalidDataException("picture was damaged in transfer");

            Directory.CreateDirectory(Log.Dir);
            foreach (var old in Directory.GetFiles(Log.Dir, "wallpaper-*"))
                try { File.Delete(old); } catch { }
            string file = Path.Combine(Log.Dir, "wallpaper-" + hash + Ext(data));
            File.WriteAllBytes(file, data);

            if (DryRun)
            {
                Log.Write("Wallpaper dry run: saved " + file + " (style " + style + ", tile " + tile + ")");
                return "wallpaper received (dry run)";
            }
            SaveOriginal(settings);
            Set(file, style, tile);
            settings.Set("wallpaperHash", hash);
            Log.Write("Wallpaper set to " + file);
            return "wallpaper updated";
        }

        static void Set(string file, string style, string tile)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(DesktopKey, true))
            {
                if (k != null)
                {
                    k.SetValue("WallpaperStyle", style);
                    k.SetValue("TileWallpaper", tile);
                }
            }
            if (!SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, file, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE))
                throw new System.ComponentModel.Win32Exception();
        }

        /// <summary>Before the first change, keep a copy of this PC's own background so it can be restored.</summary>
        static void SaveOriginal(Settings settings)
        {
            if (HasOriginal(settings)) return;
            string path = CurrentPath();
            string style, tile;
            GetStyle(out style, out tile);
            string saved = "none";
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                // Copy the bytes: Windows overwrites its TranscodedWallpaper file when the background changes.
                var d = File.ReadAllBytes(path);
                saved = Path.Combine(Log.Dir, "original-wallpaper" + Ext(d));
                File.WriteAllBytes(saved, d);
            }
            settings.Set("originalWallpaper", saved);
            settings.Set("originalWallpaperStyle", style);
            settings.Set("originalWallpaperTile", tile);
        }

        public static bool HasOriginal(Settings settings) { return settings.Get("originalWallpaper", "") != ""; }

        public static string Restore(Settings settings)
        {
            string saved = settings.Get("originalWallpaper", "");
            if (saved == "") return "This PC's own background is already in use.";
            if (saved == "none") Set("", settings.Get("originalWallpaperStyle", "10"), settings.Get("originalWallpaperTile", "0"));
            else Set(saved, settings.Get("originalWallpaperStyle", "10"), settings.Get("originalWallpaperTile", "0"));
            settings.Set("originalWallpaper", "");
            settings.Set("wallpaperHash", "");
            Log.Write("Original wallpaper restored");
            return "Your original desktop background has been restored.";
        }
    }
}
