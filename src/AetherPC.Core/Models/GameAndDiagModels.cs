namespace AetherPC.Core.Models;

public sealed class GameLibraryEntry
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Launcher { get; init; } = "";
    public string? ExecutablePath { get; init; }
    public string? InstallPath { get; init; }
    public string? Drive { get; init; }
    public long? SizeBytes { get; init; }
    public DateTimeOffset? LastPlayed { get; init; }
    public string Source { get; init; } = "";
    public string SizeLabel => SizeBytes is > 0
        ? $"{SizeBytes.Value / (1024.0 * 1024 * 1024):F1} GB"
        : "—";
}

public sealed class LauncherInfo
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public bool IsInstalled { get; init; }
}

public sealed class GamingReadiness
{
    public string StatusKey { get; init; } = "Gaming.Status.Unknown"; // Ready | Improve | Attention | Unavailable
    public string Reason { get; init; } = "";
    public IReadOnlyList<string> Issues { get; init; } = Array.Empty<string>();
}

/// <summary>Preferencias de preparación por juego (solo app).</summary>
public sealed class GamePrepProfile
{
    public string GameId { get; set; } = "";
    public string? DisplayName { get; set; }
    public List<string> SelectedActionIds { get; set; } = new();
    public List<string> AlwaysKeepProcesses { get; set; } = new();
    public List<string> CloseOnPrepare { get; set; } = new();
}

public sealed class BottleneckFinding
{
    public string Area { get; init; } = ""; // CPU | RAM | Disk | GPU | Temp | Processes | Config | Space | Driver | Unknown
    public string TitleKey { get; init; } = "";
    public string Title => AetherPC.Core.Localization.Loc.T(TitleKey);
    public string Evidence { get; init; } = "";
    public string RecommendationKey { get; init; } = "";
    public string Recommendation => AetherPC.Core.Localization.Loc.T(RecommendationKey);
    public string Severity { get; init; } = "Info"; // Info | Warn | High
    public string Confidence { get; init; } = "Medium"; // Low | Medium | High
    public string? NavigateTo { get; init; } // optimize | beast | processes | cleanup | drivers | hardware
}
