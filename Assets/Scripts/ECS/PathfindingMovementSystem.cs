using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace TMG.CoreCLRTest
{
    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PathfindingSearchSystem))]
    public partial struct PathfindingMovementSystem : ISystem
    {
        private const float MinimumMotionValue = 0f;

        /// <summary>
        /// Declares the pathfinding control singleton required before waypoint movement updates.
        /// </summary>
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<PathfindingTestControlData>();
        }

        /// <summary>
        /// Schedules fixed-height traversal of compressed waypoint buffers in parallel.
        /// </summary>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var controlData = SystemAPI.GetSingleton<PathfindingTestControlData>();
            var moveAlongPathsJob = new MoveAlongPathsJob
            {
                DeltaTime = math.max(MinimumMotionValue, SystemAPI.Time.DeltaTime),
                MoveSpeed = math.max(MinimumMotionValue, controlData.MoveSpeed),
                ArrivalDistanceSquared = math.max(MinimumMotionValue, controlData.ArrivalDistanceSquared),
                PathRequestLookup = SystemAPI.GetComponentLookup<PathfindingPathRequest>()
            };
            state.Dependency = moveAlongPathsJob.ScheduleParallel(state.Dependency);
        }

        [BurstCompile]
        [WithAll(typeof(PathfindingAgentTag))]
        private partial struct MoveAlongPathsJob : IJobEntity
        {
            private const float FixedAgentHeight = 1f;
            public float DeltaTime;
            public float MoveSpeed;
            public float ArrivalDistanceSquared;
            [NativeDisableParallelForRestriction] public ComponentLookup<PathfindingPathRequest> PathRequestLookup;

            private void Execute(Entity entity, ref LocalTransform localTransform, ref PathfindingAgentState agentState, in DynamicBuffer<PathfindingWaypoint> waypoints, EnabledRefRW<PathfindingPathReady> pathReady)
            {
                if (agentState.NextWaypointIndex < 0 || agentState.NextWaypointIndex >= waypoints.Length)
                {
                    RequestReplacementPath(entity, ref pathReady);
                    return;
                }

                var currentPosition = localTransform.Position;
                currentPosition.y = FixedAgentHeight;
                var remainingMovementDistance = MoveSpeed * DeltaTime;
                while (agentState.NextWaypointIndex < waypoints.Length)
                {
                    var waypointPosition = waypoints[agentState.NextWaypointIndex].Position;
                    waypointPosition.y = FixedAgentHeight;
                    var waypointOffset = waypointPosition - currentPosition;
                    var waypointDistanceSquared = math.lengthsq(waypointOffset);
                    if (waypointDistanceSquared <= ArrivalDistanceSquared)
                    {
                        currentPosition = waypointPosition;
                        agentState.NextWaypointIndex++;
                        continue;
                    }

                    if (remainingMovementDistance <= MinimumMotionValue) break;
                    var waypointDistance = math.sqrt(waypointDistanceSquared);
                    if (waypointDistance <= remainingMovementDistance)
                    {
                        currentPosition = waypointPosition;
                        remainingMovementDistance -= waypointDistance;
                        agentState.NextWaypointIndex++;
                        continue;
                    }

                    if (waypointDistance > MinimumMotionValue) currentPosition += waypointOffset * (remainingMovementDistance / waypointDistance);
                    remainingMovementDistance = MinimumMotionValue;
                    break;
                }

                currentPosition.y = FixedAgentHeight;
                localTransform.Position = currentPosition;
                if (agentState.NextWaypointIndex < waypoints.Length) return;
                agentState.CurrentCell = agentState.DestinationCell;
                RequestReplacementPath(entity, ref pathReady);
            }

            private void RequestReplacementPath(Entity entity, ref EnabledRefRW<PathfindingPathReady> pathReady)
            {
                pathReady.ValueRW = false;
                PathRequestLookup.SetComponentEnabled(entity, true);
            }
        }
    }
}
