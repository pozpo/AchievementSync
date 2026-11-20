using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Input;
using AchievementSync.Core.Enabler;

namespace AchievementSync.Desktop.ViewModels;

/// <summary>Multi-step wizard for the eternal Steam achievement generator flow.</summary>
internal sealed class SteamAchievementWizardViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly SteamCredentialStore _credentialStore = new();
    private SteamSchemaGeneratorService _generatorService;
    private readonly object _lock = new();
    private EnablerPlan? _currentPlan;
    private EnablerDetection? _lastDetection;

    public event PropertyChangedEventHandler? PropertyChanged;

    public SteamAchievementWizardViewModel(string? generatorRoot = null)
    {
        _generatorService = new SteamSchemaGeneratorService(generatorRoot);
        SignInCommand = new RelayCommand(_ => SignIn());
        SelectGameCommand = new RelayCommand(_ => SelectGame());
        DetectCommand = new AsyncCommand(DetectAsync, () => CanDetect);
        GeneratePlanCommand = new AsyncCommand(GeneratePlanAsync, () => CanGeneratePlan);
        ReviewPlanCommand = new RelayCommand(_ => ReviewPlan());
        ApplyPlanCommand = new AsyncCommand(ApplyPlanAsync, () => CanApplyPlan);
        BackCommand = new RelayCommand(_ => Back());
        CancelCommand = new RelayCommand(_ => Cancel());
        BrowseGameCommand = new RelayCommand(_ => BrowseGame());
        BrowseGeneratorCommand = new RelayCommand(_ => BrowseGenerator());
    }

    // ---- Navigation state ----

    public WizardStep CurrentStep
    {
        get => _currentStep;
        private set
        {
            if (Set(ref _currentStep, value))
            {
                OnPropertyChanged(nameof(StepProgressText));
                OnPropertyChanged(nameof(PageTitle));
                OnPropertyChanged(nameof(PageSubtitle));
                OnPropertyChanged(nameof(IsStepOne));
                OnPropertyChanged(nameof(IsStepTwo));
                OnPropertyChanged(nameof(IsStepThree));
                OnPropertyChanged(nameof(IsStepFour));
                OnPropertyChanged(nameof(IsStepFive));
                OnPropertyChanged(nameof(IsStepSix));
                OnPropertyChanged(nameof(IsStepSeven));
                OnPropertyChanged(nameof(IsStepApplied));
                OnPropertyChanged(nameof(CanCancel));
            }
        }
    }
    private WizardStep _currentStep = WizardStep.Welcome;

    public string StepProgressText => $"Step {_currentStep} of {WizardStepCount}";
    private const int WizardStepCount = 7;

    public bool IsStepOne => CurrentStep == WizardStep.Welcome;
    public bool IsStepTwo => CurrentStep == WizardStep.SignIn;
    public bool IsStepThree => CurrentStep == WizardStep.SelectGame;
    public bool IsStepFour => CurrentStep == WizardStep.Detect;
    public bool IsStepFive => CurrentStep == WizardStep.Configure;
    public bool IsStepSix => CurrentStep == WizardStep.GeneratePlan;
    public bool IsStepSeven => CurrentStep == WizardStep.ReviewPlan;
    public bool IsStepApplied => CurrentStep == WizardStep.Applied;

    public string PageTitle => CurrentStep switch
    {
        WizardStep.Welcome => "Steam Achievement Enabler",
        WizardStep.SignIn => "1. Sign in to Steam",
        WizardStep.SelectGame => "2. Select your game",
        WizardStep.Detect => "3. Detect game and AppID",
        WizardStep.Configure => "4. Configure generator",
        WizardStep.GeneratePlan => "5. Generate plan",
        WizardStep.ReviewPlan => "6. Review plan",
        WizardStep.Applied => "7. Plan applied",
        _ => "Steam Achievement Enabler"
    };

    public string PageSubtitle => CurrentStep switch
    {
        WizardStep.Welcome => "Permanent Steam achievement setup that runs from this app — no external apps required.",
        WizardStep.SignIn => "Save your Steam login once. Credentials stay in Windows Credential Manager.",
        WizardStep.SelectGame => "Pick a game folder or enter its Steam AppID.",
        WizardStep.Detect => "Read steam_appid.txt and detect the game executable.",
        WizardStep.Configure => "Point at the generate_emu_config.exe runtime and choose options.",
        WizardStep.GeneratePlan => "Run the local generator to produce the achievement catalog.",
        WizardStep.ReviewPlan => "Review every file that will be written before applying.",
        WizardStep.Applied => "Files written successfully. The game is ready to launch.",
        _ => string.Empty
    };

    // ---- Step 1: Welcome ----
    public ICommand SignInCommand { get; }
    public ICommand SelectGameCommand { get; }

    // ---- Step 2: Sign in ----
    public string SteamUsername
    {
        get => _steamUsername;
        set { if (Set(ref _steamUsername, value)) OnPropertyChanged(nameof(CanSignIn)); }
    }
    private string _steamUsername = string.Empty;

    public string SteamPassword
    {
        get => _steamPassword;
        set { if (Set(ref _steamPassword, value)) OnPropertyChanged(nameof(CanSignIn)); }
    }
    private string _steamPassword = string.Empty;

    public bool CanSignIn => !string.IsNullOrWhiteSpace(_steamUsername) && !string.IsNullOrWhiteSpace(_steamPassword);
    public bool CredentialsSaved => _generatorService.CredentialsConfigured;
    public string CredentialStatus =>
        _generatorService.CredentialsConfigured
            ? $"Steam credentials saved ({_steamUsername})"
            : "No Steam credentials saved yet.";

    public string SignInStatus
    {
        get => _signInStatus;
        private set { if (Set(ref _signInStatus, value)) OnPropertyChanged(); }
    }
    private string _signInStatus = string.Empty;

    public bool IsSigningIn
    {
        get => _isSigningIn;
        private set { if (Set(ref _isSigningIn, value)) OnPropertyChanged(); }
    }
    private bool _isSigningIn;

    private void SignIn()
    {
        if (!CanSignIn) return;
        _generatorService.SaveCredentials(_steamUsername.Trim(), _steamPassword);
        SignInStatus = $"Credentials saved for {_steamUsername.Trim()} in Windows Credential Manager.";
        OnPropertyChanged(nameof(CredentialsSaved));
        OnPropertyChanged(nameof(CredentialStatus));
    }

    public ICommand BrowseGeneratorCommand { get; }
    public string GeneratorRoot
    {
        get => _generatorService.GeneratorRoot;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
            {
                // Recreate the service with the new root
                _generatorService.Dispose();
                _generatorService = new SteamSchemaGeneratorService(value);
                OnPropertyChanged(nameof(GeneratorRoot));
                OnPropertyChanged(nameof(GeneratorConfigured));
                OnPropertyChanged(nameof(GeneratorStatusMessage));
            }
        }
    }
    public bool GeneratorConfigured => _generatorService.IsConfigured;
    public string GeneratorStatusMessage => GeneratorConfigured
        ? "Runtime found — generate_emu_config.exe ready"
        : "Runtime not found — generate_emu_config.exe missing";

    // ---- Step 3: Select game ----
    public string GameFolder
    {
        get => _gameFolder;
        set { if (Set(ref _gameFolder, value)) OnPropertyChanged(nameof(CanSelectGame)); }
    }
    private string _gameFolder = string.Empty;

    public string AppId
    {
        get => _appId;
        set { if (Set(ref _appId, value)) OnPropertyChanged(nameof(CanSelectGame)); }
    }
    private string _appId = string.Empty;

    public bool CanSelectGame => !string.IsNullOrWhiteSpace(_gameFolder) || !string.IsNullOrWhiteSpace(_appId);

    public ICommand BrowseGameCommand { get; }
    private void BrowseGame()
    {
        // Handled by the view via a file picker dialog
        BrowseGameRequested?.Invoke(this, EventArgs.Empty);
    }
    public event EventHandler? BrowseGameRequested;

    private void BrowseGenerator()
    {
        BrowseGeneratorRequested?.Invoke(this, EventArgs.Empty);
    }
    public event EventHandler? BrowseGeneratorRequested;

    private void SelectGame()
    {
        if (string.IsNullOrWhiteSpace(_gameFolder) && string.IsNullOrWhiteSpace(_appId)) return;
        MoveNext();
    }

    public string GameSelectionStatus
    {
        get => _gameSelectionStatus;
        private set { if (Set(ref _gameSelectionStatus, value)) OnPropertyChanged(); }
    }
    private string _gameSelectionStatus = string.Empty;

    // ---- Step 4: Detect ----
    public ICommand DetectCommand { get; }
    public bool CanDetect => !string.IsNullOrWhiteSpace(_gameFolder) && !IsDetecting;
    public bool IsDetecting
    {
        get => _isDetecting;
        private set { if (Set(ref _isDetecting, value)) OnPropertyChanged(); }
    }
    private bool _isDetecting;

    public string DetectionStatus
    {
        get => _detectionStatus;
        private set { if (Set(ref _detectionStatus, value)) OnPropertyChanged(); }
    }
    private string _detectionStatus = string.Empty;

    public string DetectedExecutable
    {
        get => _detectedExecutable;
        private set { if (Set(ref _detectedExecutable, value)) OnPropertyChanged(); }
    }
    private string _detectedExecutable = string.Empty;

    public string DetectedAppId
    {
        get => _detectedAppId;
        private set { if (Set(ref _detectedAppId, value)) OnPropertyChanged(); }
    }
    private string _detectedAppId = string.Empty;

    public IReadOnlyList<string> DetectionEvidence
    {
        get => _detectionEvidence;
        private set { if (Set(ref _detectionEvidence, value)) OnPropertyChanged(); }
    }
    private IReadOnlyList<string> _detectionEvidence = [];

    private async Task DetectAsync()
    {
        if (!CanDetect) return;
        IsDetecting = true;
        DetectionStatus = "Detecting game folder and AppID...";
        try
        {
            var folder = string.IsNullOrWhiteSpace(_gameFolder)
                ? Path.Combine(AppContext.BaseDirectory, "Games", _appId)
                : _gameFolder.Trim();

            // Use the detection logic from the enabler service
            var detection = new AchievementEnablerService().Detect(folder);
            _lastDetection = detection;

            DetectedExecutable = detection.Executable ?? "(not detected)";
            DetectedAppId = detection.SteamAppId ?? "(not detected)";
            DetectionEvidence = detection.Evidence;

            if (!string.IsNullOrWhiteSpace(detection.SteamAppId))
            {
                AppId = detection.SteamAppId;
                GameFolder = detection.GameDirectory;
                GameSelectionStatus = $"Detected: {Path.GetFileName(detection.GameDirectory)} · AppID {detection.SteamAppId}";
            }
            else
            {
                GameSelectionStatus = "No AppID detected — enter one manually below.";
            }

            DetectionStatus = "Detection complete.";
        }
        catch (Exception exception)
        {
            DetectionStatus = $"Detection failed: {exception.Message}";
        }
        finally
        {
            IsDetecting = false;
        }
    }

    // ---- Step 5: Configure ----
    public bool CanGeneratePlan =>
        !string.IsNullOrWhiteSpace(_appId) &&
        !string.IsNullOrWhiteSpace(_generatorService.GeneratorRoot) &&
        Directory.Exists(_generatorService.GeneratorRoot);

    public ICommand GeneratePlanCommand { get; }
    public bool IsGeneratingPlan
    {
        get => _isGeneratingPlan;
        private set { if (Set(ref _isGeneratingPlan, value)) OnPropertyChanged(); }
    }
    private bool _isGeneratingPlan;

    public string GenerationStatus
    {
        get => _generationStatus;
        private set { if (Set(ref _generationStatus, value)) OnPropertyChanged(); }
    }
    private string _generationStatus = string.Empty;

    public int GenerationProgress
    {
        get => _generationProgress;
        private set { if (Set(ref _generationProgress, value)) OnPropertyChanged(); }
    }
    private int _generationProgress;

    private async Task GeneratePlanAsync()
    {
        if (!CanGeneratePlan) return;
        IsGeneratingPlan = true;
        GenerationProgress = 0;
        GenerationStatus = "Refreshing Steam metadata...";
        try
        {
            var result = await _generatorService.GenerateSchemaAsync(
                _appId.Trim(),
                CancellationToken.None,
                new Progress<GeneratorProgress>(p =>
                {
                    GenerationProgress = p.Percent;
                    GenerationStatus = p.Detail;
                }));

            if (!result.Success)
            {
                GenerationStatus = result.Message;
                return;
            }

            GenerationStatus = "Building application plan...";
            await Task.Delay(300); // brief pause for UI feedback

            // Build the plan using the enabler service
            var enablerService = new AchievementEnablerService(
                _generatorService.GeneratorRoot,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AchievementSync", "metadata"));

            var detection = _lastDetection ?? new EnablerDetection(
                Path.Combine(AppContext.BaseDirectory, "Games", _appId),
                null,
                _appId,
                EnablerTarget.SteamEmulator,
                []);

            _currentPlan = enablerService.BuildPlan(detection, EnablerTarget.SteamEmulator, _appId.Trim());

            GenerationStatus = $"Plan ready: {_currentPlan.Items.Count} file(s) to write.";
            GenerationProgress = 100;

            // Auto-advance to review
            CurrentStep = WizardStep.ReviewPlan;
        }
        catch (Exception exception)
        {
            GenerationStatus = $"Generation failed: {exception.Message}";
        }
        finally
        {
            IsGeneratingPlan = false;
        }
    }

    public IReadOnlyList<PlanItemViewModel> PlanItems
    {
        get => _planItems;
        private set { if (Set(ref _planItems, value)) OnPropertyChanged(); }
    }
    private IReadOnlyList<PlanItemViewModel> _planItems = [];

    public string PlanSummary
    {
        get => _planSummary;
        private set { if (Set(ref _planSummary, value)) OnPropertyChanged(); }
    }
    private string _planSummary = string.Empty;

    // ---- Step 6: Review plan ----
    public ICommand ReviewPlanCommand { get; }
    private void ReviewPlan()
    {
        if (_currentPlan is null) return;
        PlanItems = new ObservableCollection<PlanItemViewModel>(
            _currentPlan.Items.Select(item => new PlanItemViewModel(item)));
        PlanSummary = $"{_currentPlan.Items.Count} file(s) ready to write " +
                      $"to {Path.GetFileName(_currentPlan.Detection.GameDirectory)} " +
                      $"(AppID {_currentPlan.MetadataGameId ?? "(not set)"}).";
        CurrentStep = WizardStep.ReviewPlan;
    }

    public ICommand ApplyPlanCommand { get; }
    public bool CanApplyPlan => _currentPlan is not null && !IsApplyingPlan;
    public bool IsApplyingPlan
    {
        get => _isApplyingPlan;
        private set { if (Set(ref _isApplyingPlan, value)) OnPropertyChanged(); }
    }
    private bool _isApplyingPlan;

    public string ApplyStatus
    {
        get => _applyStatus;
        private set { if (Set(ref _applyStatus, value)) OnPropertyChanged(); }
    }
    private string _applyStatus = string.Empty;

    private async Task ApplyPlanAsync()
    {
        if (_currentPlan is null || IsApplyingPlan) return;
        IsApplyingPlan = true;
        ApplyStatus = "Applying plan...";
        try
        {
            var enablerService = new AchievementEnablerService(
                _generatorService.GeneratorRoot,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AchievementSync", "metadata"));

            var result = enablerService.Apply(_currentPlan);

            ApplyStatus = $"Applied: {result.FilesWritten} file(s) written, {result.FilesSkipped} skipped.";
            foreach (var message in result.Messages)
                ApplyStatus += " · " + message;

            CurrentStep = WizardStep.Applied;
        }
        catch (Exception exception)
        {
            ApplyStatus = $"Apply failed: {exception.Message}";
        }
        finally
        {
            IsApplyingPlan = false;
        }
    }

    // ---- Navigation ----
    public ICommand BackCommand { get; }
    public ICommand CancelCommand { get; }

    private void Back()
    {
        CurrentStep = CurrentStep switch
        {
            WizardStep.SignIn => WizardStep.Welcome,
            WizardStep.SelectGame => WizardStep.SignIn,
            WizardStep.Detect => WizardStep.SelectGame,
            WizardStep.Configure => WizardStep.Detect,
            WizardStep.GeneratePlan => WizardStep.Configure,
            WizardStep.ReviewPlan => WizardStep.GeneratePlan,
            WizardStep.Applied => WizardStep.ReviewPlan,
            _ => CurrentStep
        };
    }

    private void Cancel()
    {
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }
    public event EventHandler? CancelRequested;

    public bool CanCancel => CurrentStep != WizardStep.Welcome && CurrentStep != WizardStep.Applied;

    private void MoveNext()
    {
        CurrentStep = CurrentStep switch
        {
            WizardStep.Welcome => WizardStep.SignIn,
            WizardStep.SignIn => WizardStep.SelectGame,
            WizardStep.SelectGame => WizardStep.Detect,
            WizardStep.Detect => WizardStep.Configure,
            WizardStep.Configure => WizardStep.GeneratePlan,
            WizardStep.GeneratePlan => WizardStep.ReviewPlan,
            WizardStep.ReviewPlan => WizardStep.Applied,
            _ => CurrentStep
        };
    }

    public void Dispose()
    {
        _generatorService.Dispose();
    }

    // ---- Helpers ----

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>Wizard step enumeration.</summary>
public enum WizardStep
{
    Welcome,
    SignIn,
    SelectGame,
    Detect,
    Configure,
    GeneratePlan,
    ReviewPlan,
    Applied
}

/// <summary>View model for a single plan item.</summary>
public sealed class PlanItemViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Kind { get; }
    public string Destination { get; }
    public string? Source { get; }
    public string Description { get; }
    public bool IsCopy => string.Equals(Kind, "copy", StringComparison.OrdinalIgnoreCase);
    public bool IsWrite => string.Equals(Kind, "write", StringComparison.OrdinalIgnoreCase);

    private bool _isSelected = true;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (Set(ref _isSelected, value)) OnPropertyChanged(); }
    }

    public PlanItemViewModel(EnablerPlanItem item)
    {
        Kind = item.Kind;
        Destination = item.Destination;
        Source = item.Source;
        Description = item.Description;
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

