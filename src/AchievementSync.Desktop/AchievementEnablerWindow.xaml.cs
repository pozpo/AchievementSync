using System.Windows;
using System.IO;
using Microsoft.Win32;
using AchievementSync.Core.Enabler;
using AchievementSync.Core.Metadata;
using AchievementSync.Desktop.Services;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using MessageBox = System.Windows.MessageBox;

namespace AchievementSync.Desktop;

public sealed partial class AchievementEnablerWindow : Window, System.ComponentModel.INotifyPropertyChanged
{
    private readonly AchievementEnablerService _service;
    private EnablerDetection? _detection;
    private EnablerPlan? _plan;
    private string _gamePath = string.Empty;
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

    public AchievementEnablerWindow(AchievementEnablerService service)
    {
        InitializeComponent();
        _service = service;
        DataContext = this;
        RefreshSteamAccountStatus();
        Closed += (_, _) => _operationCancellation?.Cancel();
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
            SteamAccountStatus = "No Steam credentials saved yet. Enter your Steam username and password below to save credentials and refresh token in Windows Credential Manager.";
        }
    }

    private void SaveSteamLogin_Click(object sender, RoutedEventArgs e)
    {
        var user = SteamUsernameBox.Text?.Trim();
        var pass = SteamPasswordBox.Password;
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
        {
            MessageBox.Show("Please enter both Steam username and password.", "Achievement Enabler", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            _generatorService.SaveCredentials(user, pass);
            SteamPasswordBox.Clear();
            RefreshSteamAccountStatus();
            ActivityStatus = $"Steam credentials saved for '{user}'.";
            MessageBox.Show($"Steam credentials saved for '{user}'. The internal generator will use this login and store the refresh token safely.", "Achievement Enabler", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save Steam credentials: {ex.Message}", "Achievement Enabler", MessageBoxButton.OK, MessageBoxImage.Error);
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
            UpdateBindings();
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
        new(EnablerTarget.SteamEmulator, "Steam (ColdClient launcher + generated catalog)"),
        new(EnablerTarget.UplayR2, "Ubisoft Connect R2 emulator"),
        new(EnablerTarget.UplayR1, "Ubisoft Connect R1 emulator"),
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
    public IReadOnlyList<PlanItemViewModel> PlanItems { get; private set; } = [];

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select the game folder" };
        if (dialog.ShowDialog() == true) GamePath = dialog.FolderName;
    }

    private void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select the game executable", Filter = "Executable (*.exe)|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog() == true) GamePath = dialog.FileName;
    }

    private async void Detect_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(GamePath) || !System.IO.File.Exists(GamePath) && !System.IO.Directory.Exists(GamePath))
        {
            MessageBox.Show("Pick a valid folder or executable first.", "Achievement Enabler", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var selectedPath = GamePath;
        SetBusy(true, "Inspecting the selected folder. This window remains active during the scan…");
        try
        {
            _detection = await Task.Run(() => _service.Detect(selectedPath));
            if (!string.IsNullOrWhiteSpace(SteamAppId))
                _detection = _detection with { SteamAppId = SteamAppId.Trim() };

            SelectedTarget = _detection.SuggestedTarget;
            EvidenceText.Text = string.Join("\n", _detection.Evidence);
            _plan = null;
            PlanItems = [];
            ActivityStatus = "Detection complete. Review the suggested target, then generate a plan.";
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
        var target = SelectedTarget;
        var gameId = string.IsNullOrWhiteSpace(SteamAppId) ? null : SteamAppId.Trim();
        using var operation = new CancellationTokenSource();
        _operationCancellation = operation;
        var shouldRefreshGenerator = RefreshGeneratorCheckBox.IsChecked == true
            && target is EnablerTarget.SteamEmulator or EnablerTarget.UplayR1 or EnablerTarget.UplayR2;
        var refreshMessage = string.Empty;
        SetBusy(true, shouldRefreshGenerator
            ? "Refreshing this AppID with the configured generator. This can take several minutes…"
            : "Preparing the plan preview…");
        try
        {
            if (shouldRefreshGenerator)
            {
                var refresh = await _service.RefreshSteamMetadataAsync(gameId ?? detection.SteamAppId ?? string.Empty, operation.Token);
                refreshMessage = refresh.Message;
                operation.Token.ThrowIfCancellationRequested();
                ActivityStatus = string.IsNullOrWhiteSpace(refreshMessage)
                    ? "Building the plan preview…"
                    : refreshMessage + " Building the plan preview…";
            }

            operation.Token.ThrowIfCancellationRequested();
            _plan = await Task.Run(() => _service.BuildPlan(detection, target, gameId), operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            PlanItems = _plan.Items.Select(item => new PlanItemViewModel(item)).ToArray();
            var refreshSummary = string.IsNullOrWhiteSpace(refreshMessage) ? string.Empty : " " + refreshMessage;
            ActivityStatus = $"Plan ready: {_plan.Items.Count} operation(s). Nothing has been written; review it before Apply.{refreshSummary}";
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
            DialogResult = true;
            Close();
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
        DialogResult = false;
        Close();
    }

    private void UpdateBindings()
    {
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(ShowGenerateButton));
        OnPropertyChanged(nameof(PlanWarningsCount));
        OnPropertyChanged(nameof(HasPlanWarnings));
        OnPropertyChanged(nameof(PlanWarnings));
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
        AppIdBox.IsEnabled = !busy;
        BrowseFolderButton.IsEnabled = !busy;
        BrowseExeButton.IsEnabled = !busy;
        DetectButton.IsEnabled = !busy;
        TargetCombo.IsEnabled = !busy;
        RefreshGeneratorCheckBox.IsEnabled = !busy;
        GeneratePlanButton.IsEnabled = !busy;
        ApplyButton.IsEnabled = !busy;
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
            _ => "◇"
        };
    }

    public string Kind { get; }
    public string Description { get; }
    public string Destination { get; }
    public string Source { get; }
    public string KindIcon { get; }
}
