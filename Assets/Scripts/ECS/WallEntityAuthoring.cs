using Unity.Entities;
using UnityEngine;

namespace TMG.CoreCLRTest
{
    public sealed class WallEntityAuthoring : MonoBehaviour
    {
        private sealed class Baker : Baker<WallEntityAuthoring>
        {
            /// <summary>
            /// Adds the runtime wall ownership tag while preserving a writable transform.
            /// </summary>
            public override void Bake(WallEntityAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent<PathfindingWallTag>(entity);
                AddComponent(entity, new PathfindingWallState
                {
                    CellIndex = -1,
                    GridVersion = 0
                });
            }
        }
    }
}
