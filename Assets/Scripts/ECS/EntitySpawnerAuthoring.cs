using Unity.Entities;
using UnityEngine;

namespace TMG.CoreCLRTest
{
    public struct EntitySpawnerData : IComponentData
    {
        public Entity Prefab;
    }
    
    public class EntitySpawnerAuthoring : MonoBehaviour
    {
        public GameObject Prefab;
        
        private class Baker : Baker<EntitySpawnerAuthoring>
        {
            public override void Bake(EntitySpawnerAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);
                AddComponent(entity, new EntitySpawnerData
                {
                    Prefab = GetEntity(authoring.Prefab, TransformUsageFlags.Dynamic)
                });
            }
        }
    }
}