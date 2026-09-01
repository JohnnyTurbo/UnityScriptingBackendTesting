using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace TMG.CoreCLRTest
{
    public sealed class PathfindingEntityAuthoring : MonoBehaviour
    {
        private const uint PlaceholderRandomSeed = 1u;

        private sealed class Baker : Baker<PathfindingEntityAuthoring>
        {
            /// <summary>
            /// Adds the pathfinding state, enableable navigation states, waypoint buffer, and ownership tag to the baked prefab entity.
            /// </summary>
            public override void Bake(PathfindingEntityAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent(entity, new PathfindingAgentState
                {
                    Random = new Unity.Mathematics.Random(PlaceholderRandomSeed),
                    NextWaypointIndex = 0,
                    AppliedGridVersion = 0,
                    CurrentCell = int2.zero,
                    DestinationCell = int2.zero
                });
                AddComponent<PathfindingAgentTag>(entity);
                AddComponent<PathfindingPathRequest>(entity);
                AddComponent<PathfindingPathReady>(entity);
                AddComponent<PathfindingInitializationPending>(entity);
                AddComponent<PathfindingSearchFailed>(entity);
                AddBuffer<PathfindingWaypoint>(entity);
                SetComponentEnabled<PathfindingPathReady>(entity, false);
                SetComponentEnabled<PathfindingSearchFailed>(entity, false);
            }
        }
    }
}
