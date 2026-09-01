using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace TMG.CoreCLRTest
{
    public struct PlinkoSpawnerData : IComponentData
    {
        public Entity Prefab;
        public float3 MinSpawnPosition;
        public float3 MaxSpawnPosition;
    }
    
    public class PlinkoSpawnerAuthoring : MonoBehaviour
    {
        public GameObject Prefab;
        public Vector3 MinSpawnPosition;
        public Vector3 MaxSpawnPosition;
        
        private class Baker : Baker<PlinkoSpawnerAuthoring>
        {
            public override void Bake(PlinkoSpawnerAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);
                AddComponent(entity, new PlinkoSpawnerData
                {
                    Prefab = GetEntity(authoring.Prefab, TransformUsageFlags.Dynamic),
                    MinSpawnPosition = authoring.MinSpawnPosition,
                    MaxSpawnPosition = authoring.MaxSpawnPosition
                });
            }
        }
    }
}