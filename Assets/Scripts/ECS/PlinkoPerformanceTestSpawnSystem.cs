using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;
using UnityEngine;

namespace TMG.CoreCLRTest
{
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(FixedStepSimulationSystemGroup))]
    public partial struct PlinkoPerformanceTestSpawnSystem : ISystem
    {
        private const int InvalidRuntimeEntityCount = -1;
        private const int MaximumEntityCount = 1_000_000;
        private const int MaximumDestroyBatchSize = 16_384;
        private NativeList<Entity> runtimeEntities;
        private EntityQuery spawnerQuery;
        private EntityQuery controlQuery;
        private EntityQuery runtimeQuery;
        private double nextSpawnGroupTime;
        private uint spawnSequence;
        private bool hasLoggedSingletonError;
        private bool hasLoggedPrefabError;

        /// <summary>
        /// Initializes the tracked Plinko population and declares its required singleton data.
        /// </summary>
        public void OnCreate(ref SystemState state)
        {
            spawnerQuery = state.GetEntityQuery(ComponentType.ReadOnly<PlinkoSpawnerData>());
            controlQuery = state.GetEntityQuery(ComponentType.ReadOnly<PlinkoPerformanceTestControlData>());
            runtimeQuery = state.GetEntityQuery(ComponentType.ReadWrite<PlinkoPerformanceTestRuntimeData>());
            state.RequireForUpdate(spawnerQuery);
            state.RequireForUpdate(controlQuery);
            state.RequireForUpdate(runtimeQuery);
            runtimeEntities = new NativeList<Entity>(Allocator.Persistent);
        }

        /// <summary>
        /// Reconciles the requested Plinko-ball population while emitting no more than one throttled spawn group per rendered update.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            var spawnerCount = spawnerQuery.CalculateEntityCount();
            var controlCount = controlQuery.CalculateEntityCount();
            var runtimeCount = runtimeQuery.CalculateEntityCount();
            if (spawnerCount != 1 || controlCount != 1 || runtimeCount != 1)
            {
                if (!hasLoggedSingletonError)
                {
                    Debug.LogError("Plinko benchmark requires exactly one spawner, control singleton, and runtime singleton.");
                    hasLoggedSingletonError = true;
                }
                return;
            }

            hasLoggedSingletonError = false;
            var entityManager = state.EntityManager;
            var spawnerData = spawnerQuery.GetSingleton<PlinkoSpawnerData>();
            var controlData = controlQuery.GetSingleton<PlinkoPerformanceTestControlData>();
            var runtimeEntity = runtimeQuery.GetSingletonEntity();
            for (var entityIndex = runtimeEntities.Length - 1; entityIndex >= 0; entityIndex--)
            {
                if (!entityManager.Exists(runtimeEntities[entityIndex])) runtimeEntities.RemoveAtSwapBack(entityIndex);
            }

            var prefabIsValid = spawnerData.Prefab != Entity.Null && entityManager.Exists(spawnerData.Prefab) && entityManager.HasComponent<LocalTransform>(spawnerData.Prefab) && entityManager.HasComponent<PhysicsVelocity>(spawnerData.Prefab) && entityManager.HasComponent<PhysicsMass>(spawnerData.Prefab) && entityManager.HasComponent<PhysicsCollider>(spawnerData.Prefab);
            if (!prefabIsValid)
            {
                if (!hasLoggedPrefabError)
                {
                    Debug.LogError("Plinko benchmark prefab is missing or does not contain the required dynamic Unity Physics components.");
                    hasLoggedPrefabError = true;
                }
                var invalidRuntimeData = entityManager.GetComponentData<PlinkoPerformanceTestRuntimeData>(runtimeEntity);
                invalidRuntimeData.CurrentEntityCount = InvalidRuntimeEntityCount;
                invalidRuntimeData.AppliedConfigurationVersion = controlData.ConfigurationVersion;
                entityManager.SetComponentData(runtimeEntity, invalidRuntimeData);
                return;
            }

            hasLoggedPrefabError = false;
            var desiredEntityCount = math.clamp(controlData.DesiredEntityCount, 0, MaximumEntityCount);
            var maximumBallsPerFrame = math.max(1, controlData.MaximumBallsPerFrame);
            var spawnGroupIntervalSeconds = math.max(0f, controlData.SpawnGroupIntervalSeconds);
            if (runtimeEntities.Length < desiredEntityCount && state.WorldUnmanaged.Time.ElapsedTime >= nextSpawnGroupTime)
            {
                var spawnCount = math.min(desiredEntityCount - runtimeEntities.Length, maximumBallsPerFrame);
                var instantiatedEntities = new NativeArray<Entity>(spawnCount, Allocator.Temp);
                entityManager.Instantiate(spawnerData.Prefab, instantiatedEntities);
                var baseRandomSeed = controlData.BaseRandomSeed == 0u ? 1u : controlData.BaseRandomSeed;
                var minimumSpawnPosition = math.min(spawnerData.MinSpawnPosition, spawnerData.MaxSpawnPosition);
                var maximumSpawnPosition = math.max(spawnerData.MinSpawnPosition, spawnerData.MaxSpawnPosition);
                for (var entityIndex = 0; entityIndex < instantiatedEntities.Length; entityIndex++)
                {
                    var spawnedEntity = instantiatedEntities[entityIndex];
                    var randomSeed = math.hash(new uint2(baseRandomSeed, ++spawnSequence));
                    if (randomSeed == 0u) randomSeed = 1u;
                    var random = new Unity.Mathematics.Random(randomSeed);
                    var spawnPosition = random.NextFloat3(minimumSpawnPosition, maximumSpawnPosition);
                    var localTransform = entityManager.GetComponentData<LocalTransform>(spawnedEntity);
                    localTransform.Position = spawnPosition;
                    entityManager.SetComponentData(spawnedEntity, localTransform);
                    entityManager.SetComponentData(spawnedEntity, new PhysicsVelocity { Linear = float3.zero, Angular = float3.zero });
                    if (entityManager.HasComponent<PlinkoBallRecycleData>(spawnedEntity)) entityManager.SetComponentData(spawnedEntity, new PlinkoBallRecycleData { SpawnPosition = spawnPosition });
                    else entityManager.AddComponentData(spawnedEntity, new PlinkoBallRecycleData { SpawnPosition = spawnPosition });
                    if (!entityManager.HasComponent<PlinkoPerformanceTestEntityTag>(spawnedEntity)) entityManager.AddComponent<PlinkoPerformanceTestEntityTag>(spawnedEntity);
                }
                runtimeEntities.AddRange(instantiatedEntities);
                instantiatedEntities.Dispose();
                nextSpawnGroupTime = state.WorldUnmanaged.Time.ElapsedTime + spawnGroupIntervalSeconds;
            }
            else if (runtimeEntities.Length > desiredEntityCount)
            {
                var destroyCount = math.min(runtimeEntities.Length - desiredEntityCount, MaximumDestroyBatchSize);
                var firstDestroyIndex = runtimeEntities.Length - destroyCount;
                var entitiesToDestroy = runtimeEntities.AsArray().GetSubArray(firstDestroyIndex, destroyCount);
                entityManager.DestroyEntity(entitiesToDestroy);
                runtimeEntities.ResizeUninitialized(firstDestroyIndex);
            }

            var runtimeData = entityManager.GetComponentData<PlinkoPerformanceTestRuntimeData>(runtimeEntity);
            runtimeData.CurrentEntityCount = runtimeEntities.Length;
            if (runtimeEntities.Length == desiredEntityCount) runtimeData.AppliedConfigurationVersion = controlData.ConfigurationVersion;
            entityManager.SetComponentData(runtimeEntity, runtimeData);
        }

        /// <summary>
        /// Releases the persistent Plinko runtime-entity tracker.
        /// </summary>
        public void OnDestroy(ref SystemState state)
        {
            if (runtimeEntities.IsCreated) runtimeEntities.Dispose();
        }
    }
}
