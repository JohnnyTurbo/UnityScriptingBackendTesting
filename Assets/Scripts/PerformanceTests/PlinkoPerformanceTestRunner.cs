using System;
using System.Collections;
using System.Collections.Generic;
using TMG.CoreCLRTest;
using Unity.Entities;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CoreCLRTest.PerformanceTests
{
    [DisallowMultipleComponent]
    public sealed class PlinkoPerformanceTestRunner : MonoBehaviour
    {
        private const string PlinkoScenePath = "Assets/Scenes/PlinkoScene.unity";
        private const int MaximumEntityCount = 1_000_000;
        private const int MaximumFrameRateDelta = 1000;
        private const int InitialRampEntityCount = 1024;
        private const int RequiredConsecutiveFailureSamples = 2;
        private const int InvalidRuntimeEntityCount = -1;
        private const float CandidateSettleDurationSeconds = 1f;
        private const float SampleWindowDurationSeconds = 3f;
        private const float SceneReadinessTimeoutSeconds = 60f;
        private const float EntityTransitionReadinessMarginSeconds = 30f;
        private const uint BaseRandomSeed = 0xB5297A4Du;
        [SerializeField, Min(1)] private int maximumBallsPerFrame = 128;
        [SerializeField, Min(0f)] private float spawnGroupIntervalSeconds = 0.1f;
        private readonly List<RootActiveState> mainSceneRootStates = new List<RootActiveState>();
        private Scene mainScene;
        private Scene previousActiveScene;
        private Scene plinkoScene;
        private World benchmarkWorld;
        private EntityManager entityManager;
        private Entity controlEntity;
        private EntityQuery spawnerQuery;
        private EntityQuery controlQuery;
        private EntityQuery runtimeQuery;
        private Camera plinkoCamera;
        private int targetFrameRate;
        private int frameRateDelta;
        private int configurationVersion;
        private bool isRunning;
        private bool cancellationRequested;
        private bool plinkoSceneLoaded;
        private bool controlEntityCreated;
        private bool cleanupStarted;
        private bool queriesCreated;
        private bool completionInvoked;
        private string failureMessage;
        private string cleanupFailureMessage;
        private PlinkoPerformanceTestResult measuredResult;

        internal int CurrentEntityCount { get; private set; }
        internal float CurrentSampledFrameRate { get; private set; }

        internal IEnumerator Run(int requestedTargetFrameRate, int requestedFrameRateDelta, Action<PlinkoPerformanceTestResult> onCompleted)
        {
            if (isRunning)
            {
                onCompleted?.Invoke(PlinkoPerformanceTestResult.Failure("The Plinko benchmark is already running."));
                yield break;
            }

            InitializeRun(requestedTargetFrameRate, requestedFrameRateDelta);
            ValidateConfiguration();
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

        internal void Cancel()
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
            plinkoSceneLoaded = false;
            controlEntityCreated = false;
            cleanupStarted = false;
            queriesCreated = false;
            completionInvoked = false;
            failureMessage = string.Empty;
            cleanupFailureMessage = string.Empty;
            measuredResult = PlinkoPerformanceTestResult.Failure("The Plinko benchmark did not complete.");
            mainSceneRootStates.Clear();
            mainScene = gameObject.scene;
            previousActiveScene = SceneManager.GetActiveScene();
            plinkoScene = default;
            benchmarkWorld = null;
            entityManager = default;
            controlEntity = Entity.Null;
            plinkoCamera = null;
            spawnerQuery = default;
            controlQuery = default;
            runtimeQuery = default;
        }

        private void ValidateConfiguration()
        {
            if (maximumBallsPerFrame < 1)
            {
                SetFailure("Plinko benchmark maximum balls per frame must be at least one.");
                return;
            }

            if (spawnGroupIntervalSeconds < 0f || float.IsNaN(spawnGroupIntervalSeconds) || float.IsInfinity(spawnGroupIntervalSeconds)) SetFailure("Plinko benchmark spawn group interval must be a finite non-negative value.");
        }

        private IEnumerator ExecuteBenchmark()
        {
            yield return LoadPlinkoScene();
            if (ShouldStopExecution()) yield break;

            CreateControlEntity();
            if (ShouldStopExecution()) yield break;

            var lastPassingCount = 0;
            var firstFailingCount = -1;
            var candidateCount = InitialRampEntityCount;
            var candidateEvaluation = default(CandidateEvaluation);
            yield return EvaluateCandidate(candidateCount, value => candidateEvaluation = value);
            if (ShouldStopExecution()) yield break;

            if (candidateEvaluation.WithinTolerance)
            {
                yield return FinalizeBenchmark(candidateCount, false);
                yield break;
            }

            if (candidateEvaluation.Passed)
            {
                lastPassingCount = candidateCount;
                candidateCount = Mathf.Min(candidateCount * 2, MaximumEntityCount);
            }
            else
            {
                firstFailingCount = candidateCount;
                candidateEvaluation = default;
                yield return EvaluateCandidate(0, value => candidateEvaluation = value);
                if (ShouldStopExecution()) yield break;
                if (!candidateEvaluation.Passed)
                {
                    yield return FinalizeBenchmark(0, false);
                    yield break;
                }
            }

            while (firstFailingCount < 0 && candidateCount <= MaximumEntityCount)
            {
                candidateEvaluation = default;
                yield return EvaluateCandidate(candidateCount, value => candidateEvaluation = value);
                if (ShouldStopExecution()) yield break;

                if (candidateCount == MaximumEntityCount && candidateEvaluation.Passed)
                {
                    yield return FinalizeBenchmark(MaximumEntityCount, true);
                    yield break;
                }

                if (candidateEvaluation.WithinTolerance)
                {
                    yield return FinalizeBenchmark(candidateCount, false);
                    yield break;
                }

                if (candidateEvaluation.Passed)
                {
                    lastPassingCount = candidateCount;
                    candidateCount = Mathf.Min(candidateCount * 2, MaximumEntityCount);
                    continue;
                }

                firstFailingCount = candidateCount;
            }

            if (firstFailingCount < 0)
            {
                SetFailure("The Plinko benchmark capacity search ended without a passing cap or failing boundary.");
                yield break;
            }

            while (firstFailingCount - lastPassingCount > 1)
            {
                candidateCount = lastPassingCount + (firstFailingCount - lastPassingCount) / 2;
                candidateEvaluation = default;
                yield return EvaluateCandidate(candidateCount, value => candidateEvaluation = value);
                if (ShouldStopExecution()) yield break;
                if (candidateEvaluation.WithinTolerance)
                {
                    yield return FinalizeBenchmark(candidateCount, false);
                    yield break;
                }

                if (candidateEvaluation.Passed) lastPassingCount = candidateCount;
                else firstFailingCount = candidateCount;
            }

            yield return FinalizeBenchmark(lastPassingCount, false);
        }

        private IEnumerator LoadPlinkoScene()
        {
            var loadOperation = StartPlinkoSceneLoad();
            if (loadOperation == null) yield break;
            while (!loadOperation.isDone) yield return null;

            plinkoScene = SceneManager.GetSceneByPath(PlinkoScenePath);
            if (!plinkoScene.IsValid() || !plinkoScene.isLoaded)
            {
                SetFailure($"Failed to load Plinko benchmark scene '{PlinkoScenePath}'.");
                yield break;
            }

            plinkoSceneLoaded = true;
            if (!SceneManager.SetActiveScene(plinkoScene))
            {
                SetFailure("Failed to make the Plinko benchmark scene active.");
                yield break;
            }

            plinkoCamera = FindPlinkoCamera();
            if (plinkoCamera == null)
            {
                SetFailure("Plinko benchmark scene does not contain a Camera component.");
                yield break;
            }

            var readinessStartTime = Time.realtimeSinceStartupAsDouble;
            while (World.DefaultGameObjectInjectionWorld == null || !World.DefaultGameObjectInjectionWorld.IsCreated)
            {
                if (cancellationRequested) yield break;
                if (Time.realtimeSinceStartupAsDouble - readinessStartTime >= SceneReadinessTimeoutSeconds)
                {
                    SetFailure("Timed out waiting for the default ECS world for the Plinko benchmark.");
                    yield break;
                }
                yield return null;
            }

            benchmarkWorld = World.DefaultGameObjectInjectionWorld;
            entityManager = benchmarkWorld.EntityManager;
            spawnerQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<PlinkoSpawnerData>());
            controlQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<PlinkoPerformanceTestControlData>());
            runtimeQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<PlinkoPerformanceTestRuntimeData>());
            queriesCreated = true;
            while (spawnerQuery.CalculateEntityCount() == 0)
            {
                if (cancellationRequested) yield break;
                if (Time.realtimeSinceStartupAsDouble - readinessStartTime >= SceneReadinessTimeoutSeconds)
                {
                    SetFailure("Timed out waiting for the auto-loaded Plinko SubScene and PlinkoSpawnerData.");
                    yield break;
                }
                yield return null;
            }

            if (spawnerQuery.CalculateEntityCount() != 1) SetFailure("Plinko benchmark requires exactly one PlinkoSpawnerData singleton.");
        }

        private AsyncOperation StartPlinkoSceneLoad()
        {
            try
            {
                return SceneManager.LoadSceneAsync(PlinkoScenePath, LoadSceneMode.Additive);
            }
            catch (Exception exception)
            {
                SetFailure($"Failed to start loading the Plinko benchmark scene: {exception.Message}");
                return null;
            }
        }

        private Camera FindPlinkoCamera()
        {
            var sceneRoots = plinkoScene.GetRootGameObjects();
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
                SetFailure("The default ECS world became unavailable before Plinko benchmark setup completed.");
                return;
            }

            if (controlQuery.CalculateEntityCount() != 0 || runtimeQuery.CalculateEntityCount() != 0)
            {
                SetFailure("Plinko benchmark control singleton data already exists.");
                return;
            }

            try
            {
                controlEntity = entityManager.CreateEntity(typeof(PlinkoPerformanceTestControlData), typeof(PlinkoPerformanceTestRuntimeData));
                entityManager.SetComponentData(controlEntity, new PlinkoPerformanceTestControlData
                {
                    DesiredEntityCount = 0,
                    MaximumBallsPerFrame = maximumBallsPerFrame,
                    SpawnGroupIntervalSeconds = spawnGroupIntervalSeconds,
                    BaseRandomSeed = BaseRandomSeed,
                    ConfigurationVersion = configurationVersion
                });
                entityManager.SetComponentData(controlEntity, new PlinkoPerformanceTestRuntimeData { CurrentEntityCount = 0, AppliedConfigurationVersion = configurationVersion });
                controlEntityCreated = true;
            }
            catch (Exception exception)
            {
                SetFailure($"Failed to create Plinko benchmark control data: {exception.Message}");
            }
        }

        private IEnumerator PrepareCandidate(int entityCount, Action<bool> onCompleted)
        {
            var clampedEntityCount = Mathf.Clamp(entityCount, 0, MaximumEntityCount);
            var previousEntityCount = CurrentEntityCount;
            configurationVersion = configurationVersion == int.MaxValue ? 1 : configurationVersion + 1;
            if (!TryWriteControlData(clampedEntityCount))
            {
                onCompleted(false);
                yield break;
            }

            var transitionTimeoutSeconds = CalculateTransitionTimeoutSeconds(previousEntityCount, clampedEntityCount);
            var transitionStartTime = Time.realtimeSinceStartupAsDouble;
            while (!HasRuntimeReachedCandidate(clampedEntityCount))
            {
                if (ShouldStopExecution())
                {
                    onCompleted(false);
                    yield break;
                }

                if (Time.realtimeSinceStartupAsDouble - transitionStartTime >= transitionTimeoutSeconds)
                {
                    SetFailure($"Timed out while changing the runtime Plinko-ball count to {clampedEntityCount:N0}.");
                    onCompleted(false);
                    yield break;
                }

                yield return null;
            }

            var settleStartTime = Time.realtimeSinceStartupAsDouble;
            while (Time.realtimeSinceStartupAsDouble - settleStartTime < CandidateSettleDurationSeconds)
            {
                if (ShouldStopExecution())
                {
                    onCompleted(false);
                    yield break;
                }

                yield return null;
            }

            onCompleted(true);
        }

        private IEnumerator EvaluateCandidate(int entityCount, Action<CandidateEvaluation> onCompleted)
        {
            var candidateEvaluation = default(CandidateEvaluation);
            for (var sampleIndex = 0; sampleIndex < RequiredConsecutiveFailureSamples; sampleIndex++)
            {
                yield return EvaluateCandidateSample(entityCount, value => candidateEvaluation = value);
                if (ShouldStopExecution()) yield break;
                if (candidateEvaluation.Passed || candidateEvaluation.WithinTolerance)
                {
                    onCompleted(candidateEvaluation);
                    yield break;
                }
            }

            onCompleted(candidateEvaluation);
        }

        private IEnumerator EvaluateCandidateSample(int entityCount, Action<CandidateEvaluation> onCompleted)
        {
            CurrentSampledFrameRate = 0f;
            var candidateEvaluation = default(CandidateEvaluation);
            var preparationCompleted = false;
            yield return PrepareCandidate(entityCount, value => preparationCompleted = value);
            if (!preparationCompleted || ShouldStopExecution())
            {
                onCompleted(candidateEvaluation);
                yield break;
            }

            var sampledFrames = 0;
            var sampledSeconds = 0f;
            while (sampledSeconds < SampleWindowDurationSeconds)
            {
                yield return null;
                if (ShouldStopExecution())
                {
                    onCompleted(candidateEvaluation);
                    yield break;
                }

                var frameDuration = Time.unscaledDeltaTime;
                if (frameDuration <= 0f) continue;
                sampledFrames++;
                sampledSeconds += frameDuration;
                CurrentSampledFrameRate = sampledFrames / sampledSeconds;
            }

            var averageFrameRate = CurrentSampledFrameRate;
            var passed = averageFrameRate >= targetFrameRate;
            var withinTolerance = frameRateDelta > 0 && Mathf.Abs(averageFrameRate - targetFrameRate) <= frameRateDelta;
            candidateEvaluation = new CandidateEvaluation(passed, withinTolerance);
            onCompleted(candidateEvaluation);
        }

        private IEnumerator FinalizeBenchmark(int selectedEntityCount, bool selectedCapReached)
        {
            var finalBatchEvaluation = default(FinalBatchEvaluation);
            yield return EvaluateFinalBatch(selectedEntityCount, value => finalBatchEvaluation = value);
            if (ShouldStopExecution()) yield break;
            if (finalBatchEvaluation.Stable)
            {
                measuredResult = PlinkoPerformanceTestResult.Successful(selectedEntityCount, selectedCapReached, finalBatchEvaluation.Metrics);
                yield break;
            }

            if (selectedEntityCount == 0)
            {
                SetFailure("Plinko benchmark could not maintain the minimum stable frame rate with zero balls.");
                yield break;
            }

            var stableLowerCount = 0;
            var failingUpperCount = selectedEntityCount;
            var acceptedEvaluation = default(FinalBatchEvaluation);
            yield return EvaluateFinalBatch(stableLowerCount, value => acceptedEvaluation = value);
            if (ShouldStopExecution()) yield break;
            if (!acceptedEvaluation.Stable)
            {
                SetFailure("Plinko benchmark could not maintain the minimum stable frame rate with zero balls.");
                yield break;
            }

            while (failingUpperCount - stableLowerCount > 1)
            {
                var candidateCount = stableLowerCount + (failingUpperCount - stableLowerCount) / 2;
                var candidateEvaluation = default(FinalBatchEvaluation);
                yield return EvaluateFinalBatch(candidateCount, value => candidateEvaluation = value);
                if (ShouldStopExecution()) yield break;
                if (candidateEvaluation.WithinTolerance)
                {
                    measuredResult = PlinkoPerformanceTestResult.Successful(candidateCount, false, candidateEvaluation.Metrics);
                    yield break;
                }

                if (candidateEvaluation.Stable)
                {
                    stableLowerCount = candidateCount;
                    acceptedEvaluation = candidateEvaluation;
                }
                else
                {
                    failingUpperCount = candidateCount;
                }
            }

            measuredResult = PlinkoPerformanceTestResult.Successful(stableLowerCount, false, acceptedEvaluation.Metrics);
        }

        private IEnumerator EvaluateFinalBatch(int entityCount, Action<FinalBatchEvaluation> onCompleted)
        {
            var finalBatchEvaluation = default(FinalBatchEvaluation);
            for (var sampleIndex = 0; sampleIndex < RequiredConsecutiveFailureSamples; sampleIndex++)
            {
                yield return EvaluateFinalBatchSample(entityCount, value => finalBatchEvaluation = value);
                if (ShouldStopExecution()) yield break;
                if (finalBatchEvaluation.Stable)
                {
                    onCompleted(finalBatchEvaluation);
                    yield break;
                }
            }

            onCompleted(finalBatchEvaluation);
        }

        private IEnumerator EvaluateFinalBatchSample(int entityCount, Action<FinalBatchEvaluation> onCompleted)
        {
            CurrentSampledFrameRate = 0f;
            var finalBatchEvaluation = default(FinalBatchEvaluation);
            var preparationCompleted = false;
            yield return PrepareCandidate(entityCount, value => preparationCompleted = value);
            if (!preparationCompleted || ShouldStopExecution())
            {
                onCompleted(finalBatchEvaluation);
                yield break;
            }

            var metricsSampler = new PerformanceMetricsSampler();
            if (!metricsSampler.TryBegin(out var samplerErrorMessage))
            {
                SetFailure($"Failed to begin Plinko performance metrics sampling: {samplerErrorMessage}");
                onCompleted(finalBatchEvaluation);
                yield break;
            }

            var sampledFrames = 0;
            var sampledSeconds = 0f;
            var measurementStartTime = Time.realtimeSinceStartupAsDouble;
            while (sampledSeconds < SampleWindowDurationSeconds)
            {
                metricsSampler.RequestFrameTimingCapture();
                yield return null;
                if (ShouldStopExecution())
                {
                    onCompleted(finalBatchEvaluation);
                    yield break;
                }

                if (!metricsSampler.TryRecordCompletedFrame(out samplerErrorMessage))
                {
                    SetFailure($"Failed to record Plinko performance metrics: {samplerErrorMessage}");
                    onCompleted(finalBatchEvaluation);
                    yield break;
                }

                var frameDuration = Time.unscaledDeltaTime;
                if (frameDuration <= 0f) continue;
                sampledFrames++;
                sampledSeconds += frameDuration;
                CurrentSampledFrameRate = sampledFrames / sampledSeconds;
            }

            var measurementElapsedSeconds = Time.realtimeSinceStartupAsDouble - measurementStartTime;
            if (!metricsSampler.TryComplete(measurementElapsedSeconds, out var metrics, out samplerErrorMessage))
            {
                SetFailure($"Failed to complete Plinko performance metrics sampling: {samplerErrorMessage}");
                onCompleted(finalBatchEvaluation);
                yield break;
            }

            var averageFrameRate = sampledSeconds > 0f ? sampledFrames / sampledSeconds : 0f;
            var minimumStableFrameRate = Mathf.Max(0f, targetFrameRate - frameRateDelta);
            var stable = averageFrameRate >= minimumStableFrameRate;
            var withinTolerance = frameRateDelta > 0 && Mathf.Abs(averageFrameRate - targetFrameRate) <= frameRateDelta;
            finalBatchEvaluation = new FinalBatchEvaluation(stable, withinTolerance, metrics);
            onCompleted(finalBatchEvaluation);
        }

        private bool TryWriteControlData(int desiredEntityCount)
        {
            if (!controlEntityCreated || benchmarkWorld == null || !benchmarkWorld.IsCreated || !entityManager.Exists(controlEntity))
            {
                SetFailure("Plinko benchmark control entity is unavailable.");
                return false;
            }

            try
            {
                entityManager.SetComponentData(controlEntity, new PlinkoPerformanceTestControlData
                {
                    DesiredEntityCount = Mathf.Clamp(desiredEntityCount, 0, MaximumEntityCount),
                    MaximumBallsPerFrame = Mathf.Max(1, maximumBallsPerFrame),
                    SpawnGroupIntervalSeconds = Mathf.Max(0f, spawnGroupIntervalSeconds),
                    BaseRandomSeed = BaseRandomSeed,
                    ConfigurationVersion = configurationVersion
                });
                return true;
            }
            catch (Exception exception)
            {
                SetFailure($"Failed to update Plinko benchmark control data: {exception.Message}");
                return false;
            }
        }

        private double CalculateTransitionTimeoutSeconds(int currentEntityCount, int desiredEntityCount)
        {
            var requiredSpawnCount = Math.Max(0, desiredEntityCount - Math.Max(0, currentEntityCount));
            var spawnGroupCount = requiredSpawnCount == 0 ? 0 : (requiredSpawnCount + maximumBallsPerFrame - 1) / maximumBallsPerFrame;
            var intervalDuration = Math.Max(0, spawnGroupCount - 1) * (double)Mathf.Max(0f, spawnGroupIntervalSeconds);
            return intervalDuration + EntityTransitionReadinessMarginSeconds;
        }

        private bool HasRuntimeReachedCandidate(int expectedEntityCount)
        {
            if (benchmarkWorld == null || !benchmarkWorld.IsCreated)
            {
                SetFailure("The default ECS world became unavailable during the Plinko benchmark.");
                return false;
            }

            var runtimeDataCount = runtimeQuery.CalculateEntityCount();
            if (runtimeDataCount != 1)
            {
                SetFailure($"Plinko benchmark expected one runtime singleton but found {runtimeDataCount}.");
                return false;
            }

            var runtimeData = runtimeQuery.GetSingleton<PlinkoPerformanceTestRuntimeData>();
            CurrentEntityCount = Mathf.Max(0, runtimeData.CurrentEntityCount);
            if (runtimeData.CurrentEntityCount == InvalidRuntimeEntityCount)
            {
                SetFailure("Plinko benchmark PlinkoSpawnerData contains an invalid dynamic prefab entity.");
                return false;
            }
            return runtimeData.CurrentEntityCount == expectedEntityCount && runtimeData.AppliedConfigurationVersion == configurationVersion;
        }

        private bool HasRuntimeReachedCleanup()
        {
            if (benchmarkWorld == null || !benchmarkWorld.IsCreated)
            {
                cleanupFailureMessage = "The default ECS world became unavailable during Plinko benchmark cleanup.";
                return false;
            }

            var runtimeDataCount = runtimeQuery.CalculateEntityCount();
            if (runtimeDataCount != 1)
            {
                cleanupFailureMessage = $"Plinko benchmark cleanup expected one runtime singleton but found {runtimeDataCount}.";
                return false;
            }

            var runtimeData = runtimeQuery.GetSingleton<PlinkoPerformanceTestRuntimeData>();
            CurrentEntityCount = Mathf.Max(0, runtimeData.CurrentEntityCount);
            if (runtimeData.CurrentEntityCount == InvalidRuntimeEntityCount)
            {
                cleanupFailureMessage = "Plinko benchmark cleanup could not use the invalid dynamic prefab.";
                return false;
            }
            return runtimeData.CurrentEntityCount == 0 && runtimeData.AppliedConfigurationVersion == configurationVersion;
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
                var previousEntityCount = CurrentEntityCount;
                configurationVersion = configurationVersion == int.MaxValue ? 1 : configurationVersion + 1;
                if (TryWriteControlData(0))
                {
                    var cleanupTimeoutSeconds = CalculateTransitionTimeoutSeconds(previousEntityCount, 0);
                    var cleanupStartTime = Time.realtimeSinceStartupAsDouble;
                    while (!HasRuntimeReachedCleanup() && string.IsNullOrEmpty(cleanupFailureMessage))
                    {
                        if (Time.realtimeSinceStartupAsDouble - cleanupStartTime >= cleanupTimeoutSeconds)
                        {
                            cleanupFailureMessage = "Timed out while removing Plinko benchmark balls during cleanup.";
                            break;
                        }
                        yield return null;
                    }
                }
            }

            DestroyRemainingRuntimeEntities();
            if (controlEntityCreated && benchmarkWorld != null && benchmarkWorld.IsCreated && entityManager.Exists(controlEntity)) entityManager.DestroyEntity(controlEntity);
            controlEntityCreated = false;
            controlEntity = Entity.Null;
            DisposeQueries();
            var sceneToRestore = previousActiveScene.IsValid() && previousActiveScene.isLoaded ? previousActiveScene : mainScene;
            if (sceneToRestore.IsValid() && sceneToRestore.isLoaded && !SceneManager.SetActiveScene(sceneToRestore) && string.IsNullOrEmpty(cleanupFailureMessage)) cleanupFailureMessage = "Failed to restore the active scene after the Plinko benchmark.";

            if (plinkoSceneLoaded && plinkoScene.IsValid() && plinkoScene.isLoaded)
            {
                var unloadOperation = StartPlinkoSceneUnload();
                if (unloadOperation != null)
                {
                    while (!unloadOperation.isDone) yield return null;
                    if (plinkoScene.isLoaded && string.IsNullOrEmpty(cleanupFailureMessage)) cleanupFailureMessage = "Plinko benchmark scene remained loaded after its unload operation completed.";
                }
            }

            plinkoSceneLoaded = false;
            RestoreMainSceneState();
        }

        private void DestroyRemainingRuntimeEntities()
        {
            if (benchmarkWorld == null || !benchmarkWorld.IsCreated) return;
            try
            {
                var remainingEntitiesQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<PlinkoPerformanceTestEntityTag>());
                if (remainingEntitiesQuery.CalculateEntityCount() > 0) entityManager.DestroyEntity(remainingEntitiesQuery);
                remainingEntitiesQuery.Dispose();
                CurrentEntityCount = 0;
            }
            catch (Exception exception)
            {
                cleanupFailureMessage = $"Failed to destroy remaining Plinko benchmark balls: {exception.Message}";
            }
        }

        private AsyncOperation StartPlinkoSceneUnload()
        {
            try
            {
                var unloadOperation = SceneManager.UnloadSceneAsync(plinkoScene);
                if (unloadOperation == null) cleanupFailureMessage = "Failed to start unloading the Plinko benchmark scene.";
                return unloadOperation;
            }
            catch (Exception exception)
            {
                cleanupFailureMessage = $"Failed to unload the Plinko benchmark scene: {exception.Message}";
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
            }

            queriesCreated = false;
            spawnerQuery = default;
            controlQuery = default;
            runtimeQuery = default;
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

        private PlinkoPerformanceTestResult ResolveFinalResult()
        {
            if (!string.IsNullOrEmpty(cleanupFailureMessage)) return PlinkoPerformanceTestResult.Failure(cleanupFailureMessage);
            if (cancellationRequested) return PlinkoPerformanceTestResult.Failure("Plinko benchmark was cancelled.");
            if (!string.IsNullOrEmpty(failureMessage)) return PlinkoPerformanceTestResult.Failure(failureMessage);
            return measuredResult;
        }

        private readonly struct CandidateEvaluation
        {
            internal readonly bool Passed;
            internal readonly bool WithinTolerance;

            internal CandidateEvaluation(bool passed, bool withinTolerance)
            {
                Passed = passed;
                WithinTolerance = withinTolerance;
            }
        }

        private readonly struct FinalBatchEvaluation
        {
            internal readonly bool Stable;
            internal readonly bool WithinTolerance;
            internal readonly PerformanceMetricsSnapshot Metrics;

            internal FinalBatchEvaluation(bool stable, bool withinTolerance, PerformanceMetricsSnapshot metrics)
            {
                Stable = stable;
                WithinTolerance = withinTolerance;
                Metrics = metrics;
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

    internal readonly struct PlinkoPerformanceTestResult
    {
        internal bool Success { get; }
        internal bool CapReached { get; }
        internal int MaximumPassingEntityCount { get; }
        internal PerformanceMetricsSnapshot Metrics { get; }
        internal string ErrorMessage { get; }

        private PlinkoPerformanceTestResult(bool success, bool capReached, int maximumPassingEntityCount, PerformanceMetricsSnapshot metrics, string errorMessage)
        {
            Success = success;
            CapReached = capReached;
            MaximumPassingEntityCount = maximumPassingEntityCount;
            Metrics = metrics;
            ErrorMessage = errorMessage;
        }

        internal static PlinkoPerformanceTestResult Successful(int maximumPassingEntityCount, bool capReached, PerformanceMetricsSnapshot metrics)
        {
            return new PlinkoPerformanceTestResult(true, capReached, maximumPassingEntityCount, metrics, string.Empty);
        }

        internal static PlinkoPerformanceTestResult Failure(string errorMessage)
        {
            return new PlinkoPerformanceTestResult(false, false, 0, default, errorMessage);
        }
    }
}
