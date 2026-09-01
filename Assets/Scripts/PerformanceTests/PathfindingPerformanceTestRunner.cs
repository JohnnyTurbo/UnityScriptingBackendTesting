using System;
using System.Collections;
using System.Collections.Generic;
using TMG.CoreCLRTest;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CoreCLRTest.PerformanceTests
{
    [DisallowMultipleComponent]
    public sealed class PathfindingPerformanceTestRunner : MonoBehaviour
    {
        private const string PathfindingScenePath = "Assets/Scenes/PathfindingScene.unity";
        private const int MaximumEntityCount = 1_000_000;
        private const int MaximumFrameRateDelta = 1000;
        private const int InitialRampEntityCount = 1_024;
        private const int MeasurementWindowCount = 3;
        private const int RequiredStableWarmupWindowCount = 2;
        private const int MaximumBoundaryRepairPasses = 4;
        private const int MaximumSearchRefinementEvaluations = 64;
        private const int MaximumFinalRefinementEvaluations = 24;
        private const int ReservedFinalFallbackEvaluations = 2;
        private const int MinimumGridSide = 20;
        private const float WarmupWindowDurationSeconds = 1f;
        private const float MaximumWarmupDurationSeconds = 8f;
        private const float MaximumStableWarmupRelativeDrift = 0.02f;
        private const float MaterialContradictionRelativeDifference = 0.02f;
        private const float MeasurementWindowDurationSeconds = 3f;
        private const float MinimumSimulationHalfExtent = 10f;
        private const float MovementSpeedUnitsPerSecond = 5f;
        private const float ArrivalDistance = 0.1f;
        private const float CameraBoundsPadding = 1.1f;
        private const float MinimumCameraAspect = 0.01f;
        private const float MinimumCameraFieldOfView = 1f;
        private const float MaximumCameraFieldOfView = 179f;
        private const float MinimumFarClipPadding = 10f;
        private const float SceneReadinessTimeoutSeconds = 60f;
        private const float EntityTransitionTimeoutSeconds = 900f;
        private const double CandidatePreparationDiagnosticIntervalSeconds = 5d;
        private const uint BaseRandomSeed = 0x4F1BBCDDu;
        private readonly List<RootActiveState> mainSceneRootStates = new List<RootActiveState>();
        private Scene mainScene;
        private Scene pathfindingScene;
        private World benchmarkWorld;
        private EntityManager entityManager;
        private Entity controlEntity;
        private EntityQuery spawnerQuery;
        private EntityQuery controlQuery;
        private EntityQuery runtimeQuery;
        private EntityQuery initializationPendingQuery;
        private EntityQuery searchFailedQuery;
        private Camera pathfindingCamera;
        private int targetFrameRate;
        private int frameRateDelta;
        private int configurationVersion;
        private bool isRunning;
        private bool cancellationRequested;
        private bool pathfindingSceneLoaded;
        private bool controlEntityCreated;
        private bool cleanupStarted;
        private bool queriesCreated;
        private bool completionInvoked;
        private string failureMessage;
        private string cleanupFailureMessage;
        private PathfindingPerformanceTestResult measuredResult;

        public int CurrentEntityCount { get; private set; }
        internal float CurrentSampledFrameRate { get; private set; }

        /// <summary>
        /// Runs the additive-scene pathfinding capacity benchmark and invokes the supplied completion callback.
        /// </summary>
        public IEnumerator Run(int requestedTargetFrameRate, int requestedFrameRateDelta, Action<PathfindingPerformanceTestResult> onCompleted)
        {
            if (isRunning)
            {
                onCompleted?.Invoke(PathfindingPerformanceTestResult.Failure("The A* pathfinding benchmark is already running."));
                yield break;
            }

            InitializeRun(requestedTargetFrameRate, requestedFrameRateDelta);
            CaptureAndDeactivateMainSceneRoots();
            if (string.IsNullOrEmpty(failureMessage)) yield return ExecuteBenchmark();
            yield return RemoveEntitiesAndUnloadScene();
            var finalResult = ResolveFinalResult();
            isRunning = false;
            if (!completionInvoked)
            {
                completionInvoked = true;
                onCompleted?.Invoke(finalResult);
            }
        }

        /// <summary>
        /// Requests cancellation of the active pathfinding benchmark.
        /// </summary>
        public void Cancel()
        {
            if (!isRunning) return;
            cancellationRequested = true;
        }

        private void InitializeRun(int requestedTargetFrameRate, int requestedFrameRateDelta)
        {
            targetFrameRate = Mathf.Max(1, requestedTargetFrameRate);
            frameRateDelta = Mathf.Clamp(requestedFrameRateDelta, 0, MaximumFrameRateDelta);
            configurationVersion = 0;
            CurrentEntityCount = 0;
            CurrentSampledFrameRate = 0f;
            isRunning = true;
            cancellationRequested = false;
            pathfindingSceneLoaded = false;
            controlEntityCreated = false;
            cleanupStarted = false;
            queriesCreated = false;
            completionInvoked = false;
            failureMessage = string.Empty;
            cleanupFailureMessage = string.Empty;
            measuredResult = PathfindingPerformanceTestResult.Failure("The A* pathfinding benchmark did not complete.");
            mainSceneRootStates.Clear();
            mainScene = gameObject.scene;
            pathfindingScene = default;
            benchmarkWorld = null;
            entityManager = default;
            controlEntity = Entity.Null;
            pathfindingCamera = null;
            spawnerQuery = default;
            controlQuery = default;
            runtimeQuery = default;
            initializationPendingQuery = default;
            searchFailedQuery = default;
        }

        private IEnumerator ExecuteBenchmark()
        {
            yield return LoadPathfindingScene();
            if (ShouldStopExecution()) yield break;
            CreateControlEntity();
            if (ShouldStopExecution()) yield break;
            var searchState = new CandidateSearchState();
            var candidateCount = InitialRampEntityCount;
            var candidateEvaluation = default(CandidateEvaluation);
            yield return EvaluateAndRetainCandidate(candidateCount, searchState, value => candidateEvaluation = value);
            if (ShouldStopExecution()) yield break;
            if (candidateEvaluation.Classification == CandidateFrameRateClassification.WithinTargetWindow)
            {
                yield return FinalizeBenchmark(candidateCount, searchState);
                yield break;
            }

            if (candidateEvaluation.Classification == CandidateFrameRateClassification.BelowTargetWindow)
            {
                candidateEvaluation = default;
                yield return EvaluateAndRetainCandidate(0, searchState, value => candidateEvaluation = value);
                if (ShouldStopExecution()) yield break;
                if (candidateEvaluation.Classification == CandidateFrameRateClassification.WithinTargetWindow)
                {
                    yield return FinalizeBenchmark(0, searchState);
                    yield break;
                }
            }
            else
            {
                while (candidateCount < MaximumEntityCount && !searchState.HasBelowTargetBoundary)
                {
                    candidateCount = Mathf.Min(candidateCount * 2, MaximumEntityCount);
                    candidateEvaluation = default;
                    yield return EvaluateAndRetainCandidate(candidateCount, searchState, value => candidateEvaluation = value);
                    if (ShouldStopExecution()) yield break;
                    if (candidateEvaluation.Classification == CandidateFrameRateClassification.WithinTargetWindow)
                    {
                        yield return FinalizeBenchmark(candidateCount, searchState);
                        yield break;
                    }
                }
            }

            var searchRefinementEvaluationCount = 0;
            var aboveTargetBoundary = default(CandidateEvaluation);
            var belowTargetBoundary = default(CandidateEvaluation);
            for (var boundaryRepairPass = 0; boundaryRepairPass < MaximumBoundaryRepairPasses && searchRefinementEvaluationCount < MaximumSearchRefinementEvaluations; boundaryRepairPass++)
            {
                while (TryGetValidSearchBracket(searchState, out aboveTargetBoundary, out belowTargetBoundary) && belowTargetBoundary.EntityCount - aboveTargetBoundary.EntityCount > 1 && searchRefinementEvaluationCount < MaximumSearchRefinementEvaluations)
                {
                    candidateCount = aboveTargetBoundary.EntityCount + (belowTargetBoundary.EntityCount - aboveTargetBoundary.EntityCount) / 2;
                    candidateEvaluation = default;
                    yield return EvaluateAndRetainCandidate(candidateCount, searchState, value => candidateEvaluation = value);
                    if (ShouldStopExecution()) yield break;
                    searchRefinementEvaluationCount++;
                    if (candidateEvaluation.Classification == CandidateFrameRateClassification.WithinTargetWindow)
                    {
                        yield return FinalizeBenchmark(candidateCount, searchState);
                        yield break;
                    }
                }

                if (!TryGetValidSearchBracket(searchState, out aboveTargetBoundary, out belowTargetBoundary) || belowTargetBoundary.EntityCount - aboveTargetBoundary.EntityCount > 1) continue;
                var refreshedBoundary = false;
                var exactBoundaryCount = -1;
                if (!aboveTargetBoundary.ContradictionResamplingUsed)
                {
                    yield return ResampleAndRetainCandidate(aboveTargetBoundary, searchState, value =>
                    {
                        refreshedBoundary = true;
                        if (value.Classification == CandidateFrameRateClassification.WithinTargetWindow) exactBoundaryCount = value.EntityCount;
                    });
                    if (ShouldStopExecution()) yield break;
                }

                if (!belowTargetBoundary.ContradictionResamplingUsed && exactBoundaryCount < 0)
                {
                    yield return ResampleAndRetainCandidate(belowTargetBoundary, searchState, value =>
                    {
                        refreshedBoundary = true;
                        if (value.Classification == CandidateFrameRateClassification.WithinTargetWindow) exactBoundaryCount = value.EntityCount;
                    });
                    if (ShouldStopExecution()) yield break;
                }

                if (exactBoundaryCount >= 0)
                {
                    yield return FinalizeBenchmark(exactBoundaryCount, searchState);
                    yield break;
                }

                if (!refreshedBoundary) break;
            }

            if (!searchState.HasBestObservedCandidate)
            {
                SetFailure("The A* pathfinding benchmark capacity search completed without a valid frame-rate measurement.");
                yield break;
            }

            yield return FinalizeBenchmark(searchState.BestObservedCandidate.EntityCount, searchState);
        }

        private IEnumerator LoadPathfindingScene()
        {
            var loadOperation = StartPathfindingSceneLoad();
            if (loadOperation == null) yield break;
            while (!loadOperation.isDone) yield return null;
            pathfindingScene = SceneManager.GetSceneByPath(PathfindingScenePath);
            if (!pathfindingScene.IsValid() || !pathfindingScene.isLoaded)
            {
                SetFailure($"Failed to load pathfinding benchmark scene '{PathfindingScenePath}'.");
                yield break;
            }

            pathfindingSceneLoaded = true;
            if (!SceneManager.SetActiveScene(pathfindingScene))
            {
                SetFailure("Failed to make the pathfinding benchmark scene active.");
                yield break;
            }

            pathfindingCamera = FindPathfindingCamera();
            if (pathfindingCamera == null)
            {
                SetFailure("Pathfinding benchmark scene does not contain a Camera component.");
                yield break;
            }

            var readinessStartTime = Time.realtimeSinceStartupAsDouble;
            while (World.DefaultGameObjectInjectionWorld == null || !World.DefaultGameObjectInjectionWorld.IsCreated)
            {
                if (cancellationRequested) yield break;
                if (Time.realtimeSinceStartupAsDouble - readinessStartTime >= SceneReadinessTimeoutSeconds)
                {
                    SetFailure("Timed out waiting for the default ECS world.");
                    yield break;
                }

                yield return null;
            }

            benchmarkWorld = World.DefaultGameObjectInjectionWorld;
            entityManager = benchmarkWorld.EntityManager;
            spawnerQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<PathfindingTestSpawnerData>(), ComponentType.ReadOnly<PathfindingGridData>());
            controlQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<PathfindingTestControlData>());
            runtimeQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<PathfindingTestRuntimeData>());
            initializationPendingQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<PathfindingAgentTag>(), ComponentType.ReadOnly<PathfindingInitializationPending>());
            searchFailedQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<PathfindingAgentTag>(), ComponentType.ReadOnly<PathfindingSearchFailed>());
            queriesCreated = true;
            while (spawnerQuery.CalculateEntityCount() == 0)
            {
                if (cancellationRequested) yield break;
                if (Time.realtimeSinceStartupAsDouble - readinessStartTime >= SceneReadinessTimeoutSeconds)
                {
                    SetFailure("Timed out waiting for the auto-loaded pathfinding SubScene and spawner singleton.");
                    yield break;
                }

                yield return null;
            }

            if (spawnerQuery.CalculateEntityCount() != 1) SetFailure("Pathfinding benchmark requires exactly one spawner singleton.");
        }

        private AsyncOperation StartPathfindingSceneLoad()
        {
            try
            {
                return SceneManager.LoadSceneAsync(PathfindingScenePath, LoadSceneMode.Additive);
            }
            catch (Exception exception)
            {
                SetFailure($"Failed to start loading the pathfinding benchmark scene: {exception.Message}");
                return null;
            }
        }

        private Camera FindPathfindingCamera()
        {
            var sceneRoots = pathfindingScene.GetRootGameObjects();
            for (var rootIndex = 0; rootIndex < sceneRoots.Length; rootIndex++)
            {
                var cameras = sceneRoots[rootIndex].GetComponentsInChildren<Camera>(true);
                if (cameras.Length > 0) return cameras[0];
            }

            return null;
        }

        private void CreateControlEntity()
        {
            if (benchmarkWorld == null || !benchmarkWorld.IsCreated)
            {
                SetFailure("The default ECS world became unavailable before pathfinding setup completed.");
                return;
            }

            if (controlQuery.CalculateEntityCount() != 0 || runtimeQuery.CalculateEntityCount() != 0)
            {
                SetFailure("Pathfinding benchmark control singleton data already exists.");
                return;
            }

            try
            {
                controlEntity = entityManager.CreateEntity(typeof(PathfindingTestControlData), typeof(PathfindingTestRuntimeData));
                entityManager.SetComponentData(controlEntity, CreateControlData(0, MinimumGridSide));
                entityManager.SetComponentData(controlEntity, new PathfindingTestRuntimeData
                {
                    Status = PathfindingTestRuntimeStatus.Inactive,
                    Error = PathfindingTestRuntimeError.None,
                    CurrentEntityCount = 0,
                    CurrentWallCount = 0,
                    ExpectedWallCount = 0,
                    AppliedConfigurationVersion = configurationVersion,
                    AppliedGridVersion = 0
                });
                controlEntityCreated = true;
            }
            catch (Exception exception)
            {
                SetFailure($"Failed to create pathfinding benchmark control data: {exception.Message}");
            }
        }

        private IEnumerator PrepareCandidate(int entityCount, Action<bool> onCompleted)
        {
            var clampedEntityCount = Mathf.Clamp(entityCount, 0, MaximumEntityCount);
            configurationVersion = configurationVersion == int.MaxValue ? 1 : configurationVersion + 1;
            var gridSide = CalculateGridSide(clampedEntityCount);
            if (!TryWriteControlData(clampedEntityCount, gridSide))
            {
                onCompleted(false);
                yield break;
            }

            FramePathfindingCamera(gridSide);
            var transitionStartTime = Time.realtimeSinceStartupAsDouble;
            var nextDiagnosticTime = transitionStartTime + CandidatePreparationDiagnosticIntervalSeconds;
            // Debug.Log($"A* pathfinding candidate {clampedEntityCount:N0}: preparing a {gridSide:N0} x {gridSide:N0} grid (configuration {configurationVersion}).", this);
            while (!HasRuntimeReachedCandidate(clampedEntityCount, gridSide))
            {
                if (ShouldStopExecution())
                {
                    onCompleted(false);
                    yield break;
                }

                var currentTime = Time.realtimeSinceStartupAsDouble;
                if (currentTime >= nextDiagnosticTime)
                {
                    LogCandidatePreparationState(clampedEntityCount);
                    nextDiagnosticTime = currentTime + CandidatePreparationDiagnosticIntervalSeconds;
                }

                if (currentTime - transitionStartTime >= EntityTransitionTimeoutSeconds)
                {
                    SetFailure($"Timed out while preparing {clampedEntityCount:N0} pathfinding entities on a {gridSide:N0} by {gridSide:N0} grid.");
                    onCompleted(false);
                    yield break;
                }

                yield return null;
            }

            var warmupCompleted = false;
            yield return WarmUpCandidate(value => warmupCompleted = value);
            onCompleted(warmupCompleted && !ShouldStopExecution());
        }

        private IEnumerator WarmUpCandidate(Action<bool> onCompleted)
        {
            var warmupStartTime = Time.realtimeSinceStartupAsDouble;
            var previousWindowFrameRate = 0f;
            var consecutiveStableWindowCount = 0;
            while (Time.realtimeSinceStartupAsDouble - warmupStartTime < MaximumWarmupDurationSeconds)
            {
                var windowSample = default(FrameRateWindowSample);
                yield return SampleFrameRateWindow(WarmupWindowDurationSeconds, null, value => windowSample = value);
                if (!windowSample.IsValid || ShouldStopExecution())
                {
                    onCompleted(false);
                    yield break;
                }

                if (consecutiveStableWindowCount == 0)
                {
                    consecutiveStableWindowCount = 1;
                }
                else
                {
                    var relativeDrift = Mathf.Abs(windowSample.FrameRate - previousWindowFrameRate) / Mathf.Max(Mathf.Abs(previousWindowFrameRate), Mathf.Epsilon);
                    consecutiveStableWindowCount = relativeDrift <= MaximumStableWarmupRelativeDrift ? consecutiveStableWindowCount + 1 : 1;
                }

                previousWindowFrameRate = windowSample.FrameRate;
                if (consecutiveStableWindowCount >= RequiredStableWarmupWindowCount)
                {
                    onCompleted(true);
                    yield break;
                }
            }

            onCompleted(true);
        }

        private IEnumerator EvaluateCandidate(int entityCount, Action<CandidateEvaluation> onCompleted)
        {
            CurrentSampledFrameRate = 0f;
            var preparationCompleted = false;
            yield return PrepareCandidate(entityCount, value => preparationCompleted = value);
            if (!preparationCompleted || ShouldStopExecution())
            {
                onCompleted(default);
                yield break;
            }

            var windowFrameRates = new List<float>(MeasurementWindowCount);
            var collectionCompleted = false;
            yield return CollectFrameRateWindows(MeasurementWindowCount, windowFrameRates, null, value => collectionCompleted = value);
            if (!collectionCompleted || ShouldStopExecution())
            {
                onCompleted(default);
                yield break;
            }

            var medianFrameRate = CalculateMedian(windowFrameRates);
            CurrentSampledFrameRate = medianFrameRate;
            onCompleted(new CandidateEvaluation(entityCount, medianFrameRate, ClassifyFrameRate(medianFrameRate), windowFrameRates.ToArray(), false));
        }

        private IEnumerator EvaluateAndRetainCandidate(int entityCount, CandidateSearchState searchState, Action<CandidateEvaluation> onCompleted)
        {
            var candidateEvaluation = default(CandidateEvaluation);
            yield return EvaluateCandidate(entityCount, value => candidateEvaluation = value);
            if (!candidateEvaluation.IsValid || ShouldStopExecution())
            {
                onCompleted(candidateEvaluation);
                yield break;
            }

            if (ShouldUseContradictionResampling(candidateEvaluation, searchState))
            {
                yield return ResampleCandidate(candidateEvaluation, value => candidateEvaluation = value);
                if (!candidateEvaluation.IsValid || ShouldStopExecution())
                {
                    onCompleted(candidateEvaluation);
                    yield break;
                }
            }

            RetainBestObservedCandidate(searchState, candidateEvaluation);
            onCompleted(candidateEvaluation);
        }

        private IEnumerator ResampleAndRetainCandidate(CandidateEvaluation candidateEvaluation, CandidateSearchState searchState, Action<CandidateEvaluation> onCompleted)
        {
            var resampledEvaluation = default(CandidateEvaluation);
            yield return ResampleCandidate(candidateEvaluation, value => resampledEvaluation = value);
            if (resampledEvaluation.IsValid && !ShouldStopExecution()) RetainBestObservedCandidate(searchState, resampledEvaluation);
            onCompleted(resampledEvaluation);
        }

        private IEnumerator ResampleCandidate(CandidateEvaluation candidateEvaluation, Action<CandidateEvaluation> onCompleted)
        {
            if (!candidateEvaluation.IsValid || candidateEvaluation.ContradictionResamplingUsed)
            {
                onCompleted(candidateEvaluation);
                yield break;
            }

            var preparationCompleted = false;
            yield return PrepareCandidate(candidateEvaluation.EntityCount, value => preparationCompleted = value);
            if (!preparationCompleted || ShouldStopExecution())
            {
                onCompleted(default);
                yield break;
            }

            var combinedWindowFrameRates = new List<float>(candidateEvaluation.WindowFrameRates.Length + MeasurementWindowCount);
            combinedWindowFrameRates.AddRange(candidateEvaluation.WindowFrameRates);
            var collectionCompleted = false;
            yield return CollectFrameRateWindows(MeasurementWindowCount, combinedWindowFrameRates, null, value => collectionCompleted = value);
            if (!collectionCompleted || ShouldStopExecution())
            {
                onCompleted(default);
                yield break;
            }

            var medianFrameRate = CalculateMedian(combinedWindowFrameRates);
            CurrentSampledFrameRate = medianFrameRate;
            onCompleted(new CandidateEvaluation(candidateEvaluation.EntityCount, medianFrameRate, ClassifyFrameRate(medianFrameRate), combinedWindowFrameRates.ToArray(), true));
        }

        private IEnumerator CollectFrameRateWindows(int windowCount, List<float> windowFrameRates, PerformanceMetricsSampler metricsSampler, Action<bool> onCompleted)
        {
            for (var windowIndex = 0; windowIndex < windowCount; windowIndex++)
            {
                var windowSample = default(FrameRateWindowSample);
                yield return SampleFrameRateWindow(MeasurementWindowDurationSeconds, metricsSampler, value => windowSample = value);
                if (!windowSample.IsValid || ShouldStopExecution())
                {
                    onCompleted(false);
                    yield break;
                }

                windowFrameRates.Add(windowSample.FrameRate);
            }

            onCompleted(true);
        }

        private IEnumerator SampleFrameRateWindow(float durationSeconds, PerformanceMetricsSampler metricsSampler, Action<FrameRateWindowSample> onCompleted)
        {
            var sampledFrameCount = 0;
            var sampledPositiveSeconds = 0f;
            var windowStartTime = Time.realtimeSinceStartupAsDouble;
            while (Time.realtimeSinceStartupAsDouble - windowStartTime < durationSeconds)
            {
                metricsSampler?.RequestFrameTimingCapture();
                yield return null;
                if (ShouldStopExecution())
                {
                    onCompleted(default);
                    yield break;
                }

                if (metricsSampler != null && !metricsSampler.TryRecordCompletedFrame(out var samplerErrorMessage))
                {
                    SetFailure($"Failed to record pathfinding performance metrics: {samplerErrorMessage}");
                    onCompleted(default);
                    yield break;
                }

                var frameDuration = Time.unscaledDeltaTime;
                if (frameDuration <= 0f) continue;
                sampledFrameCount++;
                sampledPositiveSeconds += frameDuration;
                CurrentSampledFrameRate = sampledFrameCount / sampledPositiveSeconds;
            }

            if (sampledPositiveSeconds <= 0f)
            {
                SetFailure("A* pathfinding benchmark sampled a frame-rate window without any positive frame durations.");
                onCompleted(default);
                yield break;
            }

            var frameRate = sampledFrameCount / sampledPositiveSeconds;
            CurrentSampledFrameRate = frameRate;
            onCompleted(new FrameRateWindowSample(true, frameRate));
        }

        private IEnumerator FinalizeBenchmark(int selectedEntityCount, CandidateSearchState searchState)
        {
            var finalEvaluations = new Dictionary<int, FinalBatchEvaluation>();
            var nextEntityCount = Mathf.Clamp(selectedEntityCount, 0, MaximumEntityCount);
            var finalEvaluationCount = 0;
            var primaryRefinementLimit = MaximumFinalRefinementEvaluations - ReservedFinalFallbackEvaluations;
            while (finalEvaluationCount < primaryRefinementLimit)
            {
                var finalBatchEvaluation = default(FinalBatchEvaluation);
                yield return EvaluateFinalBatch(nextEntityCount, searchState, value => finalBatchEvaluation = value);
                if (!finalBatchEvaluation.IsValid || ShouldStopExecution()) yield break;
                finalEvaluationCount++;
                finalEvaluations[nextEntityCount] = finalBatchEvaluation;
                RetainBestObservedCandidate(searchState, finalBatchEvaluation.ToCandidateEvaluation());
                if (finalBatchEvaluation.Classification == CandidateFrameRateClassification.WithinTargetWindow)
                {
                    CurrentSampledFrameRate = finalBatchEvaluation.MedianMeasuredFrameRate;
                    measuredResult = PathfindingPerformanceTestResult.Successful(nextEntityCount, nextEntityCount == MaximumEntityCount, finalBatchEvaluation.MedianMeasuredFrameRate, PathfindingFrameRateMatchStatus.WithinTargetWindow, finalBatchEvaluation.Metrics);
                    yield break;
                }

                if (!TryGetUnmeasuredRefinementCandidate(searchState, finalEvaluations, out nextEntityCount)) break;
            }

            if (TryGetClosestObservedCandidate(searchState, CandidateFrameRateClassification.AboveTargetWindow, out var aboveTargetFallback) && !finalEvaluations.ContainsKey(aboveTargetFallback.EntityCount) && finalEvaluationCount < MaximumFinalRefinementEvaluations)
            {
                var aboveTargetFinalEvaluation = default(FinalBatchEvaluation);
                yield return EvaluateFinalBatch(aboveTargetFallback.EntityCount, searchState, value => aboveTargetFinalEvaluation = value);
                if (!aboveTargetFinalEvaluation.IsValid || ShouldStopExecution()) yield break;
                finalEvaluationCount++;
                finalEvaluations[aboveTargetFallback.EntityCount] = aboveTargetFinalEvaluation;
                RetainBestObservedCandidate(searchState, aboveTargetFinalEvaluation.ToCandidateEvaluation());
                if (aboveTargetFinalEvaluation.Classification == CandidateFrameRateClassification.WithinTargetWindow)
                {
                    CurrentSampledFrameRate = aboveTargetFinalEvaluation.MedianMeasuredFrameRate;
                    measuredResult = PathfindingPerformanceTestResult.Successful(aboveTargetFallback.EntityCount, aboveTargetFallback.EntityCount == MaximumEntityCount, aboveTargetFinalEvaluation.MedianMeasuredFrameRate, PathfindingFrameRateMatchStatus.WithinTargetWindow, aboveTargetFinalEvaluation.Metrics);
                    yield break;
                }
            }

            if (TryGetClosestObservedCandidate(searchState, CandidateFrameRateClassification.BelowTargetWindow, out var belowTargetFallback) && !finalEvaluations.ContainsKey(belowTargetFallback.EntityCount) && finalEvaluationCount < MaximumFinalRefinementEvaluations)
            {
                var belowTargetFinalEvaluation = default(FinalBatchEvaluation);
                yield return EvaluateFinalBatch(belowTargetFallback.EntityCount, searchState, value => belowTargetFinalEvaluation = value);
                if (!belowTargetFinalEvaluation.IsValid || ShouldStopExecution()) yield break;
                finalEvaluationCount++;
                finalEvaluations[belowTargetFallback.EntityCount] = belowTargetFinalEvaluation;
                RetainBestObservedCandidate(searchState, belowTargetFinalEvaluation.ToCandidateEvaluation());
                if (belowTargetFinalEvaluation.Classification == CandidateFrameRateClassification.WithinTargetWindow)
                {
                    CurrentSampledFrameRate = belowTargetFinalEvaluation.MedianMeasuredFrameRate;
                    measuredResult = PathfindingPerformanceTestResult.Successful(belowTargetFallback.EntityCount, belowTargetFallback.EntityCount == MaximumEntityCount, belowTargetFinalEvaluation.MedianMeasuredFrameRate, PathfindingFrameRateMatchStatus.WithinTargetWindow, belowTargetFinalEvaluation.Metrics);
                    yield break;
                }
            }

            if (!TryGetClosestFinalEvaluation(finalEvaluations, out var closestFinalEvaluation))
            {
                SetFailure("The A* pathfinding benchmark completed without a valid final metrics measurement.");
                yield break;
            }

            CurrentSampledFrameRate = closestFinalEvaluation.MedianMeasuredFrameRate;
            measuredResult = PathfindingPerformanceTestResult.Successful(closestFinalEvaluation.EntityCount, closestFinalEvaluation.EntityCount == MaximumEntityCount, closestFinalEvaluation.MedianMeasuredFrameRate, PathfindingFrameRateMatchStatus.ClosestStableOutsideTargetWindow, closestFinalEvaluation.Metrics);
        }

        private IEnumerator EvaluateFinalBatch(int entityCount, CandidateSearchState searchState, Action<FinalBatchEvaluation> onCompleted)
        {
            CurrentSampledFrameRate = 0f;
            var preparationCompleted = false;
            yield return PrepareCandidate(entityCount, value => preparationCompleted = value);
            if (!preparationCompleted || ShouldStopExecution())
            {
                onCompleted(default);
                yield break;
            }

            var metricsSampler = new PerformanceMetricsSampler();
            if (!metricsSampler.TryBegin(out var samplerErrorMessage))
            {
                SetFailure($"Failed to begin pathfinding performance metrics sampling: {samplerErrorMessage}");
                onCompleted(default);
                yield break;
            }

            var windowFrameRates = new List<float>(MeasurementWindowCount * 2);
            var measurementStartTime = Time.realtimeSinceStartupAsDouble;
            var collectionCompleted = false;
            yield return CollectFrameRateWindows(MeasurementWindowCount, windowFrameRates, metricsSampler, value => collectionCompleted = value);
            if (!collectionCompleted || ShouldStopExecution())
            {
                onCompleted(default);
                yield break;
            }

            var contradictionResamplingUsed = false;
            var preliminaryMedianFrameRate = CalculateMedian(windowFrameRates);
            var preliminaryEvaluation = new CandidateEvaluation(entityCount, preliminaryMedianFrameRate, ClassifyFrameRate(preliminaryMedianFrameRate), windowFrameRates.ToArray(), false);
            if (ShouldUseContradictionResampling(preliminaryEvaluation, searchState))
            {
                yield return CollectFrameRateWindows(MeasurementWindowCount, windowFrameRates, metricsSampler, value => collectionCompleted = value);
                if (!collectionCompleted || ShouldStopExecution())
                {
                    onCompleted(default);
                    yield break;
                }

                contradictionResamplingUsed = true;
            }

            var measurementElapsedSeconds = Time.realtimeSinceStartupAsDouble - measurementStartTime;
            if (!metricsSampler.TryComplete(measurementElapsedSeconds, out var metrics, out samplerErrorMessage))
            {
                SetFailure($"Failed to complete pathfinding performance metrics sampling: {samplerErrorMessage}");
                onCompleted(default);
                yield break;
            }

            var medianFrameRate = CalculateMedian(windowFrameRates);
            CurrentSampledFrameRate = medianFrameRate;
            onCompleted(new FinalBatchEvaluation(entityCount, medianFrameRate, ClassifyFrameRate(medianFrameRate), windowFrameRates.ToArray(), contradictionResamplingUsed, metrics));
        }

        private CandidateFrameRateClassification ClassifyFrameRate(float measuredFrameRate)
        {
            var lowerTargetFrameRate = Mathf.Max(0f, targetFrameRate - frameRateDelta);
            var upperTargetFrameRate = targetFrameRate + frameRateDelta;
            if (measuredFrameRate < lowerTargetFrameRate) return CandidateFrameRateClassification.BelowTargetWindow;
            if (measuredFrameRate > upperTargetFrameRate) return CandidateFrameRateClassification.AboveTargetWindow;
            return CandidateFrameRateClassification.WithinTargetWindow;
        }

        private static float CalculateMedian(IReadOnlyList<float> frameRates)
        {
            var sortedFrameRates = new float[frameRates.Count];
            for (var frameRateIndex = 0; frameRateIndex < frameRates.Count; frameRateIndex++) sortedFrameRates[frameRateIndex] = frameRates[frameRateIndex];
            Array.Sort(sortedFrameRates);
            var middleIndex = sortedFrameRates.Length / 2;
            return sortedFrameRates.Length % 2 == 0 ? 0.5f * (sortedFrameRates[middleIndex - 1] + sortedFrameRates[middleIndex]) : sortedFrameRates[middleIndex];
        }

        private bool ShouldUseContradictionResampling(CandidateEvaluation candidateEvaluation, CandidateSearchState searchState)
        {
            if (!candidateEvaluation.IsValid || candidateEvaluation.ContradictionResamplingUsed) return false;
            if (DoWindowsSpanTargetRange(candidateEvaluation.WindowFrameRates)) return true;
            if (searchState.Candidates.TryGetValue(candidateEvaluation.EntityCount, out var previousEvaluation) && previousEvaluation.Classification != candidateEvaluation.Classification) return true;
            if (searchState.HasAboveTargetBoundary && IsHigherEntityCountMateriallyFaster(candidateEvaluation, searchState.AboveTargetBoundary)) return true;
            return searchState.HasBelowTargetBoundary && IsHigherEntityCountMateriallyFaster(candidateEvaluation, searchState.BelowTargetBoundary);
        }

        private bool DoWindowsSpanTargetRange(IReadOnlyList<float> windowFrameRates)
        {
            var lowerTargetFrameRate = Mathf.Max(0f, targetFrameRate - frameRateDelta);
            var upperTargetFrameRate = targetFrameRate + frameRateDelta;
            var hasBelowTargetWindow = false;
            var hasAboveTargetWindow = false;
            for (var windowIndex = 0; windowIndex < windowFrameRates.Count; windowIndex++)
            {
                hasBelowTargetWindow |= windowFrameRates[windowIndex] < lowerTargetFrameRate;
                hasAboveTargetWindow |= windowFrameRates[windowIndex] > upperTargetFrameRate;
            }

            return hasBelowTargetWindow && hasAboveTargetWindow;
        }

        private static bool IsHigherEntityCountMateriallyFaster(CandidateEvaluation firstEvaluation, CandidateEvaluation secondEvaluation)
        {
            if (!firstEvaluation.IsValid || !secondEvaluation.IsValid || firstEvaluation.EntityCount == secondEvaluation.EntityCount) return false;
            var higherCountEvaluation = firstEvaluation.EntityCount > secondEvaluation.EntityCount ? firstEvaluation : secondEvaluation;
            var lowerCountEvaluation = firstEvaluation.EntityCount > secondEvaluation.EntityCount ? secondEvaluation : firstEvaluation;
            var materialDifference = Mathf.Max(Mathf.Epsilon, Mathf.Abs(lowerCountEvaluation.MedianMeasuredFrameRate) * MaterialContradictionRelativeDifference);
            return higherCountEvaluation.MedianMeasuredFrameRate - lowerCountEvaluation.MedianMeasuredFrameRate > materialDifference;
        }

        private void RetainBestObservedCandidate(CandidateSearchState searchState, CandidateEvaluation candidateEvaluation)
        {
            if (!candidateEvaluation.IsValid) return;
            searchState.Candidates[candidateEvaluation.EntityCount] = candidateEvaluation;
            searchState.HasBestObservedCandidate = false;
            foreach (var observedCandidate in searchState.Candidates.Values)
            {
                if (!searchState.HasBestObservedCandidate || IsCandidateCloser(observedCandidate, searchState.BestObservedCandidate))
                {
                    searchState.HasBestObservedCandidate = true;
                    searchState.BestObservedCandidate = observedCandidate;
                }
            }

            RebuildSearchBoundaries(searchState);
        }

        private void RebuildSearchBoundaries(CandidateSearchState searchState)
        {
            searchState.HasAboveTargetBoundary = false;
            searchState.HasBelowTargetBoundary = false;
            var smallestBracketWidth = int.MaxValue;
            foreach (var aboveTargetCandidate in searchState.Candidates.Values)
            {
                if (aboveTargetCandidate.Classification != CandidateFrameRateClassification.AboveTargetWindow) continue;
                foreach (var belowTargetCandidate in searchState.Candidates.Values)
                {
                    if (belowTargetCandidate.Classification != CandidateFrameRateClassification.BelowTargetWindow || aboveTargetCandidate.EntityCount >= belowTargetCandidate.EntityCount) continue;
                    var bracketWidth = belowTargetCandidate.EntityCount - aboveTargetCandidate.EntityCount;
                    if (bracketWidth >= smallestBracketWidth) continue;
                    smallestBracketWidth = bracketWidth;
                    searchState.HasAboveTargetBoundary = true;
                    searchState.AboveTargetBoundary = aboveTargetCandidate;
                    searchState.HasBelowTargetBoundary = true;
                    searchState.BelowTargetBoundary = belowTargetCandidate;
                }
            }

            if (searchState.HasAboveTargetBoundary && searchState.HasBelowTargetBoundary) return;
            foreach (var observedCandidate in searchState.Candidates.Values)
            {
                if (observedCandidate.Classification == CandidateFrameRateClassification.AboveTargetWindow && (!searchState.HasAboveTargetBoundary || observedCandidate.EntityCount > searchState.AboveTargetBoundary.EntityCount))
                {
                    searchState.HasAboveTargetBoundary = true;
                    searchState.AboveTargetBoundary = observedCandidate;
                }

                if (observedCandidate.Classification == CandidateFrameRateClassification.BelowTargetWindow && (!searchState.HasBelowTargetBoundary || observedCandidate.EntityCount < searchState.BelowTargetBoundary.EntityCount))
                {
                    searchState.HasBelowTargetBoundary = true;
                    searchState.BelowTargetBoundary = observedCandidate;
                }
            }
        }

        private static bool TryGetValidSearchBracket(CandidateSearchState searchState, out CandidateEvaluation aboveTargetBoundary, out CandidateEvaluation belowTargetBoundary)
        {
            aboveTargetBoundary = searchState.AboveTargetBoundary;
            belowTargetBoundary = searchState.BelowTargetBoundary;
            return searchState.HasAboveTargetBoundary && searchState.HasBelowTargetBoundary && aboveTargetBoundary.EntityCount < belowTargetBoundary.EntityCount;
        }

        private static bool TryGetUnmeasuredRefinementCandidate(CandidateSearchState searchState, Dictionary<int, FinalBatchEvaluation> finalEvaluations, out int entityCount)
        {
            entityCount = 0;
            if (TryGetValidSearchBracket(searchState, out var aboveTargetBoundary, out var belowTargetBoundary))
            {
                if (belowTargetBoundary.EntityCount - aboveTargetBoundary.EntityCount <= 1) return false;
                entityCount = aboveTargetBoundary.EntityCount + (belowTargetBoundary.EntityCount - aboveTargetBoundary.EntityCount) / 2;
                return !finalEvaluations.ContainsKey(entityCount);
            }

            if (searchState.HasAboveTargetBoundary && !searchState.HasBelowTargetBoundary && searchState.AboveTargetBoundary.EntityCount < MaximumEntityCount)
            {
                var aboveTargetEntityCount = searchState.AboveTargetBoundary.EntityCount;
                entityCount = aboveTargetEntityCount == 0 ? InitialRampEntityCount : Mathf.Min(MaximumEntityCount, Mathf.Max(aboveTargetEntityCount + 1, aboveTargetEntityCount * 2));
                return !finalEvaluations.ContainsKey(entityCount);
            }

            if (searchState.HasBelowTargetBoundary && !searchState.HasAboveTargetBoundary && searchState.BelowTargetBoundary.EntityCount > 0)
            {
                entityCount = searchState.BelowTargetBoundary.EntityCount / 2;
                return !finalEvaluations.ContainsKey(entityCount);
            }

            return false;
        }

        private bool TryGetClosestObservedCandidate(CandidateSearchState searchState, CandidateFrameRateClassification classification, out CandidateEvaluation closestCandidate)
        {
            closestCandidate = default;
            var hasClosestCandidate = false;
            foreach (var observedCandidate in searchState.Candidates.Values)
            {
                if (observedCandidate.Classification != classification) continue;
                if (!hasClosestCandidate || IsCandidateCloser(observedCandidate, closestCandidate))
                {
                    hasClosestCandidate = true;
                    closestCandidate = observedCandidate;
                }
            }

            return hasClosestCandidate;
        }

        private bool TryGetClosestFinalEvaluation(Dictionary<int, FinalBatchEvaluation> finalEvaluations, out FinalBatchEvaluation closestEvaluation)
        {
            closestEvaluation = default;
            var hasClosestEvaluation = false;
            foreach (var finalEvaluation in finalEvaluations.Values)
            {
                if (!hasClosestEvaluation || IsFrameRateResultCloser(finalEvaluation.EntityCount, finalEvaluation.MedianMeasuredFrameRate, closestEvaluation.EntityCount, closestEvaluation.MedianMeasuredFrameRate))
                {
                    hasClosestEvaluation = true;
                    closestEvaluation = finalEvaluation;
                }
            }

            return hasClosestEvaluation;
        }

        private bool IsCandidateCloser(CandidateEvaluation candidate, CandidateEvaluation currentClosest)
        {
            return IsFrameRateResultCloser(candidate.EntityCount, candidate.MedianMeasuredFrameRate, currentClosest.EntityCount, currentClosest.MedianMeasuredFrameRate);
        }

        private bool IsFrameRateResultCloser(int candidateEntityCount, float candidateFrameRate, int currentEntityCount, float currentFrameRate)
        {
            var candidateDistance = Mathf.Abs(candidateFrameRate - targetFrameRate);
            var currentDistance = Mathf.Abs(currentFrameRate - targetFrameRate);
            if (!Mathf.Approximately(candidateDistance, currentDistance)) return candidateDistance < currentDistance;
            var lowerTargetFrameRate = Mathf.Max(0f, targetFrameRate - frameRateDelta);
            var candidateMeetsLowerBound = candidateFrameRate >= lowerTargetFrameRate;
            var currentMeetsLowerBound = currentFrameRate >= lowerTargetFrameRate;
            if (candidateMeetsLowerBound != currentMeetsLowerBound) return candidateMeetsLowerBound;
            return candidateEntityCount > currentEntityCount;
        }


        private bool TryWriteControlData(int desiredEntityCount, int gridSide)
        {
            if (!controlEntityCreated || benchmarkWorld == null || !benchmarkWorld.IsCreated || !entityManager.Exists(controlEntity))
            {
                SetFailure("Pathfinding benchmark control entity is unavailable.");
                return false;
            }

            try
            {
                entityManager.SetComponentData(controlEntity, CreateControlData(desiredEntityCount, gridSide));
                return true;
            }
            catch (Exception exception)
            {
                SetFailure($"Failed to update pathfinding benchmark control data: {exception.Message}");
                return false;
            }
        }

        private PathfindingTestControlData CreateControlData(int desiredEntityCount, int gridSide)
        {
            var clampedGridSide = Mathf.Max(MinimumGridSide, gridSide);
            return new PathfindingTestControlData
            {
                DesiredEntityCount = Mathf.Clamp(desiredEntityCount, 0, MaximumEntityCount),
                MoveSpeed = MovementSpeedUnitsPerSecond,
                ArrivalDistanceSquared = ArrivalDistance * ArrivalDistance,
                BaseRandomSeed = BaseRandomSeed,
                ConfigurationVersion = configurationVersion,
                RequestedGridDimensions = new int2(clampedGridSide, clampedGridSide)
            };
        }

        private bool HasRuntimeReachedCandidate(int expectedEntityCount, int expectedGridSide)
        {
            if (benchmarkWorld == null || !benchmarkWorld.IsCreated)
            {
                SetFailure("The default ECS world became unavailable during the pathfinding benchmark.");
                return false;
            }

            var runtimeDataCount = runtimeQuery.CalculateEntityCount();
            if (runtimeDataCount != 1)
            {
                SetFailure($"Pathfinding benchmark expected one runtime singleton but found {runtimeDataCount}.");
                return false;
            }

            var runtimeData = runtimeQuery.GetSingleton<PathfindingTestRuntimeData>();
            CurrentEntityCount = Mathf.Max(0, runtimeData.CurrentEntityCount);
            if (runtimeData.Status == PathfindingTestRuntimeStatus.Failed || runtimeData.CurrentEntityCount < 0)
            {
                SetFailure(GetRuntimeFailureMessage(runtimeData.Error));
                return false;
            }

            if (runtimeData.CurrentWallCount < 0 || runtimeData.CurrentWallCount != runtimeData.ExpectedWallCount) return false;
            var gridData = spawnerQuery.GetSingleton<PathfindingGridData>();
            var expectedDimensions = new int2(expectedGridSide, expectedGridSide);
            return runtimeData.CurrentEntityCount == expectedEntityCount && runtimeData.AppliedConfigurationVersion == configurationVersion && runtimeData.Status == PathfindingTestRuntimeStatus.Ready && gridData.BuildStatus == PathfindingGridBuildStatus.Ready && gridData.Dimensions.Equals(expectedDimensions) && runtimeData.AppliedGridVersion == gridData.GridVersion;
        }

        private void LogCandidatePreparationState(int desiredEntityCount)
        {
            if (benchmarkWorld == null || !benchmarkWorld.IsCreated || runtimeQuery.CalculateEntityCount() != 1 || spawnerQuery.CalculateEntityCount() != 1) return;
            var runtimeData = runtimeQuery.GetSingleton<PathfindingTestRuntimeData>();
            var gridData = spawnerQuery.GetSingleton<PathfindingGridData>();
            var pendingPathCount = initializationPendingQuery.CalculateEntityCount();
            var failedPathCount = searchFailedQuery.CalculateEntityCount();
            // Debug.Log($"A* pathfinding candidate {desiredEntityCount:N0}: agents {runtimeData.CurrentEntityCount:N0}/{desiredEntityCount:N0}, walls {runtimeData.CurrentWallCount:N0}/{runtimeData.ExpectedWallCount:N0}, pending initial paths {pendingPathCount:N0}, failed paths {failedPathCount:N0}, grid {gridData.BuildStatus} v{gridData.GridVersion}, runtime {runtimeData.Status}, applied configuration {runtimeData.AppliedConfigurationVersion}/{configurationVersion}.", this);
        }

        private static string GetRuntimeFailureMessage(PathfindingTestRuntimeError error)
        {
            return error switch
            {
                PathfindingTestRuntimeError.InvalidSingletonCount => "Pathfinding benchmark requires exactly one spawner, control, and runtime singleton.",
                PathfindingTestRuntimeError.InvalidGridDimensions => "Pathfinding benchmark received invalid or overflowing grid dimensions.",
                PathfindingTestRuntimeError.InvalidWallPrefab => "Pathfinding benchmark wall prefab is missing its writable transform or wall authoring tag.",
                PathfindingTestRuntimeError.GridBuildFailed => "Pathfinding benchmark could not allocate or build consistent grid connectivity data.",
                PathfindingTestRuntimeError.InvalidAgentPrefab => "Pathfinding benchmark agent prefab is missing required transform, navigation states, or waypoint buffer data.",
                PathfindingTestRuntimeError.InvalidGridMetadata => "Pathfinding benchmark generated inconsistent grid or reachable-region metadata.",
                PathfindingTestRuntimeError.ScratchAllocationFailed => "Pathfinding benchmark could not allocate per-worker A* scratch memory.",
                PathfindingTestRuntimeError.SearchFailed => "An agent exhausted its A* search despite selecting a destination from its connected region.",
                _ => "Pathfinding benchmark setup failed for an unspecified runtime reason."
            };
        }



        private bool HasRuntimeReachedCleanup()
        {
            if (benchmarkWorld == null || !benchmarkWorld.IsCreated)
            {
                cleanupFailureMessage = "The default ECS world became unavailable during pathfinding benchmark cleanup.";
                return false;
            }

            var runtimeDataCount = runtimeQuery.CalculateEntityCount();
            if (runtimeDataCount != 1)
            {
                cleanupFailureMessage = $"Pathfinding benchmark cleanup expected one runtime singleton but found {runtimeDataCount}.";
                return false;
            }

            var runtimeData = runtimeQuery.GetSingleton<PathfindingTestRuntimeData>();
            CurrentEntityCount = Mathf.Max(0, runtimeData.CurrentEntityCount);
            if (runtimeData.Status == PathfindingTestRuntimeStatus.Failed) return runtimeData.CurrentEntityCount <= 0;
            if (runtimeData.CurrentEntityCount < 0)
            {
                cleanupFailureMessage = "Pathfinding benchmark cleanup encountered an invalid runtime entity count.";
                return false;
            }

            return runtimeData.CurrentEntityCount == 0 && runtimeData.AppliedConfigurationVersion == configurationVersion;
        }

        private int CalculateGridSide(int entityCount)
        {
            var clampedEntityCount = Mathf.Clamp(entityCount, 0, MaximumEntityCount);
            var halfExtent = Mathf.Max(MinimumSimulationHalfExtent, 0.5f * Mathf.Sqrt(clampedEntityCount));
            return Mathf.Max(MinimumGridSide, Mathf.CeilToInt(halfExtent * 2f));
        }

        private void FramePathfindingCamera(int gridSide)
        {
            if (pathfindingCamera == null) return;
            var paddedHalfExtent = Mathf.Max(MinimumSimulationHalfExtent, gridSide * 0.5f) * CameraBoundsPadding;
            var verticalFieldOfView = Mathf.Clamp(pathfindingCamera.fieldOfView, MinimumCameraFieldOfView, MaximumCameraFieldOfView);
            var verticalHalfFieldOfViewRadians = verticalFieldOfView * 0.5f * Mathf.Deg2Rad;
            var aspect = Mathf.Max(MinimumCameraAspect, pathfindingCamera.aspect);
            var verticalFitHeight = paddedHalfExtent / Mathf.Tan(verticalHalfFieldOfViewRadians);
            var horizontalHalfFieldOfViewRadians = Mathf.Atan(Mathf.Tan(verticalHalfFieldOfViewRadians) * aspect);
            var horizontalFitHeight = paddedHalfExtent / Mathf.Tan(horizontalHalfFieldOfViewRadians);
            var cameraHeight = Mathf.Max(verticalFitHeight, horizontalFitHeight);
            pathfindingCamera.orthographic = false;
            pathfindingCamera.useOcclusionCulling = false;
            pathfindingCamera.transform.SetPositionAndRotation(new Vector3(0f, cameraHeight, 0f), Quaternion.Euler(90f, 0f, 0f));
            pathfindingCamera.farClipPlane = Mathf.Max(pathfindingCamera.farClipPlane, cameraHeight + MinimumFarClipPadding);
        }

        private void CaptureAndDeactivateMainSceneRoots()
        {
            if (!mainScene.IsValid() || !mainScene.isLoaded)
            {
                SetFailure("The menu's MainScene is not loaded.");
                return;
            }

            var menuRoot = transform.root.gameObject;
            var sceneRoots = mainScene.GetRootGameObjects();
            for (var rootIndex = 0; rootIndex < sceneRoots.Length; rootIndex++)
            {
                var sceneRoot = sceneRoots[rootIndex];
                mainSceneRootStates.Add(new RootActiveState(sceneRoot.activeSelf, sceneRoot));
                if (sceneRoot != menuRoot && sceneRoot.activeSelf) sceneRoot.SetActive(false);
            }
        }

        private IEnumerator RemoveEntitiesAndUnloadScene()
        {
            if (cleanupStarted) yield break;
            cleanupStarted = true;
            if (controlEntityCreated && benchmarkWorld != null && benchmarkWorld.IsCreated && entityManager.Exists(controlEntity))
            {
                configurationVersion = configurationVersion == int.MaxValue ? 1 : configurationVersion + 1;
                if (TryWriteControlData(0, MinimumGridSide))
                {
                    var cleanupStartTime = Time.realtimeSinceStartupAsDouble;
                    while (!HasRuntimeReachedCleanup() && string.IsNullOrEmpty(cleanupFailureMessage))
                    {
                        if (Time.realtimeSinceStartupAsDouble - cleanupStartTime >= EntityTransitionTimeoutSeconds)
                        {
                            cleanupFailureMessage = "Timed out while removing pathfinding benchmark entities during cleanup.";
                            break;
                        }

                        yield return null;
                    }
                }

                DestroyRemainingRuntimeEntities();
                if (entityManager.Exists(controlEntity)) entityManager.DestroyEntity(controlEntity);
                controlEntityCreated = false;
                controlEntity = Entity.Null;
            }

            DisposeQueries();
            if (mainScene.IsValid() && mainScene.isLoaded) SceneManager.SetActiveScene(mainScene);
            if (pathfindingSceneLoaded && pathfindingScene.IsValid() && pathfindingScene.isLoaded)
            {
                var unloadOperation = StartPathfindingSceneUnload();
                if (unloadOperation != null)
                {
                    while (!unloadOperation.isDone) yield return null;
                }
            }

            pathfindingSceneLoaded = false;
            RestoreMainSceneState();
        }

        private void DestroyRemainingRuntimeEntities()
        {
            if (benchmarkWorld == null || !benchmarkWorld.IsCreated) return;
            try
            {
                var remainingAgentsQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<PathfindingAgentTag>());
                if (remainingAgentsQuery.CalculateEntityCount() > 0) entityManager.DestroyEntity(remainingAgentsQuery);
                remainingAgentsQuery.Dispose();
                var remainingWallsQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<PathfindingWallTag>());
                if (remainingWallsQuery.CalculateEntityCount() > 0) entityManager.DestroyEntity(remainingWallsQuery);
                remainingWallsQuery.Dispose();
                CurrentEntityCount = 0;
            }
            catch (Exception exception)
            {
                cleanupFailureMessage = $"Failed to destroy remaining pathfinding benchmark entities: {exception.Message}";
            }
        }

        private AsyncOperation StartPathfindingSceneUnload()
        {
            try
            {
                var unloadOperation = SceneManager.UnloadSceneAsync(pathfindingScene);
                if (unloadOperation == null) cleanupFailureMessage = "Failed to start unloading the pathfinding benchmark scene.";
                return unloadOperation;
            }
            catch (Exception exception)
            {
                cleanupFailureMessage = $"Failed to unload the pathfinding benchmark scene: {exception.Message}";
                return null;
            }
        }

        private void DisposeQueries()
        {
            if (!queriesCreated) return;
            if (benchmarkWorld != null && benchmarkWorld.IsCreated)
            {
                spawnerQuery.Dispose();
                controlQuery.Dispose();
                runtimeQuery.Dispose();
                initializationPendingQuery.Dispose();
                searchFailedQuery.Dispose();
            }

            queriesCreated = false;
            spawnerQuery = default;
            controlQuery = default;
            runtimeQuery = default;
            initializationPendingQuery = default;
            searchFailedQuery = default;
        }

        private void RestoreMainSceneState()
        {
            for (var rootIndex = 0; rootIndex < mainSceneRootStates.Count; rootIndex++)
            {
                var rootState = mainSceneRootStates[rootIndex];
                if (rootState.Root != null) rootState.Root.SetActive(rootState.WasActive);
            }

            mainSceneRootStates.Clear();
        }

        private bool ShouldStopExecution()
        {
            return cancellationRequested || !string.IsNullOrEmpty(failureMessage);
        }

        private void SetFailure(string message)
        {
            if (string.IsNullOrEmpty(failureMessage)) failureMessage = message;
        }

        private PathfindingPerformanceTestResult ResolveFinalResult()
        {
            if (!string.IsNullOrEmpty(cleanupFailureMessage)) return PathfindingPerformanceTestResult.Failure(cleanupFailureMessage);
            if (cancellationRequested) return PathfindingPerformanceTestResult.Failure("A* pathfinding benchmark was cancelled.");
            if (!string.IsNullOrEmpty(failureMessage)) return PathfindingPerformanceTestResult.Failure(failureMessage);
            return measuredResult;
        }

        private enum CandidateFrameRateClassification : byte
        {
            BelowTargetWindow,
            WithinTargetWindow,
            AboveTargetWindow
        }

        private sealed class CandidateSearchState
        {
            internal readonly Dictionary<int, CandidateEvaluation> Candidates = new Dictionary<int, CandidateEvaluation>();
            internal bool HasBestObservedCandidate;
            internal bool HasAboveTargetBoundary;
            internal bool HasBelowTargetBoundary;
            internal CandidateEvaluation BestObservedCandidate;
            internal CandidateEvaluation AboveTargetBoundary;
            internal CandidateEvaluation BelowTargetBoundary;
        }

        private readonly struct FrameRateWindowSample
        {
            internal readonly bool IsValid;
            internal readonly float FrameRate;

            internal FrameRateWindowSample(bool isValid, float frameRate)
            {
                IsValid = isValid;
                FrameRate = frameRate;
            }
        }

        private readonly struct CandidateEvaluation
        {
            internal readonly bool ContradictionResamplingUsed;
            internal readonly CandidateFrameRateClassification Classification;
            internal readonly int EntityCount;
            internal readonly float MedianMeasuredFrameRate;
            internal readonly float[] WindowFrameRates;
            internal bool IsValid => WindowFrameRates != null;

            internal CandidateEvaluation(int entityCount, float medianMeasuredFrameRate, CandidateFrameRateClassification classification, float[] windowFrameRates, bool contradictionResamplingUsed)
            {
                ContradictionResamplingUsed = contradictionResamplingUsed;
                Classification = classification;
                EntityCount = entityCount;
                MedianMeasuredFrameRate = medianMeasuredFrameRate;
                WindowFrameRates = windowFrameRates;
            }
        }

        private readonly struct FinalBatchEvaluation
        {
            internal readonly bool ContradictionResamplingUsed;
            internal readonly CandidateFrameRateClassification Classification;
            internal readonly int EntityCount;
            internal readonly float MedianMeasuredFrameRate;
            internal readonly float[] WindowFrameRates;
            internal readonly PerformanceMetricsSnapshot Metrics;
            internal bool IsValid => WindowFrameRates != null;

            internal FinalBatchEvaluation(int entityCount, float medianMeasuredFrameRate, CandidateFrameRateClassification classification, float[] windowFrameRates, bool contradictionResamplingUsed, PerformanceMetricsSnapshot metrics)
            {
                ContradictionResamplingUsed = contradictionResamplingUsed;
                Classification = classification;
                EntityCount = entityCount;
                MedianMeasuredFrameRate = medianMeasuredFrameRate;
                WindowFrameRates = windowFrameRates;
                Metrics = metrics;
            }

            internal CandidateEvaluation ToCandidateEvaluation()
            {
                return new CandidateEvaluation(EntityCount, MedianMeasuredFrameRate, Classification, WindowFrameRates, ContradictionResamplingUsed);
            }
        }

        private readonly struct RootActiveState
        {
            internal readonly bool WasActive;
            internal readonly GameObject Root;

            internal RootActiveState(bool wasActive, GameObject root)
            {
                WasActive = wasActive;
                Root = root;
            }
        }
    }

    public enum PathfindingFrameRateMatchStatus : byte
    {
        None,
        WithinTargetWindow,
        ClosestStableOutsideTargetWindow
    }

    public readonly struct PathfindingPerformanceTestResult
    {
        public bool Success { get; }
        public bool CapReached { get; }
        public PathfindingFrameRateMatchStatus MatchStatus { get; }
        public int SelectedEntityCount { get; }
        public float MeasuredFrameRate { get; }
        public PerformanceMetricsSnapshot Metrics { get; }
        public string ErrorMessage { get; }

        private PathfindingPerformanceTestResult(bool success, bool capReached, PathfindingFrameRateMatchStatus matchStatus, int selectedEntityCount, float measuredFrameRate, PerformanceMetricsSnapshot metrics, string errorMessage)
        {
            Success = success;
            CapReached = capReached;
            MatchStatus = matchStatus;
            SelectedEntityCount = selectedEntityCount;
            MeasuredFrameRate = measuredFrameRate;
            Metrics = metrics;
            ErrorMessage = errorMessage;
        }

        internal static PathfindingPerformanceTestResult Successful(int selectedEntityCount, bool capReached, float measuredFrameRate, PathfindingFrameRateMatchStatus matchStatus, PerformanceMetricsSnapshot metrics)
        {
            return new PathfindingPerformanceTestResult(true, capReached, matchStatus, selectedEntityCount, measuredFrameRate, metrics, string.Empty);
        }

        internal static PathfindingPerformanceTestResult Failure(string errorMessage)
        {
            return new PathfindingPerformanceTestResult(false, false, PathfindingFrameRateMatchStatus.None, 0, 0f, default, errorMessage);
        }
    }
}
