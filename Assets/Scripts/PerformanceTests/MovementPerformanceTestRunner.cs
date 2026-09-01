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
    public sealed class MovementPerformanceTestRunner : MonoBehaviour
    {
        private const string MovementScenePath = "Assets/Scenes/MovementTestScene.unity";
        private const int MaximumEntityCount = 1_000_000;
        private const int MaximumFrameRateDelta = 1000;
        private const int InitialRampEntityCount = 1_024;
        private const int RequiredConsecutiveFailureSamples = 2;
        private const float CandidateSettleDurationSeconds = 1f;
        private const float SampleWindowDurationSeconds = 3f;
        private const float EntityDensityPerSquareMeter = 1f;
        private const float MinimumSimulationHalfExtent = 10f;
        private const float MovementSpeedUnitsPerSecond = 5f;
        private const float ArrivalDistance = 0.1f;
        private const float CameraBoundsPadding = 1.1f;
        private const float MinimumCameraAspect = 0.01f;
        private const float MinimumCameraFieldOfView = 1f;
        private const float MaximumCameraFieldOfView = 179f;
        private const float MinimumFarClipPadding = 10f;
        private const float SceneReadinessTimeoutSeconds = 60f;
        private const float EntityTransitionTimeoutSeconds = 120f;
        private const uint BaseRandomSeed = 0x6E624EB7u;
        private readonly List<RootActiveState> mainSceneRootStates = new List<RootActiveState>();
        private Scene mainScene;
        private Scene movementScene;
        private World benchmarkWorld;
        private EntityManager entityManager;
        private Entity controlEntity;
        private EntityQuery spawnerQuery;
        private EntityQuery controlQuery;
        private EntityQuery runtimeQuery;
        private Camera movementCamera;
        private int targetFrameRate;
        private int frameRateDelta;
        private int configurationVersion;
        private bool isRunning;
        private bool cancellationRequested;
        private bool movementSceneLoaded;
        private bool controlEntityCreated;
        private bool cleanupStarted;
        private bool queriesCreated;
        private bool completionInvoked;
        private string failureMessage;
        private string cleanupFailureMessage;
        private MovementPerformanceTestResult measuredResult;

        internal int CurrentEntityCount { get; private set; }
        internal float CurrentSampledFrameRate { get; private set; }

        internal IEnumerator Run(int requestedTargetFrameRate, int requestedFrameRateDelta, Action<MovementPerformanceTestResult> onCompleted)
        {
            if (isRunning)
            {
                onCompleted?.Invoke(MovementPerformanceTestResult.Failure("The movement benchmark is already running."));
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
            movementSceneLoaded = false;
            controlEntityCreated = false;
            cleanupStarted = false;
            queriesCreated = false;
            completionInvoked = false;
            failureMessage = string.Empty;
            cleanupFailureMessage = string.Empty;
            measuredResult = MovementPerformanceTestResult.Failure("The movement benchmark did not complete.");
            mainSceneRootStates.Clear();
            mainScene = gameObject.scene;
            movementScene = default;
            benchmarkWorld = null;
            entityManager = default;
            controlEntity = Entity.Null;
            movementCamera = null;
            spawnerQuery = default;
            controlQuery = default;
            runtimeQuery = default;
        }

        private IEnumerator ExecuteBenchmark()
        {
            yield return LoadMovementScene();
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
                SetFailure("The movement benchmark capacity search ended without a passing cap or failing boundary.");
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

                if (candidateEvaluation.Passed)
                {
                    lastPassingCount = candidateCount;
                }
                else
                {
                    firstFailingCount = candidateCount;
                }
            }

            yield return FinalizeBenchmark(lastPassingCount, false);
        }

        private IEnumerator LoadMovementScene()
        {
            var loadOperation = StartMovementSceneLoad();
            if (loadOperation == null) yield break;

            while (!loadOperation.isDone) yield return null;
            movementScene = SceneManager.GetSceneByPath(MovementScenePath);
            if (!movementScene.IsValid() || !movementScene.isLoaded)
            {
                SetFailure($"Failed to load movement benchmark scene '{MovementScenePath}'.");
                yield break;
            }

            movementSceneLoaded = true;
            if (!SceneManager.SetActiveScene(movementScene))
            {
                SetFailure("Failed to make the movement benchmark scene active.");
                yield break;
            }

            movementCamera = FindMovementCamera();
            if (movementCamera == null)
            {
                SetFailure("Movement benchmark scene does not contain a Camera component.");
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
            spawnerQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<EntitySpawnerData>());
            controlQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<MovementTestControlData>());
            runtimeQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<MovementTestRuntimeData>());
            queriesCreated = true;

            while (spawnerQuery.CalculateEntityCount() == 0)
            {
                if (cancellationRequested) yield break;
                if (Time.realtimeSinceStartupAsDouble - readinessStartTime >= SceneReadinessTimeoutSeconds)
                {
                    SetFailure("Timed out waiting for the auto-loaded movement SubScene and EntitySpawnerData.");
                    yield break;
                }

                yield return null;
            }

            if (spawnerQuery.CalculateEntityCount() != 1) SetFailure("Movement benchmark requires exactly one EntitySpawnerData singleton.");
        }

        private AsyncOperation StartMovementSceneLoad()
        {
            try
            {
                return SceneManager.LoadSceneAsync(MovementScenePath, LoadSceneMode.Additive);
            }
            catch (Exception exception)
            {
                SetFailure($"Failed to start loading movement benchmark scene: {exception.Message}");
                return null;
            }
        }

        private Camera FindMovementCamera()
        {
            var sceneRoots = movementScene.GetRootGameObjects();
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
                SetFailure("The default ECS world became unavailable before benchmark setup completed.");
                return;
            }

            if (controlQuery.CalculateEntityCount() != 0 || runtimeQuery.CalculateEntityCount() != 0)
            {
                SetFailure("Movement benchmark control singleton data already exists.");
                return;
            }

            try
            {
                controlEntity = entityManager.CreateEntity(typeof(MovementTestControlData), typeof(MovementTestRuntimeData));
                entityManager.SetComponentData(controlEntity, new MovementTestControlData
                {
                    DesiredEntityCount = 0,
                    SimulationHalfExtent = MinimumSimulationHalfExtent,
                    MoveSpeed = MovementSpeedUnitsPerSecond,
                    ArrivalDistanceSquared = ArrivalDistance * ArrivalDistance,
                    BaseRandomSeed = BaseRandomSeed,
                    ConfigurationVersion = configurationVersion
                });
                entityManager.SetComponentData(controlEntity, new MovementTestRuntimeData
                {
                    CurrentEntityCount = 0,
                    AppliedConfigurationVersion = configurationVersion
                });
                controlEntityCreated = true;
            }
            catch (Exception exception)
            {
                SetFailure($"Failed to create movement benchmark control data: {exception.Message}");
            }
        }

        private IEnumerator PrepareCandidate(int entityCount, Action<bool> onCompleted)
        {
            var clampedEntityCount = Mathf.Clamp(entityCount, 0, MaximumEntityCount);
            configurationVersion = configurationVersion == int.MaxValue ? 1 : configurationVersion + 1;
            var simulationHalfExtent = CalculateSimulationHalfExtent(clampedEntityCount);
            if (!TryWriteControlData(clampedEntityCount, simulationHalfExtent))
            {
                onCompleted(false);
                yield break;
            }

            FrameMovementCamera(simulationHalfExtent);
            var transitionStartTime = Time.realtimeSinceStartupAsDouble;
            while (!HasRuntimeReachedCandidate(clampedEntityCount))
            {
                if (ShouldStopExecution())
                {
                    onCompleted(false);
                    yield break;
                }

                if (Time.realtimeSinceStartupAsDouble - transitionStartTime >= EntityTransitionTimeoutSeconds)
                {
                    SetFailure($"Timed out while changing the runtime movement entity count to {clampedEntityCount:N0}.");
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
                measuredResult = MovementPerformanceTestResult.Successful(selectedEntityCount, selectedCapReached, finalBatchEvaluation.Metrics);
                yield break;
            }

            if (selectedEntityCount == 0)
            {
                SetFailure("Movement benchmark could not maintain the minimum stable frame rate with zero entities.");
                yield break;
            }

            var stableLowerCount = 0;
            var failingUpperCount = selectedEntityCount;
            var acceptedEvaluation = default(FinalBatchEvaluation);
            yield return EvaluateFinalBatch(stableLowerCount, value => acceptedEvaluation = value);
            if (ShouldStopExecution()) yield break;
            if (!acceptedEvaluation.Stable)
            {
                SetFailure("Movement benchmark could not maintain the minimum stable frame rate with zero entities.");
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
                    measuredResult = MovementPerformanceTestResult.Successful(candidateCount, false, candidateEvaluation.Metrics);
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

            measuredResult = MovementPerformanceTestResult.Successful(stableLowerCount, false, acceptedEvaluation.Metrics);
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
                SetFailure($"Failed to begin movement performance metrics sampling: {samplerErrorMessage}");
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
                    SetFailure($"Failed to record movement performance metrics: {samplerErrorMessage}");
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
                SetFailure($"Failed to complete movement performance metrics sampling: {samplerErrorMessage}");
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

        private bool TryWriteControlData(int desiredEntityCount, float simulationHalfExtent)
        {
            if (!controlEntityCreated || benchmarkWorld == null || !benchmarkWorld.IsCreated || !entityManager.Exists(controlEntity))
            {
                SetFailure("Movement benchmark control entity is unavailable.");
                return false;
            }

            try
            {
                entityManager.SetComponentData(controlEntity, new MovementTestControlData
                {
                    DesiredEntityCount = Mathf.Clamp(desiredEntityCount, 0, MaximumEntityCount),
                    SimulationHalfExtent = Mathf.Max(MinimumSimulationHalfExtent, simulationHalfExtent),
                    MoveSpeed = MovementSpeedUnitsPerSecond,
                    ArrivalDistanceSquared = ArrivalDistance * ArrivalDistance,
                    BaseRandomSeed = BaseRandomSeed,
                    ConfigurationVersion = configurationVersion
                });
                return true;
            }
            catch (Exception exception)
            {
                SetFailure($"Failed to update movement benchmark control data: {exception.Message}");
                return false;
            }
        }

        private bool HasRuntimeReachedCandidate(int expectedEntityCount)
        {
            if (benchmarkWorld == null || !benchmarkWorld.IsCreated)
            {
                SetFailure("The default ECS world became unavailable during the benchmark.");
                return false;
            }

            var runtimeDataCount = runtimeQuery.CalculateEntityCount();
            if (runtimeDataCount != 1)
            {
                SetFailure($"Movement benchmark expected one runtime singleton but found {runtimeDataCount}.");
                return false;
            }

            var runtimeData = runtimeQuery.GetSingleton<MovementTestRuntimeData>();
            CurrentEntityCount = Mathf.Max(0, runtimeData.CurrentEntityCount);
            if (runtimeData.CurrentEntityCount < 0)
            {
                SetFailure("Movement benchmark EntitySpawnerData contains an invalid prefab entity.");
                return false;
            }

            return runtimeData.CurrentEntityCount == expectedEntityCount && runtimeData.AppliedConfigurationVersion == configurationVersion;
        }

        private bool HasRuntimeReachedCleanup()
        {
            if (benchmarkWorld == null || !benchmarkWorld.IsCreated)
            {
                cleanupFailureMessage = "The default ECS world became unavailable during movement benchmark cleanup.";
                return false;
            }

            var runtimeDataCount = runtimeQuery.CalculateEntityCount();
            if (runtimeDataCount != 1)
            {
                cleanupFailureMessage = $"Movement benchmark cleanup expected one runtime singleton but found {runtimeDataCount}.";
                return false;
            }

            var runtimeData = runtimeQuery.GetSingleton<MovementTestRuntimeData>();
            CurrentEntityCount = Mathf.Max(0, runtimeData.CurrentEntityCount);
            if (runtimeData.CurrentEntityCount < 0)
            {
                cleanupFailureMessage = "Movement benchmark cleanup could not use the invalid spawner prefab.";
                return false;
            }

            return runtimeData.CurrentEntityCount == 0 && runtimeData.AppliedConfigurationVersion == configurationVersion;
        }

        private float CalculateSimulationHalfExtent(int entityCount)
        {
            var clampedEntityCount = Mathf.Clamp(entityCount, 0, MaximumEntityCount);
            return Mathf.Max(MinimumSimulationHalfExtent, 0.5f * Mathf.Sqrt(clampedEntityCount / EntityDensityPerSquareMeter));
        }

        private void FrameMovementCamera(float halfExtent)
        {
            if (movementCamera == null) return;

            var paddedHalfExtent = Mathf.Max(MinimumSimulationHalfExtent, halfExtent) * CameraBoundsPadding;
            var verticalFieldOfView = Mathf.Clamp(movementCamera.fieldOfView, MinimumCameraFieldOfView, MaximumCameraFieldOfView);
            var verticalHalfFieldOfViewRadians = verticalFieldOfView * 0.5f * Mathf.Deg2Rad;
            var aspect = Mathf.Max(MinimumCameraAspect, movementCamera.aspect);
            var verticalFitHeight = paddedHalfExtent / Mathf.Tan(verticalHalfFieldOfViewRadians);
            var horizontalHalfFieldOfViewRadians = Mathf.Atan(Mathf.Tan(verticalHalfFieldOfViewRadians) * aspect);
            var horizontalFitHeight = paddedHalfExtent / Mathf.Tan(horizontalHalfFieldOfViewRadians);
            var cameraHeight = Mathf.Max(verticalFitHeight, horizontalFitHeight);
            movementCamera.orthographic = false;
            movementCamera.transform.SetPositionAndRotation(new Vector3(0f, cameraHeight, 0f), Quaternion.Euler(90f, 0f, 0f));
            movementCamera.farClipPlane = Mathf.Max(movementCamera.farClipPlane, cameraHeight + MinimumFarClipPadding);
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
                if (TryWriteControlData(0, MinimumSimulationHalfExtent))
                {
                    var cleanupStartTime = Time.realtimeSinceStartupAsDouble;
                    while (!HasRuntimeReachedCleanup() && string.IsNullOrEmpty(cleanupFailureMessage))
                    {
                        if (Time.realtimeSinceStartupAsDouble - cleanupStartTime >= EntityTransitionTimeoutSeconds)
                        {
                            cleanupFailureMessage = "Timed out while removing movement benchmark entities during cleanup.";
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

            if (movementSceneLoaded && movementScene.IsValid() && movementScene.isLoaded)
            {
                var unloadOperation = StartMovementSceneUnload();
                if (unloadOperation != null)
                {
                    while (!unloadOperation.isDone) yield return null;
                }
            }

            movementSceneLoaded = false;
            RestoreMainSceneState();
        }

        private void DestroyRemainingRuntimeEntities()
        {
            if (benchmarkWorld == null || !benchmarkWorld.IsCreated) return;

            try
            {
                var remainingEntitiesQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<MovementTestEntityTag>());
                if (remainingEntitiesQuery.CalculateEntityCount() > 0) entityManager.DestroyEntity(remainingEntitiesQuery);
                remainingEntitiesQuery.Dispose();
                CurrentEntityCount = 0;
            }
            catch (Exception exception)
            {
                cleanupFailureMessage = $"Failed to destroy remaining movement benchmark entities: {exception.Message}";
            }
        }

        private AsyncOperation StartMovementSceneUnload()
        {
            try
            {
                var unloadOperation = SceneManager.UnloadSceneAsync(movementScene);
                if (unloadOperation == null) cleanupFailureMessage = "Failed to start unloading the movement benchmark scene.";
                return unloadOperation;
            }
            catch (Exception exception)
            {
                cleanupFailureMessage = $"Failed to unload the movement benchmark scene: {exception.Message}";
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

        private MovementPerformanceTestResult ResolveFinalResult()
        {
            if (!string.IsNullOrEmpty(cleanupFailureMessage)) return MovementPerformanceTestResult.Failure(cleanupFailureMessage);
            if (cancellationRequested) return MovementPerformanceTestResult.Failure("Movement benchmark was cancelled.");
            if (!string.IsNullOrEmpty(failureMessage)) return MovementPerformanceTestResult.Failure(failureMessage);
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

    internal readonly struct MovementPerformanceTestResult
    {
        internal bool Success { get; }
        internal bool CapReached { get; }
        internal int MaximumPassingEntityCount { get; }
        internal PerformanceMetricsSnapshot Metrics { get; }
        internal string ErrorMessage { get; }

        private MovementPerformanceTestResult(bool success, bool capReached, int maximumPassingEntityCount, PerformanceMetricsSnapshot metrics, string errorMessage)
        {
            Success = success;
            CapReached = capReached;
            MaximumPassingEntityCount = maximumPassingEntityCount;
            Metrics = metrics;
            ErrorMessage = errorMessage;
        }

        internal static MovementPerformanceTestResult Successful(int maximumPassingEntityCount, bool capReached, PerformanceMetricsSnapshot metrics)
        {
            return new MovementPerformanceTestResult(true, capReached, maximumPassingEntityCount, metrics, string.Empty);
        }

        internal static MovementPerformanceTestResult Failure(string errorMessage)
        {
            return new MovementPerformanceTestResult(false, false, 0, default, errorMessage);
        }
    }
}
