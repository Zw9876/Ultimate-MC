using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MinecraftLauncher.Core;

namespace MinecraftLauncher.UI
{
    /// <summary>
    /// Optional wallpaper for the launcher window: drop a background.png (or .jpg)
    /// next to the executable and it is used instead of the flat dark backdrop.
    /// </summary>
    internal static class AppBackground
    {
        /// <summary>Checked in order; the first one found wins.</summary>
        private static readonly string[] FileNames =
        {
            "background.png", "background.jpg", "background.jpeg"
        };

        public static string BaseDirectory => Paths.BaseDir;

        /// <summary>Applies the image if one is present. Returns false when none is.</summary>
        public static bool TryApply(Window window)
        {
            foreach (string name in FileNames)
            {
                string path = Path.Combine(Paths.BaseDir, name);
                if (!File.Exists(path)) continue;

                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(path, UriKind.Absolute);
                    // OnLoad reads the file fully up front so it is not left locked —
                    // the image can be replaced while the launcher is running.
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();

                    window.Background = new ImageBrush(bitmap) { Stretch = Stretch.Fill };
                    return true;
                }
                catch
                {
                    // A corrupt or unreadable image just means no wallpaper.
                }
            }

            return false;
        }
    }
}
