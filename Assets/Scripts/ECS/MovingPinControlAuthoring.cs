using Unity.Entities;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace TMG.CoreCLRTest
{
    public struct MovingPinControlData : IComponentData
    {
        public float MinMoveSpeed;
        public float MaxMoveSpeed;
    }
    
    public class MovingPinControlAuthoring : MonoBehaviour
    {
        public float MinMoveSpeed;
        public float MaxMoveSpeed;
        
        private class Baker : Baker<MovingPinControlAuthoring>
        {
            public override void Bake(MovingPinControlAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);
                AddComponent(entity, new MovingPinControlData
                {
                    MinMoveSpeed = authoring.MinMoveSpeed,
                    MaxMoveSpeed = authoring.MaxMoveSpeed
                });
            }
        }
    }
    
    public partial struct InitializePinSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<InitializePinTag>();
            state.RequireForUpdate<MovingPinControlData>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var controlData = SystemAPI.GetSingleton<MovingPinControlData>();
            var random = new Random(100u);
            var ecb = new EntityCommandBuffer(state.WorldUpdateAllocator);
            
            foreach (var (pinData, pinState, entity) in SystemAPI.Query<RefRW<MovingPinData>, RefRW<MovingPinState>>().WithAll<InitializePinTag>().WithEntityAccess())
            {
                pinData.ValueRW.MoveSpeed = random.NextFloat(controlData.MinMoveSpeed, controlData.MaxMoveSpeed);
                pinState.ValueRW.IsRight = random.NextBool();
                ecb.RemoveComponent<InitializePinTag>(entity);
            }

            ecb.Playback(state.EntityManager);
        }
    }
}