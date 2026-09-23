using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// Loads the WebView2 assemblies out of <c>runtime\webview2\</c> instead of from
    /// beside the executable, keeping the deployment layout to a single copy.
    /// </summary>
    /// <remarks>
    /// The csproj references these DLLs for compilation only
    /// (<c>&lt;Private&gt;false&lt;/Private&gt;</c>), so nothing copies them to the
    /// output directory and the default loader cannot find them — this resolver is
    /// what makes them load at all. It runs from a module initializer so it is in
    /// place before any code touches a WebView2 type.
    /// </remarks>
    public static class WebView2Resolver
    {
        public static string Directory => Path.Combine(Paths.Runtime, "webview2");

        /// <summary>Why WebView2 is unavailable, or null when it looks usable.</summary>
        public static string? Error { get; private set; }

        public static bool Available => Error is null;

        [ModuleInitializer]
        internal static void Register()
        {
            string dir = Directory;

            if (!System.IO.Directory.Exists(dir))
            {
                Error = $"Folder not found: {dir}";
                return;
            }

            foreach (string required in new[]
                     {
                         "Microsoft.Web.WebView2.Core.dll",
                         "Microsoft.Web.WebView2.Wpf.dll",
                         "WebView2Loader.dll"
                     })
            {
                if (File.Exists(Path.Combine(dir, required))) continue;

                string present = string.Join(", ",
                    System.IO.Directory.GetFiles(dir).Select(Path.GetFileName));
                Error = $"{required} missing from {dir}.\nFiles present: {present}";
                return;
            }

            // WebView2Loader.dll is native and is P/Invoked by Core.dll, so it has to
            // be discoverable through the OS search path rather than this resolver.
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            Environment.SetEnvironmentVariable("PATH", $"{dir}{Path.PathSeparator}{path}");

            AssemblyLoadContext.Default.Resolving += ResolveFromRuntimeFolder;
        }

        private static Assembly? ResolveFromRuntimeFolder(AssemblyLoadContext context, AssemblyName name)
        {
            if (name.Name is null ||
                !name.Name.StartsWith("Microsoft.Web.WebView2", StringComparison.OrdinalIgnoreCase))
                return null;

            string candidate = Path.Combine(Directory, name.Name + ".dll");
            return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
        }
    }
}
