using Unity.Entities;
using Unity.Mathematics;

namespace TMG.CoreCLRTest
{
    public enum PathfindingGridBuildStatus : byte
    {
        Inactive,
        Rebuilding,
        Ready,
        Failed
    }

    public enum PathfindingTestRuntimeStatus : byte
    {
        Inactive,
        Preparing,
        Ready,
        Failed
    }

    public enum PathfindingTestRuntimeError : byte
    {
        None,
        InvalidSingletonCount,
        InvalidGridDimensions,
        InvalidWallPrefab,
        GridBuildFailed,
        InvalidAgentPrefab,
        InvalidGridMetadata,
        ScratchAllocationFailed,
        SearchFailed
    }

    public struct PathfindingTestSpawnerData : IComponentData
    {
        public float WallDensity;
        public int EntitySpawnSubBatchSize;
        public int FramesBetweenSpawnSubBatches;
        public Entity AgentPrefab;
        public Entity WallPrefab;
    }

    public struct PathfindingTestControlData : IComponentData
    {
        public int DesiredEntityCount;
        public float MoveSpeed;
        public float ArrivalDistanceSquared;
        public uint BaseRandomSeed;
        public int ConfigurationVersion;
        public int2 RequestedGridDimensions;
    }

    public struct PathfindingTestRuntimeData : IComponentData
    {
        public PathfindingTestRuntimeStatus Status;
        public PathfindingTestRuntimeError Error;
        public int CurrentEntityCount;
        public int CurrentWallCount;
        public int ExpectedWallCount;
        public int AppliedConfigurationVersion;
        public int AppliedGridVersion;
    }

    public struct PathfindingGridData : IComponentData
    {
        public PathfindingGridBuildStatus BuildStatus;
        public int GridVersion;
        public int2 Dimensions;
        public float3 CellOrigin;
    }

    public struct PathfindingAgentState : IComponentData
    {
        public Random Random;
        public int NextWaypointIndex;
        public int AppliedGridVersion;
        public int2 CurrentCell;
        public int2 DestinationCell;
    }

    public struct PathfindingAgentTag : IComponentData
    {
    }

    public struct PathfindingWallTag : IComponentData
    {
    }

    public struct PathfindingWallState : IComponentData
    {
        public int CellIndex;
        public int GridVersion;
    }

    public struct PathfindingPathRequest : IComponentData, IEnableableComponent
    {
    }

    public struct PathfindingPathReady : IComponentData, IEnableableComponent
    {
    }

    public struct PathfindingInitializationPending : IComponentData, IEnableableComponent
    {
    }

    public struct PathfindingSearchFailed : IComponentData, IEnableableComponent
    {
    }

    public struct PathfindingGridCell : IBufferElementData
    {
        public int RegionIndex;
    }

    public struct PathfindingRegionRange : IBufferElementData
    {
        public int StartIndex;
        public int Count;
    }

    public struct PathfindingRegionCell : IBufferElementData
    {
        public int CellIndex;
    }

    public struct PathfindingWaypoint : IBufferElementData
    {
        public float3 Position;
    }
}
