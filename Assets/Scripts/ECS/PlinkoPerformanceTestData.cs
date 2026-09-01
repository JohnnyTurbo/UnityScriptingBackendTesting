using Unity.Entities;
using Unity.Mathematics;

namespace TMG.CoreCLRTest
{
    public struct PlinkoPerformanceTestControlData : IComponentData
    {
        public int DesiredEntityCount;
        public int MaximumBallsPerFrame;
        public float SpawnGroupIntervalSeconds;
        public uint BaseRandomSeed;
        public int ConfigurationVersion;
    }

    public struct PlinkoPerformanceTestRuntimeData : IComponentData
    {
        public int CurrentEntityCount;
        public int AppliedConfigurationVersion;
    }

    public struct PlinkoPerformanceTestEntityTag : IComponentData
    {
    }

    public struct PlinkoBallRecycleData : IComponentData
    {
        public float3 SpawnPosition;
    }
}
