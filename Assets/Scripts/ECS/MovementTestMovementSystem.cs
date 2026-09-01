using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace TMG.CoreCLRTest
{
    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(MovementTestSpawnSystem))]
    public partial struct MovementTestMovementSystem : ISystem
    {
        /// <summary>
        /// Declares the movement configuration singleton required before the system updates.
        /// </summary>
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<MovementTestControlData>();
        }

        /// <summary>
        /// Schedules deterministic benchmark movement over all runtime mover entities in parallel.
        /// </summary>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var controlData = SystemAPI.GetSingleton<MovementTestControlData>();
            var moveEntitiesJob = new MoveEntitiesJob
            {
                DeltaTime = SystemAPI.Time.DeltaTime,
                SimulationHalfExtent = math.max(0f, controlData.SimulationHalfExtent),
                MoveSpeed = math.max(0f, controlData.MoveSpeed),
                ArrivalDistanceSquared = math.max(0f, controlData.ArrivalDistanceSquared),
                BaseRandomSeed = math.max(1u, controlData.BaseRandomSeed),
                ConfigurationVersion = controlData.ConfigurationVersion
            };
            state.Dependency = moveEntitiesJob.ScheduleParallel(state.Dependency);
        }

        [BurstCompile]
        [WithAll(typeof(MovementTestEntityTag))]
        private partial struct MoveEntitiesJob : IJobEntity
        {
            private const float FixedHeight = 1f;
            private const int MaximumTargetSelectionAttempts = 8;
            public float DeltaTime;
            public float SimulationHalfExtent;
            public float MoveSpeed;
            public float ArrivalDistanceSquared;
            public uint BaseRandomSeed;
            public int ConfigurationVersion;

            private void Execute(Entity entity, ref LocalTransform localTransform, ref MoverState moverState)
            {
                if (moverState.AppliedConfigurationVersion != ConfigurationVersion)
                {
                    var randomSeed = math.hash(new uint3(BaseRandomSeed, (uint)entity.Index + 1u, (uint)ConfigurationVersion));
                    randomSeed = math.max(1u, randomSeed);
                    var random = new Random(randomSeed);
                    var initialPosition = GenerateRandomPosition(ref random);
                    localTransform.Position = initialPosition;
                    moverState.TargetPosition = GenerateDistinctRandomPosition(ref random, initialPosition);
                    moverState.Random = random;
                    moverState.AppliedConfigurationVersion = ConfigurationVersion;
                    return;
                }

                var currentPosition = localTransform.Position;
                currentPosition.y = FixedHeight;
                var targetOffset = moverState.TargetPosition - currentPosition;
                var remainingDistanceSquared = math.lengthsq(targetOffset);
                if (remainingDistanceSquared <= ArrivalDistanceSquared)
                {
                    var random = moverState.Random;
                    moverState.TargetPosition = GenerateDistinctRandomPosition(ref random, currentPosition);
                    moverState.Random = random;
                    localTransform.Position = currentPosition;
                    return;
                }

                var remainingDistance = math.sqrt(remainingDistanceSquared);
                var movementDistance = math.min(MoveSpeed * DeltaTime, remainingDistance);
                currentPosition += math.normalizesafe(targetOffset) * movementDistance;
                currentPosition.y = FixedHeight;
                localTransform.Position = currentPosition;
            }

            private float3 GenerateDistinctRandomPosition(ref Random random, float3 currentPosition)
            {
                var targetPosition = GenerateRandomPosition(ref random);
                for (var attempt = 0; attempt < MaximumTargetSelectionAttempts && math.lengthsq(targetPosition - currentPosition) <= ArrivalDistanceSquared; attempt++)
                {
                    targetPosition = GenerateRandomPosition(ref random);
                }

                if (math.lengthsq(targetPosition - currentPosition) <= ArrivalDistanceSquared)
                {
                    var fallbackX = currentPosition.x <= 0f ? SimulationHalfExtent : -SimulationHalfExtent;
                    var fallbackZ = currentPosition.z <= 0f ? SimulationHalfExtent : -SimulationHalfExtent;
                    targetPosition = new float3(fallbackX, FixedHeight, fallbackZ);
                }

                return targetPosition;
            }

            private float3 GenerateRandomPosition(ref Random random)
            {
                return new float3(random.NextFloat(-SimulationHalfExtent, SimulationHalfExtent), FixedHeight, random.NextFloat(-SimulationHalfExtent, SimulationHalfExtent));
            }
        }
    }
}
