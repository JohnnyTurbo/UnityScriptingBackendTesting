using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;
using UnityEngine;

namespace TMG.CoreCLRTest
{
    public struct MovingPinData : IComponentData
    {
        public float MinZPos;
        public float MaxZPos;
        public float MoveSpeed;
    }

    public struct MovingPinState : IComponentData
    {
        public bool IsRight;
    }
    
    public struct InitializePinTag : IComponentData {}
    
    public class MovingPinAuthoring : MonoBehaviour
    {
        public float MinZPos;
        public float MaxZPos;
        public float MoveSpeed;
        
        private class Baker : Baker<MovingPinAuthoring>
        {
            public override void Bake(MovingPinAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent<InitializePinTag>(entity);
                AddComponent<MovingPinState>(entity);
                AddComponent(entity, new MovingPinData
                {
                    MinZPos = authoring.MinZPos,
                    MaxZPos = authoring.MaxZPos,
                    MoveSpeed = authoring.MoveSpeed
                });
            }
        }
    }

    public partial struct MovePinSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (velocity, pinState, pinData, transform) in SystemAPI.Query<RefRW<PhysicsVelocity>, RefRW<MovingPinState>, MovingPinData, LocalTransform>())
            {
                var modifier = pinState.ValueRO.IsRight ? 1f : -1f;
                var moveZ = pinData.MoveSpeed * modifier;
                velocity.ValueRW.Linear = new float3
                {
                    x = 0f,
                    y = 0f,
                    z = moveZ
                };

                if (transform.Position.z > pinData.MaxZPos)
                {
                    pinState.ValueRW.IsRight = false;
                }
                else if (transform.Position.z < pinData.MinZPos)
                {
                    pinState.ValueRW.IsRight = true;
                }
            }
        }
    }
}