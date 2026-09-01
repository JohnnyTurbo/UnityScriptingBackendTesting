using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using CoreCLRTest.PerformanceTests;
using TMG.CoreCLRTest;
using Unity.Entities;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreCLRTest.UI
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(UIDocument))]
    [RequireComponent(typeof(MovementPerformanceTestRunner))]
    [RequireComponent(typeof(PlinkoPerformanceTestRunner))]
    [RequireComponent(typeof(PathfindingPerformanceTestRunner))]
    internal sealed class PerformanceTestMenuController : MonoBehaviour
    {
        private const int MinimumTargetFrameRate = 10;
        private const int MaximumTargetFrameRate = 1000;
        private const int MinimumFrameRateDelta = 0;
        private const int MaximumFrameRateDelta = 1000;
        private const int DefaultFrameRateDelta = 1;
        private const int ThirtyFramesPerSecond = 30;
        private const int DefaultTargetFrameRate = 60;
        private const int MovementPerformanceTestIndex = 0;
        private const int PlinkoPerformanceTestIndex = 1;
        private const int PathfindingPerformanceTestIndex = 2;
        private const int NoActivePerformanceTestIndex = -1;
        private const int FirstCollectionIndex = 0;
        private const int CollectionIndexToDisplayNumberOffset = 1;
        private const int TestNotSelectedNumber = 0;
        private const float MinimumFrameDurationSeconds = 0.000001f;
        private const float CurrentFrameRateRefreshIntervalSeconds = 0.25f;
        private const float TooltipHorizontalOffset = 16f;
        private const float TooltipVerticalOffset = 16f;
        private const int UnlockedTargetFrameRate = -1;
        private const double MillisecondsPerSecond = 1000d;
        private const double PercentageScale = 100d;
        private const double BytesPerDecimalGigabyte = 1_000_000_000d;

        private const string MainMenuScreenName = "main-menu-screen";
        private const string MainBuildConfigurationLabelName = "main-build-configuration-label";
        private const string ResultsScreenName = "results-screen";
        private const string ResultsBuildConfigurationLabelName = "results-build-configuration-label";
        private const string RunningTestOverlayName = "running-test-overlay";
        private const string RunningTestTitleLabelName = "running-test-title-label";
        private const string RunningTestCountLabelName = "running-test-count-label";
        private const string RunningResolutionLabelName = "running-resolution-label";
        private const string RunningTargetFrameRateLabelName = "running-target-frame-rate-label";
        private const string RunningFrameRateDeltaLabelName = "running-frame-rate-delta-label";
        private const string RunningCurrentFrameRateLabelName = "running-current-frame-rate-label";
        private const string RunningEntityCountLabelName = "running-entity-count-label";
        private const string EndTestButtonName = "end-test-button";
        private const string ResolutionDropdownName = "resolution-dropdown";
        private const string FullscreenToggleName = "fullscreen-toggle";
        private const string TargetFrameRateDropdownName = "target-frame-rate-dropdown";
        private const string TargetFrameRateFieldName = "target-frame-rate-field";
        private const string TargetFrameRateWarningName = "target-frame-rate-warning";
        private const string TargetFrameRateRowName = "target-frame-rate-row";
        private const string FrameRateDeltaFieldName = "frame-rate-delta-field";
        private const string FrameRateDeltaWarningName = "frame-rate-delta-warning";
        private const string FrameRateDeltaRowName = "frame-rate-delta-row";
        private const string PerformanceTest1RowName = "performance-test-1-row";
        private const string PerformanceTest2RowName = "performance-test-2-row";
        private const string PerformanceTest3RowName = "performance-test-3-row";
        private const string PerformanceTestTooltipName = "performance-test-tooltip";
        private const string PerformanceTest1ToggleName = "performance-test-1-toggle";
        private const string PerformanceTest2ToggleName = "performance-test-2-toggle";
        private const string PerformanceTest3ToggleName = "performance-test-3-toggle";
        private const string RunTestsButtonName = "run-tests-button";
        private const string MainQuitButtonName = "main-quit-button";
        private const string ResultsTargetFrameRateLabelName = "results-target-frame-rate-label";
        private const string ResultsFrameRateDeltaLabelName = "results-frame-rate-delta-label";
        private const string ResultsResolutionLabelName = "results-resolution-label";
        private const string ResultsProcessCpuHeaderName = "results-process-cpu-header";
        private const string ResultsCpuFrameTimeHeaderName = "results-cpu-frame-time-header";
        private const string ResultsGpuFrameTimeHeaderName = "results-gpu-frame-time-header";
        private const string ResultsPeakAppMemoryHeaderName = "results-peak-app-memory-header";
        private const string ResultsTableBodyName = "results-table-body";
        private const string ReturnToMenuButtonName = "return-to-menu-button";
        private const string ResultsQuitButtonName = "results-quit-button";

        private const string HiddenClassName = "hidden";
        private const string ValidationWarningHiddenClassName = "validation-warning-hidden";
        private const string PhysicsUpdateRateWarningClassName = "physics-update-rate-warning";
        private const string ThirtyTargetFrameRateOption = "30";
        private const string SixtyTargetFrameRateOption = "60";
        private const string CustomTargetFrameRateOption = "Custom";
        private const string ResultsRowClassName = "results-row";
        private const string ResultsCellClassName = "results-cell";
        private const string ResultsTestNameColumnClassName = "results-test-name-column";
        private const string ResultsEntityCountColumnClassName = "results-entity-count-column";
        private const string ResultsProcessCpuColumnClassName = "results-process-cpu-column";
        private const string ResultsFrameTimeColumnClassName = "results-frame-time-column";
        private const string ResultsMemoryColumnClassName = "results-memory-column";
        private const string TargetFrameRateWarningText = "Target frame rate must be between 10 and 1000.";
        private const string PhysicsUpdateRateWarningFormat = "Physics update rate will be changed to {0} Hz to match the FPS target.";
        private const string FrameRateDeltaWarningText = "Frame rate delta must be between 0 and 1000.";
        private const string TargetFrameRateSummaryPrefix = "Target Frame Rate: ";
        private const string FrameRateDeltaSummaryPrefix = "Frame Rate Delta: ±";
        private const string FramesPerSecondSuffix = " FPS";
        private const string RunningTestTitlePrefix = "Now Running: ";
        private const string RunningTestCountFormat = "(Test {0} of {1})";
        private const string RunningResolutionPrefix = "Resolution: ";
        private const string RunningTargetFrameRatePrefix = "Target FPS: ";
        private const string RunningFrameRateDeltaPrefix = "FPS Delta: ±";
        private const string RunningCurrentFrameRatePrefix = "Current FPS: ";
        private const string RunningEntityCountPrefix = "Entity Count: ";
        private const string ResolutionSummaryPrefix = "Render Resolution: ";
        private const string PlaceholderResultValue = "—";
        private const string FailedResultValue = "Error";
        private const string ProcessCpuResultFormat = "{0:F1}%";
        private const string FrameTimeResultFormat = "{0:F2} ms ({1:F1}%)";
        private const string MemoryResultFormat = "{0:F1} GB";

        [NoAutoStaticsCleanup]
        private static readonly ResolutionOption[] SupportedResolutions =
        {
            new ResolutionOption("1280 × 720 (720p)", 1280, 720),
            new ResolutionOption("1920 × 1080 (1080p)", 1920, 1080),
            new ResolutionOption("2560 × 1440 (1440p)", 2560, 1440),
            new ResolutionOption("3840 × 2160 (2160p)", 3840, 2160)
        };

        private static readonly string[] PerformanceTestNames =
        {
            "Random Movement",
            "Plinko Physics",
            "A* Pathfinding"
        };

        [SerializeField]
        private PerformanceTestMenuSettings menuSettings;

        private UIDocument uiDocument;
        private MovementPerformanceTestRunner movementPerformanceTestRunner;
        private PlinkoPerformanceTestRunner plinkoPerformanceTestRunner;
        private PathfindingPerformanceTestRunner pathfindingPerformanceTestRunner;
        private VisualElement mainMenuScreen;
        private Label mainBuildConfigurationLabel;
        private VisualElement resultsScreen;
        private Label resultsBuildConfigurationLabel;
        private VisualElement runningTestOverlay;
        private Label runningTestTitleLabel;
        private Label runningTestCountLabel;
        private Label runningResolutionLabel;
        private Label runningTargetFrameRateLabel;
        private Label runningFrameRateDeltaLabel;
        private Label runningCurrentFrameRateLabel;
        private Label runningEntityCountLabel;
        private Button endTestButton;
        private DropdownField resolutionDropdown;
        private Toggle fullscreenToggle;
        private DropdownField targetFrameRateDropdown;
        private IntegerField targetFrameRateField;
        private Label targetFrameRateWarning;
        private VisualElement targetFrameRateRow;
        private IntegerField frameRateDeltaField;
        private Label frameRateDeltaWarning;
        private VisualElement frameRateDeltaRow;
        private VisualElement performanceTest1Row;
        private VisualElement performanceTest2Row;
        private VisualElement performanceTest3Row;
        private Label performanceTestTooltip;
        private Toggle performanceTest1Toggle;
        private Toggle performanceTest2Toggle;
        private Toggle performanceTest3Toggle;
        private Button runTestsButton;
        private Button mainQuitButton;
        private Label resultsTargetFrameRateLabel;
        private Label resultsFrameRateDeltaLabel;
        private Label resultsResolutionLabel;
        private Label resultsProcessCpuHeader;
        private Label resultsCpuFrameTimeHeader;
        private Label resultsGpuFrameTimeHeader;
        private Label resultsPeakAppMemoryHeader;
        private VisualElement resultsTableBody;
        private Button returnToMenuButton;
        private Button resultsQuitButton;
        private Toggle[] performanceTestToggles;
        private VisualElement[] performanceTestRows;

        private ResolutionOption selectedResolution;
        private IReadOnlyList<SelectedPerformanceTest> snapshotSelectedTests;
        private int snapshotTargetFrameRate;
        private int snapshotFrameRateDelta;
        private int activeRunVersion;
        private int activePerformanceTestIndex = NoActivePerformanceTestIndex;
        private int currentFrameCount;
        private float currentFrameElapsedSeconds;
        private ResolutionOption snapshotResolution;
        private Coroutine activeRunCoroutine;
        private Func<int> activeEntityCountSource;
        private bool[] manuallyEndedPerformanceTests;
        private bool callbacksRegistered;
        private bool isTransitionActive;

        private void OnEnable()
        {
            try
            {
                uiDocument = GetComponent<UIDocument>();
                movementPerformanceTestRunner = GetComponent<MovementPerformanceTestRunner>();
                plinkoPerformanceTestRunner = GetComponent<PlinkoPerformanceTestRunner>();
                pathfindingPerformanceTestRunner = GetComponent<PathfindingPerformanceTestRunner>();
                if (uiDocument == null)
                {
                    throw new InvalidOperationException("A UIDocument component is required.");
                }

                if (movementPerformanceTestRunner == null)
                {
                    throw new InvalidOperationException("A MovementPerformanceTestRunner component is required.");
                }

                if (plinkoPerformanceTestRunner == null)
                {
                    throw new InvalidOperationException("A PlinkoPerformanceTestRunner component is required.");
                }

                if (pathfindingPerformanceTestRunner == null)
                {
                    throw new InvalidOperationException("A PathfindingPerformanceTestRunner component is required.");
                }

                if (menuSettings == null)
                {
                    throw new InvalidOperationException("A PerformanceTestMenuSettings asset must be assigned.");
                }

                CacheVisualElements();
                RefreshBuildConfigurationLabels();
                InitializeApplicationFramePacing();
                InitializeConfiguration();
                selectedResolution = SelectInitialResolution(Screen.currentResolution);
                resolutionDropdown.SetValueWithoutNotify(selectedResolution.DisplayName);
                RegisterCallbacks();
                ApplyDisplaySettings();
                ShowMainMenu();
            }
            catch (InvalidOperationException exception)
            {
                Debug.LogError($"{nameof(PerformanceTestMenuController)} could not initialize: {exception.Message}", this);
                enabled = false;
            }
        }

        private void OnDisable()
        {
            UnregisterCallbacks();
            movementPerformanceTestRunner?.Cancel();
            plinkoPerformanceTestRunner?.Cancel();
            pathfindingPerformanceTestRunner?.Cancel();
            activeEntityCountSource = null;
            activePerformanceTestIndex = NoActivePerformanceTestIndex;
            manuallyEndedPerformanceTests = null;
            activeRunVersion++;
            activeRunCoroutine = null;
            isTransitionActive = false;
            HideRunningOverlay();
        }

        private void Update()
        {
            if (!isTransitionActive || runningTestOverlay == null || runningTestOverlay.ClassListContains(HiddenClassName)) return;

            var frameDuration = Time.unscaledDeltaTime;
            if (frameDuration > MinimumFrameDurationSeconds)
            {
                currentFrameCount++;
                currentFrameElapsedSeconds += frameDuration;
                if (currentFrameElapsedSeconds >= CurrentFrameRateRefreshIntervalSeconds)
                {
                    var currentFrameRate = currentFrameCount / currentFrameElapsedSeconds;
                    runningCurrentFrameRateLabel.text = RunningCurrentFrameRatePrefix + currentFrameRate.ToString("F1", CultureInfo.InvariantCulture) + FramesPerSecondSuffix;
                    currentFrameCount = 0;
                    currentFrameElapsedSeconds = 0f;
                }
            }

            var currentEntityCount = activeEntityCountSource != null ? activeEntityCountSource() : 0;
            runningEntityCountLabel.text = RunningEntityCountPrefix + currentEntityCount.ToString("N0", CultureInfo.InvariantCulture);
        }

        private void CacheVisualElements()
        {
            var root = uiDocument.rootVisualElement;
            mainMenuScreen = root.Q<VisualElement>(MainMenuScreenName);
            mainBuildConfigurationLabel = root.Q<Label>(MainBuildConfigurationLabelName);
            resultsScreen = root.Q<VisualElement>(ResultsScreenName);
            resultsBuildConfigurationLabel = root.Q<Label>(ResultsBuildConfigurationLabelName);
            runningTestOverlay = root.Q<VisualElement>(RunningTestOverlayName);
            runningTestTitleLabel = root.Q<Label>(RunningTestTitleLabelName);
            runningTestCountLabel = root.Q<Label>(RunningTestCountLabelName);
            runningResolutionLabel = root.Q<Label>(RunningResolutionLabelName);
            runningTargetFrameRateLabel = root.Q<Label>(RunningTargetFrameRateLabelName);
            runningFrameRateDeltaLabel = root.Q<Label>(RunningFrameRateDeltaLabelName);
            runningCurrentFrameRateLabel = root.Q<Label>(RunningCurrentFrameRateLabelName);
            runningEntityCountLabel = root.Q<Label>(RunningEntityCountLabelName);
            endTestButton = root.Q<Button>(EndTestButtonName);
            resolutionDropdown = root.Q<DropdownField>(ResolutionDropdownName);
            fullscreenToggle = root.Q<Toggle>(FullscreenToggleName);
            targetFrameRateDropdown = root.Q<DropdownField>(TargetFrameRateDropdownName);
            targetFrameRateField = root.Q<IntegerField>(TargetFrameRateFieldName);
            targetFrameRateWarning = root.Q<Label>(TargetFrameRateWarningName);
            targetFrameRateRow = root.Q<VisualElement>(TargetFrameRateRowName);
            frameRateDeltaField = root.Q<IntegerField>(FrameRateDeltaFieldName);
            frameRateDeltaWarning = root.Q<Label>(FrameRateDeltaWarningName);
            frameRateDeltaRow = root.Q<VisualElement>(FrameRateDeltaRowName);
            performanceTest1Row = root.Q<VisualElement>(PerformanceTest1RowName);
            performanceTest2Row = root.Q<VisualElement>(PerformanceTest2RowName);
            performanceTest3Row = root.Q<VisualElement>(PerformanceTest3RowName);
            performanceTestTooltip = root.Q<Label>(PerformanceTestTooltipName);
            performanceTest1Toggle = root.Q<Toggle>(PerformanceTest1ToggleName);
            performanceTest2Toggle = root.Q<Toggle>(PerformanceTest2ToggleName);
            performanceTest3Toggle = root.Q<Toggle>(PerformanceTest3ToggleName);
            runTestsButton = root.Q<Button>(RunTestsButtonName);
            mainQuitButton = root.Q<Button>(MainQuitButtonName);
            resultsTargetFrameRateLabel = root.Q<Label>(ResultsTargetFrameRateLabelName);
            resultsFrameRateDeltaLabel = root.Q<Label>(ResultsFrameRateDeltaLabelName);
            resultsResolutionLabel = root.Q<Label>(ResultsResolutionLabelName);
            resultsProcessCpuHeader = root.Q<Label>(ResultsProcessCpuHeaderName);
            resultsCpuFrameTimeHeader = root.Q<Label>(ResultsCpuFrameTimeHeaderName);
            resultsGpuFrameTimeHeader = root.Q<Label>(ResultsGpuFrameTimeHeaderName);
            resultsPeakAppMemoryHeader = root.Q<Label>(ResultsPeakAppMemoryHeaderName);
            resultsTableBody = root.Q<VisualElement>(ResultsTableBodyName);
            returnToMenuButton = root.Q<Button>(ReturnToMenuButtonName);
            resultsQuitButton = root.Q<Button>(ResultsQuitButtonName);

            var missingElementNames = new List<string>();
            AddMissingElementName(mainMenuScreen, MainMenuScreenName, missingElementNames);
            AddMissingElementName(mainBuildConfigurationLabel, MainBuildConfigurationLabelName, missingElementNames);
            AddMissingElementName(resultsScreen, ResultsScreenName, missingElementNames);
            AddMissingElementName(resultsBuildConfigurationLabel, ResultsBuildConfigurationLabelName, missingElementNames);
            AddMissingElementName(runningTestOverlay, RunningTestOverlayName, missingElementNames);
            AddMissingElementName(runningTestTitleLabel, RunningTestTitleLabelName, missingElementNames);
            AddMissingElementName(runningTestCountLabel, RunningTestCountLabelName, missingElementNames);
            AddMissingElementName(runningResolutionLabel, RunningResolutionLabelName, missingElementNames);
            AddMissingElementName(runningTargetFrameRateLabel, RunningTargetFrameRateLabelName, missingElementNames);
            AddMissingElementName(runningFrameRateDeltaLabel, RunningFrameRateDeltaLabelName, missingElementNames);
            AddMissingElementName(runningCurrentFrameRateLabel, RunningCurrentFrameRateLabelName, missingElementNames);
            AddMissingElementName(runningEntityCountLabel, RunningEntityCountLabelName, missingElementNames);
            AddMissingElementName(endTestButton, EndTestButtonName, missingElementNames);
            AddMissingElementName(resolutionDropdown, ResolutionDropdownName, missingElementNames);
            AddMissingElementName(fullscreenToggle, FullscreenToggleName, missingElementNames);
            AddMissingElementName(targetFrameRateDropdown, TargetFrameRateDropdownName, missingElementNames);
            AddMissingElementName(targetFrameRateField, TargetFrameRateFieldName, missingElementNames);
            AddMissingElementName(targetFrameRateWarning, TargetFrameRateWarningName, missingElementNames);
            AddMissingElementName(targetFrameRateRow, TargetFrameRateRowName, missingElementNames);
            AddMissingElementName(frameRateDeltaField, FrameRateDeltaFieldName, missingElementNames);
            AddMissingElementName(frameRateDeltaWarning, FrameRateDeltaWarningName, missingElementNames);
            AddMissingElementName(frameRateDeltaRow, FrameRateDeltaRowName, missingElementNames);
            AddMissingElementName(performanceTest1Row, PerformanceTest1RowName, missingElementNames);
            AddMissingElementName(performanceTest2Row, PerformanceTest2RowName, missingElementNames);
            AddMissingElementName(performanceTest3Row, PerformanceTest3RowName, missingElementNames);
            AddMissingElementName(performanceTestTooltip, PerformanceTestTooltipName, missingElementNames);
            AddMissingElementName(performanceTest1Toggle, PerformanceTest1ToggleName, missingElementNames);
            AddMissingElementName(performanceTest2Toggle, PerformanceTest2ToggleName, missingElementNames);
            AddMissingElementName(performanceTest3Toggle, PerformanceTest3ToggleName, missingElementNames);
            AddMissingElementName(runTestsButton, RunTestsButtonName, missingElementNames);
            AddMissingElementName(mainQuitButton, MainQuitButtonName, missingElementNames);
            AddMissingElementName(resultsTargetFrameRateLabel, ResultsTargetFrameRateLabelName, missingElementNames);
            AddMissingElementName(resultsFrameRateDeltaLabel, ResultsFrameRateDeltaLabelName, missingElementNames);
            AddMissingElementName(resultsResolutionLabel, ResultsResolutionLabelName, missingElementNames);
            AddMissingElementName(resultsProcessCpuHeader, ResultsProcessCpuHeaderName, missingElementNames);
            AddMissingElementName(resultsCpuFrameTimeHeader, ResultsCpuFrameTimeHeaderName, missingElementNames);
            AddMissingElementName(resultsGpuFrameTimeHeader, ResultsGpuFrameTimeHeaderName, missingElementNames);
            AddMissingElementName(resultsPeakAppMemoryHeader, ResultsPeakAppMemoryHeaderName, missingElementNames);
            AddMissingElementName(resultsTableBody, ResultsTableBodyName, missingElementNames);
            AddMissingElementName(returnToMenuButton, ReturnToMenuButtonName, missingElementNames);
            AddMissingElementName(resultsQuitButton, ResultsQuitButtonName, missingElementNames);

            if (missingElementNames.Count > 0)
            {
                throw new InvalidOperationException(
                    $"The assigned UXML is missing required named elements: {string.Join(", ", missingElementNames)}.");
            }

            performanceTestToggles = new[]
            {
                performanceTest1Toggle,
                performanceTest2Toggle,
                performanceTest3Toggle
            };

            performanceTestRows = new[]
            {
                performanceTest1Row,
                performanceTest2Row,
                performanceTest3Row
            };
        }

        private static void AddMissingElementName(
            VisualElement visualElement,
            string elementName,
            ICollection<string> missingElementNames)
        {
            if (visualElement == null)
            {
                missingElementNames.Add(elementName);
            }
        }

        private void RegisterCallbacks()
        {
            if (callbacksRegistered)
            {
                return;
            }

            resolutionDropdown.RegisterValueChangedCallback(OnResolutionChanged);
            fullscreenToggle.RegisterValueChangedCallback(OnFullscreenChanged);
            targetFrameRateDropdown.RegisterValueChangedCallback(OnTargetFrameRateSelectionChanged);
            targetFrameRateField.RegisterValueChangedCallback(OnTargetFrameRateChanged);
            targetFrameRateRow.RegisterCallback<PointerEnterEvent>(OnTargetFrameRateRowPointerEntered);
            targetFrameRateRow.RegisterCallback<PointerMoveEvent>(OnTooltipPointerMoved);
            targetFrameRateRow.RegisterCallback<PointerLeaveEvent>(OnTooltipPointerLeft);
            frameRateDeltaField.RegisterValueChangedCallback(OnFrameRateDeltaChanged);
            frameRateDeltaRow.RegisterCallback<PointerEnterEvent>(OnFrameRateDeltaRowPointerEntered);
            frameRateDeltaRow.RegisterCallback<PointerMoveEvent>(OnTooltipPointerMoved);
            frameRateDeltaRow.RegisterCallback<PointerLeaveEvent>(OnTooltipPointerLeft);
            foreach (Toggle performanceTestToggle in performanceTestToggles)
            {
                performanceTestToggle.RegisterValueChangedCallback(OnTestSelectionChanged);
            }

            foreach (VisualElement performanceTestRow in performanceTestRows)
            {
                performanceTestRow.RegisterCallback<PointerEnterEvent>(OnPerformanceTestRowPointerEntered);
                performanceTestRow.RegisterCallback<PointerMoveEvent>(OnTooltipPointerMoved);
                performanceTestRow.RegisterCallback<PointerLeaveEvent>(OnTooltipPointerLeft);
            }

            runTestsButton.clicked += OnRunTestsClicked;
            endTestButton.clicked += OnEndTestClicked;
            mainQuitButton.clicked += QuitApplication;
            returnToMenuButton.clicked += ShowMainMenu;
            resultsQuitButton.clicked += QuitApplication;
            callbacksRegistered = true;
        }

        private void UnregisterCallbacks()
        {
            if (!callbacksRegistered)
            {
                return;
            }

            resolutionDropdown.UnregisterValueChangedCallback(OnResolutionChanged);
            fullscreenToggle.UnregisterValueChangedCallback(OnFullscreenChanged);
            targetFrameRateDropdown.UnregisterValueChangedCallback(OnTargetFrameRateSelectionChanged);
            targetFrameRateField.UnregisterValueChangedCallback(OnTargetFrameRateChanged);
            targetFrameRateRow.UnregisterCallback<PointerEnterEvent>(OnTargetFrameRateRowPointerEntered);
            targetFrameRateRow.UnregisterCallback<PointerMoveEvent>(OnTooltipPointerMoved);
            targetFrameRateRow.UnregisterCallback<PointerLeaveEvent>(OnTooltipPointerLeft);
            frameRateDeltaField.UnregisterValueChangedCallback(OnFrameRateDeltaChanged);
            frameRateDeltaRow.UnregisterCallback<PointerEnterEvent>(OnFrameRateDeltaRowPointerEntered);
            frameRateDeltaRow.UnregisterCallback<PointerMoveEvent>(OnTooltipPointerMoved);
            frameRateDeltaRow.UnregisterCallback<PointerLeaveEvent>(OnTooltipPointerLeft);
            foreach (Toggle performanceTestToggle in performanceTestToggles)
            {
                performanceTestToggle.UnregisterValueChangedCallback(OnTestSelectionChanged);
            }

            foreach (VisualElement performanceTestRow in performanceTestRows)
            {
                performanceTestRow.UnregisterCallback<PointerEnterEvent>(OnPerformanceTestRowPointerEntered);
                performanceTestRow.UnregisterCallback<PointerMoveEvent>(OnTooltipPointerMoved);
                performanceTestRow.UnregisterCallback<PointerLeaveEvent>(OnTooltipPointerLeft);
            }

            runTestsButton.clicked -= OnRunTestsClicked;
            endTestButton.clicked -= OnEndTestClicked;
            mainQuitButton.clicked -= QuitApplication;
            returnToMenuButton.clicked -= ShowMainMenu;
            resultsQuitButton.clicked -= QuitApplication;
            callbacksRegistered = false;
        }

        private void InitializeApplicationFramePacing()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = UnlockedTargetFrameRate;
        }

        private void InitializeConfiguration()
        {
            var resolutionChoices = new List<string>(SupportedResolutions.Length);
            foreach (ResolutionOption resolutionOption in SupportedResolutions)
            {
                resolutionChoices.Add(resolutionOption.DisplayName);
            }

            resolutionDropdown.choices = resolutionChoices;
            targetFrameRateDropdown.choices = new List<string>
            {
                ThirtyTargetFrameRateOption,
                SixtyTargetFrameRateOption,
                CustomTargetFrameRateOption
            };
            targetFrameRateDropdown.SetValueWithoutNotify(SixtyTargetFrameRateOption);
            targetFrameRateField.SetValueWithoutNotify(DefaultTargetFrameRate);
            targetFrameRateField.AddToClassList(HiddenClassName);
            frameRateDeltaField.SetValueWithoutNotify(DefaultFrameRateDelta);
            performanceTest1Toggle.SetValueWithoutNotify(true);
            performanceTest2Toggle.SetValueWithoutNotify(true);
            performanceTest3Toggle.SetValueWithoutNotify(true);
            fullscreenToggle.SetValueWithoutNotify(true);
            targetFrameRateWarning.text = TargetFrameRateWarningText;
            frameRateDeltaWarning.text = FrameRateDeltaWarningText;
        }

        private ResolutionOption SelectInitialResolution(Resolution currentResolution)
        {
            foreach (ResolutionOption resolutionOption in SupportedResolutions)
            {
                if (resolutionOption.Width == currentResolution.width &&
                    resolutionOption.Height == currentResolution.height)
                {
                    return resolutionOption;
                }
            }

            ResolutionOption bestFit = SupportedResolutions[0];
            bool foundSupportedFit = false;
            foreach (ResolutionOption resolutionOption in SupportedResolutions)
            {
                if (resolutionOption.Width <= currentResolution.width &&
                    resolutionOption.Height <= currentResolution.height)
                {
                    bestFit = resolutionOption;
                    foundSupportedFit = true;
                }
            }

            return foundSupportedFit ? bestFit : SupportedResolutions[0];
        }

        private void ApplyDisplaySettings()
        {
            FullScreenMode screenMode = fullscreenToggle.value
                ? FullScreenMode.FullScreenWindow
                : FullScreenMode.Windowed;

            Screen.SetResolution(selectedResolution.Width, selectedResolution.Height, screenMode);
        }

        private void OnResolutionChanged(ChangeEvent<string> changeEvent)
        {
            foreach (ResolutionOption resolutionOption in SupportedResolutions)
            {
                if (resolutionOption.DisplayName == changeEvent.newValue)
                {
                    selectedResolution = resolutionOption;
                    ApplyDisplaySettings();
                    return;
                }
            }
        }

        private void OnFullscreenChanged(ChangeEvent<bool> changeEvent)
        {
            ApplyDisplaySettings();
        }

        private void OnTargetFrameRateSelectionChanged(ChangeEvent<string> changeEvent)
        {
            bool isCustomTargetFrameRateSelected = changeEvent.newValue == CustomTargetFrameRateOption;
            targetFrameRateField.EnableInClassList(HiddenClassName, !isCustomTargetFrameRateSelected);
            RefreshValidationState();
        }

        private void OnTargetFrameRateChanged(ChangeEvent<int> changeEvent)
        {
            RefreshValidationState();
        }

        private void OnTargetFrameRateRowPointerEntered(PointerEnterEvent pointerEnterEvent)
        {
            ShowTooltip(menuSettings.GetTargetFrameRateDescription(), pointerEnterEvent.position);
        }


        private void OnFrameRateDeltaChanged(ChangeEvent<int> changeEvent)
        {
            RefreshValidationState();
        }

        private void OnTestSelectionChanged(ChangeEvent<bool> changeEvent)
        {
            RefreshValidationState();
        }

        private void OnFrameRateDeltaRowPointerEntered(PointerEnterEvent pointerEnterEvent)
        {
            ShowTooltip(menuSettings.GetFrameRateDeltaDescription(), pointerEnterEvent.position);
        }

        private void OnPerformanceTestRowPointerEntered(PointerEnterEvent pointerEnterEvent)
        {
            var hoveredPerformanceTestRow = pointerEnterEvent.currentTarget as VisualElement;
            var testIndex = Array.IndexOf(performanceTestRows, hoveredPerformanceTestRow);
            if (testIndex < 0)
            {
                return;
            }

            ShowTooltip(menuSettings.GetPerformanceTestDescription(testIndex), pointerEnterEvent.position);
        }

        private void ShowTooltip(string description, Vector2 pointerPanelPosition)
        {
            performanceTestTooltip.text = description;
            performanceTestTooltip.RemoveFromClassList(HiddenClassName);
            performanceTestTooltip.BringToFront();
            UpdateTooltipPosition(pointerPanelPosition);
        }

        private void OnTooltipPointerMoved(PointerMoveEvent pointerMoveEvent)
        {
            UpdateTooltipPosition(pointerMoveEvent.position);
        }

        private void UpdateTooltipPosition(Vector2 pointerPanelPosition)
        {
            var localPointerPosition = mainMenuScreen.WorldToLocal(pointerPanelPosition);
            performanceTestTooltip.style.left = localPointerPosition.x + TooltipHorizontalOffset;
            performanceTestTooltip.style.top = localPointerPosition.y + TooltipVerticalOffset;
        }

        private void OnTooltipPointerLeft(PointerLeaveEvent pointerLeaveEvent)
        {
            performanceTestTooltip.AddToClassList(HiddenClassName);
        }


        private bool IsTargetFrameRateValid()
        {
            if (targetFrameRateDropdown.value != CustomTargetFrameRateOption)
            {
                return true;
            }

            int targetFrameRate = targetFrameRateField.value;
            return targetFrameRate >= MinimumTargetFrameRate && targetFrameRate <= MaximumTargetFrameRate;
        }

        private bool IsFrameRateDeltaValid()
        {
            return frameRateDeltaField.value >= MinimumFrameRateDelta && frameRateDeltaField.value <= MaximumFrameRateDelta;
        }

        private int GetConfiguredTargetFrameRate()
        {
            if (targetFrameRateDropdown.value == ThirtyTargetFrameRateOption)
            {
                return ThirtyFramesPerSecond;
            }

            if (targetFrameRateDropdown.value == SixtyTargetFrameRateOption)
            {
                return DefaultTargetFrameRate;
            }

            return targetFrameRateField.value;
        }

        private int GetSelectedTestCount()
        {
            int selectedTestCount = 0;
            foreach (Toggle performanceTestToggle in performanceTestToggles)
            {
                if (performanceTestToggle.value)
                {
                    selectedTestCount++;
                }
            }

            return selectedTestCount;
        }

        private static void ConfigurePhysicsUpdateRate(int targetFrameRate)
        {
            var defaultWorld = World.DefaultGameObjectInjectionWorld;
            if (defaultWorld == null) return;

            var configurationSystem = defaultWorld.GetExistingSystemManaged<ConfigurePhysicsFrequencySystem>();
            if (configurationSystem == null) return;

            configurationSystem.ConfigureForTargetFrameRate(targetFrameRate);
        }

        private void RefreshValidationState()
        {
            var isTargetFrameRateValid = IsTargetFrameRateValid();
            var isFrameRateDeltaValid = IsFrameRateDeltaValid();
            var configuredTargetFrameRate = isTargetFrameRateValid ? GetConfiguredTargetFrameRate() : DefaultTargetFrameRate;
            var willMatchPhysicsUpdateRate = isTargetFrameRateValid && configuredTargetFrameRate < DefaultTargetFrameRate;
            targetFrameRateWarning.text = willMatchPhysicsUpdateRate ? string.Format(CultureInfo.InvariantCulture, PhysicsUpdateRateWarningFormat, configuredTargetFrameRate) : TargetFrameRateWarningText;
            targetFrameRateWarning.EnableInClassList(PhysicsUpdateRateWarningClassName, willMatchPhysicsUpdateRate);
            targetFrameRateWarning.EnableInClassList(ValidationWarningHiddenClassName, isTargetFrameRateValid && !willMatchPhysicsUpdateRate);
            frameRateDeltaWarning.EnableInClassList(ValidationWarningHiddenClassName, isFrameRateDeltaValid);
            if (isTargetFrameRateValid) ConfigurePhysicsUpdateRate(configuredTargetFrameRate);

            var canRunTests = isTargetFrameRateValid &&
                              isFrameRateDeltaValid &&
                              GetSelectedTestCount() > 0 &&
                              !isTransitionActive;
            runTestsButton.SetEnabled(canRunTests);
        }

        private void OnRunTestsClicked()
        {
            if (isTransitionActive || !IsTargetFrameRateValid() || !IsFrameRateDeltaValid() || GetSelectedTestCount() == 0) return;

            var selectedTests = new List<SelectedPerformanceTest>(PerformanceTestNames.Length);
            for (var testIndex = 0; testIndex < performanceTestToggles.Length; testIndex++)
            {
                if (performanceTestToggles[testIndex].value) selectedTests.Add(new SelectedPerformanceTest(testIndex, PerformanceTestNames[testIndex]));
            }

            snapshotSelectedTests = selectedTests;
            manuallyEndedPerformanceTests = new bool[PerformanceTestNames.Length];
            snapshotTargetFrameRate = GetConfiguredTargetFrameRate();
            snapshotFrameRateDelta = frameRateDeltaField.value;
            snapshotResolution = selectedResolution;
            activeRunVersion++;
            isTransitionActive = true;
            mainMenuScreen.AddToClassList(HiddenClassName);
            resultsScreen.AddToClassList(HiddenClassName);
            if (IsTestSelected(MovementPerformanceTestIndex) || IsTestSelected(PlinkoPerformanceTestIndex) || IsTestSelected(PathfindingPerformanceTestIndex)) ShowRunningOverlay(snapshotSelectedTests[FirstCollectionIndex].Index);
            RefreshValidationState();
            var startedCoroutine = StartCoroutine(ExecuteSelectedTests(activeRunVersion));
            if (isTransitionActive) activeRunCoroutine = startedCoroutine;
        }

        private IEnumerator ExecuteSelectedTests(int runVersion)
        {
            var movementResult = default(MovementPerformanceTestResult);
            var hasMovementResult = false;
            var plinkoResult = default(PlinkoPerformanceTestResult);
            var hasPlinkoResult = false;
            var pathfindingResult = default(PathfindingPerformanceTestResult);
            var hasPathfindingResult = false;
            if (IsTestSelected(MovementPerformanceTestIndex))
            {
                BeginRunningTest(MovementPerformanceTestIndex, () => movementPerformanceTestRunner.CurrentEntityCount);
                yield return movementPerformanceTestRunner.Run(snapshotTargetFrameRate, snapshotFrameRateDelta, result =>
                {
                    movementResult = result;
                    hasMovementResult = true;
                });
                CompleteRunningTest();
            }

            if (runVersion != activeRunVersion || !isActiveAndEnabled) yield break;
            if (IsTestSelected(PlinkoPerformanceTestIndex))
            {
                BeginRunningTest(PlinkoPerformanceTestIndex, () => plinkoPerformanceTestRunner.CurrentEntityCount);
                yield return plinkoPerformanceTestRunner.Run(snapshotTargetFrameRate, snapshotFrameRateDelta, result =>
                {
                    plinkoResult = result;
                    hasPlinkoResult = true;
                });
                CompleteRunningTest();
            }

            if (runVersion != activeRunVersion || !isActiveAndEnabled) yield break;
            if (IsTestSelected(PathfindingPerformanceTestIndex))
            {
                BeginRunningTest(PathfindingPerformanceTestIndex, () => pathfindingPerformanceTestRunner.CurrentEntityCount);
                yield return pathfindingPerformanceTestRunner.Run(snapshotTargetFrameRate, snapshotFrameRateDelta, result =>
                {
                    pathfindingResult = result;
                    hasPathfindingResult = true;
                });
                CompleteRunningTest();
            }

            if (runVersion != activeRunVersion || !isActiveAndEnabled) yield break;
            PopulateResults(snapshotSelectedTests, snapshotTargetFrameRate, snapshotFrameRateDelta, snapshotResolution, hasMovementResult, movementResult, hasPlinkoResult, plinkoResult, hasPathfindingResult, pathfindingResult);
            activeEntityCountSource = null;
            HideRunningOverlay();
            resultsScreen.RemoveFromClassList(HiddenClassName);
            isTransitionActive = false;
            activeRunCoroutine = null;
            RefreshValidationState();
        }


        private void BeginRunningTest(int testIndex, Func<int> entityCountSource)
        {
            activePerformanceTestIndex = testIndex;
            activeEntityCountSource = entityCountSource;
            UpdateRunningTestHeading(testIndex);
            endTestButton.SetEnabled(true);
        }

        private void UpdateRunningTestHeading(int testIndex)
        {
            var selectedTestNumber = GetSelectedTestNumber(testIndex);
            runningTestTitleLabel.text = RunningTestTitlePrefix + PerformanceTestNames[testIndex];
            runningTestCountLabel.text = string.Format(CultureInfo.InvariantCulture, RunningTestCountFormat, selectedTestNumber, snapshotSelectedTests.Count);
        }

        private int GetSelectedTestNumber(int testIndex)
        {
            for (var selectedTestIndex = FirstCollectionIndex; selectedTestIndex < snapshotSelectedTests.Count; selectedTestIndex++)
            {
                if (snapshotSelectedTests[selectedTestIndex].Index == testIndex) return selectedTestIndex + CollectionIndexToDisplayNumberOffset;
            }

            return TestNotSelectedNumber;
        }


        private void CompleteRunningTest()
        {
            activeEntityCountSource = null;
            activePerformanceTestIndex = NoActivePerformanceTestIndex;
            endTestButton.SetEnabled(false);
        }

        private void OnEndTestClicked()
        {
            if (!isTransitionActive || activePerformanceTestIndex == NoActivePerformanceTestIndex || manuallyEndedPerformanceTests == null) return;

            manuallyEndedPerformanceTests[activePerformanceTestIndex] = true;
            endTestButton.SetEnabled(false);
            if (activePerformanceTestIndex == MovementPerformanceTestIndex)
            {
                movementPerformanceTestRunner.Cancel();
            }
            else if (activePerformanceTestIndex == PlinkoPerformanceTestIndex)
            {
                plinkoPerformanceTestRunner.Cancel();
            }
            else if (activePerformanceTestIndex == PathfindingPerformanceTestIndex)
            {
                pathfindingPerformanceTestRunner.Cancel();
            }
        }

        private bool IsTestSelected(int testIndex)
        {
            foreach (var selectedTest in snapshotSelectedTests)
            {
                if (selectedTest.Index == testIndex) return true;
            }

            return false;
        }

        private void PopulateResults(IReadOnlyList<SelectedPerformanceTest> selectedTests, int targetFrameRate, int frameRateDelta, ResolutionOption resolution, bool hasMovementResult, MovementPerformanceTestResult movementResult, bool hasPlinkoResult, PlinkoPerformanceTestResult plinkoResult, bool hasPathfindingResult, PathfindingPerformanceTestResult pathfindingResult)
        {
            resultsTableBody.Clear();
            RefreshBuildConfigurationLabels();
            resultsTargetFrameRateLabel.text = TargetFrameRateSummaryPrefix + targetFrameRate;
            resultsFrameRateDeltaLabel.text = FrameRateDeltaSummaryPrefix + frameRateDelta + FramesPerSecondSuffix;
            resultsResolutionLabel.text = ResolutionSummaryPrefix + resolution.DisplayName;

            var metricAvailability = GetResultsMetricAvailability(selectedTests, hasMovementResult, movementResult, hasPlinkoResult, plinkoResult, hasPathfindingResult, pathfindingResult);
            ApplyResultsMetricAvailability(metricAvailability);

            foreach (var selectedTest in selectedTests)
            {
                var entityCountResult = PlaceholderResultValue;
                var processCpuResult = PlaceholderResultValue;
                var cpuFrameTimeResult = PlaceholderResultValue;
                var gpuFrameTimeResult = PlaceholderResultValue;
                var peakAppMemoryResult = PlaceholderResultValue;
                if (!manuallyEndedPerformanceTests[selectedTest.Index] && selectedTest.Index == MovementPerformanceTestIndex)
                {
                    if (hasMovementResult && movementResult.Success)
                    {
                        entityCountResult = movementResult.MaximumPassingEntityCount.ToString("N0", CultureInfo.InvariantCulture);
                        processCpuResult = FormatProcessCpu(movementResult.Metrics);
                        cpuFrameTimeResult = FormatFrameTime(movementResult.Metrics.IsCpuFrameTimeAvailable, movementResult.Metrics.AverageCpuFrameTimeMilliseconds, targetFrameRate);
                        gpuFrameTimeResult = FormatFrameTime(movementResult.Metrics.IsGpuFrameTimeAvailable, movementResult.Metrics.AverageGpuFrameTimeMilliseconds, targetFrameRate);
                        peakAppMemoryResult = FormatPeakAppMemory(movementResult.Metrics);
                    }
                    else
                    {
                        entityCountResult = FailedResultValue;
                        Debug.LogError($"Movement performance test failed: {movementResult.ErrorMessage}", this);
                    }
                }
                else if (!manuallyEndedPerformanceTests[selectedTest.Index] && selectedTest.Index == PlinkoPerformanceTestIndex)
                {
                    if (hasPlinkoResult && plinkoResult.Success)
                    {
                        entityCountResult = plinkoResult.MaximumPassingEntityCount.ToString("N0", CultureInfo.InvariantCulture);
                        processCpuResult = FormatProcessCpu(plinkoResult.Metrics);
                        cpuFrameTimeResult = FormatFrameTime(plinkoResult.Metrics.IsCpuFrameTimeAvailable, plinkoResult.Metrics.AverageCpuFrameTimeMilliseconds, targetFrameRate);
                        gpuFrameTimeResult = FormatFrameTime(plinkoResult.Metrics.IsGpuFrameTimeAvailable, plinkoResult.Metrics.AverageGpuFrameTimeMilliseconds, targetFrameRate);
                        peakAppMemoryResult = FormatPeakAppMemory(plinkoResult.Metrics);
                    }
                    else
                    {
                        entityCountResult = FailedResultValue;
                        Debug.LogError($"Plinko performance test failed: {plinkoResult.ErrorMessage}", this);
                    }
                }
                else if (!manuallyEndedPerformanceTests[selectedTest.Index] && selectedTest.Index == PathfindingPerformanceTestIndex)
                {
                    if (hasPathfindingResult && pathfindingResult.Success)
                    {
                        entityCountResult = pathfindingResult.SelectedEntityCount.ToString("N0", CultureInfo.InvariantCulture);
                        processCpuResult = FormatProcessCpu(pathfindingResult.Metrics);
                        cpuFrameTimeResult = FormatFrameTime(pathfindingResult.Metrics.IsCpuFrameTimeAvailable, pathfindingResult.Metrics.AverageCpuFrameTimeMilliseconds, targetFrameRate);
                        gpuFrameTimeResult = FormatFrameTime(pathfindingResult.Metrics.IsGpuFrameTimeAvailable, pathfindingResult.Metrics.AverageGpuFrameTimeMilliseconds, targetFrameRate);
                        peakAppMemoryResult = FormatPeakAppMemory(pathfindingResult.Metrics);
                    }
                    else
                    {
                        entityCountResult = FailedResultValue;
                        Debug.LogError($"A* pathfinding performance test failed: {pathfindingResult.ErrorMessage}", this);
                    }
                }

                resultsTableBody.Add(CreateResultRow(selectedTest.DisplayName, entityCountResult, processCpuResult, cpuFrameTimeResult, gpuFrameTimeResult, peakAppMemoryResult, metricAvailability));
            }
        }

        private ResultsMetricAvailability GetResultsMetricAvailability(IReadOnlyList<SelectedPerformanceTest> selectedTests, bool hasMovementResult, MovementPerformanceTestResult movementResult, bool hasPlinkoResult, PlinkoPerformanceTestResult plinkoResult, bool hasPathfindingResult, PathfindingPerformanceTestResult pathfindingResult)
        {
            var isProcessCpuAvailable = false;
            var isCpuFrameTimeAvailable = false;
            var isGpuFrameTimeAvailable = false;
            var isPeakAppMemoryAvailable = false;
            foreach (var selectedTest in selectedTests)
            {
                if (manuallyEndedPerformanceTests[selectedTest.Index]) continue;

                var hasSuccessfulResult = false;
                var metrics = default(PerformanceMetricsSnapshot);
                if (selectedTest.Index == MovementPerformanceTestIndex && hasMovementResult && movementResult.Success)
                {
                    metrics = movementResult.Metrics;
                    hasSuccessfulResult = true;
                }
                else if (selectedTest.Index == PlinkoPerformanceTestIndex && hasPlinkoResult && plinkoResult.Success)
                {
                    metrics = plinkoResult.Metrics;
                    hasSuccessfulResult = true;
                }
                else if (selectedTest.Index == PathfindingPerformanceTestIndex && hasPathfindingResult && pathfindingResult.Success)
                {
                    metrics = pathfindingResult.Metrics;
                    hasSuccessfulResult = true;
                }

                if (!hasSuccessfulResult) continue;

                isProcessCpuAvailable |= metrics.IsProcessCpuUtilizationAvailable;
                isCpuFrameTimeAvailable |= metrics.IsCpuFrameTimeAvailable;
                isGpuFrameTimeAvailable |= metrics.IsGpuFrameTimeAvailable;
                isPeakAppMemoryAvailable |= metrics.IsPeakWorkingSetAvailable;
            }

            return new ResultsMetricAvailability(isProcessCpuAvailable, isCpuFrameTimeAvailable, isGpuFrameTimeAvailable, isPeakAppMemoryAvailable);
        }

        private void ApplyResultsMetricAvailability(ResultsMetricAvailability metricAvailability)
        {
            resultsProcessCpuHeader.EnableInClassList(HiddenClassName, !metricAvailability.IsProcessCpuAvailable);
            resultsCpuFrameTimeHeader.EnableInClassList(HiddenClassName, !metricAvailability.IsCpuFrameTimeAvailable);
            resultsGpuFrameTimeHeader.EnableInClassList(HiddenClassName, !metricAvailability.IsGpuFrameTimeAvailable);
            resultsPeakAppMemoryHeader.EnableInClassList(HiddenClassName, !metricAvailability.IsPeakAppMemoryAvailable);
        }

        private static string FormatProcessCpu(PerformanceMetricsSnapshot metrics)
        {
            if (!metrics.IsProcessCpuUtilizationAvailable || double.IsNaN(metrics.ProcessCpuUtilizationPercent) || double.IsInfinity(metrics.ProcessCpuUtilizationPercent)) return PlaceholderResultValue;
            return string.Format(CultureInfo.InvariantCulture, ProcessCpuResultFormat, metrics.ProcessCpuUtilizationPercent);
        }

        private static string FormatFrameTime(bool isAvailable, double frameTimeMilliseconds, int targetFrameRate)
        {
            if (!isAvailable || frameTimeMilliseconds <= 0d || double.IsNaN(frameTimeMilliseconds) || double.IsInfinity(frameTimeMilliseconds) || targetFrameRate <= 0) return PlaceholderResultValue;
            var frameBudgetMilliseconds = MillisecondsPerSecond / targetFrameRate;
            var frameBudgetPercentage = frameTimeMilliseconds / frameBudgetMilliseconds * PercentageScale;
            return string.Format(CultureInfo.InvariantCulture, FrameTimeResultFormat, frameTimeMilliseconds, frameBudgetPercentage);
        }

        private static string FormatPeakAppMemory(PerformanceMetricsSnapshot metrics)
        {
            if (!metrics.IsPeakWorkingSetAvailable) return PlaceholderResultValue;
            var peakAppMemoryGigabytes = metrics.PeakWorkingSetBytes / BytesPerDecimalGigabyte;
            return string.Format(CultureInfo.InvariantCulture, MemoryResultFormat, peakAppMemoryGigabytes);
        }

        private VisualElement CreateResultRow(string testName, string entityCount, string processCpu, string cpuFrameTime, string gpuFrameTime, string peakAppMemory, ResultsMetricAvailability metricAvailability)
        {
            var resultRow = new VisualElement();
            resultRow.AddToClassList(ResultsRowClassName);
            resultRow.Add(CreateResultCell(testName, ResultsTestNameColumnClassName));
            resultRow.Add(CreateResultCell(entityCount, ResultsEntityCountColumnClassName));
            if (metricAvailability.IsProcessCpuAvailable)
            {
                resultRow.Add(CreateResultCell(processCpu, ResultsProcessCpuColumnClassName));
            }

            if (metricAvailability.IsCpuFrameTimeAvailable)
            {
                resultRow.Add(CreateResultCell(cpuFrameTime, ResultsFrameTimeColumnClassName));
            }

            if (metricAvailability.IsGpuFrameTimeAvailable)
            {
                resultRow.Add(CreateResultCell(gpuFrameTime, ResultsFrameTimeColumnClassName));
            }

            if (metricAvailability.IsPeakAppMemoryAvailable)
            {
                resultRow.Add(CreateResultCell(peakAppMemory, ResultsMemoryColumnClassName));
            }

            return resultRow;
        }

        private static Label CreateResultCell(string text, string columnClassName)
        {
            var resultCell = new Label(text);
            resultCell.AddToClassList(ResultsCellClassName);
            resultCell.AddToClassList(columnClassName);
            return resultCell;
        }

        private void ShowRunningOverlay(int testIndex)
        {
            currentFrameCount = 0;
            currentFrameElapsedSeconds = 0f;
            UpdateRunningTestHeading(testIndex);
            runningResolutionLabel.text = RunningResolutionPrefix + snapshotResolution.DisplayName;
            runningTargetFrameRateLabel.text = RunningTargetFrameRatePrefix + snapshotTargetFrameRate + FramesPerSecondSuffix;
            runningFrameRateDeltaLabel.text = RunningFrameRateDeltaPrefix + snapshotFrameRateDelta + FramesPerSecondSuffix;
            runningCurrentFrameRateLabel.text = RunningCurrentFrameRatePrefix + PlaceholderResultValue;
            runningEntityCountLabel.text = RunningEntityCountPrefix + "0";
            endTestButton.SetEnabled(false);
            runningTestOverlay.RemoveFromClassList(HiddenClassName);
            runningTestOverlay.BringToFront();
        }

        private void HideRunningOverlay()
        {
            if (runningTestOverlay != null) runningTestOverlay.AddToClassList(HiddenClassName);
        }

        private void RefreshBuildConfigurationLabels()
        {
            var summaryText = RuntimeBuildConfiguration.GetSummaryText();
            mainBuildConfigurationLabel.text = summaryText;
            resultsBuildConfigurationLabel.text = summaryText;
        }

        private void ShowMainMenu()
        {
            RefreshBuildConfigurationLabels();
            HideRunningOverlay();
            resultsScreen.AddToClassList(HiddenClassName);
            mainMenuScreen.RemoveFromClassList(HiddenClassName);
            RefreshValidationState();
        }

        private void QuitApplication()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.ExitPlaymode();
#else
            Application.Quit();
#endif
        }

        private readonly struct ResultsMetricAvailability
        {
            internal bool IsProcessCpuAvailable { get; }
            internal bool IsCpuFrameTimeAvailable { get; }
            internal bool IsGpuFrameTimeAvailable { get; }
            internal bool IsPeakAppMemoryAvailable { get; }

            internal ResultsMetricAvailability(bool isProcessCpuAvailable, bool isCpuFrameTimeAvailable, bool isGpuFrameTimeAvailable, bool isPeakAppMemoryAvailable)
            {
                IsProcessCpuAvailable = isProcessCpuAvailable;
                IsCpuFrameTimeAvailable = isCpuFrameTimeAvailable;
                IsGpuFrameTimeAvailable = isGpuFrameTimeAvailable;
                IsPeakAppMemoryAvailable = isPeakAppMemoryAvailable;
            }
        }

        private readonly struct SelectedPerformanceTest
        {
            internal int Index { get; }
            internal string DisplayName { get; }

            internal SelectedPerformanceTest(int index, string displayName)
            {
                Index = index;
                DisplayName = displayName;
            }
        }

        private readonly struct ResolutionOption
        {
            internal ResolutionOption(string displayName, int width, int height)
            {
                DisplayName = displayName;
                Width = width;
                Height = height;
            }

            internal int Width { get; }
            internal int Height { get; }
            internal string DisplayName { get; }
        }
    }
}
