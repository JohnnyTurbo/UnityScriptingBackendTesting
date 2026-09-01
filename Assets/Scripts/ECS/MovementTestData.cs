using Unity.Entities;
using Unity.Mathematics;

namespace TMG.CoreCLRTest
{
    public struct MovementTestControlData : IComponentData
    {
        public int DesiredEntityCount;
        public float SimulationHalfExtent;
        public float MoveSpeed;
        public float ArrivalDistanceSquared;
        public uint BaseRandomSeed;
        public int ConfigurationVersion;
    }

    public struct MovementTestRuntimeData : IComponentData
    {
        public int CurrentEntityCount;
        public int AppliedConfigurationVersion;
    }

    public struct MovementTestEntityTag : IComponentData
    {
    }

    public struct MoverState : IComponentData
    {
        public Random Random;
        public int AppliedConfigurationVersion;
        public float3 TargetPosition;
    }
}
