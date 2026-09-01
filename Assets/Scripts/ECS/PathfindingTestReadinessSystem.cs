using Unity.Entities;
using Unity.Mathematics;

namespace TMG.CoreCLRTest
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PathfindingSearchSystem))]
    public partial struct PathfindingTestReadinessSystem : ISystem
    {
        private const int MaximumEntityCount = 1_000_000;
        private EntityQuery spawnerQuery;
        private EntityQuery controlQuery;
        private EntityQuery runtimeQuery;
        private EntityQuery initializationPendingQuery;
        private EntityQuery searchFailedQuery;

        /// <summary>
        /// Creates singleton and enabled-state queries used to publish candidate readiness.
        /// </summary>
        public void OnCreate(ref SystemState state)
        {
            spawnerQuery = state.GetEntityQuery(ComponentType.ReadOnly<PathfindingTestSpawnerData>(), ComponentType.ReadOnly<PathfindingGridData>());
            controlQuery = state.GetEntityQuery(ComponentType.ReadOnly<PathfindingTestControlData>());
            runtimeQuery = state.GetEntityQuery(ComponentType.ReadWrite<PathfindingTestRuntimeData>());
            initializationPendingQuery = state.GetEntityQuery(ComponentType.ReadOnly<PathfindingAgentTag>(), ComponentType.ReadOnly<PathfindingInitializationPending>());
            searchFailedQuery = state.GetEntityQuery(ComponentType.ReadOnly<PathfindingAgentTag>(), ComponentType.ReadOnly<PathfindingSearchFailed>());
            state.RequireForUpdate(spawnerQuery);
            state.RequireForUpdate(controlQuery);
            state.RequireForUpdate(runtimeQuery);
        }

        /// <summary>
        /// Applies a configuration version only after its grid, walls, agents, and initial paths are complete.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            if (spawnerQuery.CalculateEntityCount() != 1 || controlQuery.CalculateEntityCount() != 1 || runtimeQuery.CalculateEntityCount() != 1)
            {
                MarkSingletonFailure(ref state);
                return;
            }

            var entityManager = state.EntityManager;
            var spawnerEntity = spawnerQuery.GetSingletonEntity();
            var runtimeEntity = runtimeQuery.GetSingletonEntity();
            var gridData = entityManager.GetComponentData<PathfindingGridData>(spawnerEntity);
            var controlData = controlQuery.GetSingleton<PathfindingTestControlData>();
            var runtimeData = entityManager.GetComponentData<PathfindingTestRuntimeData>(runtimeEntity);
            if (runtimeData.Status == PathfindingTestRuntimeStatus.Failed || gridData.BuildStatus == PathfindingGridBuildStatus.Failed)
            {
                runtimeData.Status = PathfindingTestRuntimeStatus.Failed;
                entityManager.SetComponentData(runtimeEntity, runtimeData);
                return;
            }

            if (runtimeData.AppliedConfigurationVersion == controlData.ConfigurationVersion) return;
            if (searchFailedQuery.CalculateEntityCount() > 0)
            {
                runtimeData.Status = PathfindingTestRuntimeStatus.Failed;
                runtimeData.Error = PathfindingTestRuntimeError.SearchFailed;
                entityManager.SetComponentData(runtimeEntity, runtimeData);
                return;
            }

            var desiredEntityCount = math.clamp(controlData.DesiredEntityCount, 0, MaximumEntityCount);
            var gridMatchesRequest = gridData.BuildStatus == PathfindingGridBuildStatus.Ready && gridData.Dimensions.Equals(controlData.RequestedGridDimensions) && runtimeData.AppliedGridVersion == gridData.GridVersion;
            var populationIsReady = runtimeData.CurrentEntityCount == desiredEntityCount && runtimeData.CurrentWallCount == runtimeData.ExpectedWallCount;
            if (!gridMatchesRequest || !populationIsReady || initializationPendingQuery.CalculateEntityCount() != 0) return;
            runtimeData.AppliedConfigurationVersion = controlData.ConfigurationVersion;
            runtimeData.Status = PathfindingTestRuntimeStatus.Ready;
            runtimeData.Error = PathfindingTestRuntimeError.None;
            entityManager.SetComponentData(runtimeEntity, runtimeData);
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
    }
}
