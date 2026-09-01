using Unity.Burst;
using Unity.Entities;
using Unity.Physics;
using Unity.Physics.Systems;
using Unity.Transforms;

namespace TMG.CoreCLRTest
{
    [BurstCompile]
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    [UpdateAfter(typeof(PhysicsSystemGroup))]
    public partial struct PlinkoPerformanceTestRecycleSystem : ISystem
    {
        /// <summary>
        /// Declares the benchmark control singleton required before Plinko recycling can run.
        /// </summary>
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<PlinkoPerformanceTestControlData>();
        }

        /// <summary>
        /// Schedules post-physics recycling for benchmark balls that have fallen below the track.
        /// </summary>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var recycleBallsJob = new RecycleBallsJob();
            state.Dependency = recycleBallsJob.ScheduleParallel(state.Dependency);
        }

        [BurstCompile]
        [WithAll(typeof(PlinkoPerformanceTestEntityTag))]
        private partial struct RecycleBallsJob : IJobEntity
        {
            private const float RecycleHeight = -70f;

            private void Execute(ref LocalTransform localTransform, ref PhysicsVelocity physicsVelocity, in PlinkoBallRecycleData recycleData)
            {
                if (localTransform.Position.y >= RecycleHeight) return;
                localTransform.Position = recycleData.SpawnPosition;
                physicsVelocity.Linear = Unity.Mathematics.float3.zero;
                physicsVelocity.Angular = Unity.Mathematics.float3.zero;
            }
        }
    }
}
