using System;
using System.IO;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// Builds a self-contained HTML document that renders a skin in 3D with
    /// skinview3d.
    /// </summary>
    /// <remarks>
    /// Everything is inlined — the library source and the skin as a data URI — so
    /// the page needs no file:// navigation and no network access, which is what
    /// keeps WebView2's local-content restrictions out of the picture.
    /// </remarks>
    public static class SkinPreviewHtml
    {
        public static string BundlePath =>
            Path.Combine(Paths.Runtime, "skinview3d", "skinview3d.bundle.js");

        public static bool BundleAvailable => File.Exists(BundlePath);

        /// <param name="fillWindow">
        /// True for the embedded viewer, which tracks the pane size. False for the
        /// standalone browser page, which uses a fixed canvas.
        /// </param>
        public static string Build(string skinPath, string model, bool fillWindow = true)
        {
            if (!BundleAvailable)
                throw new FileNotFoundException($"skinview3d bundle not found at {BundlePath}");

            string bundleJs = File.ReadAllText(BundlePath);
            string skinBase64 = Convert.ToBase64String(File.ReadAllBytes(skinPath));
            string modelType = model == "alex" ? "slim" : "default";

            string size = fillWindow
                ? "width: window.innerWidth, height: window.innerHeight"
                : "width: 300, height: 500";

            string resizeHook = fillWindow
                ? "window.addEventListener('resize', () => viewer.setSize(window.innerWidth, window.innerHeight));"
                : "";

            string hint = fillWindow
                ? ""
                : "<div class=\"hint\">Drag to rotate &middot; Scroll to zoom</div>";

            return $$"""
            <!DOCTYPE html><html><head><meta charset="UTF-8">
            <style>
              *{margin:0;padding:0;box-sizing:border-box}
              body{background:#141414;overflow:hidden;display:flex;flex-direction:column;
                   align-items:center;justify-content:center;height:100vh;
                   font-family:'Segoe UI',sans-serif}
              canvas{display:block}
              .hint{color:#9a9a9a;font-size:12px;margin-top:12px}
            </style></head><body>
            <canvas id="c"></canvas>
            {{hint}}
            <script>
            {{bundleJs}}
            </script>
            <script>
            const viewer = new skinview3d.SkinViewer({
              canvas: document.getElementById('c'),
              {{size}},
              skin: 'data:image/png;base64,{{skinBase64}}',
              // Must be a constructor option: the skin texture loads asynchronously
              // and applies the model on completion, so assigning
              // playerObject.skin.modelType afterwards is silently overwritten.
              model: '{{modelType}}'
            });
            viewer.controls = skinview3d.createOrbitControls(viewer);
            viewer.controls.enableRotate = true;
            viewer.controls.enableZoom   = true;
            viewer.controls.enablePan    = false;
            viewer.autoRotate      = true;
            viewer.autoRotateSpeed = 0.5;
            viewer.zoom            = 0.75;
            viewer.animation = new skinview3d.WalkingAnimation();
            {{resizeHook}}
            </script>
            </body></html>
            """;
        }

        /// <summary>Writes the standalone page used by the "open in browser" fallback.</summary>
        public static string WriteStandalone(string skinPath, string model, string username)
        {
            string html = Build(skinPath, model, fillWindow: false);
            string outPath = Path.Combine(Path.GetTempPath(), $"skinpreview_{username}.html");
            File.WriteAllText(outPath, html);
            return outPath;
        }
    }
}
