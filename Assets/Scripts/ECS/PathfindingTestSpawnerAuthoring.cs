using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace TMG.CoreCLRTest
{
    public sealed class PathfindingTestSpawnerAuthoring : MonoBehaviour
    {
        private const float MinimumWallDensity = 0f;
        private const float MaximumWallDensity = 0.95f;
        private const float DefaultWallDensity = 0.15f;
        private const int MinimumSpawnSubBatchSize = 1;
        private const int DefaultSpawnSubBatchSize = 128;
        private const int MinimumFramesBetweenSpawnSubBatches = 0;
        private const int DefaultFramesBetweenSpawnSubBatches = 1;

        [SerializeField] private GameObject agentPrefab;
        [SerializeField] private GameObject wallPrefab;
        [SerializeField, Range(MinimumWallDensity, MaximumWallDensity)] private float wallDensity = DefaultWallDensity;
        [SerializeField, Min(MinimumSpawnSubBatchSize)] private int entitiesPerSpawnSubBatch = DefaultSpawnSubBatchSize;
        [SerializeField, Min(MinimumFramesBetweenSpawnSubBatches)] private int completeFramesBetweenSpawnSubBatches = DefaultFramesBetweenSpawnSubBatches;

        private sealed class Baker : Baker<PathfindingTestSpawnerAuthoring>
        {
            /// <summary>
            /// Bakes the pathfinding prefab references, generation settings, grid state, and reusable grid buffers onto one singleton entity.
            /// </summary>
            public override void Bake(PathfindingTestSpawnerAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);
                AddComponent(entity, new PathfindingTestSpawnerData
                {
                    WallDensity = math.clamp(authoring.wallDensity, MinimumWallDensity, MaximumWallDensity),
                    EntitySpawnSubBatchSize = math.max(MinimumSpawnSubBatchSize, authoring.entitiesPerSpawnSubBatch),
                    FramesBetweenSpawnSubBatches = math.max(MinimumFramesBetweenSpawnSubBatches, authoring.completeFramesBetweenSpawnSubBatches),
                    AgentPrefab = authoring.agentPrefab == null ? Entity.Null : GetEntity(authoring.agentPrefab, TransformUsageFlags.Dynamic),
                    WallPrefab = authoring.wallPrefab == null ? Entity.Null : GetEntity(authoring.wallPrefab, TransformUsageFlags.Dynamic)
                });
                AddComponent(entity, new PathfindingGridData
                {
                    BuildStatus = PathfindingGridBuildStatus.Inactive,
                    GridVersion = 0,
                    Dimensions = int2.zero,
                    CellOrigin = float3.zero
                });
                AddBuffer<PathfindingGridCell>(entity);
                AddBuffer<PathfindingRegionRange>(entity);
                AddBuffer<PathfindingRegionCell>(entity);
            }
        }
    }
}
