using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Media;
using AetherPC.App.Services;
using AetherPC.Application.Scanning;
using AetherPC.Core.Abstractions;
using AetherPC.Core.Enums;
using AetherPC.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AetherPC.App.ViewModels;

/// <summary>Acción seleccionable en la UI de optimización.</summary>
public partial class OptimizeActionItem : ObservableObject
{
    public OptimizationAction Action { get; }
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string? _lastResult;

    public OptimizeActionItem(OptimizationAction action, bool? startSelected = null)
    {
        Action = action;
        _isSelected = startSelected ?? action.IsSelected || action.IsRecommendedDefault;
        Action.IsSelected = _isSelected;
    }

    partial void OnIsSelectedChanged(bool value) => Action.IsSelected = value;

    public string DisplayName => Action.DisplayName;
    public string Category => Action.Category;

    /// <summary>Explicación corta y clara para el usuario.</summary>
    public string SimpleWhat => BuildSimpleWhat(Action);

    /// <summary>Estimación orientativa de mejora (no promesa). Rango realista.</summary>
    public int GainPercent => EstimateGainPercent(Action);
    public string GainLabel => $"+{GainPercent}% est.";
    public string GainHint => UiLoc.Instance.T("Gain.Hint");

    public string WhatWillHappen => SimpleWhat;
    public string Why => string.IsNullOrWhiteSpace(Action.WhyRecommended)
        ? Action.ExpectedImpact
        : Truncate(Action.WhyRecommended, 140);
    public string Impact => Action.ExpectedImpact;
    public string RiskLabel => Action.RiskLayer switch
    {
        OptimizationRiskLayer.Safe => UiLoc.Instance.T("Risk.Safe"),
        OptimizationRiskLayer.Recommended => UiLoc.Instance.T("Risk.Recommended"),
        OptimizationRiskLayer.Advanced => UiLoc.Instance.T("Risk.Advanced"),
        OptimizationRiskLayer.Experimental => UiLoc.Instance.T("Risk.Experimental"),
        _ => Action.Risk switch
        {
            RiskLevel.Low => UiLoc.Instance.T("Risk.Safe"),
            RiskLevel.Medium => UiLoc.Instance.T("Risk.Recommended"),
            _ => UiLoc.Instance.T("Risk.Advanced")
        }
    };
    public string RiskColor => Action.Risk switch
    {
        RiskLevel.Low => "#3DDC97",
        RiskLevel.Medium => "#F0A202",
        _ => "#FF5C5C"
    };
    public bool NeedsAdmin => Action.RequiresElevation;
    public bool NeedsReboot => Action.RequiresReboot;
    public bool IsTemporary => Action.IsTemporary || Action.PersistenceType == ActionPersistenceType.Temporary;
    public bool AffectsVisuals => Action.AffectsVisuals;
    public bool AffectsConvenience => Action.AffectsConvenience;
    public string PersistenceLabel => Action.PersistenceType switch
    {
        ActionPersistenceType.Temporary => UiLoc.Instance.T("Meta.Temporary"),
        ActionPersistenceType.RequiresReboot => UiLoc.Instance.T("Meta.NeedsReboot"),
        ActionPersistenceType.PersistentReversible => UiLoc.Instance.T("Meta.Reversible"),
        _ => UiLoc.Instance.T("Meta.Na")
    };
    public string ImpactLabel => Action.ImpactLevel switch
    {
        ActionImpactLevel.High => UiLoc.Instance.T("Meta.ImpactHigh"),
        ActionImpactLevel.Medium => UiLoc.Instance.T("Meta.ImpactMedium"),
        _ => UiLoc.Instance.T("Meta.ImpactLow")
    };
    public string? BytesHint => Action.EstimatedBytesFreed is > 0
        ? $"~{Action.EstimatedBytesFreed.Value / (1024.0 * 1024):F0} MB"
        : null;

    public BeastPriorityTier PriorityTier => Action.PriorityTier;
    public string PriorityLabel => Action.PriorityTier switch
    {
        BeastPriorityTier.Critical => UiLoc.Instance.T("Priority.Critical"),
        BeastPriorityTier.Recommended => UiLoc.Instance.T("Priority.Recommended"),
        BeastPriorityTier.Optional => UiLoc.Instance.T("Priority.Optional"),
        BeastPriorityTier.Advanced => UiLoc.Instance.T("Priority.Advanced"),
        _ => UiLoc.Instance.T("Priority.Incompatible")
    };
    public string CurrentState => string.IsNullOrWhiteSpace(Action.CurrentState) ? "—" : Action.CurrentState;
    public string RecommendedState => string.IsNullOrWhiteSpace(Action.RecommendedState) ? "—" : Action.RecommendedState;
    public bool IsCompatible => Action.IsCompatible;
    public string TechnicalLevel => Action.TechnicalLevel;
    public string StateLine => $"{CurrentState}  →  {RecommendedState}";
    public bool CanSelect => Action.IsCompatible;

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";

    private static string BuildSimpleWhat(OptimizationAction a)
    {
        var id = a.Id ?? "";
        if (id.StartsWith("process.close", StringComparison.OrdinalIgnoreCase))
            return UiLoc.Instance.T("Explain.ProcessClose");
        if (id.StartsWith("process.disable_startup", StringComparison.OrdinalIgnoreCase))
            return UiLoc.Instance.T("Explain.ProcessDisableStartup");
        if (id.StartsWith("process.priority", StringComparison.OrdinalIgnoreCase))
            return UiLoc.Instance.T("Explain.ProcessPriority");
        if (id.StartsWith("process.suspend", StringComparison.OrdinalIgnoreCase))
            return UiLoc.Instance.T("Explain.ProcessSuspend");
        if (id is "cleanup.temp" or "cleanup.advanced")
            return UiLoc.Instance.T("Explain.CleanupTemp");
        if (id.StartsWith("power.", StringComparison.OrdinalIgnoreCase))
            return UiLoc.Instance.T("Explain.Power");
        if (id.StartsWith("windows.gamemode", StringComparison.OrdinalIgnoreCase))
            return UiLoc.Instance.T("Explain.GameMode");
        if (id.Contains("gamedvr", StringComparison.OrdinalIgnoreCase))
            return UiLoc.Instance.T("Explain.GameDvr");
        if (id.StartsWith("disk.trim", StringComparison.OrdinalIgnoreCase))
            return UiLoc.Instance.T("Explain.DiskTrim");
        if (id.StartsWith("net.flushdns", StringComparison.OrdinalIgnoreCase))
            return UiLoc.Instance.T("Explain.FlushDns");
        if (id.StartsWith("nvidia.", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("amd.", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("intel.", StringComparison.OrdinalIgnoreCase))
            return UiLoc.Instance.T("Explain.Gpu");
        if (id.StartsWith("service.", StringComparison.OrdinalIgnoreCase))
            return UiLoc.Instance.T("Explain.Service");
        if (id.StartsWith("privacy.", StringComparison.OrdinalIgnoreCase))
            return UiLoc.Instance.T("Explain.Privacy");
        if (id is "defender.reduce_load")
            return UiLoc.Instance.T("Explain.Defender");
        if (id is "service.search.manual")
            return UiLoc.Instance.T("Explain.SearchManual");
        if (id is "process.boost_games")
            return UiLoc.Instance.T("Explain.BoostGames");
        if (id.StartsWith("perf.", StringComparison.OrdinalIgnoreCase))
            return UiLoc.Instance.T("Explain.PerfPrivacy");
        if (id.StartsWith("windows.", StringComparison.OrdinalIgnoreCase))
            return UiLoc.Instance.T("Explain.Windows");

        var raw = string.IsNullOrWhiteSpace(a.WhatWillHappen) ? a.Description : a.WhatWillHappen;
        return Truncate(raw, 160);
    }

    private static int EstimateGainPercent(OptimizationAction a)
    {
        var id = a.Id ?? "";
        // Rangos modestos y honestos — orientación, no marketing
        if (id.StartsWith("process.close", StringComparison.OrdinalIgnoreCase)) return 8;
        if (id.StartsWith("process.disable_startup", StringComparison.OrdinalIgnoreCase)) return 6;
        if (id.StartsWith("process.priority", StringComparison.OrdinalIgnoreCase)) return 3;
        if (id.StartsWith("process.suspend", StringComparison.OrdinalIgnoreCase)) return 5;
        if (id is "cleanup.temp") return 4;
        if (id is "cleanup.advanced") return 5;
        if (id is "power.ultimate" or "power.high") return 12;
        if (id is "power.cpu_max" or "intel.cpu_max" or "power.core_unpark") return 10;
        if (id is "power.balanced") return 3;
        if (id is "windows.gamemode") return 7;
        if (id.Contains("gamedvr", StringComparison.OrdinalIgnoreCase)) return 5;
        if (id.StartsWith("nvidia.", StringComparison.OrdinalIgnoreCase)) return 9;
        if (id.StartsWith("amd.", StringComparison.OrdinalIgnoreCase) || id.StartsWith("intel.gpu", StringComparison.OrdinalIgnoreCase)) return 7;
        if (id is "disk.trim") return 4;
        if (id.StartsWith("service.sysmain", StringComparison.OrdinalIgnoreCase)) return 6;
        if (id is "net.flushdns") return 2;
        if (id is "privacy.telemetry" or "privacy.diagtrack") return 6;
        if (id is "privacy.widgets" or "privacy.background_apps" or "privacy.tips") return 5;
        if (id.StartsWith("privacy.", StringComparison.OrdinalIgnoreCase)) return 3;
        if (id is "perf.visual_perf" or "perf.network_throttle") return 6;
        if (id is "defender.reduce_load") return 7;
        if (id is "process.boost_games") return 8;
        if (id is "service.search.manual") return 5;
        if (id is "perf.ntfs_lastaccess" or "perf.transparency_off") return 4;
        if (id is "perf.hibernate_off") return 3;
        if (id.StartsWith("perf.", StringComparison.OrdinalIgnoreCase)) return 4;
        if (id.StartsWith("windows.", StringComparison.OrdinalIgnoreCase)) return 4;
        return a.Risk switch
        {
            RiskLevel.Low => 4,
            RiskLevel.Medium => 7,
            _ => 5
        };
    }
}

public partial class OptimizeViewModel : ObservableObject
{
    private readonly ScanEngine _scan;
    private readonly IOptimizationEngine _opt;
    private readonly IHealthScorer _health;
    private readonly IAppSettingsStore _settings;
    private readonly DashboardViewModel _home;
    private SystemSnapshot? _lastSnapshot;
    private OptimizationPlan? _lastPlan;
    private CancellationTokenSource? _applyCts;
    private UserProfile _prefs = new();

    public ObservableCollection<OptimizeActionItem> Actions { get; } = new();
    public ObservableCollection<OptimizeActionItem> VisibleActions { get; } = new();

    /// <summary>0 Inicio · 1 Resultados · 2 Plan · 3 Confirmación · 4 Progreso · 5 Resultados finales</summary>
    [ObservableProperty] private int _wizardStep;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string? _progressText;
    [ObservableProperty] private string? _hardwareSummary;
    [ObservableProperty] private string _selectionSummary = "0 seleccionadas";
    [ObservableProperty] private string _confirmPreview = "";
    [ObservableProperty] private string _hardwareProfileText = "";
    [ObservableProperty] private string _aggressivenessLabel = "";
    [ObservableProperty] private string _systemStateSummary = "";
    [ObservableProperty] private string _healthBreakdown = "";
    [ObservableProperty] private string _filterCategory = "Todas";
    [ObservableProperty] private string _filterRisk = "Todas";
    [ObservableProperty] private string _searchText = "";
    /// <summary>all | safe | recommended | reboot — filtra la lista, no selecciona.</summary>
    [ObservableProperty] private string _listFilter = "all";
    [ObservableProperty] private bool _sortByGainDesc;
    [ObservableProperty] private bool _createRestorePoint;
    [ObservableProperty] private bool _isAnalyzing;
    [ObservableProperty] private bool _isApplying;
    [ObservableProperty] private bool _hasPlan;
    [ObservableProperty] private string _applyLog = "";
    [ObservableProperty] private string _currentActionTitle = "";
    [ObservableProperty] private string _currentActionDetail = "";
    [ObservableProperty] private string _resultHeadline = "";
    [ObservableProperty] private string _resultSubtitle = "";
    [ObservableProperty] private string _finalReport = "";
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private int _applyDoneCount;
    [ObservableProperty] private int _applyTotalCount;
    [ObservableProperty] private int _applyOkCount;
    [ObservableProperty] private int _applyFailCount;
    [ObservableProperty] private OptimizationResult? _lastResult;

    public bool IsStepStart => WizardStep == 0;
    public bool IsStepResults => WizardStep == 1;
    public bool IsStepPlan => WizardStep == 2;
    public bool IsStepConfirm => WizardStep == 3;
    public bool IsStepProgress => WizardStep == 4;
    public bool IsStepDone => WizardStep == 5;
    public bool ShowPageChrome => WizardStep is not (4 or 5);
    public bool HasApplyErrors => ApplyFailCount > 0;
    public string ApplyCountLabel => ApplyTotalCount <= 0
        ? ""
        : $"{ApplyDoneCount}/{ApplyTotalCount}";

    public OptimizeViewModel(ScanEngine scan, IOptimizationEngine opt, IHealthScorer health, IAppSettingsStore settings, DashboardViewModel home)
    {
        _scan = scan;
        _opt = opt;
        _health = health;
        _settings = settings;
        _home = home;
        UiLoc.Instance.PropertyChanged += (_, _) =>
        {
            // Los DisplayName del plan se congelan al analizar: al cambiar idioma hay que reanalizar.
            if (!IsApplying && HasPlan)
            {
                HasPlan = false;
                Actions.Clear();
                VisibleActions.Clear();
                WizardStep = 0;
                Status = UiLoc.Instance.T("Vm.AnalyzeFirst");
                ProgressText = Status;
            }
            UpdateSelectionSummary();
        };
    }

    partial void OnWizardStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsStepStart));
        OnPropertyChanged(nameof(IsStepResults));
        OnPropertyChanged(nameof(IsStepPlan));
        OnPropertyChanged(nameof(IsStepConfirm));
        OnPropertyChanged(nameof(IsStepProgress));
        OnPropertyChanged(nameof(IsStepDone));
        OnPropertyChanged(nameof(ShowPageChrome));
    }

    partial void OnApplyDoneCountChanged(int value) => OnPropertyChanged(nameof(ApplyCountLabel));
    partial void OnApplyTotalCountChanged(int value) => OnPropertyChanged(nameof(ApplyCountLabel));
    partial void OnApplyFailCountChanged(int value) => OnPropertyChanged(nameof(HasApplyErrors));

    private void OnActionItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OptimizeActionItem.IsSelected))
            UpdateSelectionSummary();
    }

    private async Task LoadPrefsAsync()
    {
        try { _prefs = await _settings.LoadProfileAsync(); }
        catch (Exception)
        {
            _prefs = new UserProfile();
        }
    }

    [RelayCommand]
    private async Task AnalyzeAsync() => await AnalyzeCoreAsync();

    public Task AnalyzeCoreAsync() => AnalyzeInternalAsync();

    private async Task AnalyzeInternalAsync()
    {
        if (IsAnalyzing || IsApplying) return;
        IsAnalyzing = true;
        LastResult = null;
        ApplyLog = "";
        try
        {
            WizardStep = 0;
            ProgressText = UiLoc.Instance.T("Vm.DeepScan");
            Status = ProgressText;
            // Dejar que WPF pinte IsAnalyzing / ProgressText antes del trabajo pesado
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);

            // Fast + caché: el Deep force:true congelaba el primer clic (WMI pesado en UI).
            var snap = await Task.Run(async () =>
                await _scan.GetSnapshotAsync(ScanDepth.Fast, force: false).ConfigureAwait(false)).ConfigureAwait(true);
            _lastSnapshot = snap;
            var (score, factors) = _health.Score(_lastSnapshot);
            _lastSnapshot.HealthScore = score;
            _lastSnapshot.HealthFactors = factors;
            HealthBreakdown = string.Join(Environment.NewLine,
                factors.Select(f => $"• {f.Name}: {f.Score}/100 (peso {f.Weight}) — {f.Detail}"));

            ProgressText = UiLoc.Instance.T("Vm.BuildingPlan");
            Status = ProgressText;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);

            await LoadPrefsAsync().ConfigureAwait(true);

            var planSnap = _lastSnapshot;
            _lastPlan = await Task.Run(async () =>
                await _opt.BuildPlanAsync(planSnap, beastMode: false).ConfigureAwait(false)).ConfigureAwait(true);

            foreach (var old in Actions)
                old.PropertyChanged -= OnActionItemPropertyChanged;
            Actions.Clear();
            foreach (var a in _lastPlan.Actions)
            {
                if (!_prefs.ShowIncompatibleActions && !a.IsCompatible) continue;
                if (_prefs.ShowSafeRecommendationsOnly &&
                    a.RiskLayer is not (OptimizationRiskLayer.Safe or OptimizationRiskLayer.Recommended) &&
                    a.Risk > RiskLevel.Low)
                    continue;
                if (!_prefs.ShowExperimentalActions && a.RiskLayer == OptimizationRiskLayer.Experimental)
                    continue;
                if (!_prefs.ShowAdvancedRecommendations && a.RiskLayer == OptimizationRiskLayer.Advanced)
                    continue;
                var item = new OptimizeActionItem(a, startSelected: false);
                item.PropertyChanged += OnActionItemPropertyChanged;
                Actions.Add(item);
            }

            CreateRestorePoint = _prefs.AutoRestorePointWhenNeeded;
            ListFilter = "all";
            SortByGainDesc = false;

            var form = _lastSnapshot.IsPortable == true
                ? UiLoc.Instance.T("Form.LaptopCap")
                : _lastSnapshot.IsPortable == false
                    ? UiLoc.Instance.T("Form.DesktopCap")
                    : "PC";
            HardwareSummary =
                $"{form} · {_lastSnapshot.Cpu.Name} · {_lastSnapshot.Gpu?.Name ?? NotDetected.Text} · " +
                UiLoc.Instance.T("Vm.HealthLine", _lastSnapshot.Memory.UsagePercent, score, _lastSnapshot.Os.Caption);
            HasPlan = Actions.Count > 0;
            SystemStateSummary = _lastPlan.SystemStateSummary;
            UpdateSelectionSummary();
            ApplyFilter();
            Status = _lastPlan.Summary;
            HardwareProfileText = _lastPlan.HardwareProfileText;
            AggressivenessLabel = _lastPlan.AggressivenessLabel;
            ProgressText = HasPlan
                ? UiLoc.Instance.T("Vm.PlanReady", Actions.Count)
                : UiLoc.Instance.T("Vm.NoActions");
            WizardStep = 1;
        }
        catch (Exception ex)
        {
            Status = UiLoc.Instance.T("Vm.AnalyzeError", ex.Message);
            ProgressText = Status;
            HasPlan = false;
            WizardStep = 0;
        }
        finally { IsAnalyzing = false; }
    }

    [RelayCommand]
    private void GoToPlan()
    {
        if (!HasPlan) return;
        WizardStep = 2;
        ApplyFilter();
    }

    [RelayCommand(CanExecute = nameof(CanGoToConfirm))]
    private void GoToConfirm()
    {
        var selected = Actions.Where(a => a.IsSelected).ToList();
        if (selected.Count == 0)
        {
            AetherDialog.Warn(UiLoc.Instance.T("Vm.Attention"), UiLoc.Instance.T("Vm.MarkOne"));
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine(UiLoc.Instance.T("Optimize.ConfirmSimple", selected.Count));
        sb.AppendLine();
        foreach (var a in selected.Take(16))
            sb.AppendLine($"• {a.DisplayName}");
        if (selected.Count > 16)
            sb.AppendLine(UiLoc.Instance.T("Beast.ConfirmMore", selected.Count - 16));
        sb.AppendLine();
        var permanent = selected.Count(a =>
            !a.IsTemporary &&
            a.Action.Id is not ("net.flushdns" or "cleanup.temp" or "cleanup.advanced"));
        CreateRestorePoint = permanent > 0;
        sb.AppendLine(CreateRestorePoint
            ? UiLoc.Instance.T("Optimize.ConfirmRpYes")
            : UiLoc.Instance.T("Optimize.ConfirmRpNo"));
        ConfirmPreview = sb.ToString();
        WizardStep = 3;
    }

    [RelayCommand]
    private void BackToPlan() => WizardStep = 2;

    [RelayCommand]
    private void BackToStart() => WizardStep = HasPlan ? 1 : 0;

    [RelayCommand]
    private void SetListFilter(string? f)
    {
        ListFilter = string.IsNullOrWhiteSpace(f) ? "all" : f.Trim().ToLowerInvariant();
        ApplyFilter();
    }

    [RelayCommand]
    private void ToggleSortByGain()
    {
        SortByGainDesc = !SortByGainDesc;
    }

    [RelayCommand]
    private void SelectVisible()
    {
        var selectable = VisibleActions.Where(a => a.CanSelect).ToList();
        if (selectable.Count == 0) return;
        // Segundo clic: desmarcar todo lo visible.
        var allOn = selectable.All(a => a.IsSelected);
        foreach (var a in selectable)
            a.IsSelected = !allOn;
        UpdateSelectionSummary();
    }

    [RelayCommand]
    private void ApplyFilter()
    {
        IEnumerable<OptimizeActionItem> q = Actions;
        q = ListFilter switch
        {
            "safe" => q.Where(a =>
                a.Action.RiskLayer == OptimizationRiskLayer.Safe || a.Action.Risk == RiskLevel.Low),
            "recommended" => q.Where(a =>
                a.Action.IsRecommendedDefault ||
                a.Action.RiskLayer is OptimizationRiskLayer.Safe or OptimizationRiskLayer.Recommended),
            "reboot" => q.Where(a => a.NeedsReboot),
            _ => q
        };
        if (!string.IsNullOrWhiteSpace(FilterCategory) && FilterCategory != "Todas")
            q = q.Where(a => a.Category.Equals(FilterCategory, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(FilterRisk) && FilterRisk != "Todas")
            q = q.Where(a => a.RiskLabel.Equals(FilterRisk, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(SearchText))
            q = q.Where(a => a.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                             a.SimpleWhat.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        if (!_prefs.ShowIncompatibleActions)
            q = q.Where(a => a.CanSelect);

        var next = (SortByGainDesc
            ? q.OrderByDescending(a => a.GainPercent).ThenBy(a => a.DisplayName)
            : q).ToList();
        if (VisibleActions.Count == next.Count &&
            VisibleActions.Zip(next, (a, b) => ReferenceEquals(a, b)).All(eq => eq))
            return;

        VisibleActions.Clear();
        foreach (var a in next)
            VisibleActions.Add(a);
    }

    /// <summary>Umbral: tercio superior de +est. del plan (mín. 6).</summary>
    internal static int HighGainThreshold(IReadOnlyList<OptimizeActionItem> actions)
    {
        if (actions.Count == 0) return 6;
        var ordered = actions.Select(a => a.GainPercent).OrderByDescending(g => g).ToList();
        var idx = Math.Clamp(ordered.Count / 3, 0, ordered.Count - 1);
        return Math.Max(6, ordered[idx]);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnFilterCategoryChanged(string value) => ApplyFilter();
    partial void OnFilterRiskChanged(string value) => ApplyFilter();
    partial void OnListFilterChanged(string value) => ApplyFilter();
    partial void OnSortByGainDescChanged(bool value) => ApplyFilter();

    [RelayCommand(CanExecute = nameof(CanApplySelected))]
    private async Task ApplySelectedAsync() => await ApplyCoreAsync();

    private bool CanApplySelected() =>
        !IsApplying && !IsAnalyzing && HasPlan && Actions.Any(a => a.IsSelected);

    private bool CanGoToConfirm() =>
        !IsApplying && !IsAnalyzing && HasPlan && Actions.Any(a => a.IsSelected);

    [RelayCommand(CanExecute = nameof(CanCancelApply))]
    private void CancelApply()
    {
        try { _applyCts?.Cancel(); }
        catch (ObjectDisposedException) { /* cts disposed */ }
    }

    private bool CanCancelApply() => IsApplying;

    partial void OnIsApplyingChanged(bool value)
    {
        ApplySelectedCommand.NotifyCanExecuteChanged();
        CancelApplyCommand.NotifyCanExecuteChanged();
        GoToConfirmCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsAnalyzingChanged(bool value)
    {
        ApplySelectedCommand.NotifyCanExecuteChanged();
        GoToConfirmCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasPlanChanged(bool value)
    {
        ApplySelectedCommand.NotifyCanExecuteChanged();
        GoToConfirmCommand.NotifyCanExecuteChanged();
    }

    public async Task ApplyCoreAsync()
    {
        if (IsApplying) return;
        if (_lastSnapshot is null || !HasPlan)
        {
            Status = UiLoc.Instance.T("Vm.AnalyzeFirst");
            ProgressText = Status;
            AetherDialog.Info("AetherPC", Status);
            return;
        }

        if (WizardStep < 3)
            GoToConfirm();

        if (WizardStep == 3)
        {
            await LoadPrefsAsync().ConfigureAwait(true);
            if (_prefs.ConfirmBeforeApply)
            {
                if (!AetherDialog.Confirm(
                        UiLoc.Instance.T("Optimize.ConfirmTitle", Actions.Count(a => a.IsSelected)),
                        ConfirmPreview,
                        UiLoc.Instance.T("Optimize.ApplyYes"),
                        UiLoc.Instance.T("Common.Cancel")))
                {
                    Status = UiLoc.Instance.T("Vm.Cancelled");
                    return;
                }
            }
        }

        if (WizardStep is not (3 or 4))
            return;

        if (!ElevationGate.EnsureAdminOrWarn())
        {
            Status = UiLoc.Instance.T("Vm.NeedAdmin");
            ProgressText = Status;
            return;
        }

        var selectedIds = Actions.Where(a => a.IsSelected)
            .Select(a => a.Action.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var selected = selectedIds
            .Select(id => Actions.First(a => a.Action.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (selected.Count == 0)
        {
            AetherDialog.Warn(UiLoc.Instance.T("Vm.Attention"), UiLoc.Instance.T("Vm.MarkApply"));
            return;
        }

        foreach (var item in Actions)
        {
            var on = selectedIds.Contains(item.Action.Id, StringComparer.OrdinalIgnoreCase);
            item.IsSelected = on;
            item.Action.IsSelected = on;
        }

        IsApplying = true;
        WizardStep = 4;
        ApplyLog = "";
        ProgressPercent = 2;
        ApplyDoneCount = 0;
        ApplyTotalCount = selected.Count;
        ApplyOkCount = 0;
        ApplyFailCount = 0;
        if (!_prefs.AutoRestorePointWhenNeeded)
            CreateRestorePoint = false;
        CurrentActionTitle = UiLoc.Instance.T("Optimize.ApplyingTitle");
        CurrentActionDetail = UiLoc.Instance.T("Optimize.ApplyingPrep");
        ProgressText = UiLoc.Instance.T("Vm.ApplyingN", selected.Count);
        Status = ProgressText;
        _applyCts = new CancellationTokenSource();

        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
        await Task.Delay(40).ConfigureAwait(true);

        try
        {
            foreach (var s in selected)
                s.Action.IsSelected = true;

            var plan = new OptimizationPlan
            {
                Name = "Optimización seleccionada",
                Summary = $"Aplicar {selected.Count} acciones elegidas",
                RestorePointRequested = CreateRestorePoint,
                CreateRestorePoint = CreateRestorePoint,
                PlanKind = OptimizationPlanKind.Standard,
                Actions = selected.Select(s =>
                {
                    s.Action.IsSelected = true;
                    return s.Action;
                }).ToList()
            };

            var lastUiTick = 0L;
            var typedProgress = new Progress<OptimizationProgress>(p =>
            {
                var now = Environment.TickCount64;
                var isLast = p.Index >= p.Total && p.Total > 0;
                if (!isLast && now - lastUiTick < 80) return;
                lastUiTick = now;

                ApplyDoneCount = Math.Clamp(p.Index, 0, p.Total);
                ApplyTotalCount = p.Total > 0 ? p.Total : selected.Count;
                CurrentActionTitle = string.IsNullOrWhiteSpace(p.DisplayName)
                    ? UiLoc.Instance.T("Optimize.ApplyingTitle")
                    : p.DisplayName;
                CurrentActionDetail = string.IsNullOrWhiteSpace(p.Detail)
                    ? (p.Phase ?? "")
                    : $"{p.Phase}: {p.Detail}";
                ProgressText = $"[{p.Index}/{p.Total}] {CurrentActionTitle}";
                Status = ProgressText;
                if (p.Total > 0)
                    ProgressPercent = Math.Min(99, 100.0 * p.Index / Math.Max(1, p.Total));
            });

            var snap = _lastSnapshot;
            var token = _applyCts.Token;
            LastResult = await Task.Run(async () =>
                await _opt.ExecutePlanAsync(plan, selectedOnly: true, typedProgress, snap, token).ConfigureAwait(false)
            ).ConfigureAwait(true);

            ProgressPercent = 100;
            ApplyDoneCount = ApplyTotalCount;

            var results = LastResult.ActionResults
                .Where(r => selectedIds.Any(id => ActionOutcomeUi.Match(r.ActionId, id))
                            || r.ActionId.Equals("backup.restorepoint", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var item in Actions)
            {
                var on = selectedIds.Contains(item.Action.Id, StringComparer.OrdinalIgnoreCase);
                item.IsSelected = on;
                item.Action.IsSelected = on;
                var r = results.FirstOrDefault(x => ActionOutcomeUi.Match(x.ActionId, item.Action.Id));
                if (r is null)
                {
                    if (!on) item.LastResult = null;
                    continue;
                }
                item.LastResult = ActionOutcomeUi.Format(r);
            }

            var failItems = results.Where(ActionOutcomeUi.IsHardFail).ToList();
            ApplyOkCount = results.Count(r => r.Success && r.Status != ActionApplyStatus.Skipped
                                             && !r.ActionId.Equals("backup.restorepoint", StringComparison.OrdinalIgnoreCase));
            ApplyFailCount = failItems.Count;
            Status = failItems.Count == 0
                ? $"✓ Completado: {ApplyOkCount}/{selected.Count} ok"
                : $"✗ {ApplyOkCount} ok / {failItems.Count} errores — de {selected.Count}";
            ProgressText = Status;
            FinalReport = string.IsNullOrWhiteSpace(LastResult.Message)
                ? Status
                : LastResult.Message;

            if (failItems.Count == 0)
            {
                ResultHeadline = UiLoc.Instance.T("Optimize.DoneOk");
                ResultSubtitle = UiLoc.Instance.T("Optimize.DoneOkSub", ApplyOkCount);
            }
            else
            {
                var why = string.Join(" · ", failItems.Take(2).Select(r => TruncateDetail(r.Detail)));
                ResultHeadline = UiLoc.Instance.T("Optimize.DoneErr");
                ResultSubtitle = UiLoc.Instance.T("Optimize.DoneErrSub", ApplyOkCount, ApplyFailCount)
                                + (string.IsNullOrWhiteSpace(why) ? "" : "\n" + why);
            }

            // Primero la pantalla de resultado; el popup solo si no hay resumen en página.
            WizardStep = _prefs.ShowFinishSummary ? 5 : 2;
            if (_prefs.NotifyOnOptimizeDone && !_prefs.ShowFinishSummary)
            {
                if (failItems.Count == 0)
                    AetherDialog.Success(ResultHeadline, ResultSubtitle);
                else
                    AetherDialog.Warn(ResultHeadline, ResultSubtitle);
            }

            if (_prefs.NotifyRestartPending && selected.Any(a => a.NeedsReboot) && failItems.Count == 0)
                AetherDialog.Warn(UiLoc.Instance.T("Notify.RestartTitle"), UiLoc.Instance.T("Notify.RestartBody"));

            _scan.InvalidateCache();
            _ = _home.RefreshAfterOptimizationsAsync();
        }
        catch (OperationCanceledException)
        {
            Status = UiLoc.Instance.T("Vm.Stopped");
            ProgressText = Status;
            ResultHeadline = "";
            ResultSubtitle = "";
            FinalReport = Status;
            // Volver al plan para poder reaplicar sin quedar pegado en resultados.
            WizardStep = 2;
        }
        catch (Exception ex)
        {
            Status = UiLoc.Instance.T("Vm.ApplyError", ex.Message);
            ProgressText = Status;
            ResultHeadline = UiLoc.Instance.T("Vm.ApplyErrorTitle");
            ResultSubtitle = ex.Message;
            FinalReport = ex.Message;
            WizardStep = 5;
            AetherDialog.Error(UiLoc.Instance.T("Vm.ApplyErrorTitle"), UiLoc.Instance.T("Vm.ApplyErrorBody", ex.Message));
        }
        finally
        {
            IsApplying = false;
            _applyCts?.Dispose();
            _applyCts = null;
        }
    }

    private static string TruncateDetail(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "(sin detalle)";
        s = s.Trim();
        return s.Length <= 140 ? s : s[..137] + "…";
    }

    private void UpdateSelectionSummary()
    {
        var selected = Actions.Where(a => a.IsSelected).ToList();
        var n = selected.Count;
        var gain = selected.Sum(a => a.GainPercent);
        var shown = Math.Min(gain, 45);
        var reboot = selected.Count(a => a.NeedsReboot);
        var admin = selected.Count(a => a.NeedsAdmin);
        SelectionSummary = n == 0
            ? "0 seleccionadas — marca casillas (solo esas se aplicarán)"
            : $"{n} seleccionadas · se aplicarán solo estas {n}" +
              (admin > 0 ? $" · {admin} admin" : "") +
              (reboot > 0 ? $" · {reboot} reinicio" : "") +
              $" · ~+{shown}% est.";
        ApplySelectedCommand.NotifyCanExecuteChanged();
        GoToConfirmCommand.NotifyCanExecuteChanged();
    }
}

public partial class BeastModeViewModel : ObservableObject
{
    private readonly ScanEngine _scan;
    private readonly IOptimizationEngine _opt;
    private readonly IAppSettingsStore _settings;
    private readonly IHistoryStore _history;
    private readonly DashboardViewModel _home;
    private SystemSnapshot? _lastSnapshot;
    private UserProfile _prefs = new();
    private CancellationTokenSource? _applyCts;
    public ObservableCollection<OptimizeActionItem> Actions { get; } = new();
    /// <summary>Lista filtrada estable (mismo ItemsSource; evita rebind saltado).</summary>
    public ObservableCollection<OptimizeActionItem> VisibleActions { get; } = new();
    [ObservableProperty] private OptimizationPlan? _plan;
    [ObservableProperty] private OptimizationResult? _result;
    [ObservableProperty] private string _progressText = UiLoc.Instance.T("Beast.PressAnalyzeHint");
    [ObservableProperty] private string _selectionSummary = "";
    [ObservableProperty] private string _planStats = "";
    [ObservableProperty] private string _profileBlurb = "";
    [ObservableProperty] private string _finalReport = "";
    [ObservableProperty] private string _currentActionTitle = "";
    [ObservableProperty] private string _currentActionDetail = "";
    [ObservableProperty] private string _resultHeadline = "";
    [ObservableProperty] private string _resultSubtitle = "";
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private int _applyDoneCount;
    [ObservableProperty] private int _applyTotalCount;
    [ObservableProperty] private int _applyOkCount;
    [ObservableProperty] private int _applyFailCount;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isAnalyzing;
    [ObservableProperty] private bool _hasPlan;
    [ObservableProperty] private bool _createRestorePoint;
    [ObservableProperty] private bool _sortByGainDesc;
    /// <summary>0 plan · 1 aplicando · 2 resultado</summary>
    [ObservableProperty] private int _uiPhase;
    [ObservableProperty] private string _filter = "all"; // all|critical|recommended|optional|advanced|gain

    public bool IsPlanPhase => UiPhase == 0;
    public bool IsApplyingPhase => UiPhase == 1;
    public bool IsDonePhase => UiPhase == 2;
    public bool HasApplyErrors => ApplyFailCount > 0;
    public bool HasActiveBeastSession => ActiveBeastHistoryId is Guid id && id != Guid.Empty;
    public string ApplyCountLabel => ApplyTotalCount <= 0
        ? ""
        : $"{ApplyDoneCount}/{ApplyTotalCount}";
    public string BeastSessionLabel => HasActiveBeastSession
        ? UiLoc.Instance.T("Beast.SessionActive")
        : "";

    [ObservableProperty] private Guid? _activeBeastHistoryId;

    public BeastModeViewModel(ScanEngine scan, IOptimizationEngine opt, IAppSettingsStore settings, IHistoryStore history, DashboardViewModel home)
    {
        _scan = scan;
        _opt = opt;
        _settings = settings;
        _history = history;
        _home = home;
        UiLoc.Instance.PropertyChanged += (_, _) =>
        {
            if (!IsRunning && HasPlan)
            {
                HasPlan = false;
                Plan = null;
                Actions.Clear();
                VisibleActions.Clear();
                UiPhase = 0;
                ProgressText = UiLoc.Instance.T("Beast.PressAnalyzeHint");
            }
            OnPropertyChanged(nameof(BeastSessionLabel));
            UpdateSelection();
        };
        _ = LoadSessionFromPrefsAsync();
    }

    partial void OnUiPhaseChanged(int value)
    {
        OnPropertyChanged(nameof(IsPlanPhase));
        OnPropertyChanged(nameof(IsApplyingPhase));
        OnPropertyChanged(nameof(IsDonePhase));
    }

    partial void OnApplyDoneCountChanged(int value) => OnPropertyChanged(nameof(ApplyCountLabel));
    partial void OnApplyTotalCountChanged(int value) => OnPropertyChanged(nameof(ApplyCountLabel));
    partial void OnApplyFailCountChanged(int value) => OnPropertyChanged(nameof(HasApplyErrors));
    partial void OnActiveBeastHistoryIdChanged(Guid? value)
    {
        OnPropertyChanged(nameof(HasActiveBeastSession));
        OnPropertyChanged(nameof(BeastSessionLabel));
    }

    private void OnActionItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OptimizeActionItem.IsSelected))
            UpdateSelection();
    }

    private async Task LoadPrefsAsync()
    {
        try { _prefs = await _settings.LoadProfileAsync(); }
        catch (Exception)
        {
            _prefs = new UserProfile();
        }
    }

    private async Task LoadSessionFromPrefsAsync()
    {
        await LoadPrefsAsync();
        var id = _prefs.ActiveBeastSessionHistoryId;
        if (id is null || id == Guid.Empty)
        {
            ActiveBeastHistoryId = null;
            return;
        }

        // Solo mostrar banner si hay un plan Bestia real aplicado y reversible en historial.
        try
        {
            var entry = await _history.GetAsync(id.Value).ConfigureAwait(true);
            var valid = entry is not null
                        && entry.IsBeastKind
                        && entry.CanRollback
                        && !entry.RolledBack;
            if (!valid)
            {
                _prefs.ActiveBeastSessionHistoryId = null;
                _prefs.BeastSessionStartedAt = null;
                try { await _settings.SaveProfileAsync(_prefs); } catch { /* */ }
                ActiveBeastHistoryId = null;
                return;
            }
            ActiveBeastHistoryId = id;
        }
        catch
        {
            ActiveBeastHistoryId = null;
        }
    }

    private async Task PersistBeastSessionAsync(Guid? historyId)
    {
        await LoadPrefsAsync();
        _prefs.ActiveBeastSessionHistoryId = historyId;
        _prefs.BeastSessionStartedAt = historyId is null ? null : DateTimeOffset.Now;
        try { await _settings.SaveProfileAsync(_prefs); } catch { /* */ }
        ActiveBeastHistoryId = historyId;
    }

    /// <summary>Sincroniza banner de sesión si el rollback vino del Historial.</summary>
    public void ClearActiveSessionIf(Guid historyId)
    {
        if (ActiveBeastHistoryId == historyId)
            ActiveBeastHistoryId = null;
        if (_prefs.ActiveBeastSessionHistoryId == historyId)
        {
            _prefs.ActiveBeastSessionHistoryId = null;
            _prefs.BeastSessionStartedAt = null;
        }
    }

    [RelayCommand]
    private async Task RestoreBeastSessionAsync()
    {
        if (ActiveBeastHistoryId is not Guid id) return;
        if (!AetherDialog.Confirm(
                UiLoc.Instance.T("Beast.RestoreTitle"),
                UiLoc.Instance.T("Beast.RestoreBody"),
                UiLoc.Instance.T("Beast.RestoreYes"),
                UiLoc.Instance.T("Common.Cancel")))
            return;
        try
        {
            ProgressText = UiLoc.Instance.T("Beast.Restoring");
            var ok = await _opt.RollbackAsync(id);
            await PersistBeastSessionAsync(null);
            ProgressText = ok
                ? UiLoc.Instance.T("Beast.RestoreOk")
                : UiLoc.Instance.T("Beast.RestorePartial");
            if (ok) AetherDialog.Success(UiLoc.Instance.T("Beast.RestoreTitle"), ProgressText);
            else AetherDialog.Warn(UiLoc.Instance.T("Beast.RestoreTitle"), ProgressText);
        }
        catch (Exception ex)
        {
            AetherDialog.Error(UiLoc.Instance.T("Beast.RestoreTitle"), ex.Message);
        }
    }

    partial void OnFilterChanged(string value) => RefreshVisibleActions();

    [RelayCommand]
    private void BackToPlan()
    {
        if (IsRunning) return;
        UiPhase = 0;
        ProgressText = Plan?.Summary ?? ProgressText;
    }

    [RelayCommand]
    private void SetFilter(string? f)
    {
        var next = string.IsNullOrWhiteSpace(f) ? "all" : f;
        // Segundo clic en el mismo filtro: desmarcar lo visible / lo que ese filtro agrupa
        if (string.Equals(Filter, next, StringComparison.OrdinalIgnoreCase))
        {
            DeselectForFilter(next);
            return;
        }
        Filter = next;
    }

    [RelayCommand]
    private void SelectAllVisible()
    {
        var selectable = VisibleActions.Where(a => a.CanSelect).ToList();
        if (selectable.Count == 0) return;

        // Segundo clic: si ya están todas marcadas, desmarcar
        var allSelected = selectable.All(a => a.IsSelected);
        foreach (var a in selectable)
            a.IsSelected = !allSelected;
        UpdateSelection();
    }

    [RelayCommand]
    private void ToggleSortByGain()
    {
        SortByGainDesc = !SortByGainDesc;
        RefreshVisibleActions(force: true);
    }

    /// <summary>Desmarca las acciones del ámbito del filtro (doble activación del chip).</summary>
    private void DeselectForFilter(string filter)
    {
        foreach (var a in ActionsInFilter(filter))
        {
            if (!a.CanSelect) continue;
            a.IsSelected = false;
        }
        UpdateSelection();
    }

    private IEnumerable<OptimizeActionItem> ActionsInFilter(string filter)
    {
        IEnumerable<OptimizeActionItem> q = filter switch
        {
            "critical" => Actions.Where(a => a.PriorityTier == BeastPriorityTier.Critical),
            "recommended" => Actions.Where(a => a.PriorityTier is BeastPriorityTier.Critical or BeastPriorityTier.Recommended),
            "optional" => Actions.Where(a => a.PriorityTier == BeastPriorityTier.Optional),
            "advanced" => Actions.Where(a => a.PriorityTier == BeastPriorityTier.Advanced),
            "gain" => Actions.OrderByDescending(a => a.GainPercent).ThenBy(a => a.DisplayName),
            _ => Actions
        };
        if (!_prefs.ShowIncompatibleActions)
            q = q.Where(a => a.CanSelect);
        return q;
    }

    private void RefreshVisibleActions(bool force = false)
    {
        IEnumerable<OptimizeActionItem> q = ActionsInFilter(Filter);
        if (SortByGainDesc && !string.Equals(Filter, "gain", StringComparison.OrdinalIgnoreCase))
            q = q.OrderByDescending(a => a.GainPercent).ThenBy(a => a.DisplayName);
        var next = q.ToList();
        // Con orden por +est. hay que refrescar si cambia el orden aunque sean las mismas refs
        if (!force && VisibleActions.Count == next.Count &&
            VisibleActions.Zip(next, (a, b) => ReferenceEquals(a, b)).All(eq => eq))
            return;

        VisibleActions.Clear();
        foreach (var a in next)
            VisibleActions.Add(a);
    }

    [RelayCommand]
    private async Task AnalyzeAsync() => await AnalyzeCoreAsync();

    public async Task AnalyzeCoreAsync()
    {
        if (IsAnalyzing || IsRunning) return;
        IsAnalyzing = true;
        Result = null;
        FinalReport = "";
        UiPhase = 0;
        ProgressPercent = 0;
        ApplyDoneCount = 0;
        ApplyTotalCount = 0;
        try
        {
            ProgressText = UiLoc.Instance.T("Beast.DeepScan");
            ProgressPercent = 8;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);

            ProgressPercent = 20;
            _lastSnapshot = await Task.Run(async () =>
                await _scan.GetSnapshotAsync(ScanDepth.Fast, force: false).ConfigureAwait(false)).ConfigureAwait(true);
            ProgressPercent = 50;
            ProgressText = UiLoc.Instance.T("Beast.Building");
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            await LoadPrefsAsync().ConfigureAwait(true);

            var planSnap = _lastSnapshot;
            Plan = await Task.Run(async () =>
                await _opt.BuildPlanAsync(planSnap, beastMode: true).ConfigureAwait(false)).ConfigureAwait(true);
            ProgressPercent = 90;
            foreach (var old in Actions)
                old.PropertyChanged -= OnActionItemPropertyChanged;
            Actions.Clear();
            foreach (var a in Plan.Actions)
            {
                if (!_prefs.ShowIncompatibleActions && !a.IsCompatible) continue;
                if (_prefs.ShowSafeRecommendationsOnly &&
                    a.RiskLayer is not (OptimizationRiskLayer.Safe or OptimizationRiskLayer.Recommended) &&
                    a.Risk > RiskLevel.Low)
                    continue;
                if (!_prefs.ShowExperimentalActions && a.RiskLayer == OptimizationRiskLayer.Experimental)
                    continue;
                if (!_prefs.ShowAdvancedRecommendations && a.RiskLayer == OptimizationRiskLayer.Advanced)
                    continue;
                var item = new OptimizeActionItem(a, startSelected: a.IsSelected && a.IsCompatible);
                if (!a.IsCompatible) item.IsSelected = false;
                item.PropertyChanged += OnActionItemPropertyChanged;
                Actions.Add(item);
            }
            UpdateSelection();
            RefreshVisibleActions();
            HasPlan = Actions.Count > 0;
            ProfileBlurb = Plan.HardwareProfileText;
            PlanStats = UiLoc.Instance.T("Beast.Stats",
                Plan.CriticalCount, Plan.RecommendedCount, Plan.OptionalCount, Plan.AdvancedCount, Plan.PrimaryLimitation);
            ProgressPercent = 100;
            ProgressText = HasPlan
                ? Plan.Summary
                : UiLoc.Instance.T("Beast.NoActions");
            UiPhase = 0;
        }
        catch (Exception ex)
        {
            ProgressText = UiLoc.Instance.T("Vm.AnalyzeError", ex.Message);
            HasPlan = false;
        }
        finally { IsAnalyzing = false; }
    }

    [RelayCommand(CanExecute = nameof(CanConfirmAndExecute))]
    private async Task ConfirmAndExecuteAsync() => await ApplyCoreAsync();

    private bool CanConfirmAndExecute() =>
        !IsRunning && !IsAnalyzing && HasPlan && Actions.Any(a => a.IsSelected && a.IsCompatible);

    [RelayCommand(CanExecute = nameof(CanCancelApply))]
    private void CancelApply()
    {
        try { _applyCts?.Cancel(); }
        catch { /* */ }
    }

    private bool CanCancelApply() => IsRunning;

    partial void OnIsRunningChanged(bool value)
    {
        ConfirmAndExecuteCommand.NotifyCanExecuteChanged();
        CancelApplyCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsAnalyzingChanged(bool value) => ConfirmAndExecuteCommand.NotifyCanExecuteChanged();
    partial void OnHasPlanChanged(bool value) => ConfirmAndExecuteCommand.NotifyCanExecuteChanged();

    public async Task ApplyCoreAsync()
    {
        if (IsRunning) return;
        if (Plan is null || _lastSnapshot is null || !HasPlan)
        {
            ProgressText = UiLoc.Instance.T("Vm.AnalyzeFirst");
            AetherDialog.Info("AetherPC", ProgressText);
            return;
        }

        var selected = Actions.Where(a => a.IsSelected && a.IsCompatible).ToList();
        if (selected.Count == 0)
        {
            ProgressText = UiLoc.Instance.T("Beast.MarkCompat");
            AetherDialog.Warn(UiLoc.Instance.T("Vm.Attention"), ProgressText);
            return;
        }

        if (!ElevationGate.EnsureAdminOrWarn())
        {
            ProgressText = UiLoc.Instance.T("Vm.NeedAdmin");
            return;
        }

        var tempN = selected.Count(a => a.IsTemporary);
        var persistN = selected.Count(a =>
            !a.IsTemporary && a.Action.PersistenceType == ActionPersistenceType.PersistentReversible);
        var closeN = selected.Count(a =>
            a.Action.Id.StartsWith("process.close", StringComparison.OrdinalIgnoreCase));
        var visualN = selected.Count(a => a.AffectsVisuals);
        var rebootN = selected.Count(a => a.NeedsReboot);
        var msg = new StringBuilder();
        msg.AppendLine(UiLoc.Instance.T("Beast.ConfirmHeader", selected.Count));
        msg.AppendLine(UiLoc.Instance.T("Beast.ConfirmTemp", tempN));
        msg.AppendLine(UiLoc.Instance.T("Beast.ConfirmPersist", persistN));
        msg.AppendLine(UiLoc.Instance.T("Beast.ConfirmClose", closeN));
        msg.AppendLine(UiLoc.Instance.T("Beast.ConfirmVisual", visualN));
        msg.AppendLine(UiLoc.Instance.T("Beast.ConfirmRebootLine", rebootN));
        msg.AppendLine();
        msg.AppendLine(UiLoc.Instance.T("Beast.ConfirmProfile", Plan.AggressivenessLabel, Plan.PrimaryLimitation));
        if (!string.IsNullOrWhiteSpace(Plan.SystemStateSummary))
            msg.AppendLine(UiLoc.Instance.T("Beast.ConfirmWinState", Plan.SystemStateSummary));
        msg.AppendLine();
        foreach (var a in selected.Take(10))
            msg.AppendLine($"• [{a.PriorityLabel}] {a.DisplayName} ({a.PersistenceLabel})");
        if (selected.Count > 10) msg.AppendLine(UiLoc.Instance.T("Beast.ConfirmMore", selected.Count - 10));
        msg.AppendLine();
        msg.AppendLine(UiLoc.Instance.T("Beast.ConfirmFooter"));
        await LoadPrefsAsync().ConfigureAwait(true);
        if (_prefs.ConfirmBeforeApply)
        {
            if (!AetherDialog.Confirm(
                    UiLoc.Instance.T("Beast.ConfirmTitle", selected.Count),
                    msg.ToString(),
                    UiLoc.Instance.T("Beast.ApplyYes"),
                    UiLoc.Instance.T("Common.Cancel")))
            {
                ProgressText = UiLoc.Instance.T("Vm.Cancelled");
                return;
            }
        }

        // Congelar selección y pintar pantalla de aplicación YA (antes del trabajo pesado)
        var selectedIds = selected
            .Select(a => a.Action.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var item in Actions)
        {
            var on = selectedIds.Contains(item.Action.Id, StringComparer.OrdinalIgnoreCase);
            item.IsSelected = on;
            item.Action.IsSelected = on;
        }

        IsRunning = true;
        UiPhase = 1;
        ProgressPercent = 2;
        ApplyDoneCount = 0;
        ApplyTotalCount = selected.Count;
        ApplyOkCount = 0;
        ApplyFailCount = 0;
        CurrentActionTitle = UiLoc.Instance.T("Beast.ApplyingTitle");
        CurrentActionDetail = UiLoc.Instance.T("Beast.ApplyingPrep");
        ProgressText = UiLoc.Instance.T("Vm.ApplyingN", selected.Count);
        _applyCts = new CancellationTokenSource();

        // Dejar que WPF pinte la página «Aplicando…» antes de ejecutar
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
        await Task.Delay(40).ConfigureAwait(true);

        try
        {
            // Copia de seguridad solo si el usuario la marcó (opcional, como en Optimizar).
            var needsRp = CreateRestorePoint && selected.Any(a =>
                !a.Action.Id.StartsWith("process.", StringComparison.OrdinalIgnoreCase) &&
                a.Action.Id is not ("net.flushdns" or "cleanup.temp" or "cleanup.advanced"));

            var execPlan = new OptimizationPlan
            {
                Name = Plan.Name,
                Summary = UiLoc.Instance.T("Vm.PlanSummary", selectedIds.Count),
                PlanKind = OptimizationPlanKind.Beast,
                RestorePointRequested = needsRp,
                CreateRestorePoint = needsRp,
                Actions = selected.Select(a =>
                {
                    a.Action.IsSelected = true;
                    return a.Action;
                }).ToList(),
                EstimatedBytesRecovered = Plan.EstimatedBytesRecovered,
                PrimaryLimitation = Plan.PrimaryLimitation,
                HardwareProfileText = Plan.HardwareProfileText,
                AggressivenessLabel = Plan.AggressivenessLabel,
                SystemStateSummary = Plan.SystemStateSummary,
                CriticalCount = selected.Count(a => a.PriorityTier == BeastPriorityTier.Critical),
                RecommendedCount = selected.Count(a => a.PriorityTier == BeastPriorityTier.Recommended),
                OptionalCount = selected.Count(a => a.PriorityTier == BeastPriorityTier.Optional),
                AdvancedCount = selected.Count(a => a.PriorityTier == BeastPriorityTier.Advanced)
            };

            var lastUiTick = 0L;
            var typedProgress = new Progress<OptimizationProgress>(p =>
            {
                // Throttle UI: evita congelar al actualizar mil veces
                var now = Environment.TickCount64;
                var isLast = p.Index >= p.Total && p.Total > 0;
                if (!isLast && now - lastUiTick < 80) return;
                lastUiTick = now;

                ApplyDoneCount = Math.Clamp(p.Index, 0, p.Total);
                ApplyTotalCount = p.Total > 0 ? p.Total : selected.Count;
                CurrentActionTitle = string.IsNullOrWhiteSpace(p.DisplayName)
                    ? UiLoc.Instance.T("Beast.ApplyingTitle")
                    : p.DisplayName;
                CurrentActionDetail = string.IsNullOrWhiteSpace(p.Detail)
                    ? (p.Phase ?? "")
                    : $"{p.Phase}: {p.Detail}";
                ProgressText = $"[{p.Index}/{p.Total}] {CurrentActionTitle}";
                if (p.Total > 0)
                    ProgressPercent = Math.Min(99, 100.0 * p.Index / Math.Max(1, p.Total));
            });

            var snap = _lastSnapshot;
            var token = _applyCts.Token;
            Result = await Task.Run(async () =>
                await _opt.ExecutePlanAsync(execPlan, selectedOnly: true, typedProgress, snap, token).ConfigureAwait(false)
            ).ConfigureAwait(true);

            ProgressPercent = 100;
            ApplyDoneCount = ApplyTotalCount;

            foreach (var item in Actions)
            {
                var wasSelected = selectedIds.Contains(item.Action.Id, StringComparer.OrdinalIgnoreCase);
                item.IsSelected = wasSelected;
                item.Action.IsSelected = wasSelected;

                var r = Result.ActionResults.FirstOrDefault(x => ActionOutcomeUi.Match(x.ActionId, item.Action.Id));
                if (r is null)
                {
                    if (!wasSelected) item.LastResult = null;
                    continue;
                }
                item.LastResult = ActionOutcomeUi.Format(r);
            }
            UpdateSelection();

            var scoped = Result.ActionResults
                .Where(r => selectedIds.Any(id => ActionOutcomeUi.Match(r.ActionId, id)))
                .ToList();
            ApplyOkCount = scoped.Count(r => r.Success && r.Status != ActionApplyStatus.Skipped);
            ApplyFailCount = scoped.Count(ActionOutcomeUi.IsHardFail);

            FinalReport = string.IsNullOrWhiteSpace(Result.ProfessionalReport)
                ? Result.Message
                : Result.ProfessionalReport;
            ProgressText = Result.Message;

            if (ApplyOkCount > 0 && Result?.HistoryId is Guid hid)
                await PersistBeastSessionAsync(hid).ConfigureAwait(true);

            if (ApplyFailCount == 0)
            {
                ResultHeadline = UiLoc.Instance.T("Beast.DoneOk");
                ResultSubtitle = UiLoc.Instance.T("Beast.DoneOkSub", ApplyOkCount);
            }
            else
            {
                var fails = scoped.Where(ActionOutcomeUi.IsHardFail).Take(2).ToList();
                var why = string.Join(" · ", fails.Select(r =>
                {
                    var d = (r.Detail ?? "").Trim();
                    return d.Length <= 120 ? d : d[..117] + "…";
                }));
                ResultHeadline = UiLoc.Instance.T("Beast.DoneErr");
                ResultSubtitle = UiLoc.Instance.T("Beast.DoneErrSub", ApplyOkCount, ApplyFailCount)
                                + (string.IsNullOrWhiteSpace(why) ? "" : "\n" + why);
            }

            // Siempre hay overlay de resultado: no abrir popup encima (quedaba descentrado).
            UiPhase = 2;

            if (_prefs.NotifyRestartPending && selected.Any(a => a.NeedsReboot) && ApplyFailCount == 0)
                AetherDialog.Warn(UiLoc.Instance.T("Notify.RestartTitle"), UiLoc.Instance.T("Notify.RestartBody"));

            _scan.InvalidateCache();
            _ = _home.RefreshAfterOptimizationsAsync();
        }
        catch (OperationCanceledException)
        {
            ProgressText = UiLoc.Instance.T("Vm.Stopped");
            ResultHeadline = "";
            ResultSubtitle = "";
            FinalReport = ProgressText;
            UiPhase = 0;
        }
        catch (Exception ex)
        {
            ProgressText = UiLoc.Instance.T("Vm.ApplyError", ex.Message);
            ResultHeadline = UiLoc.Instance.T("Vm.ApplyErrorTitle");
            ResultSubtitle = ex.Message;
            FinalReport = ex.Message;
            UiPhase = 2;
            AetherDialog.Error(UiLoc.Instance.T("Vm.ApplyErrorTitle"), UiLoc.Instance.T("Vm.ApplyErrorBody", ex.Message));
        }
        finally
        {
            IsRunning = false;
            _applyCts?.Dispose();
            _applyCts = null;
        }
    }

    private void UpdateSelection()
    {
        var selected = Actions.Where(a => a.IsSelected && a.IsCompatible).ToList();
        var n = selected.Count;
        var reboot = selected.Count(a => a.NeedsReboot);
        var admin = selected.Count(a => a.NeedsAdmin);
        var visual = selected.Count(a => a.AffectsVisuals);
        var temp = selected.Count(a => a.IsTemporary);
        var secs = selected.Sum(a => (int)a.Action.EstimatedDuration.TotalSeconds);
        SelectionSummary = n == 0
            ? UiLoc.Instance.T("Beast.SelectHint")
            : UiLoc.Instance.T("Beast.SelectSummary", n, secs, admin, reboot, visual, temp);
        ConfirmAndExecuteCommand.NotifyCanExecuteChanged();
    }
}


public partial class CleanupCandidateItem : ObservableObject
{
    public CleanupCandidate Candidate { get; }
    [ObservableProperty] private bool _isSelected = true;
    public string DisplayName => Candidate.DisplayName;
    public string Path => Candidate.Path;
    public string Reason => Candidate.Reason;
    public string SizeLabel => Candidate.EstimatedBytes > 0
        ? $"~{Candidate.EstimatedBytes / (1024.0 * 1024):F1} MB"
        : UiLoc.Instance.T("Cleanup.VariableSize");
    public string Id => Candidate.Id;

    public CleanupCandidateItem(CleanupCandidate c) => Candidate = c;
}

public partial class CleanupViewModel : ObservableObject
{
    private readonly ICleanupService _cleanup;
    public ObservableCollection<CleanupCandidateItem> Candidates { get; } = new();
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _lastResult = "";
    [ObservableProperty] private bool _isBusy;

    public CleanupViewModel(ICleanupService cleanup)
    {
        _cleanup = cleanup;
        // Carga en Loaded de la vista (evita crash al navegar)
    }

    [RelayCommand]
    private async Task ScanAsync() => await ScanCoreAsync();

    public async Task ScanCoreAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            Status = UiLoc.Instance.T("Cleanup.Scanning");
            Candidates.Clear();
            foreach (var c in await _cleanup.ScanAsync())
                Candidates.Add(new CleanupCandidateItem(c)
                {
                    IsSelected = c.Id is "temp.user" or "temp.windows"
                });
            Status = UiLoc.Instance.T("Cleanup.ScanOk", Candidates.Count,
                Candidates.Sum(c => c.Candidate.EstimatedBytes) / (1024.0 * 1024));
        }
        catch (Exception ex)
        {
            Status = UiLoc.Instance.T("Cleanup.ScanErr", ex.Message);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task CleanSafeAsync() => await CleanCoreAsync(safeOnly: true);

    [RelayCommand]
    private async Task CleanSelectedAsync() => await CleanCoreAsync(safeOnly: false);

    public async Task CleanCoreAsync(bool safeOnly)
    {
        if (IsBusy) return;
        // «Solo temporales» = temp usuario + Windows Temp (ya no duplica LocalAppData)
        var ids = safeOnly
            ? new[] { "temp.user", "temp.windows" }
            : Candidates.Where(c => c.IsSelected).Select(c => c.Id).ToArray();

        if (ids.Length == 0)
        {
            Status = UiLoc.Instance.T("Cleanup.MarkOne");
            AetherDialog.Info(UiLoc.Instance.T("Cleanup.Title"), Status);
            return;
        }

        var labels = safeOnly
            ? UiLoc.Instance.T("Cleanup.SafeLabelsList")
            : string.Join(", ", Candidates.Where(c => c.IsSelected).Select(c => c.DisplayName));

        if (!AetherDialog.Confirm(
                UiLoc.Instance.T("Cleanup.ConfirmTitle"),
                safeOnly
                    ? UiLoc.Instance.T("Cleanup.ConfirmSafe")
                    : UiLoc.Instance.T("Cleanup.ConfirmSelected", labels),
                UiLoc.Instance.T("Cleanup.ConfirmYes"),
                UiLoc.Instance.T("Common.Cancel")))
            return;

        IsBusy = true;
        Status = UiLoc.Instance.T("Cleanup.Working");
        try
        {
            var result = await _cleanup.CleanAsync(ids);
            var freedMb = (result.BytesFreed ?? 0) / (1024.0 * 1024);
            var freedText = UiLoc.Instance.T("Exec.FreedMb", freedMb);
            var hasSkipped = result.SkippedCount > 0;

            LastResult = hasSkipped
                ? $"{freedText} · {UiLoc.Instance.T("Cleanup.DonePartialHint")}"
                : freedText;
            Status = UiLoc.Instance.T("Cleanup.DoneTitle");

            // Sólo se avisa de errores reales (excepciones) más abajo; los archivos en uso
            // son un resultado normal y se muestran como pista secundaria, no como fallo.
            var body = hasSkipped
                ? $"{freedText}\n\n{UiLoc.Instance.T("Cleanup.DonePartialHint")}"
                : freedText;
            AetherDialog.Info(UiLoc.Instance.T("Cleanup.DoneTitle"), body);
            await ScanCoreAsync();
        }
        catch (Exception ex)
        {
            Status = UiLoc.Instance.T("Cleanup.Error", ex.Message);
            AetherDialog.Error(UiLoc.Instance.T("Cleanup.ErrorTitle"), UiLoc.Instance.T("Cleanup.ErrorBody", ex.Message));
        }
        finally { IsBusy = false; }
    }
}

public partial class SecurityViewModel : ObservableObject
{
    private readonly ScanEngine _scan;
    private readonly IAppSettingsStore _settings;

    [ObservableProperty] private SecurityInfo? _info;
    [ObservableProperty] private int _score;
    [ObservableProperty] private string _scoreLabel = "";
    [ObservableProperty] private SecurityTone _scoreTone = SecurityTone.Unknown;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _risksTitle = "";
    [ObservableProperty] private bool _hasRisks;
    [ObservableProperty] private bool _hasHistory;
    [ObservableProperty] private bool _isBusy;

    public ObservableCollection<SecurityCardItem> Cards { get; } = new();
    public ObservableCollection<string> Risks { get; } = new();
    public ObservableCollection<string> Recommendations { get; } = new();
    public ObservableCollection<SecurityScoreSample> History { get; } = new();
    public ObservableCollection<KeyValueItem> DeviceInfo { get; } = new();

    public Brush ScoreBrush => ScoreTone switch
    {
        SecurityTone.Ok => SecurityBrushes.Ok,
        SecurityTone.Warn => SecurityBrushes.Warn,
        SecurityTone.Bad => SecurityBrushes.Bad,
        SecurityTone.Info => SecurityBrushes.Info,
        _ => SecurityBrushes.Muted
    };

    public SecurityViewModel(ScanEngine scan, IAppSettingsStore settings)
    {
        _scan = scan;
        _settings = settings;
        UiLoc.Instance.PropertyChanged += (_, _) =>
        {
            if (Info is not null)
                RunOnUi(() => RebuildUi(Info, skipHistory: true));
        };
    }

    partial void OnScoreToneChanged(SecurityTone value) => OnPropertyChanged(nameof(ScoreBrush));

    [RelayCommand]
    private async Task LoadAsync() => await LoadCoreAsync(forceRefresh: true);

    public Task LoadCoreAsync() => LoadCoreAsync(forceRefresh: false);

    public async Task LoadCoreAsync(bool forceRefresh)
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = UiLoc.Instance.T("Security.Status.Reading");
        try
        {
            SecurityInfo sec;
            OsInfo os;
            try
            {
                sec = await _scan.RefreshSecurityAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                sec = new SecurityInfo { Source = "error:" + ex.Message };
            }

            try
            {
                var snap = await _scan.GetSnapshotAsync(ScanDepth.Fast, force: false).ConfigureAwait(true);
                snap.Security = sec;
                os = snap.Os ?? new OsInfo();
            }
            catch
            {
                os = new OsInfo();
            }

            Info = sec;
            await RebuildUiAsync(sec, os).ConfigureAwait(true);
            Status = UiLoc.Instance.T("Security.Status.Ready", sec.ReadAt.ToLocalTime().ToString("g"));
            _ = forceRefresh;
        }
        catch (Exception ex)
        {
            Status = UiLoc.Instance.T("Security.Status.Error", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RebuildUiAsync(SecurityInfo sec, OsInfo os)
    {
        RunOnUi(() =>
        {
            RebuildUi(sec, skipHistory: false);
            DeviceInfo.Clear();
            foreach (var (label, value) in SecurityDashboardBuilder.BuildOsRows(os, sec))
                DeviceInfo.Add(new KeyValueItem(label, value));
        });

        try
        {
            var profile = await _settings.LoadProfileAsync().ConfigureAwait(true);
            profile.SecurityScoreHistory ??= new List<SecurityScoreSample>();
            var last = profile.SecurityScoreHistory.LastOrDefault();
            if (last is null || last.Score != Score || (DateTimeOffset.Now - last.At).TotalMinutes > 10)
            {
                profile.SecurityScoreHistory.Add(new SecurityScoreSample
                {
                    At = DateTimeOffset.Now,
                    Score = Score,
                    Label = ScoreLabel
                });
                while (profile.SecurityScoreHistory.Count > 12)
                    profile.SecurityScoreHistory.RemoveAt(0);
                await _settings.SaveProfileAsync(profile).ConfigureAwait(true);
            }

            RunOnUi(() =>
            {
                History.Clear();
                foreach (var h in profile.SecurityScoreHistory.AsEnumerable().Reverse().Take(6))
                    History.Add(h);
                HasHistory = History.Count > 0;
            });
        }
        catch { /* historial opcional */ }
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.Invoke(action);
    }

    private void RebuildUi(SecurityInfo sec, bool skipHistory)
    {
        var (score, label, tone) = SecurityDashboardBuilder.Score(sec);
        Score = score;
        ScoreLabel = label;
        ScoreTone = tone;

        Cards.Clear();
        foreach (var c in SecurityDashboardBuilder.BuildCards(sec))
            Cards.Add(c);

        Risks.Clear();
        foreach (var r in SecurityDashboardBuilder.BuildRisks(sec))
            Risks.Add(r);
        HasRisks = Risks.Count > 0;
        RisksTitle = HasRisks
            ? UiLoc.Instance.T("Security.Risks.Detected")
            : UiLoc.Instance.T("Security.Risks.None");

        Recommendations.Clear();
        foreach (var r in SecurityDashboardBuilder.BuildRecommendations(sec))
            Recommendations.Add(r);

        if (!skipHistory) { /* history handled async */ }
    }

    [RelayCommand]
    private void OpenWindowsSecurity() => OpenUri("windowsdefender:", "ms-settings:windowsdefender");

    [RelayCommand]
    private void OpenFirewall()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WF.msc"),
                UseShellExecute = true
            });
        }
        catch (Exception ex) { AetherDialog.Error(UiLoc.Instance.T("Dialog.Error"), ex.Message); }
    }

    [RelayCommand]
    private void OpenBitLocker() => OpenUri("ms-settings:deviceencryption", null);

    [RelayCommand]
    private void OpenWindowsUpdate() => OpenUri("ms-settings:windowsupdate", null);

    [RelayCommand]
    private void OpenCoreIsolation() => OpenUri("windowsdefender://coreisolation", "ms-settings:windowsdefender-devicesecurity");

    private static void OpenUri(string primary, string? fallback)
    {
        try { Process.Start(new ProcessStartInfo { FileName = primary, UseShellExecute = true }); }
        catch
        {
            if (fallback is null)
            {
                AetherDialog.Error(UiLoc.Instance.T("Dialog.Error"), UiLoc.Instance.T("Security.OpenFailed"));
                return;
            }
            try { Process.Start(new ProcessStartInfo { FileName = fallback, UseShellExecute = true }); }
            catch (Exception ex) { AetherDialog.Error(UiLoc.Instance.T("Dialog.Error"), ex.Message); }
        }
    }
}

public sealed class KeyValueItem
{
    public KeyValueItem(string key, string value)
    {
        Key = key;
        Value = value;
    }

    public string Key { get; }
    public string Value { get; }
}

public partial class DriversViewModel : ObservableObject
{
    private readonly IDriverService _drivers;
    public ObservableCollection<DriverInfo> Items { get; } = new();
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _summaryLine = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _okCount;
    [ObservableProperty] private int _attentionCount;
    [ObservableProperty] private int _oldCount;

    public DriversViewModel(IDriverService drivers)
    {
        _drivers = drivers;
    }

    [RelayCommand]
    private async Task LoadAsync() => await LoadCoreAsync();

    public async Task LoadCoreAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            Status = UiLoc.Instance.T("Drivers.Loading");
            SummaryLine = "";
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            Items.Clear();
            var list = await _drivers.GetDriversAsync().ConfigureAwait(true);
            foreach (var d in list)
                Items.Add(d);
            OkCount = list.Count(d => d.HealthLabel == "OK");
            AttentionCount = list.Count(d => d.HealthLabel == "Attention");
            OldCount = list.Count(d => d.HealthLabel == "Old");
            SummaryLine = UiLoc.Instance.T("Drivers.Summary", OkCount, AttentionCount, OldCount);
            Status = AttentionCount > 0
                ? UiLoc.Instance.T("Drivers.StatusAttention", AttentionCount)
                : UiLoc.Instance.T("Drivers.StatusOk", Items.Count);
        }
        catch (Exception ex)
        {
            Status = UiLoc.Instance.T("Drivers.Error", ex.Message);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void OpenDeviceManager()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "devmgmt.msc"),
                UseShellExecute = true
            });
        }
        catch (Exception ex) { AetherDialog.Error("Error", ex.Message); }
    }

    [RelayCommand]
    private void OpenWindowsUpdate()
    {
        try { Process.Start(new ProcessStartInfo { FileName = "ms-settings:windowsupdate", UseShellExecute = true }); }
        catch (Exception ex) { AetherDialog.Error("Error", ex.Message); }
    }
}

public partial class HistoryViewModel : ObservableObject
{
    private readonly IHistoryStore _history;
    private readonly IOptimizationEngine _opt;
    private readonly IAppSettingsStore _settings;
    private readonly BeastModeViewModel _beast;
    public ObservableCollection<HistoryEntry> Items { get; } = new();
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private HistoryEntry? _selected;

    public HistoryViewModel(IHistoryStore history, IOptimizationEngine opt, IAppSettingsStore settings, BeastModeViewModel beast)
    {
        _history = history;
        _opt = opt;
        _settings = settings;
        _beast = beast;
    }

    [RelayCommand]
    private async Task LoadAsync() => await LoadCoreAsync();

    public async Task LoadCoreAsync()
    {
        try
        {
            Items.Clear();
            foreach (var h in await _history.ListAsync())
                Items.Add(h);
            Status = UiLoc.Instance.T("History.Records", Items.Count);
        }
        catch (Exception ex)
        {
            Status = "Error: " + ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRollback))]
    private async Task RollbackAsync(HistoryEntry? entry)
    {
        entry ??= Selected;
        if (entry is null)
        {
            AetherDialog.Info(UiLoc.Instance.T("History.Title"), UiLoc.Instance.T("History.Pick"));
            return;
        }

        if (!CanRollback(entry))
        {
            AetherDialog.Info(UiLoc.Instance.T("History.Title"), UiLoc.Instance.T("History.NoRb"));
            return;
        }

        if (!AetherDialog.Confirm(
                UiLoc.Instance.T("History.ConfirmTitle"),
                UiLoc.Instance.T("History.ConfirmBody", entry.ResolvedTitle),
                UiLoc.Instance.T("History.ConfirmYes"),
                UiLoc.Instance.T("Common.Cancel")))
            return;

        try
        {
            var ok = await _opt.RollbackAsync(entry.Id);
            if (ok)
            {
                try
                {
                    var prefs = await _settings.LoadProfileAsync();
                    if (prefs.ActiveBeastSessionHistoryId == entry.Id)
                    {
                        prefs.ActiveBeastSessionHistoryId = null;
                        prefs.BeastSessionStartedAt = null;
                        await _settings.SaveProfileAsync(prefs);
                        _beast.ClearActiveSessionIf(entry.Id);
                    }
                }
                catch (Exception ex)
                {
                    Status = UiLoc.Instance.T("History.BeastSessionWarn", ex.Message);
                }
                AetherDialog.Success(UiLoc.Instance.T("History.OkTitle"), UiLoc.Instance.T("History.OkBody"));
                await LoadCoreAsync();
            }
            else
                AetherDialog.Warn(UiLoc.Instance.T("History.OkTitle"), UiLoc.Instance.T("History.FailBody"));
        }
        catch (Exception ex)
        {
            AetherDialog.Error("Rollback", ex.Message);
        }
    }

    private bool CanRollback(HistoryEntry? entry)
    {
        entry ??= Selected;
        return entry is { CanRollback: true, RolledBack: false };
    }
}

public partial class SettingsViewModel : ObservableObject
{
    private readonly IAppSettingsStore _settings;
    private bool _isLoadingSettings;
    private bool _persistQueued;
    private bool _persistDirty;

    [ObservableProperty] private UserProfile _profile = new();
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _selectedCategoryId = "appearance";
    [ObservableProperty] private string _summaryTheme = "";
    [ObservableProperty] private string _summaryLanguage = "";
    [ObservableProperty] private string _compactSummary = "";
    [ObservableProperty] private string _aboutVersion = "";
    [ObservableProperty] private string _aboutBuild = "";
    [ObservableProperty] private string _aboutPath = "";
    [ObservableProperty] private string _aboutArch = "";
    [ObservableProperty] private string _languageNote = "";
    [ObservableProperty] private string _aboutOs = "";

    public ObservableCollection<SettingsCategoryItem> Categories { get; } = new();

    public bool IsAppearance => SelectedCategoryId == "appearance";
    public bool IsRecommendations => SelectedCategoryId == "recommendations";
    public bool IsAnalysis => SelectedCategoryId == "analysis";
    public bool IsOptimization => SelectedCategoryId == "optimization";
    public bool IsNotifications => SelectedCategoryId == "notifications";
    public bool IsAdvanced => SelectedCategoryId == "advanced";
    public bool IsAbout => SelectedCategoryId == "about";
    public bool IsSearchMode => !string.IsNullOrWhiteSpace(SearchText);

    public string[] DetailLevels => new[]
    {
        UiLoc.Instance.T("Settings.Detail.Basic"),
        UiLoc.Instance.T("Settings.Detail.Intermediate"),
        UiLoc.Instance.T("Settings.Detail.Full")
    };

    public SettingsViewModel(IAppSettingsStore settings)
    {
        _settings = settings;
        BuildCategories();
        UiLoc.Instance.PropertyChanged += (_, _) =>
        {
            RefreshSummary();
            ApplySearchFilter();
            OnPropertyChanged(nameof(DetailLevels));
        };
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplySearchFilter();
        OnPropertyChanged(nameof(IsSearchMode));
    }

    partial void OnSelectedCategoryIdChanged(string value)
    {
        foreach (var c in Categories)
            c.IsSelected = c.Id == value;
        OnPropertyChanged(nameof(IsAppearance));
        OnPropertyChanged(nameof(IsRecommendations));
        OnPropertyChanged(nameof(IsAnalysis));
        OnPropertyChanged(nameof(IsOptimization));
        OnPropertyChanged(nameof(IsNotifications));
        OnPropertyChanged(nameof(IsAdvanced));
        OnPropertyChanged(nameof(IsAbout));
    }

    [RelayCommand]
    private void SelectCategory(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        SearchText = "";
        SelectedCategoryId = id;
    }

    [RelayCommand]
    private async Task LoadAsync() => await LoadCoreAsync();

    public async Task LoadCoreAsync()
    {
        _isLoadingSettings = true;
        try
        {
            Profile = await _settings.LoadProfileAsync();
            NormalizeProfile(Profile);
            if (!string.Equals(UiLoc.Instance.Language, Profile.Language, StringComparison.OrdinalIgnoreCase))
                UiLoc.Instance.SetLanguage(Profile.Language);
            // No reaplicar tema al entrar: evita Light→Dark al reabrir Configuración.
            FillAbout();
            RefreshSummary();
            ApplySearchFilter();
            Status = UiLoc.Instance.T("Settings.Ready");
            OnPropertyChanged(nameof(Profile));
        }
        catch (Exception ex)
        {
            Status = "Error: " + ex.Message;
            Profile = new UserProfile();
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    [RelayCommand]
    private async Task SetTheme(string? theme)
    {
        if (_isLoadingSettings || string.IsNullOrWhiteSpace(theme)) return;
        Profile.Theme = theme;
        OnPropertyChanged(nameof(Profile));
        ThemeService.Apply(theme);
        await PersistAsync();
        RefreshSummary();
        Status = theme switch
        {
            "Light" => UiLoc.Instance.T("Settings.ThemeHintLight"),
            "Auto" => UiLoc.Instance.T("Settings.ThemeHintAuto"),
            _ => UiLoc.Instance.T("Settings.ThemeHintDark")
        };
    }

    [RelayCommand]
    private async Task SetLanguage(string? lang)
    {
        if (_isLoadingSettings || string.IsNullOrWhiteSpace(lang)) return;
        Profile.Language = lang;
        OnPropertyChanged(nameof(Profile));
        UiLoc.Instance.SetLanguage(lang);
        // Forzar refresco de textos de Configuración que no van por indexer XAML.
        LanguageNote = UiLoc.Instance.T("Settings.LanguageLegacyNote");
        Status = lang == "en"
            ? UiLoc.Instance.T("Settings.LangHintEn")
            : UiLoc.Instance.T("Settings.LangHintEs");
        OnPropertyChanged(nameof(DetailLevels));
        OnPropertyChanged(nameof(IsAppearance));
        OnPropertyChanged(nameof(IsRecommendations));
        OnPropertyChanged(nameof(IsAnalysis));
        OnPropertyChanged(nameof(IsOptimization));
        OnPropertyChanged(nameof(IsNotifications));
        OnPropertyChanged(nameof(IsAdvanced));
        OnPropertyChanged(nameof(IsAbout));
        foreach (var c in Categories)
            c.RefreshTitle();
        await PersistAsync();
        RefreshSummary();
    }

    [RelayCommand]
    private async Task SetDetailLevel(string? level)
    {
        if (_isLoadingSettings || string.IsNullOrWhiteSpace(level)) return;
        Profile.RecommendationDetail =
            level == UiLoc.Instance.T("Settings.Detail.Basic") || level.Equals("Basic", StringComparison.OrdinalIgnoreCase) ? "Basic" :
            level == UiLoc.Instance.T("Settings.Detail.Full") || level.Equals("Full", StringComparison.OrdinalIgnoreCase) ? "Full" :
            "Intermediate";
        OnPropertyChanged(nameof(Profile));
        await PersistAsync();
    }

    [RelayCommand]
    private async Task SavePreferenceAsync()
    {
        if (_isLoadingSettings) return;
        await PersistAsync();
        RefreshSummary();
        Status = UiLoc.Instance.T("Settings.Saved");
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            await PersistAsync();
            Status = UiLoc.Instance.T("Settings.Saved");
            AetherDialog.Success(
                UiLoc.Instance.T("Dialog.Settings"),
                UiLoc.Instance.T("Settings.SaveBody", Profile.Theme, Profile.Language == "en" ? "English" : "Español"));
        }
        catch (Exception ex)
        {
            Status = UiLoc.Instance.T("Settings.SaveError", ex.Message);
            AetherDialog.Error(UiLoc.Instance.T("Dialog.Settings"), Status);
        }
    }

    [RelayCommand]
    private async Task ExportConfigAsync()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                $"AetherPC-settings-{DateTime.Now:yyyyMMdd-HHmm}.json");
            var export = new UserProfile
            {
                UsageType = Profile.UsageType,
                Theme = Profile.Theme,
                Language = Profile.Language,
                AnimationsEnabled = Profile.AnimationsEnabled,
                RecommendationDetail = Profile.RecommendationDetail,
                ShowAdvancedRecommendations = Profile.ShowAdvancedRecommendations,
                ShowSafeRecommendationsOnly = Profile.ShowSafeRecommendationsOnly,
                ShowExperimentalActions = Profile.ShowExperimentalActions,
                ShowTechnicalExplanations = Profile.ShowTechnicalExplanations,
                AutoRefreshOnLaunch = Profile.AutoRefreshOnLaunch,
                PreferCachedAnalysis = Profile.PreferCachedAnalysis,
                AnalysisFreshMinutes = Profile.AnalysisFreshMinutes,
                SaveAnalysisHistory = Profile.SaveAnalysisHistory,
                AutoRestorePointWhenNeeded = Profile.AutoRestorePointWhenNeeded,
                ConfirmBeforeApply = Profile.ConfirmBeforeApply,
                ShowAdvancedWarnings = Profile.ShowAdvancedWarnings,
                ShowIncompatibleActions = Profile.ShowIncompatibleActions,
                ShowFinishSummary = Profile.ShowFinishSummary,
                LiveLogHiddenByDefault = Profile.LiveLogHiddenByDefault,
                OpenLiveLogOnOptimize = Profile.OpenLiveLogOnOptimize,
                NotifyOnOptimizeDone = Profile.NotifyOnOptimizeDone,
                NotifyRestartPending = Profile.NotifyRestartPending,
                NotifyNewRecommendations = Profile.NotifyNewRecommendations,
                DeveloperMode = Profile.DeveloperMode
            };
            await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(export, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Status = UiLoc.Instance.T("Settings.ExportOk", path);
            AetherDialog.Success(UiLoc.Instance.T("Dialog.Settings"), Status);
        }
        catch (Exception ex)
        {
            AetherDialog.Error(UiLoc.Instance.T("Dialog.Error"), ex.Message);
        }
    }

    [RelayCommand]
    private async Task ImportConfigAsync()
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "JSON (*.json)|*.json",
                Title = UiLoc.Instance.T("Settings.Import")
            };
            if (dlg.ShowDialog() != true) return;
            var json = await File.ReadAllTextAsync(dlg.FileName);
            var imported = System.Text.Json.JsonSerializer.Deserialize<UserProfile>(json);
            if (imported is null) throw new InvalidOperationException("Invalid file");
            NormalizeProfile(imported);
            imported.SecurityScoreHistory = Profile.SecurityScoreHistory;
            imported.OnboardingCompleted = Profile.OnboardingCompleted;
            Profile = imported;
            ThemeService.Apply(Profile.Theme);
            UiLoc.Instance.SetLanguage(Profile.Language);
            await PersistAsync();
            RefreshSummary();
            ApplySearchFilter();
            Status = UiLoc.Instance.T("Settings.ImportOk");
            OnPropertyChanged(nameof(Profile));
        }
        catch (Exception ex)
        {
            AetherDialog.Error(UiLoc.Instance.T("Dialog.Error"), ex.Message);
        }
    }

    [RelayCommand]
    private async Task ResetConfigAsync()
    {
        if (!AetherDialog.Confirm(UiLoc.Instance.T("Dialog.Settings"), UiLoc.Instance.T("Settings.ResetConfirm")))
            return;
        var keepOnboarding = Profile.OnboardingCompleted;
        Profile = new UserProfile { OnboardingCompleted = keepOnboarding };
        ThemeService.Apply(Profile.Theme);
        UiLoc.Instance.SetLanguage(Profile.Language);
        await PersistAsync();
        RefreshSummary();
        Status = UiLoc.Instance.T("Settings.ResetOk");
        OnPropertyChanged(nameof(Profile));
    }

    private async Task PersistAsync()
    {
        _persistDirty = true;
        if (_persistQueued) return;
        _persistQueued = true;
        try
        {
            while (_persistDirty)
            {
                _persistDirty = false;
                await _settings.SaveProfileAsync(Profile);
            }
        }
        finally { _persistQueued = false; }
    }

    private void BuildCategories()
    {
        Categories.Clear();
        Categories.Add(new SettingsCategoryItem("appearance", "Settings.Cat.Appearance",
            "tema theme claro oscuro auto apariencia idioma language español english"));
        Categories.Add(new SettingsCategoryItem("recommendations", "Settings.Cat.Recommendations",
            "recomendaciones advanced experimental detalle recommendations safe"));
        Categories.Add(new SettingsCategoryItem("analysis", "Settings.Cat.Analysis",
            "análisis analysis historial cache fresh auto refresh iniciar"));
        Categories.Add(new SettingsCategoryItem("optimization", "Settings.Cat.Optimization",
            "optimización restauración confirmación advertencias restore incompatible"));
        Categories.Add(new SettingsCategoryItem("notifications", "Settings.Cat.Notifications",
            "notificaciones reinicio notify restart errors"));
        Categories.Add(new SettingsCategoryItem("advanced", "Settings.Cat.Advanced",
            "avanzado developer export import reset depuración configuración"));
        Categories.Add(new SettingsCategoryItem("about", "Settings.Cat.About",
            "acerca version build licencia créditos about path"));
        SelectedCategoryId = "appearance";
        foreach (var c in Categories) c.IsSelected = c.Id == SelectedCategoryId;
    }

    private void ApplySearchFilter()
    {
        var q = (SearchText ?? "").Trim().ToLowerInvariant();
        foreach (var c in Categories)
        {
            if (string.IsNullOrEmpty(q))
            {
                c.IsVisible = true;
                continue;
            }
            var title = UiLoc.Instance.T(c.TitleKey).ToLowerInvariant();
            c.IsVisible = title.Contains(q) || c.Keywords.Contains(q, StringComparison.OrdinalIgnoreCase);
        }

        // Con búsqueda: saltar a la primera categoría visible
        if (!string.IsNullOrEmpty(q))
        {
            var first = Categories.FirstOrDefault(c => c.IsVisible);
            if (first is not null && SelectedCategoryId != first.Id)
                SelectedCategoryId = first.Id;
        }
    }

    private void RefreshSummary()
    {
        SummaryTheme = Profile.Theme switch
        {
            "Light" => UiLoc.Instance.T("Settings.ThemeLight"),
            "Auto" => UiLoc.Instance.T("Settings.ThemeAuto"),
            _ => UiLoc.Instance.T("Settings.ThemeDark")
        };
        SummaryLanguage = Profile.Language == "en" ? "English" : "Español";
        CompactSummary = $"{SummaryTheme} · {SummaryLanguage}";
    }

    private void FillAbout()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var ver = asm.GetName().Version;
        AboutVersion = ver?.ToString(3) ?? "1.0.0";
        AboutBuild = ver?.ToString() ?? "1.0.0.0";
        // Carpeta real del EXE (single-file: no usar AppContext.BaseDirectory = extract temp)
        AboutPath = ResolveAppDirectory();
        AboutArch = Environment.Is64BitProcess ? "x64" : "x86";
        AboutOs = $"{Environment.OSVersion.VersionString} · {(Environment.Is64BitOperatingSystem ? "x64" : "x86")}";
    }

    private static string ResolveAppDirectory()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                var dir = Path.GetDirectoryName(processPath);
                if (!string.IsNullOrWhiteSpace(dir))
                    return dir;
            }
        }
        catch { /* ignore */ }

        return AppContext.BaseDirectory.TrimEnd('\\', '/');
    }

    [RelayCommand]
    private void CopyAbout()
    {
        var text = $"AetherPC {AboutVersion}\nBuild {AboutBuild}\n{AboutArch}\n{AboutOs}\n{AboutPath}";
        try
        {
            System.Windows.Clipboard.SetText(text);
            Status = UiLoc.Instance.T("Hardware.Copied");
        }
        catch { Status = UiLoc.Instance.T("Hardware.CopyFailed"); }
    }

    [RelayCommand]
    private void OpenInstallFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = AboutPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    private static void NormalizeProfile(UserProfile p)
    {
        if (string.IsNullOrWhiteSpace(p.Theme)) p.Theme = "Dark";
        if (string.IsNullOrWhiteSpace(p.Language)) p.Language = "es";
        if (string.IsNullOrWhiteSpace(p.UsageType)) p.UsageType = "General";
        if (string.IsNullOrWhiteSpace(p.RecommendationDetail)) p.RecommendationDetail = "Intermediate";
        if (p.AnalysisFreshMinutes <= 0) p.AnalysisFreshMinutes = 30;
        p.SecurityScoreHistory ??= new List<SecurityScoreSample>();
    }
}

public partial class SettingsCategoryItem : ObservableObject
{
    public SettingsCategoryItem(string id, string titleKey, string keywords)
    {
        Id = id;
        TitleKey = titleKey;
        Keywords = keywords;
        // Title no se cachea (siempre lee de UiLoc), pero el binding necesita que
        // se notifique el cambio para refrescar el texto al cambiar de idioma.
        UiLoc.Instance.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Title));
    }

    public string Id { get; }
    public string TitleKey { get; }
    public string Keywords { get; }
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _isSelected;
    public string Title => UiLoc.Instance.T(TitleKey);
    public void RefreshTitle() => OnPropertyChanged(nameof(Title));
}

public sealed class ServiceRowItem
{
    public ServiceInfo Info { get; }
    public ServiceRowItem(ServiceInfo info) => Info = info;

    public string Name => Info.Name;
    public string DisplayName => Info.DisplayName;
    public string Status => LocalizeState(Info.Status);
    public string StartType => LocalizeStart(Info.StartType);
    public string? Description => Info.Description;
    public bool IsCritical => Info.IsCritical;
    public int ProcessId => Info.ProcessId;
    public double CpuPercent => Info.CpuPercent;
    public double WorkingSetMb => Info.WorkingSetMb;
    public string CpuLabel => Info.ProcessId > 0 ? $"{Info.CpuPercent:F1}%" : "—";
    public string RamLabel => Info.ProcessId > 0 ? $"{Info.WorkingSetMb:F0} MB" : "—";
    public string PidLabel => Info.ProcessId > 0 ? Info.ProcessId.ToString() : "—";
    public string CriticalLabel => Info.IsCritical ? UiLoc.Instance.T("Services.CriticalYes") : "";

    private static string LocalizeState(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Equals("Running", StringComparison.OrdinalIgnoreCase)
            || s.Contains("ejecuci", StringComparison.OrdinalIgnoreCase))
            return UiLoc.Instance.T("Services.State.Running");
        if (s.Equals("Stopped", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Detenido", StringComparison.OrdinalIgnoreCase))
            return UiLoc.Instance.T("Services.State.Stopped");
        if (s.Contains("Start Pending", StringComparison.OrdinalIgnoreCase)
            || s.Contains("inicio pendiente", StringComparison.OrdinalIgnoreCase))
            return UiLoc.Instance.T("Services.State.StartPending");
        if (s.Contains("Stop Pending", StringComparison.OrdinalIgnoreCase)
            || s.Contains("detención pendiente", StringComparison.OrdinalIgnoreCase))
            return UiLoc.Instance.T("Services.State.StopPending");
        if (s.Equals("Paused", StringComparison.OrdinalIgnoreCase)
            || s.Equals("En pausa", StringComparison.OrdinalIgnoreCase))
            return UiLoc.Instance.T("Services.State.Paused");
        return string.IsNullOrWhiteSpace(s) ? UiLoc.Instance.T("Common.NotDetected") : s;
    }

    private static string LocalizeStart(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Equals("Automatic", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Auto", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Automático", StringComparison.OrdinalIgnoreCase))
            return UiLoc.Instance.T("Services.Start.Automatic");
        if (s.Equals("Manual", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Demand", StringComparison.OrdinalIgnoreCase))
            return UiLoc.Instance.T("Services.Start.Manual");
        if (s.Equals("Disabled", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Deshabilitado", StringComparison.OrdinalIgnoreCase))
            return UiLoc.Instance.T("Services.Start.Disabled");
        if (s.Equals("Boot", StringComparison.OrdinalIgnoreCase))
            return UiLoc.Instance.T("Services.Start.Boot");
        if (s.Equals("System", StringComparison.OrdinalIgnoreCase))
            return UiLoc.Instance.T("Services.Start.System");
        return string.IsNullOrWhiteSpace(s) ? UiLoc.Instance.T("Common.NotDetected") : s;
    }
}

public partial class ServicesViewModel : ObservableObject
{
    private readonly IServiceEnumerator _services;
    private IReadOnlyList<ServiceInfo> _cache = Array.Empty<ServiceInfo>();
    private System.Windows.Threading.DispatcherTimer? _filterDebounce;

    public ObservableCollection<ServiceRowItem> Items { get; } = new();

    /// <summary>Valor sentinel interno (no traducido) usado para "sin filtro" en estado/inicio.
    /// Se mantiene en inglés a propósito para que la comparación no dependa del idioma activo.</summary>
    private const string AllFilterValue = "All";

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _status = UiLoc.Instance.T("Services.Status.Ready");
    [ObservableProperty] private string _statusFilter = AllFilterValue;
    [ObservableProperty] private string _startTypeFilter = AllFilterValue;
    [ObservableProperty] private string _sortBy = "RAM ↓";
    [ObservableProperty] private bool _onlyRunning;
    [ObservableProperty] private bool _onlyWithRam;
    [ObservableProperty] private bool _hideCritical;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private ServiceRowItem? _selectedRow;
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _summary = "";

    public string[] StatusFilters { get; } =
        { AllFilterValue, "Running", "Stopped", "Start Pending", "Stop Pending", "Paused" };

    public string[] StartTypeFilters { get; } =
        { AllFilterValue, "Automatic", "Manual", "Disabled", "Boot", "System" };

    public string[] SortOptions { get; } =
        { "RAM ↓", "RAM ↑", "CPU ↓", "CPU ↑", "Name A-Z", "Status" };

    private bool _statusIsDefault = true;

    public ServicesViewModel(IServiceEnumerator services)
    {
        _services = services;
        UiLoc.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(UiLoc.Language)) return;
            if (_cache.Count > 0)
                _ = RefreshCoreAsync();
            else
            {
                _status = UiLoc.Instance.T("Services.Status.Ready");
                OnPropertyChanged(nameof(Status));
            }
        };
    }

    partial void OnStatusChanged(string value) => _statusIsDefault = false;

    [RelayCommand] private void SortRamDesc() => SortBy = "RAM ↓";
    [RelayCommand] private void SortRamAsc() => SortBy = "RAM ↑";
    [RelayCommand] private void SortCpuDesc() => SortBy = "CPU ↓";
    [RelayCommand] private void SortCpuAsc() => SortBy = "CPU ↑";

    partial void OnSelectedRowChanged(ServiceRowItem? value)
    {
        if (value is null) { Detail = ""; return; }
        var s = value.Info;
        Detail =
            $"{s.DisplayName} ({s.Name})\n" +
            $"{s.Status} · {s.StartType}" +
            (s.ProcessId > 0 ? $" · PID {s.ProcessId} · CPU {s.CpuPercent:F1}% · RAM {s.WorkingSetMb:F0} MB" : "") +
            (s.IsCritical ? " · " + UiLoc.Instance.T("Services.CriticalTag") : "") + "\n" +
            (s.Description ?? UiLoc.Instance.T("Services.NoDescription")) +
            (string.IsNullOrWhiteSpace(s.PathName) ? "" : "\n" + s.PathName);
    }

    partial void OnFilterChanged(string value) => DebounceFilter();
    partial void OnStatusFilterChanged(string value) => ApplyFilter();
    partial void OnStartTypeFilterChanged(string value) => ApplyFilter();
    partial void OnSortByChanged(string value) => ApplyFilter();
    partial void OnOnlyRunningChanged(bool value) => ApplyFilter();
    partial void OnOnlyWithRamChanged(bool value) => ApplyFilter();
    partial void OnHideCriticalChanged(bool value) => ApplyFilter();

    private void DebounceFilter()
    {
        _filterDebounce ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _filterDebounce.Stop();
        _filterDebounce.Tick -= OnFilterTick;
        _filterDebounce.Tick += OnFilterTick;
        _filterDebounce.Start();
    }

    private void OnFilterTick(object? sender, EventArgs e)
    {
        _filterDebounce?.Stop();
        ApplyFilter();
    }

    [RelayCommand]
    private async Task RefreshAsync() => await RefreshCoreAsync();

    public async Task RefreshCoreAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = UiLoc.Instance.T("Services.Status.Loading");
        try
        {
            _cache = await _services.GetServicesAsync().ConfigureAwait(true);
            ApplyFilter();
            var running = _cache.Count(s => s.Status.Equals("Running", StringComparison.OrdinalIgnoreCase));
            Status = UiLoc.Instance.T("Services.Status.Loaded", _cache.Count, running, Items.Count);
        }
        catch (Exception ex)
        {
            Status = UiLoc.Instance.T("Services.Status.Error", ex.Message);
            _cache = Array.Empty<ServiceInfo>();
            Items.Clear();
        }
        finally { IsBusy = false; }
    }

    private void ApplyFilter()
    {
        var filter = Filter ?? "";
        IEnumerable<ServiceInfo> q = _cache;

        if (OnlyRunning)
            q = q.Where(s => s.Status.Equals("Running", StringComparison.OrdinalIgnoreCase));
        if (OnlyWithRam)
            q = q.Where(s => s.WorkingSetBytes > 0);
        if (HideCritical)
            q = q.Where(s => !s.IsCritical);
        if (StatusFilter != AllFilterValue)
            q = q.Where(s => s.Status.Equals(StatusFilter, StringComparison.OrdinalIgnoreCase) ||
                             s.Status.Replace(" ", "").Equals(StatusFilter.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
        if (StartTypeFilter != AllFilterValue)
            q = q.Where(s => s.StartType.Equals(StartTypeFilter, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(filter))
        {
            q = q.Where(s =>
                s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                s.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                (s.Description?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.PathName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        q = SortBy switch
        {
            "RAM ↑" => q.OrderBy(s => s.WorkingSetBytes).ThenBy(s => s.DisplayName),
            "CPU ↓" => q.OrderByDescending(s => s.CpuPercent).ThenByDescending(s => s.WorkingSetBytes),
            "CPU ↑" => q.OrderBy(s => s.CpuPercent).ThenBy(s => s.DisplayName),
            "Name A-Z" or "Nombre A-Z" => q.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase),
            "Status" or "Estado" => q.OrderBy(s => s.Status).ThenBy(s => s.DisplayName),
            _ => q.OrderByDescending(s => s.WorkingSetBytes).ThenByDescending(s => s.CpuPercent)
        };

        var built = q.Take(800).Select(s => new ServiceRowItem(s)).ToList();
        Items.Clear();
        foreach (var row in built)
            Items.Add(row);

        var ramMb = built.Where(r => r.ProcessId > 0).Sum(r => r.WorkingSetMb);
        // Nota: svchost compartido suma de más; el resumen es orientativo por fila
        Summary = UiLoc.Instance.T("Services.SummaryVisible", Items.Count, $"{ramMb:F0}");
        if (Items.Count > 0 && SelectedRow is null)
            SelectedRow = Items[0];
    }
}
