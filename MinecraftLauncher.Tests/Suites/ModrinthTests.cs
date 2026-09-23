using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MinecraftLauncher.Core;
using static MinecraftLauncher.Tests.Harness;

namespace MinecraftLauncher.Tests
{
    /// <summary>
    /// Searching and installing mods from Modrinth. Needs the internet.
    /// </summary>
    /// <remarks>
    /// Runs against the live API, because the point of this code is that it agrees
    /// with a service nobody here controls. Everything it writes goes to a temp folder
    /// that is deleted at the end.
    /// </remarks>
    public static class ModrinthTests
    {
        public static async Task RunAsync()
        {
            var ct = new CancellationTokenSource(TimeSpan.FromMinutes(8)).Token;

            ModrinthApi.SearchPage page;
            try
            {
                page = await ModrinthApi.SearchAsync("sodium", "1.20.1", "Fabric", 5, ct);
            }
            catch (Exception ex)
            {
                Skip("modrinth", $"cannot reach the API ({ex.GetType().Name})");
                return;
            }

            Mapping();
            Searching(page);
            await SortingAndPaging(ct);
            await Icons(page, ct);
            await VersionsAndDependencies(ct);
            await Installing(ct);
            await Upgrading(ct);
            Names();
        }

        private static void Mapping()
        {
            Section("loader mapping");

            Expect("fabric maps", ModrinthApi.LoaderFacet("FABRIC"), "fabric");
            Expect("neoforge maps", ModrinthApi.LoaderFacet("NeoForge"), "neoforge");
            Expect("paper maps", ModrinthApi.LoaderFacet("Paper"), "paper");
            Check("vanilla maps to nothing — it runs no mods",
                  ModrinthApi.LoaderFacet("Vanilla") is null);
        }

        private static void Searching(ModrinthApi.SearchPage page)
        {
            Section("search, filtered to a real version and loader");

            var hits = page.Hits;
            Check($"search returned results ({hits.Count})", hits.Count > 0);
            foreach (var h in hits.Take(3))
                Note($"{h.Title,-24} by {h.Author,-16} {h.DownloadsText,8}  {h.Slug}");

            Check("results carry a project id", hits.All(h => h.ProjectId.Length > 0));
            Check("total hits is reported", page.TotalHits > 0, page.TotalHits.ToString());

            var sodium = hits.FirstOrDefault(h => h.Slug == "sodium");
            if (sodium is null) { Skip("card fields", "sodium not in the results"); return; }

            Note($"icon {sodium.IconUrl}");
            Note($"categories '{sodium.CategoriesText}'  {sodium.UpdatedText}  {sodium.EnvironmentText}");

            Check("it has an icon url", !string.IsNullOrWhiteSpace(sodium.IconUrl));
            Check("it has categories", sodium.Categories.Count > 0);
            Check("loader names are stripped from the tags",
                  !sodium.CategoriesText.Contains("fabric", StringComparison.OrdinalIgnoreCase),
                  sodium.CategoriesText);
            Check("it knows when it was updated", sodium.UpdatedText.Length > 0);
            Check("it knows which side it runs on", sodium.EnvironmentText.Length > 0);
            Expect("it has a first letter for the fallback tile", sodium.Initial, "S");
            Check("downloads are humanised",
                  sodium.DownloadsText.EndsWith("M") || sodium.DownloadsText.EndsWith("k"),
                  sodium.DownloadsText);
        }

        private static async Task SortingAndPaging(CancellationToken ct)
        {
            Section("sorting and paging");

            var byDownloads = await ModrinthApi.SearchAsync("", "1.20.1", "Fabric", 5, ct, index: "downloads");
            Check("sorting by downloads returns results", byDownloads.Hits.Count > 0);
            Check("  and they really are descending",
                  byDownloads.Hits.Zip(byDownloads.Hits.Skip(1)).All(p => p.First.Downloads >= p.Second.Downloads),
                  string.Join(" > ", byDownloads.Hits.Select(h => h.Downloads)));
            Note($"top: {string.Join(", ", byDownloads.Hits.Take(3).Select(h => h.Title))}");

            var second = await ModrinthApi.SearchAsync("", "1.20.1", "Fabric", 5, ct, offset: 5, index: "downloads");
            Check("a second page returns different mods",
                  !byDownloads.Hits.Select(h => h.ProjectId)
                      .Intersect(second.Hits.Select(h => h.ProjectId)).Any(),
                  "the pages overlap");
            Check("the first page knows more exist", byDownloads.HasMore);
            Expect("the second page reports its offset", second.Offset, 5);

            foreach (var (label, index) in ModrinthApi.SortOptions)
            {
                var r = await ModrinthApi.SearchAsync("", "1.20.1", "Fabric", 3, ct, index: index);
                Check($"  {label} ({index}) is accepted", r.Hits.Count > 0, "no results");
            }

            var nonsense = await ModrinthApi.SearchAsync("zzzqqqnotathing", "1.20.1", "Fabric", 5, ct);
            Expect("a hopeless search returns nothing rather than failing", nonsense.Hits.Count, 0);
        }

        private static async Task Icons(ModrinthApi.SearchPage page, CancellationToken ct)
        {
            Section("icons");

            var withIcon = page.Hits.FirstOrDefault(h => !string.IsNullOrWhiteSpace(h.IconUrl));
            if (withIcon is null) { Skip("icons", "no result had one"); return; }

            byte[]? bytes = await ModrinthApi.IconAsync(withIcon.IconUrl, ct);
            Check("an icon downloads", bytes is not null && bytes.Length > 0);
            if (bytes is not null)
                Note($"{bytes.Length} bytes, magic {BitConverter.ToString(bytes, 0, 4)}");

            byte[]? again = await ModrinthApi.IconAsync(withIcon.IconUrl, ct);
            Check("a second fetch is served from the cache, byte-identical",
                  again is not null && bytes is not null && again.SequenceEqual(bytes));

            // A missing icon is cosmetic and must never take the browser down.
            Check("a missing url is null, not a crash", await ModrinthApi.IconAsync(null, ct) is null);
            Check("a broken url is null, not a crash",
                  await ModrinthApi.IconAsync("https://cdn.modrinth.com/data/NOPE/nothing.webp", ct) is null);
        }

        private static async Task VersionsAndDependencies(CancellationToken ct)
        {
            Section("versions and required dependencies");

            var versions = await ModrinthApi.VersionsAsync("sodium", "1.20.1", "Fabric", ct);
            Check($"sodium has versions for 1.20.1 fabric ({versions.Count})", versions.Count > 0);

            var best = ModrinthApi.BestOf(versions);
            Check("a best version is chosen", best is not null);
            if (best is null) return;

            Note($"picked {best.Describe()}  {best.File!.FileName} ({best.File.SizeText})");
            Check("it is a release", best.IsRelease, best.VersionType);
            Check("the file has a sha1 to verify against", !string.IsNullOrWhiteSpace(best.File.Sha1));
            Check("the file is a jar",
                  best.File.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));

            Expect("an impossible game version yields nothing",
                   (await ModrinthApi.VersionsAsync("sodium", "1.4.7", "Fabric", ct)).Count, 0);

            // sodium-extra genuinely requires Fabric API and Sodium.
            var extra = ModrinthApi.BestOf(await ModrinthApi.VersionsAsync("sodium-extra", "1.20.1", "Fabric", ct));
            if (extra is null) { Skip("dependencies", "sodium-extra unavailable"); return; }

            var set = await ModrinthApi.ResolveWithDependenciesAsync(extra, "1.20.1", "Fabric", ct);
            foreach (var v in set) Note($"{v.File!.FileName} ({v.File.SizeText})");

            Check($"the set is bigger than the one mod ({set.Count})", set.Count > 1);
            Expect("the mod itself is first", set[0].ProjectId, extra.ProjectId);
            Check("sodium came along as a dependency",
                  set.Any(v => v.File!.FileName.Contains("sodium-fabric", StringComparison.OrdinalIgnoreCase)));
            Check("fabric api came along as a dependency",
                  set.Any(v => v.File!.FileName.Contains("fabric-api", StringComparison.OrdinalIgnoreCase)));
            Check("nothing is listed twice",
                  set.Select(v => v.ProjectId).Distinct().Count() == set.Count);

            // "Optional" on Modrinth usually means an integration, not a requirement.
            Check("optional dependencies were not dragged in", set.Count < 6, set.Count.ToString());
        }

        private static async Task Installing(CancellationToken ct)
        {
            Section("downloading for real");

            string folder = Path.Combine(Path.GetTempPath(), "modrinth-install-test");
            if (Directory.Exists(folder)) Directory.Delete(folder, true);

            var best = ModrinthApi.BestOf(await ModrinthApi.VersionsAsync("sodium", "1.20.1", "Fabric", ct));
            if (best is null) { Skip("install", "no sodium version"); return; }

            var result = await ModrinthApi.InstallAsync(best.File!, folder, ct);
            Expect("the install reports success", result.Status, ModrinthApi.InstallStatus.Installed);

            string landed = Path.Combine(folder, best.File!.FileName);
            Check("the file is there", File.Exists(landed));
            Expect("the size matches what was advertised",
                   new FileInfo(landed).Length, best.File.Size);
            Check("the SHA-1 matches what Modrinth published",
                  Downloader.HashMatches(landed, best.File.Sha1!), "hash mismatch");

            // The payoff: a downloaded jar goes through the same checks as one copied in.
            var detected = ModInspector.Detect(landed);
            Note($"detected as {ModInspector.Describe(detected)}");
            Check("it is a real Fabric mod", detected.HasFlag(ModLoaders.Fabric));
            Check("the loader check accepts it for Fabric",
                  ModInspector.IsCompatible(detected, "FABRIC"));
            Check("and rejects it for NeoForge",
                  !ModInspector.IsCompatible(detected, "NEOFORGE"));
            Check("it appears in the mods list",
                  ModManager.List(folder) is { Count: 1 } list && list[0].Enabled);

            Expect("installing the same build again is a no-op",
                   (await ModrinthApi.InstallAsync(best.File, folder, ct)).Status,
                   ModrinthApi.InstallStatus.AlreadyThere);

            // A disabled copy counts as present, or the folder fills with twins.
            File.Move(landed, landed + ".disabled");
            Expect("a turned-off copy still counts as present",
                   (await ModrinthApi.InstallAsync(best.File, folder, ct)).Status,
                   ModrinthApi.InstallStatus.AlreadyThere);

            Directory.Delete(folder, true);
        }

        private static async Task Upgrading(CancellationToken ct)
        {
            Section("installing a newer build of a mod already present");

            string folder = Path.Combine(Path.GetTempPath(), "modrinth-upgrade-test");
            if (Directory.Exists(folder)) Directory.Delete(folder, true);

            var versions = (await ModrinthApi.VersionsAsync("sodium", "1.20.1", "Fabric", ct))
                           .Where(v => v.File is not null).ToList();
            if (versions.Count < 2) { Skip("upgrading", "needs two builds"); return; }

            var newest = versions[0];
            var older = versions.First(v => v.VersionNumber != newest.VersionNumber);
            Note($"older {older.File!.FileName}");
            Note($"newer {newest.File!.FileName}");

            var first = await ModrinthApi.InstallAsync(older.File, folder, ct);
            Expect("the first install is a plain install", first.Status, ModrinthApi.InstallStatus.Installed);
            Expect("  it displaced nothing", first.Superseded.Count, 0);

            var second = await ModrinthApi.InstallAsync(newest.File, folder, ct);
            Expect("upgrading reports a replacement", second.Status, ModrinthApi.InstallStatus.Replaced);
            Expect("  and names what it turned off", second.Superseded.Count, 1);

            var live = ModManager.List(folder);
            Expect("exactly one copy is enabled", live.Count(m => m.Enabled), 1);
            Expect("  and it is the newer one",
                   live.Single(m => m.Enabled).FileName, newest.File.FileName);
            Check("the older build was kept, not deleted",
                  File.Exists(Path.Combine(folder, older.File.FileName + ".disabled")));
            Check("no duplicate warning remains",
                  ModInspector.WarnAboutDuplicates(live) is null,
                  ModInspector.WarnAboutDuplicates(live));

            // File names cannot answer this; the mod id inside the jar can.
            string newPath = Path.Combine(folder, newest.File.FileName);
            string oldPath = Path.Combine(folder, older.File.FileName + ".disabled");
            Check("both builds report the same mod id",
                  ModInspector.ModIdOf(newPath) == ModInspector.ModIdOf(oldPath),
                  $"{ModInspector.ModIdOf(newPath)} vs {ModInspector.ModIdOf(oldPath)}");
            Expect("  and it is sodium", ModInspector.ModIdOf(newPath), "sodium");

            // Picking an older version from the dropdown works the same way round.
            File.Delete(oldPath);
            var down = await ModrinthApi.InstallAsync(older.File, folder, ct);
            Expect("a deliberate downgrade also replaces", down.Status, ModrinthApi.InstallStatus.Replaced);
            Expect("  leaving one enabled", ModManager.List(folder).Count(m => m.Enabled), 1);
            Expect("  and it is the older one now",
                   ModManager.List(folder).Single(m => m.Enabled).FileName, older.File.FileName);

            var fabricApi = ModrinthApi.BestOf(await ModrinthApi.VersionsAsync("fabric-api", "1.20.1", "Fabric", ct));
            if (fabricApi is not null)
            {
                var other = await ModrinthApi.InstallAsync(fabricApi.File!, folder, ct);
                Expect("an unrelated mod displaces nothing", other.Superseded.Count, 0);
                Expect("  and both live together", ModManager.List(folder).Count(m => m.Enabled), 2);
            }

            // The same crash arrives when someone drags a second copy in by hand.
            File.Copy(Path.Combine(folder, older.File.FileName),
                      Path.Combine(folder, "sodium-copied-by-hand.jar"));
            string? warning = ModInspector.WarnAboutDuplicates(ModManager.List(folder));
            Note(warning ?? "(no warning)");
            Check("a copy added by hand is noticed", warning is not null);
            Check("  and the warning names the mod id", warning!.Contains("sodium"), warning);

            Directory.Delete(folder, true);
        }

        private static void Names()
        {
            Section("file names cannot escape the mods folder");

            Expect("a windows traversal is flattened",
                   ModrinthApi.SafeName(@"..\..\windows\system32\evil.jar"), "evil.jar");
            Expect("a unix traversal too", ModrinthApi.SafeName("../../etc/passwd"), "passwd");
            Expect("an empty name gets something harmless", ModrinthApi.SafeName(""), "mod.jar");
        }
    }
}
