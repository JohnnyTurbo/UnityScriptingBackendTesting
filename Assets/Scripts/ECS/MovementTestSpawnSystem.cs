using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace TMG.CoreCLRTest
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct MovementTestSpawnSystem : ISystem
    {
        private const int MaximumEntityCount = 1_000_000;
        private const int MaximumBatchSize = 16_384;
        private NativeList<Entity> runtimeEntities;
        private EntityQuery spawnerQuery;
        private EntityQuery controlQuery;
        private EntityQuery runtimeQuery;

        /// <summary>
        /// Initializes the runtime-entity tracker and declares the singleton data required for population control.
        /// </summary>
        public void OnCreate(ref SystemState state)
        {
            spawnerQuery = state.GetEntityQuery(ComponentType.ReadOnly<EntitySpawnerData>());
            controlQuery = state.GetEntityQuery(ComponentType.ReadOnly<MovementTestControlData>());
            runtimeQuery = state.GetEntityQuery(ComponentType.ReadWrite<MovementTestRuntimeData>());
            state.RequireForUpdate(spawnerQuery);
            state.RequireForUpdate(controlQuery);
            state.RequireForUpdate(runtimeQuery);
            runtimeEntities = new NativeList<Entity>(Allocator.Persistent);
        }

        /// <summary>
        /// Reconciles the requested runtime entity count in bounded structural-change batches.
        /// </summary>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (spawnerQuery.CalculateEntityCount() != 1 || controlQuery.CalculateEntityCount() != 1 || runtimeQuery.CalculateEntityCount() != 1)
            {
                Debug.LogError("Movement benchmark requires exactly one spawner, control singleton, and runtime singleton.");
                return;
            }

            var entityManager = state.EntityManager;
            var spawnerData = spawnerQuery.GetSingleton<EntitySpawnerData>();
            var controlData = controlQuery.GetSingleton<MovementTestControlData>();
            var runtimeEntity = runtimeQuery.GetSingletonEntity();
            var desiredEntityCount = math.clamp(controlData.DesiredEntityCount, 0, MaximumEntityCount);

            if (spawnerData.Prefab == Entity.Null || !entityManager.Exists(spawnerData.Prefab))
            {
                var invalidRuntimeData = entityManager.GetComponentData<MovementTestRuntimeData>(runtimeEntity);
                invalidRuntimeData.CurrentEntityCount = -1;
                invalidRuntimeData.AppliedConfigurationVersion = controlData.ConfigurationVersion;
                entityManager.SetComponentData(runtimeEntity, invalidRuntimeData);
                return;
            }

            if (runtimeEntities.Length < desiredEntityCount)
            {
                var spawnCount = math.min(desiredEntityCount - runtimeEntities.Length, MaximumBatchSize);
                var instantiatedEntities = new NativeArray<Entity>(spawnCount, Allocator.Temp);
                entityManager.Instantiate(spawnerData.Prefab, instantiatedEntities);
                runtimeEntities.AddRange(instantiatedEntities);
                instantiatedEntities.Dispose();
            }
            else if (runtimeEntities.Length > desiredEntityCount)
            {
                var destroyCount = math.min(runtimeEntities.Length - desiredEntityCount, MaximumBatchSize);
                var firstDestroyIndex = runtimeEntities.Length - destroyCount;
                var entitiesToDestroy = runtimeEntities.AsArray().GetSubArray(firstDestroyIndex, destroyCount);
                entityManager.DestroyEntity(entitiesToDestroy);
                runtimeEntities.ResizeUninitialized(firstDestroyIndex);
            }

            var runtimeData = entityManager.GetComponentData<MovementTestRuntimeData>(runtimeEntity);
            runtimeData.CurrentEntityCount = runtimeEntities.Length;
            if (runtimeEntities.Length == desiredEntityCount) runtimeData.AppliedConfigurationVersion = controlData.ConfigurationVersion;
            entityManager.SetComponentData(runtimeEntity, runtimeData);
        }

        /// <summary>
        /// Releases the persistent runtime-entity tracker.
        /// </summary>
        public void OnDestroy(ref SystemState state)
        {
            if (runtimeEntities.IsCreated) runtimeEntities.Dispose();
        }
    }
}
