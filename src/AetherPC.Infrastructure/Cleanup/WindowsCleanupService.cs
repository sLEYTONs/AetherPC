using System.Runtime.InteropServices;
using AetherPC.Core.Abstractions;
using AetherPC.Core.Enums;
using AetherPC.Core.Localization;
using AetherPC.Core.Models;
using Microsoft.Extensions.Logging;

namespace AetherPC.Infrastructure.Cleanup;

public sealed class WindowsCleanupService : ICleanupService
{
    private readonly ILogger<WindowsCleanupService> _logger;

    public WindowsCleanupService(ILogger<WindowsCleanupService> logger) => _logger = logger;

    public Task<IReadOnlyList<CleanupCandidate>> ScanAsync(CancellationToken ct = default)
        => Task.Run(() => ScanCore(ct), ct);

    public Task<ActionResult> CleanAsync(IEnumerable<string> candidateIds, CancellationToken ct = default)
    {
        var ids = candidateIds.ToArray();
        return Task.Run(() => CleanCore(ids, ct), ct);
    }

    private IReadOnlyList<CleanupCandidate> ScanCore(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var list = new List<CleanupCandidate>();

        AddDir(list, "temp.user", Loc.T("Cleanup.Cat.TempUser"),
            Path.GetTempPath(), RiskLevel.Low,
            Loc.T("Cleanup.Cat.TempUserReason"));

        AddDir(list, "temp.windows", Loc.T("Cleanup.Cat.TempWindows"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
            RiskLevel.Low,
            Loc.T("Cleanup.Cat.TempWindowsReason"));

        AddDir(list, "cache.thumbnails", Loc.T("Cleanup.Cat.Thumbnails"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\Windows\Explorer"),
            RiskLevel.Low, Loc.T("Cleanup.Cat.ThumbnailsReason"));

        list.Add(new CleanupCandidate
        {
            Id = "recycle.user",
            DisplayName = Loc.T("Cleanup.Cat.Recycle"),
            Path = "shell:RecycleBinFolder",
            EstimatedBytes = 0,
            Risk = RiskLevel.Low,
            Reason = Loc.T("Cleanup.Cat.RecycleReason")
        });

        return list;
    }

    private ActionResult CleanCore(string[] ids, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var idSet = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        long freed = 0;
        var skippedTotal = 0;
        var details = new List<string>();
        var anyAttempted = false;

        foreach (var c in ScanCore(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (!idSet.Contains(c.Id)) continue;
            anyAttempted = true;

            if (c.Id == "recycle.user")
            {
                var (ok, msg, bytes) = EmptyRecycleBin();
                if (ok) freed += bytes;
                details.Add(msg);
                continue;
            }

            if (c.Id == "cache.thumbnails")
            {
                freed += ClearThumbcaches(c.Path, details, out var thumbSkipped);
                skippedTotal += thumbSkipped;
                continue;
            }

            if (!Directory.Exists(c.Path))
            {
                details.Add(Loc.T("Cleanup.Detail.MissingFolder", c.DisplayName));
                continue;
            }

            freed += TryClearDirectoryRecursive(c.Path, details, out var dirSkipped);
            skippedTotal += dirSkipped;
        }

        var success = anyAttempted && (freed > 0 || details.Any(d => d.Contains("OK", StringComparison.OrdinalIgnoreCase)
            || d.Contains(Loc.T("Cleanup.Detail.RecycleOkMarker"), StringComparison.OrdinalIgnoreCase)));
        if (!anyAttempted)
        {
            return new ActionResult
            {
                ActionId = "cleanup.selected",
                Success = false,
                Detail = Loc.T("Cleanup.Detail.NoneSelected")
            };
        }

        if (freed == 0 && !details.Any(d => d.Contains(Loc.T("Cleanup.Detail.RecycleOkMarker"), StringComparison.OrdinalIgnoreCase)))
        {
            // Archivos en uso / vacío: la limpieza sigue considerándose completada (éxito parcial).
            success = true;
            if (details.Count == 0)
                details.Add(Loc.T("Cleanup.Detail.NothingDeleted"));
        }
        else if (freed > 0)
        {
            success = true;
        }

        _logger.LogInformation("Limpieza: liberados {Bytes} bytes · omitidos={Skipped} · ok={Ok}", freed, skippedTotal, success);
        return new ActionResult
        {
            ActionId = "cleanup.selected",
            Success = success,
            Detail = string.Join(" | ", details.Take(10)) + " · " + Loc.T("Cleanup.Detail.TotalMb", freed / (1024.0 * 1024)),
            BytesFreed = freed,
            SkippedCount = skippedTotal
        };
    }

    private static void AddDir(List<CleanupCandidate> list, string id, string name, string path, RiskLevel risk, string reason)
    {
        long size = 0;
        try
        {
            if (Directory.Exists(path))
                size = DirSizeRecursive(path);
        }
        catch { /* ignore */ }

        list.Add(new CleanupCandidate
        {
            Id = id,
            DisplayName = name,
            Path = path,
            EstimatedBytes = size,
            Risk = risk,
            Reason = reason
        });
    }

    private static long DirSizeRecursive(string path)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch { /* locked */ }
            }
        }
        catch { /* ignore */ }
        return total;
    }

    private static long TryClearDirectoryRecursive(string path, List<string> details, out int skippedCount)
    {
        long freed = 0;
        var deleted = 0;
        var skipped = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(file);
                    var len = fi.Length;
                    fi.Attributes &= ~FileAttributes.ReadOnly;
                    File.Delete(file);
                    freed += len;
                    deleted++;
                }
                catch { skipped++; }
            }

            foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                try { Directory.Delete(dir, recursive: false); } catch { /* not empty / in use */ }
            }

            details.Add(Loc.T("Cleanup.Detail.DirSummary",
                Path.GetFileName(path.TrimEnd('\\')), deleted, skipped, freed / (1024.0 * 1024)));
        }
        catch (Exception ex)
        {
            details.Add($"{path}: {ex.Message}");
        }
        skippedCount = skipped;
        return freed;
    }

    private static long ClearThumbcaches(string explorerCacheDir, List<string> details, out int skippedCount)
    {
        long freed = 0;
        var n = 0;
        var skipped = 0;
        try
        {
            if (!Directory.Exists(explorerCacheDir))
            {
                details.Add(Loc.T("Cleanup.Detail.ThumbsMissing"));
                skippedCount = 0;
                return 0;
            }

            foreach (var file in Directory.EnumerateFiles(explorerCacheDir, "thumbcache_*.db"))
            {
                try
                {
                    var len = new FileInfo(file).Length;
                    File.Delete(file);
                    freed += len;
                    n++;
                }
                catch { skipped++; }
            }

            details.Add(n > 0
                ? Loc.T("Cleanup.Detail.ThumbsOk", n, freed / (1024.0 * 1024))
                : Loc.T("Cleanup.Detail.ThumbsBusy"));
        }
        catch (Exception ex)
        {
            details.Add(Loc.T("Cleanup.Detail.ThumbsError", ex.Message));
        }
        skippedCount = skipped;
        return freed;
    }

    private static (bool Ok, string Message, long BytesHint) EmptyRecycleBin()
    {
        try
        {
            const int flags = 0x1 | 0x2 | 0x4;
            var hr = SHEmptyRecycleBin(IntPtr.Zero, null, flags);
            if (hr == 0 || hr == unchecked((int)0x8007871C))
                return (true, Loc.T("Cleanup.Detail.RecycleOk"), 0);
            return (false, Loc.T("Cleanup.Detail.RecycleCode", hr), 0);
        }
        catch (Exception ex)
        {
            return (false, Loc.T("Cleanup.Detail.RecycleError", ex.Message), 0);
        }
    }

    [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, int dwFlags);
}
