using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace TMG.CoreCLRTest
{
    public sealed class MoverEntityAuthoring : MonoBehaviour
    {
        private sealed class Baker : Baker<MoverEntityAuthoring>
        {
            /// <summary>
            /// Adds the benchmark movement state and runtime-instance tag to the baked prefab entity.
            /// </summary>
            public override void Bake(MoverEntityAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent(entity, new MoverState
                {
                    Random = new Unity.Mathematics.Random(1u),
                    AppliedConfigurationVersion = 0,
                    TargetPosition = float3.zero
                });
                AddComponent<MovementTestEntityTag>(entity);
            }
        }
    }
}
