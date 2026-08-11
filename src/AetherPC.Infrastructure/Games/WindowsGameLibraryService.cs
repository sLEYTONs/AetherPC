using System.Text.Json;
using System.Text.RegularExpressions;
using AetherPC.Core.Abstractions;
using AetherPC.Core.Models;
using Microsoft.Extensions.Logging;

namespace AetherPC.Infrastructure.Games;

/// <summary>
/// Detecta launchers y juegos en ubicaciones conocidas (Steam ACF, Epic manifests, etc.).
/// No escanea todo el disco.
/// </summary>
public sealed class WindowsGameLibraryService : IGameLibraryService
{
    private readonly ILogger<WindowsGameLibraryService> _log;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public WindowsGameLibraryService(ILogger<WindowsGameLibraryService> log) => _log = log;

    public Task<IReadOnlyList<LauncherInfo>> DetectLaunchersAsync(CancellationToken ct = default)
        => Task.Run(() => (IReadOnlyList<LauncherInfo>)DetectLaunchersCore(), ct);

    public Task<IReadOnlyList<GameLibraryEntry>> DetectGamesAsync(CancellationToken ct = default)
        => Task.Run(() => (IReadOnlyList<GameLibraryEntry>)DetectGamesCore(), ct);

    private List<LauncherInfo> DetectLaunchersCore()
    {
        var list = new List<LauncherInfo>();
        void Add(string name, params string[] paths)
        {
            foreach (var p in paths)
            {
                if (string.IsNullOrWhiteSpace(p) || !Directory.Exists(p)) continue;
                list.Add(new LauncherInfo { Name = name, Path = p, IsInstalled = true });
                return;
            }
            list.Add(new LauncherInfo { Name = name, Path = "", IsInstalled = false });
        }

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        Add("Steam", Path.Combine(pf86, "Steam"), Path.Combine(pf, "Steam"));
        Add("Epic Games", Path.Combine(common, "Epic"), Path.Combine(pf86, "Epic Games"));
        Add("Xbox", Path.Combine(local, "Microsoft", "XboxGames"));
        Add("Battle.net", Path.Combine(pf86, "Battle.net"), Path.Combine(pf, "Battle.net"));
        Add("EA App", Path.Combine(pf86, "Electronic Arts", "EA Desktop"), Path.Combine(local, "Electronic Arts", "EA Desktop"));
        Add("Ubisoft Connect", Path.Combine(pf86, "Ubisoft", "Ubisoft Game Launcher"), Path.Combine(pf, "Ubisoft", "Ubisoft Game Launcher"));
        Add("Riot Client", Path.Combine(local, "Riot Games", "Riot Client"));
        Add("GOG Galaxy", Path.Combine(pf86, "GOG Galaxy"), Path.Combine(pf, "GOG Galaxy"));
        return list;
    }

    private List<GameLibraryEntry> DetectGamesCore()
    {
        var games = new List<GameLibraryEntry>();
        try { games.AddRange(ReadSteamGames()); }
        catch (Exception ex) { _log.LogDebug(ex, "Steam games"); }
        try { games.AddRange(ReadEpicGames()); }
        catch (Exception ex) { _log.LogDebug(ex, "Epic games"); }

        return games
            .GroupBy(g => g.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<GameLibraryEntry> ReadSteamGames()
    {
        var steamRoots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam")
        };
        var steam = steamRoots.FirstOrDefault(Directory.Exists);
        if (steam is null) yield break;

        var libraryFolders = new List<string> { Path.Combine(steam, "steamapps") };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase))
            {
                var p = m.Groups[1].Value.Replace("\\\\", "\\");
                var apps = Path.Combine(p, "steamapps");
                if (Directory.Exists(apps) && !libraryFolders.Contains(apps, StringComparer.OrdinalIgnoreCase))
                    libraryFolders.Add(apps);
            }
        }

        foreach (var apps in libraryFolders)
        {
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                string text;
                try { text = File.ReadAllText(acf); }
                catch { continue; }

                var appId = MatchVdf(text, "appid");
                var name = MatchVdf(text, "name");
                var installdir = MatchVdf(text, "installdir");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(appId)) continue;
                if (string.Equals(name, "Steamworks Common Redistributables", StringComparison.OrdinalIgnoreCase))
                    continue;

                var installPath = string.IsNullOrWhiteSpace(installdir)
                    ? null
                    : Path.Combine(apps, "common", installdir);
                long? size = null;
                DateTimeOffset? last = null;
                string? exe = null;
                if (installPath is not null && Directory.Exists(installPath))
                {
                    try
                    {
                        size = DirSizeSafe(installPath, maxFiles: 4000);
                        var exeFile = Directory.EnumerateFiles(installPath, "*.exe", SearchOption.TopDirectoryOnly)
                            .FirstOrDefault(f => !f.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
                                              && !f.Contains("crash", StringComparison.OrdinalIgnoreCase));
                        exe = exeFile;
                        if (exeFile is not null)
                            last = File.GetLastWriteTimeUtc(exeFile);
                    }
                    catch { /* */ }
                }

                yield return new GameLibraryEntry
                {
                    Id = "steam:" + appId,
                    Name = name,
                    Launcher = "Steam",
                    ExecutablePath = exe,
                    InstallPath = installPath,
                    Drive = installPath is null ? null : Path.GetPathRoot(installPath)?.TrimEnd('\\'),
                    SizeBytes = size,
                    LastPlayed = last,
                    Source = "Steam appmanifest"
                };
            }
        }
    }

    private static IEnumerable<GameLibraryEntry> ReadEpicGames()
    {
        var manifests = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifests)) yield break;

        foreach (var file in Directory.EnumerateFiles(manifests, "*.item"))
        {
            EpicManifest? m;
            try
            {
                m = JsonSerializer.Deserialize<EpicManifest>(File.ReadAllText(file), JsonOpts);
            }
            catch { continue; }
            if (m is null || string.IsNullOrWhiteSpace(m.DisplayName)) continue;
            if (m.bIsIncompleteInstall) continue;

            var install = m.InstallLocation;
            long? size = m.InstallSize > 0 ? m.InstallSize : null;
            string? exe = null;
            DateTimeOffset? last = null;
            if (!string.IsNullOrWhiteSpace(install) && Directory.Exists(install))
            {
                try
                {
                    var launch = m.LaunchExecutable;
                    if (!string.IsNullOrWhiteSpace(launch))
                    {
                        var full = Path.Combine(install, launch);
                        if (File.Exists(full))
                        {
                            exe = full;
                            last = File.GetLastWriteTimeUtc(full);
                        }
                    }
                    size ??= DirSizeSafe(install, maxFiles: 4000);
                }
                catch { /* */ }
            }

            yield return new GameLibraryEntry
            {
                Id = "epic:" + (m.CatalogItemId ?? m.AppName ?? m.DisplayName),
                Name = m.DisplayName!,
                Launcher = "Epic Games",
                ExecutablePath = exe,
                InstallPath = install,
                Drive = install is null ? null : Path.GetPathRoot(install)?.TrimEnd('\\'),
                SizeBytes = size,
                LastPlayed = last,
                Source = "Epic manifest"
            };
        }
    }

    private static string? MatchVdf(string text, string key)
    {
        var m = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static long DirSizeSafe(string path, int maxFiles)
    {
        long total = 0;
        var n = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { /* */ }
                if (++n >= maxFiles) break;
            }
        }
        catch { /* */ }
        return total;
    }

    private sealed class EpicManifest
    {
        public string? DisplayName { get; set; }
        public string? InstallLocation { get; set; }
        public string? LaunchExecutable { get; set; }
        public string? CatalogItemId { get; set; }
        public string? AppName { get; set; }
        public long InstallSize { get; set; }
        public bool bIsIncompleteInstall { get; set; }
    }
}
