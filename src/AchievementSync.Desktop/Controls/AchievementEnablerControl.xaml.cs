using System.Windows;
using System.Windows.Controls;
using System.IO;
using Microsoft.Win32;
using AchievementSync.Core.Enabler;
using AchievementSync.Core.Metadata;
using AchievementSync.Desktop.Services;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using MessageBox = System.Windows.MessageBox;
using WpfUserControl = System.Windows.Controls.UserControl;

namespace AchievementSync.Desktop.Controls;

public sealed partial class AchievementEnablerControl : WpfUserControl, System.ComponentModel.INotifyPropertyChanged
{
    private readonly AchievementEnablerService _service;
    private EnablerDetection? _detection;
    private EnablerPlan? _plan;
    private string _gamePath = string.Empty;
    private string _executablePath = string.Empty;
    private IReadOnlyList<string> _discoveredExecutables = [];
    private string _steamAppId = string.Empty;
    private string _activityStatus = "Choose a game folder or executable.";
    private bool _isBusy;
    private CancellationTokenSource? _operationCancellation;
    private EnablerTarget _selectedTarget = EnablerTarget.SteamEmulator;
    private readonly SteamCredentialStore _credentialStore = new();
    private readonly SteamSchemaGeneratorService _generatorService = new();
    private string _steamUsername = string.Empty;
    private string _steamAccountStatus = string.Empty;
    private bool _hasSteamCredentials;

    public AchievementEnablerControl() : this(new AchievementEnablerService()) { }

    public AchievementEnablerControl(AchievementEnablerService service)
    {
        InitializeComponent();
        _service = service;
        DataContext = this;
        RefreshSteamAccountStatus();
    }

    public string SteamUsername
    {
        get => _steamUsername;
        set { if (_steamUsername == value) return; _steamUsername = value; OnPropertyChanged(nameof(SteamUsername)); }
    }

    public string SteamAccountStatus
    {
        get => _steamAccountStatus;
        private set { if (_steamAccountStatus == value) return; _steamAccountStatus = value; OnPropertyChanged(nameof(SteamAccountStatus)); }
    }

    public bool HasSteamCredentials
    {
        get => _hasSteamCredentials;
        private set { if (_hasSteamCredentials == value) return; _hasSteamCredentials = value; OnPropertyChanged(nameof(HasSteamCredentials)); }
    }

    public void RefreshSteamAccountStatus()
    {
        var creds = _credentialStore.Read();
        if (creds is not null && !string.IsNullOrWhiteSpace(creds.Username))
        {
            var hasToken = !string.IsNullOrWhiteSpace(creds.RefreshToken) ||
                           File.Exists(EmbeddedGeneratorRuntime.RefreshTokensPath) ||
                           File.Exists(Path.Combine(_generatorService.GeneratorRoot, "refresh_tokens.json"));
            HasSteamCredentials = true;
            SteamUsername = creds.Username;
            SteamAccountStatus = hasToken
                ? $"✓ Steam login saved for '{creds.Username}' (refresh token active in Windows Credential Manager)."
                : $"✓ Credentials saved for '{creds.Username}' (refresh token will be acquired and saved on next generator run).";
        }
        else
        {
            HasSteamCredentials = false;
            SteamAccountStatus = "No Steam credentials saved yet. Enter your Steam username and password below to authenticate and save a refresh token in Windows Credential Manager.";
        }
    }

    private async void SaveSteamLogin_Click(object sender, RoutedEventArgs e)
    {
        var user = SteamUsernameBox.Text?.Trim();
        var pass = SteamPasswordBox.Password;
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
        {
            new Views.SteamLoginResultDialog("Steam login details required", "Enter both your Steam username and password to continue.", false)
            {
                Owner = Window.GetWindow(this)
            }.ShowDialog();
            return;
        }

        IsBusy = true;
        ActivityStatus = "Authenticating with Steam and generating refresh token...";
        try
        {
            var result = await _generatorService.AuthenticateSteamWith2FaAsync(
                user,
                pass,
                async challenge =>
                {
                    return await Dispatcher.InvokeAsync(() =>
                    {
                        var dialog = new Views.SteamTwoFactorDialog(challenge)
                        {
                            Owner = Window.GetWindow(this)
                        };
                        var dialogResult = dialog.ShowDialog();
                        return dialogResult == true ? dialog.EnteredCode : null;
                    });
                });

            if (result.Success)
            {
                SteamPasswordBox.Clear();
                RefreshSteamAccountStatus();
                ActivityStatus = $"✓ Steam account connected: '{user}' (Refresh token active ✓)";
                new Views.SteamLoginResultDialog("Steam login saved", $"Signed in as {user}. A refresh token is stored in Windows Credential Manager, so you can use the schema generator without entering your password again.", true)
                {
                    Owner = Window.GetWindow(this)
                }.ShowDialog();
            }
            else
            {
                RefreshSteamAccountStatus();
                ActivityStatus = $"Steam authentication failed: {result.Message}";
                new Views.SteamLoginResultDialog("Steam authentication failed", result.Message, false)
                {
                    Owner = Window.GetWindow(this)
                }.ShowDialog();
            }
        }
        catch (Exception ex)
        {
            ActivityStatus = $"Authentication error: {ex.Message}";
            new Views.SteamLoginResultDialog("Steam authentication error", ex.Message, false)
            {
                Owner = Window.GetWindow(this)
            }.ShowDialog();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ClearSteamLogin_Click(object sender, RoutedEventArgs e)
    {
        _generatorService.ClearCredentials();
        SteamPasswordBox.Clear();
        RefreshSteamAccountStatus();
        ActivityStatus = "Steam credentials cleared.";
    }

    private void BackToEdit_Click(object sender, RoutedEventArgs e)
    {
        ClearPlan();
        UpdateBindings();
    }

    public string GamePath
    {
        get => _gamePath;
        set
        {
            if (_gamePath == value) return;
            _gamePath = value;
            _detection = null;
            ClearPlan();
            OnPropertyChanged(nameof(GamePath));
            RefreshDiscoveredExecutablesFromPath(value);
            UpdateBindings();
        }
    }

    public string ExecutablePath
    {
        get => _executablePath;
        set
        {
            if (_executablePath == value) return;
            _executablePath = value;
            ClearPlan();
            OnPropertyChanged(nameof(ExecutablePath));
            UpdateBindings();
        }
    }

    public IReadOnlyList<string> DiscoveredExecutables
    {
        get => _discoveredExecutables;
        private set { _discoveredExecutables = value; OnPropertyChanged(nameof(DiscoveredExecutables)); }
    }

    private void RefreshDiscoveredExecutablesFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            DiscoveredExecutables = [];
            return;
        }
        try
        {
            var full = Path.GetFullPath(path.Trim());
            if (File.Exists(full) && !Directory.Exists(full))
            {
                ExecutablePath = full;
                var parent = Path.GetDirectoryName(full);
                var list = parent is not null ? _service.FindCandidateExecutables(parent, SelectedTarget).ToList() : new List<string>();
                if (!list.Contains(full, StringComparer.OrdinalIgnoreCase))
                    list.Insert(0, full);
                DiscoveredExecutables = list;
            }
            else if (Directory.Exists(full))
            {
                var list = _service.FindCandidateExecutables(full, SelectedTarget);
                DiscoveredExecutables = list;
                if (list.Count > 0 && (string.IsNullOrWhiteSpace(ExecutablePath) ||
                    !ExecutablePath.StartsWith(full.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                {
                    ExecutablePath = list[0];
                }
            }
            else
            {
                DiscoveredExecutables = [];
            }
        }
        catch
        {
            DiscoveredExecutables = [];
        }
    }

    public string SteamAppId
    {
        get => _steamAppId;
        set
        {
            if (_steamAppId == value) return;
            _steamAppId = value;
            ClearPlan();
            OnPropertyChanged(nameof(SteamAppId));
            UpdateBindings();
        }
    }

    public IReadOnlyList<EnablerTargetOption> TargetOptions { get; } =
    [
        new(EnablerTarget.SteamEmulator, "Steam (Goldberg steam_api emulator + generated catalog)"),
        new(EnablerTarget.SteamColdLoaderWithRegularEmulator, "Steam ColdLoader + Regular Emulator (version.dll proxy + Goldberg replacement + GameOverlayRenderer64 + steamclient64)"),
        new(EnablerTarget.SteamColdClient, "Steam ColdClient (standalone launcher + generated catalog)"),
        new(EnablerTarget.UplayR2, "Ubisoft Connect R2 emulator"),
        new(EnablerTarget.UplayR1, "Ubisoft Connect R1 emulator"),
        new(EnablerTarget.UplayLumaPlay, "Ubisoft LumaPlay (x86 / x64 runtime)"),
        new(EnablerTarget.EpicEmulator, "Epic Games (Nemirtingas emulator)"),
        new(EnablerTarget.GogUniverseLan, "GOG (UniverseLAN)"),
        new(EnablerTarget.EaOrigin, "EA / Origin emulator"),
    ];
    public EnablerTarget SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (_selectedTarget == value) return;
            _selectedTarget = value;
            ClearPlan();
            OnPropertyChanged(nameof(SelectedTarget));
            OnPropertyChanged(nameof(ShowSteamStubHelperOption));
            UpdateBindings();
        }
    }
    public bool IsBusy { get => _isBusy; private set { if (_isBusy == value) return; _isBusy = value; OnPropertyChanged(nameof(IsBusy)); } }
    public string ActivityStatus { get => _activityStatus; private set { if (_activityStatus == value) return; _activityStatus = value; OnPropertyChanged(nameof(ActivityStatus)); } }
    public bool HasPlan => _plan is not null;
    public bool ShowGenerateButton => !HasPlan;
    public int PlanWarningsCount => _plan?.Warnings.Count ?? 0;
    public bool HasPlanWarnings => PlanWarningsCount > 0;
    public IReadOnlyList<string> PlanWarnings => _plan?.Warnings ?? [];
    public int PlanNotesCount => _plan?.Notes.Count ?? 0;
    public bool HasPlanNotes => PlanNotesCount > 0;
    public IReadOnlyList<string> PlanNotes => _plan?.Notes ?? [];
    public int PlanBlockersCount => _plan?.Blockers.Count ?? 0;
    public bool HasPlanBlockers => PlanBlockersCount > 0;
    public IReadOnlyList<string> PlanBlockers => _plan?.Blockers ?? [];
    public bool HasPlanDetails => HasPlanWarnings || HasPlanNotes || HasPlanBlockers;
    public bool CanApplyPlan => HasPlan && !HasPlanBlockers && _plan!.Items.Count > 0;
    public bool ShowSteamStubHelperOption => SelectedTarget == EnablerTarget.SteamColdClient;
    public IReadOnlyList<PlanItemViewModel> PlanItems { get; private set; } = [];

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select the game folder" };
        if (dialog.ShowDialog() == true) GamePath = dialog.FolderName;
    }

    private void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var initialDir = !string.IsNullOrWhiteSpace(GamePath) && Directory.Exists(GamePath)
            ? GamePath
            : (!string.IsNullOrWhiteSpace(ExecutablePath) && File.Exists(ExecutablePath) ? Path.GetDirectoryName(ExecutablePath) : null);
        var dialog = new OpenFileDialog
        {
            Title = "Select the game executable",
            Filter = "Executable (*.exe)|*.exe",
            CheckFileExists = true,
            InitialDirectory = initialDir ?? string.Empty
        };
        if (dialog.ShowDialog() == true)
        {
            var selectedExe = dialog.FileName;
            ExecutablePath = selectedExe;
            if (!DiscoveredExecutables.Contains(selectedExe, StringComparer.OrdinalIgnoreCase))
                DiscoveredExecutables = new[] { selectedExe }.Concat(DiscoveredExecutables).ToArray();

            if (string.IsNullOrWhiteSpace(GamePath) || (!Directory.Exists(GamePath) && !File.Exists(GamePath)))
            {
                GamePath = Path.GetDirectoryName(selectedExe) ?? selectedExe;
            }
            else if (_detection is not null)
            {
                _detection = _detection with { Executable = selectedExe };
            }
            ActivityStatus = $"Selected executable: {Path.GetFileName(selectedExe)}";
        }
    }

    private async void Detect_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(GamePath) || (!System.IO.Directory.Exists(GamePath) && !System.IO.File.Exists(GamePath)))
        {
            MessageBox.Show("Pick a valid game folder or executable first.", "Achievement Enabler", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetBusy(true, "Inspecting the selected folder. This window remains active during the scan…");
        try
        {
            var explicitExe = !string.IsNullOrWhiteSpace(ExecutablePath) && File.Exists(ExecutablePath)
                ? ExecutablePath.Trim()
                : null;
            _detection = await _service.DetectAsync(GamePath, explicitExe);

            var searchRoot = Directory.Exists(GamePath) ? GamePath : Path.GetDirectoryName(GamePath)!;
            var candidates = _service.FindCandidateExecutables(searchRoot, _detection.SuggestedTarget).ToList();
            if (_detection.Executable is not null && !candidates.Contains(_detection.Executable, StringComparer.OrdinalIgnoreCase))
                candidates.Insert(0, _detection.Executable);
            DiscoveredExecutables = candidates;

            if (_detection.Executable is not null)
                ExecutablePath = _detection.Executable;

            // Auto-fill the proper detected AppID into the text box.
            if (_detection.SteamAppId is not null)
            {
                SteamAppId = _detection.SteamAppId;
                AppIdBox.Text = _detection.SteamAppId;
            }
            else if (!string.IsNullOrWhiteSpace(SteamAppId))
            {
                _detection = _detection with { SteamAppId = SteamAppId.Trim() };
            }

            // Removed automatic crack/target suggestion — the user chooses the emulator target manually.
            EvidenceText.Text = string.Join("\n", _detection.Evidence);
            _plan = null;
            PlanItems = [];
            ActivityStatus = "Detection complete. Review the executable, AppID, and evidence, choose a target, then generate a plan.";
            UpdateBindings();
        }
        catch (Exception ex)
        {
            ActivityStatus = "Detection failed. Check the selected path and try again.";
            MessageBox.Show($"Detection failed: {ex.Message}", "Achievement Enabler", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false, ActivityStatus); }
    }

    private async void GeneratePlan_Click(object sender, RoutedEventArgs e)
    {
        if (_detection is null)
        {
            MessageBox.Show("Run detection first.", "Achievement Enabler", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var detection = _detection;
        if (!string.IsNullOrWhiteSpace(ExecutablePath) && File.Exists(ExecutablePath.Trim()))
        {
            detection = detection with { Executable = Path.GetFullPath(ExecutablePath.Trim()) };
        }

        var target = SelectedTarget;
        var gameId = string.IsNullOrWhiteSpace(SteamAppId) ? null : SteamAppId.Trim();
        using var operation = new CancellationTokenSource();
        _operationCancellation = operation;
        var shouldRefreshMetadata = RefreshGeneratorCheckBox.IsChecked == true;
        var refreshMessage = string.Empty;
        SetBusy(true, shouldRefreshMetadata
            ? "Refreshing metadata & compatibility data for the selected target…"
            : "Preparing the plan preview…");
        try
        {
            if (shouldRefreshMetadata)
            {
                EnablerGeneratorRefreshResult refresh;
                if (target == EnablerTarget.EpicEmulator)
                {
                    refresh = await _service.RefreshEpicMetadataAsync(gameId ?? detection.SteamAppId, detection.GameDirectory, operation.Token);
                }
                else if (target == EnablerTarget.GogUniverseLan)
                {
                    refresh = await _service.RefreshGogMetadataAsync(detection, operation.Token);
                }
                else if (target == EnablerTarget.EaOrigin)
                {
                    refresh = await _service.RefreshEaMetadataAsync(detection, gameId, operation.Token);
                }
                else
                {
                    refresh = await _service.RefreshSteamMetadataAsync(gameId ?? detection.SteamAppId ?? string.Empty, operation.Token);
                }

                refreshMessage = refresh.Message;
                operation.Token.ThrowIfCancellationRequested();
                ActivityStatus = string.IsNullOrWhiteSpace(refreshMessage)
                    ? "Building the plan preview…"
                    : refreshMessage + " Building the plan preview…";
            }

            operation.Token.ThrowIfCancellationRequested();
            var includeSteamStubHelpers = SteamStubHelperCheckBox.IsChecked == true;
            _plan = await Task.Run(() => _service.BuildPlan(detection, target, gameId, includeSteamStubHelpers), operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            PlanItems = _plan.Items.Select(item => new PlanItemViewModel(item)).ToArray();
            var refreshSummary = string.IsNullOrWhiteSpace(refreshMessage) ? string.Empty : " " + refreshMessage;
            ActivityStatus = _plan.Blockers.Count > 0
                ? $"Plan preview has {_plan.Blockers.Count} blocking issue(s); Apply is disabled until they are resolved.{refreshSummary}"
                : $"Plan ready: {_plan.Items.Count} operation(s). Nothing has been written; review it before Apply.{refreshSummary}";
            UpdateBindings();
        }
        catch (OperationCanceledException)
        {
            _plan = null;
            PlanItems = [];
            ActivityStatus = "Operation canceled. No plan files were applied.";
            UpdateBindings();
        }
        catch (Exception ex)
        {
            ActivityStatus = "Plan generation failed. Your game folder has not been changed.";
            MessageBox.Show($"Plan generation failed: {ex.Message}", "Achievement Enabler", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (ReferenceEquals(_operationCancellation, operation)) _operationCancellation = null;
            SetBusy(false, ActivityStatus);
        }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is null) return;
        if (_plan.Blockers.Count > 0)
        {
            MessageBox.Show("This plan cannot be applied until the blocking issues shown in the review are resolved.",
                "Achievement Enabler", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_plan.Items.Count == 0)
        {
            MessageBox.Show("The plan has no files to apply. Review the warnings and choose a supported target.",
                "Achievement Enabler", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var destinations = _plan.Items.Select(item => item.Destination).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var existing = destinations.Count(File.Exists);
        var preview = string.Join(Environment.NewLine, destinations.Take(6).Select(path => "• " + path));
        if (destinations.Length > 6) preview += Environment.NewLine + $"• and {destinations.Length - 6} more file(s)";
        var confirmationMessage = $"Apply {_plan.Items.Count} file operation(s) for {Path.GetFileName(_plan.Detection.GameDirectory)}?\n\n" +
                                  $"Existing files: {existing}. Existing files will be copied to a timestamped .achievementsync-backup file before replacement.\n\n" +
                                  preview;
        if (MessageBox.Show(confirmationMessage, "Review file changes", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        ApplyButton.IsEnabled = false;
        ApplyButton.Content = "Applying…";
        try
        {
            var result = await Task.Run(() => _service.Apply(_plan)).ConfigureAwait(true);
            var message = result.FilesWritten > 0
                ? $"Applied {result.FilesWritten} file(s). {result.FilesSkipped} skipped."
                : "No files were written.";
            if (result.Messages.Count > 0) message += "\n\n" + string.Join("\n", result.Messages);
            MessageBox.Show(message, "Achievement Enabler", MessageBoxButton.OK, MessageBoxImage.Information);
            ClearPlan();
            UpdateBindings();
            ActivityStatus = "Plan applied successfully. You can generate a new plan for another game.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Apply failed: {ex.Message}", "Achievement Enabler", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ApplyButton.IsEnabled = true;
            ApplyButton.Content = "Apply plan";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy)
        {
            _operationCancellation?.Cancel();
            ActivityStatus = "Canceling the current operation…";
            return;
        }
        ClearPlan();
        UpdateBindings();
        ActivityStatus = "Choose a game folder containing steam_api64.dll and/or steam_api.dll.";
    }

    private void UpdateBindings()
    {
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(ShowGenerateButton));
        OnPropertyChanged(nameof(PlanWarningsCount));
        OnPropertyChanged(nameof(HasPlanWarnings));
        OnPropertyChanged(nameof(PlanWarnings));
        OnPropertyChanged(nameof(PlanNotesCount));
        OnPropertyChanged(nameof(HasPlanNotes));
        OnPropertyChanged(nameof(PlanNotes));
        OnPropertyChanged(nameof(PlanBlockersCount));
        OnPropertyChanged(nameof(HasPlanBlockers));
        OnPropertyChanged(nameof(PlanBlockers));
        OnPropertyChanged(nameof(HasPlanDetails));
        OnPropertyChanged(nameof(CanApplyPlan));
        OnPropertyChanged(nameof(ShowSteamStubHelperOption));
        OnPropertyChanged(nameof(PlanItems));
    }

    private void ClearPlan()
    {
        _plan = null;
        PlanItems = [];
    }

    private void SetBusy(bool busy, string status)
    {
        IsBusy = busy;
        ActivityStatus = status;
        GamePathBox.IsEnabled = !busy;
        ExecutableBox.IsEnabled = !busy;
        AppIdBox.IsEnabled = !busy;
        BrowseFolderButton.IsEnabled = !busy;
        BrowseMainExeButton.IsEnabled = !busy;
        BrowseExeButton.IsEnabled = !busy;
        DetectButton.IsEnabled = !busy;
        TargetCombo.IsEnabled = !busy;
        RefreshGeneratorCheckBox.IsEnabled = !busy;
        SteamStubHelperCheckBox.IsEnabled = !busy;
        GeneratePlanButton.IsEnabled = !busy;
        ApplyButton.IsEnabled = !busy && CanApplyPlan;
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

public sealed record EnablerTargetOption(EnablerTarget Value, string Label)
{
    public override string ToString() => Label;
}

public sealed class PlanItemViewModel
{
    public PlanItemViewModel(EnablerPlanItem item)
    {
        Kind = item.Kind;
        Description = item.Description;
        Destination = item.Destination;
        Source = item.Source is null ? string.Empty : $"From: {item.Source}";
        KindIcon = item.Kind switch
        {
            "copy" => "⇅",
            "write" => "✎",
            "delete" => "✕",
            "patch-ea-exe" => "⚙",
            _ => "◇"
        };
    }

    public string Kind { get; }
    public string Description { get; }
    public string Destination { get; }
    public string Source { get; }
    public string KindIcon { get; }
}
