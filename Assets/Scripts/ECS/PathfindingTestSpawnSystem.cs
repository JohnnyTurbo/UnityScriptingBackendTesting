using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace TMG.CoreCLRTest
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PathfindingGridBuildSystem))]
    public partial struct PathfindingTestSpawnSystem : ISystem
    {
        private const int InvalidRuntimeEntityCount = -1;
        private const int MaximumEntityCount = 1_000_000;
        private const int MaximumStructuralChangeBatchSize = 16_384;
        private const int MinimumSpawnSubBatchSize = 1;
        private const int MinimumFrameInterval = 0;
        private NativeList<Entity> runtimeAgents;
        private EntityQuery spawnerQuery;
        private EntityQuery controlQuery;
        private EntityQuery runtimeQuery;
        private int completeFramesUntilNextSpawn;

        /// <summary>
        /// Creates singleton queries and persistent agent tracking used by the throttled population lifecycle.
        /// </summary>
        public void OnCreate(ref SystemState state)
        {
            spawnerQuery = state.GetEntityQuery(ComponentType.ReadOnly<PathfindingTestSpawnerData>(), ComponentType.ReadOnly<PathfindingGridData>());
            controlQuery = state.GetEntityQuery(ComponentType.ReadOnly<PathfindingTestControlData>());
            runtimeQuery = state.GetEntityQuery(ComponentType.ReadWrite<PathfindingTestRuntimeData>());
            state.RequireForUpdate(spawnerQuery);
            state.RequireForUpdate(controlQuery);
            state.RequireForUpdate(runtimeQuery);
            runtimeAgents = new NativeList<Entity>(Allocator.Persistent);
        }

        /// <summary>
        /// Reconciles the requested agent count while honoring authored spawn sub-batches and complete skipped frames.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            if (spawnerQuery.CalculateEntityCount() != 1 || controlQuery.CalculateEntityCount() != 1 || runtimeQuery.CalculateEntityCount() != 1)
            {
                MarkSingletonFailure(ref state);
                return;
            }

            state.Dependency.Complete();
            var entityManager = state.EntityManager;
            RemoveExternallyDestroyedAgents(entityManager);
            var spawnerEntity = spawnerQuery.GetSingletonEntity();
            var runtimeEntity = runtimeQuery.GetSingletonEntity();
            var spawnerData = entityManager.GetComponentData<PathfindingTestSpawnerData>(spawnerEntity);
            var gridData = entityManager.GetComponentData<PathfindingGridData>(spawnerEntity);
            var controlData = controlQuery.GetSingleton<PathfindingTestControlData>();
            var runtimeData = entityManager.GetComponentData<PathfindingTestRuntimeData>(runtimeEntity);
            var desiredEntityCount = math.clamp(controlData.DesiredEntityCount, 0, MaximumEntityCount);

            if (!ValidateAgentPrefab(entityManager, spawnerData.AgentPrefab))
            {
                runtimeData.Status = PathfindingTestRuntimeStatus.Failed;
                runtimeData.Error = PathfindingTestRuntimeError.InvalidAgentPrefab;
                runtimeData.CurrentEntityCount = InvalidRuntimeEntityCount;
                entityManager.SetComponentData(runtimeEntity, runtimeData);
                return;
            }

            if (runtimeData.AppliedConfigurationVersion != controlData.ConfigurationVersion && runtimeData.Status != PathfindingTestRuntimeStatus.Failed) runtimeData.Status = PathfindingTestRuntimeStatus.Preparing;
            if (runtimeAgents.Length > desiredEntityCount)
            {
                var destroyCount = math.min(runtimeAgents.Length - desiredEntityCount, MaximumStructuralChangeBatchSize);
                var firstDestroyIndex = runtimeAgents.Length - destroyCount;
                var entitiesToDestroy = runtimeAgents.AsArray().GetSubArray(firstDestroyIndex, destroyCount);
                entityManager.DestroyEntity(entitiesToDestroy);
                runtimeAgents.ResizeUninitialized(firstDestroyIndex);
                completeFramesUntilNextSpawn = 0;
            }
            else if (gridData.BuildStatus == PathfindingGridBuildStatus.Ready && runtimeAgents.Length < desiredEntityCount)
            {
                if (completeFramesUntilNextSpawn > 0)
                {
                    completeFramesUntilNextSpawn--;
                }
                else
                {
                    var spawnSubBatchSize = math.clamp(spawnerData.EntitySpawnSubBatchSize, MinimumSpawnSubBatchSize, MaximumStructuralChangeBatchSize);
                    var spawnCount = math.min(desiredEntityCount - runtimeAgents.Length, spawnSubBatchSize);
                    var instantiatedAgents = new NativeArray<Entity>(spawnCount, Allocator.Temp);
                    entityManager.Instantiate(spawnerData.AgentPrefab, instantiatedAgents);
                    runtimeAgents.AddRange(instantiatedAgents);
                    instantiatedAgents.Dispose();
                    completeFramesUntilNextSpawn = math.max(MinimumFrameInterval, spawnerData.FramesBetweenSpawnSubBatches);
                }
            }

            runtimeData.CurrentEntityCount = runtimeAgents.Length;
            entityManager.SetComponentData(runtimeEntity, runtimeData);
        }

        /// <summary>
        /// Releases persistent agent tracking.
        /// </summary>
        public void OnDestroy(ref SystemState state)
        {
            if (runtimeAgents.IsCreated) runtimeAgents.Dispose();
        }

        private void RemoveExternallyDestroyedAgents(EntityManager entityManager)
        {
            for (var agentIndex = runtimeAgents.Length - 1; agentIndex >= 0; agentIndex--)
            {
                if (entityManager.Exists(runtimeAgents[agentIndex])) continue;
                runtimeAgents.RemoveAtSwapBack(agentIndex);
            }
        }

        private void MarkSingletonFailure(ref SystemState state)
        {
            if (runtimeQuery.CalculateEntityCount() != 1) return;
            var runtimeEntity = runtimeQuery.GetSingletonEntity();
            var runtimeData = state.EntityManager.GetComponentData<PathfindingTestRuntimeData>(runtimeEntity);
            runtimeData.Status = PathfindingTestRuntimeStatus.Failed;
            runtimeData.Error = PathfindingTestRuntimeError.InvalidSingletonCount;
            state.EntityManager.SetComponentData(runtimeEntity, runtimeData);
        }

        private static bool ValidateAgentPrefab(EntityManager entityManager, Entity agentPrefab)
        {
            return agentPrefab != Entity.Null && entityManager.Exists(agentPrefab) && entityManager.HasComponent<LocalTransform>(agentPrefab) && entityManager.HasComponent<PathfindingAgentTag>(agentPrefab) && entityManager.HasComponent<PathfindingAgentState>(agentPrefab) && entityManager.HasComponent<PathfindingPathRequest>(agentPrefab) && entityManager.HasComponent<PathfindingPathReady>(agentPrefab) && entityManager.HasComponent<PathfindingInitializationPending>(agentPrefab) && entityManager.HasComponent<PathfindingSearchFailed>(agentPrefab) && entityManager.HasComponent<PathfindingWaypoint>(agentPrefab);
        }
    }
}
