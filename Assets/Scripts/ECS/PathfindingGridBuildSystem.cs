using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Jobs;
using Unity.Transforms;
using UnityEngine;

namespace TMG.CoreCLRTest
{
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    public partial struct PathfindingGridBuildSystem : ISystem
    {
        private const int MaximumStructuralChangeBatchSize = 16_384;
        private const float MinimumWallDensity = 0f;
        private const float MaximumWallDensity = 0.95f;
        private const uint MinimumRandomSeed = 1u;
        private const uint DimensionSeedMultiplier = 0x9E3779B9u;
        private NativeList<int> wallCellIndices;
        private EntityQuery spawnerQuery;
        private EntityQuery controlQuery;
        private EntityQuery runtimeQuery;
        private EntityQuery agentQuery;
        private EntityQuery wallQuery;
        private int nextWallSpawnIndex;
        private bool destroyWallsBeforeSpawn;
        private bool invalidateAgentsWhenReady;

        /// <summary>
        /// Creates singleton queries and persistent wall tracking used across bounded structural-change updates.
        /// </summary>
        public void OnCreate(ref SystemState state)
        {
            spawnerQuery = state.GetEntityQuery(ComponentType.ReadOnly<PathfindingTestSpawnerData>(), ComponentType.ReadWrite<PathfindingGridData>(), ComponentType.ReadWrite<PathfindingGridCell>(), ComponentType.ReadWrite<PathfindingRegionRange>(), ComponentType.ReadWrite<PathfindingRegionCell>());
            controlQuery = state.GetEntityQuery(ComponentType.ReadOnly<PathfindingTestControlData>());
            runtimeQuery = state.GetEntityQuery(ComponentType.ReadWrite<PathfindingTestRuntimeData>());
            agentQuery = state.GetEntityQuery(ComponentType.ReadOnly<PathfindingAgentTag>());
            wallQuery = state.GetEntityQuery(ComponentType.ReadOnly<PathfindingWallTag>(), ComponentType.ReadOnly<PathfindingWallState>());
            state.RequireForUpdate(spawnerQuery);
            state.RequireForUpdate(controlQuery);
            state.RequireForUpdate(runtimeQuery);
            wallCellIndices = new NativeList<int>(Allocator.Persistent);
        }

        /// <summary>
        /// Rebuilds changed grid dimensions and reconciles visual walls in bounded batches.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            if (spawnerQuery.CalculateEntityCount() != 1 || controlQuery.CalculateEntityCount() != 1 || runtimeQuery.CalculateEntityCount() != 1)
            {
                MarkSingletonFailure(ref state);
                return;
            }

            state.Dependency.Complete();
            var entityManager = state.EntityManager;
            var spawnerEntity = spawnerQuery.GetSingletonEntity();
            var runtimeEntity = runtimeQuery.GetSingletonEntity();
            var spawnerData = entityManager.GetComponentData<PathfindingTestSpawnerData>(spawnerEntity);
            var controlData = controlQuery.GetSingleton<PathfindingTestControlData>();
            var gridData = entityManager.GetComponentData<PathfindingGridData>(spawnerEntity);
            var runtimeData = entityManager.GetComponentData<PathfindingTestRuntimeData>(runtimeEntity);
            var requestedDimensions = controlData.RequestedGridDimensions;

            if (!TryGetCellCount(requestedDimensions, out var cellCount))
            {
                MarkFailure(entityManager, spawnerEntity, runtimeEntity, gridData, runtimeData, PathfindingTestRuntimeError.InvalidGridDimensions);
                return;
            }

            if (!ValidateWallPrefab(entityManager, spawnerData.WallPrefab))
            {
                MarkFailure(entityManager, spawnerEntity, runtimeEntity, gridData, runtimeData, PathfindingTestRuntimeError.InvalidWallPrefab);
                return;
            }

            if (!requestedDimensions.Equals(gridData.Dimensions))
            {
                gridData.BuildStatus = PathfindingGridBuildStatus.Rebuilding;
                gridData.GridVersion = NextPositiveVersion(gridData.GridVersion);
                gridData.Dimensions = requestedDimensions;
                gridData.CellOrigin = GetCellOrigin(requestedDimensions);
                runtimeData.Status = PathfindingTestRuntimeStatus.Preparing;
                runtimeData.Error = PathfindingTestRuntimeError.None;
                runtimeData.ExpectedWallCount = 0;
                runtimeData.AppliedGridVersion = 0;
                nextWallSpawnIndex = 0;
                destroyWallsBeforeSpawn = true;
                invalidateAgentsWhenReady = true;
                wallCellIndices.Clear();
                entityManager.SetComponentData(spawnerEntity, gridData);
                entityManager.SetComponentData(runtimeEntity, runtimeData);
                ClearGridBuffers(entityManager, spawnerEntity);
            }

            var actualWallCount = wallQuery.CalculateEntityCount();
            if (gridData.BuildStatus == PathfindingGridBuildStatus.Ready && actualWallCount != runtimeData.ExpectedWallCount)
            {
                gridData.BuildStatus = PathfindingGridBuildStatus.Rebuilding;
                runtimeData.Status = PathfindingTestRuntimeStatus.Preparing;
                runtimeData.CurrentWallCount = actualWallCount;
                runtimeData.AppliedGridVersion = 0;
                nextWallSpawnIndex = 0;
                destroyWallsBeforeSpawn = true;
                invalidateAgentsWhenReady = false;
                if (wallCellIndices.Length != runtimeData.ExpectedWallCount) wallCellIndices.Clear();
                entityManager.SetComponentData(spawnerEntity, gridData);
                entityManager.SetComponentData(runtimeEntity, runtimeData);
                Debug.LogWarning($"Pathfinding wall population changed unexpectedly ({actualWallCount:N0}/{runtimeData.ExpectedWallCount:N0}); rebuilding visual walls for grid version {gridData.GridVersion}.");
            }

            if (gridData.BuildStatus == PathfindingGridBuildStatus.Rebuilding && destroyWallsBeforeSpawn)
            {
                if (actualWallCount > 0)
                {
                    DestroyWallBatch(entityManager);
                    runtimeData.CurrentWallCount = wallQuery.CalculateEntityCount();
                    entityManager.SetComponentData(runtimeEntity, runtimeData);
                    return;
                }

                destroyWallsBeforeSpawn = false;
            }

            if (gridData.BuildStatus == PathfindingGridBuildStatus.Rebuilding && wallCellIndices.Length == 0)
            {
                if (!TryBuildGrid(entityManager, spawnerEntity, spawnerData, controlData, gridData, cellCount))
                {
                    MarkFailure(entityManager, spawnerEntity, runtimeEntity, gridData, runtimeData, PathfindingTestRuntimeError.GridBuildFailed);
                    return;
                }

                runtimeData.ExpectedWallCount = wallCellIndices.Length;
                runtimeData.CurrentWallCount = wallQuery.CalculateEntityCount();
                entityManager.SetComponentData(runtimeEntity, runtimeData);
            }

            if (gridData.BuildStatus == PathfindingGridBuildStatus.Rebuilding && nextWallSpawnIndex < wallCellIndices.Length)
            {
                SpawnWallBatch(entityManager, spawnerData.WallPrefab, gridData);
                runtimeData.CurrentWallCount = wallQuery.CalculateEntityCount();
                entityManager.SetComponentData(runtimeEntity, runtimeData);
                return;
            }

            if (gridData.BuildStatus == PathfindingGridBuildStatus.Rebuilding)
            {
                gridData.BuildStatus = PathfindingGridBuildStatus.Ready;
                runtimeData.Status = PathfindingTestRuntimeStatus.Preparing;
                runtimeData.CurrentWallCount = wallQuery.CalculateEntityCount();
                runtimeData.ExpectedWallCount = wallCellIndices.Length;
                runtimeData.AppliedGridVersion = gridData.GridVersion;
                entityManager.SetComponentData(spawnerEntity, gridData);
                entityManager.SetComponentData(runtimeEntity, runtimeData);
                if (invalidateAgentsWhenReady) InvalidateAgents(entityManager);
                invalidateAgentsWhenReady = false;
            }
        }

        /// <summary>
        /// Releases persistent generated wall-cell data.
        /// </summary>
        public void OnDestroy(ref SystemState state)
        {
            if (wallCellIndices.IsCreated) wallCellIndices.Dispose();
        }

        private bool TryBuildGrid(EntityManager entityManager, Entity spawnerEntity, PathfindingTestSpawnerData spawnerData, PathfindingTestControlData controlData, PathfindingGridData gridData, int cellCount)
        {
            var cellRegions = default(NativeArray<int>);
            var traversalQueue = default(NativeArray<int>);
            var regionRanges = default(NativeList<PathfindingRegionRange>);
            var regionCells = default(NativeList<PathfindingRegionCell>);
            var generatedWallIndices = default(NativeList<int>);
            try
            {
                cellRegions = new NativeArray<int>(cellCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                traversalQueue = new NativeArray<int>(cellCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                regionRanges = new NativeList<PathfindingRegionRange>(Allocator.TempJob);
                regionCells = new NativeList<PathfindingRegionCell>(cellCount, Allocator.TempJob);
                generatedWallIndices = new NativeList<int>(Allocator.TempJob);
                var randomSeed = ComposeGridSeed(controlData.BaseRandomSeed, gridData.Dimensions);
                var buildJob = new BuildGridJob
                {
                    Dimensions = gridData.Dimensions,
                    WallDensity = math.clamp(spawnerData.WallDensity, MinimumWallDensity, MaximumWallDensity),
                    RandomSeed = randomSeed,
                    CellRegions = cellRegions,
                    TraversalQueue = traversalQueue,
                    RegionRanges = regionRanges,
                    RegionCells = regionCells,
                    WallIndices = generatedWallIndices
                };
                buildJob.Schedule().Complete();
                if (regionRanges.Length == 0) return false;
                var gridCellBuffer = entityManager.GetBuffer<PathfindingGridCell>(spawnerEntity);
                gridCellBuffer.ResizeUninitialized(cellCount);
                for (var cellIndex = 0; cellIndex < cellCount; cellIndex++)
                {
                    gridCellBuffer[cellIndex] = new PathfindingGridCell { RegionIndex = cellRegions[cellIndex] };
                }

                var regionRangeBuffer = entityManager.GetBuffer<PathfindingRegionRange>(spawnerEntity);
                regionRangeBuffer.Clear();
                regionRangeBuffer.AddRange(regionRanges.AsArray());
                var regionCellBuffer = entityManager.GetBuffer<PathfindingRegionCell>(spawnerEntity);
                regionCellBuffer.Clear();
                regionCellBuffer.AddRange(regionCells.AsArray());
                wallCellIndices.Clear();
                wallCellIndices.AddRange(generatedWallIndices.AsArray());
                nextWallSpawnIndex = 0;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (generatedWallIndices.IsCreated) generatedWallIndices.Dispose();
                if (regionCells.IsCreated) regionCells.Dispose();
                if (regionRanges.IsCreated) regionRanges.Dispose();
                if (traversalQueue.IsCreated) traversalQueue.Dispose();
                if (cellRegions.IsCreated) cellRegions.Dispose();
            }
        }

        private void SpawnWallBatch(EntityManager entityManager, Entity wallPrefab, PathfindingGridData gridData)
        {
            var remainingWallCount = wallCellIndices.Length - nextWallSpawnIndex;
            var spawnCount = math.min(remainingWallCount, MaximumStructuralChangeBatchSize);
            var instantiatedWalls = new NativeArray<Entity>(spawnCount, Allocator.Temp);
            entityManager.Instantiate(wallPrefab, instantiatedWalls);
            for (var batchIndex = 0; batchIndex < spawnCount; batchIndex++)
            {
                var wallEntity = instantiatedWalls[batchIndex];
                var cellIndex = wallCellIndices[nextWallSpawnIndex + batchIndex];
                var cell = IndexToCell(cellIndex, gridData.Dimensions.x);
                var position = gridData.CellOrigin + new float3(cell.x, 0.5f, cell.y);
                entityManager.SetComponentData(wallEntity, LocalTransform.FromPositionRotationScale(position, quaternion.identity, 1f));
                entityManager.SetComponentData(wallEntity, new PathfindingWallState
                {
                    CellIndex = cellIndex,
                    GridVersion = gridData.GridVersion
                });
            }

            nextWallSpawnIndex += spawnCount;
            instantiatedWalls.Dispose();
        }

        private void DestroyWallBatch(EntityManager entityManager)
        {
            var runtimeWallEntities = wallQuery.ToEntityArray(Allocator.Temp);
            var destroyCount = math.min(runtimeWallEntities.Length, MaximumStructuralChangeBatchSize);
            var firstDestroyIndex = runtimeWallEntities.Length - destroyCount;
            entityManager.DestroyEntity(runtimeWallEntities.GetSubArray(firstDestroyIndex, destroyCount));
            runtimeWallEntities.Dispose();
        }

        private void InvalidateAgents(EntityManager entityManager)
        {
            var agents = agentQuery.ToEntityArray(Allocator.Temp);
            for (var agentIndex = 0; agentIndex < agents.Length; agentIndex++)
            {
                var agent = agents[agentIndex];
                entityManager.SetComponentEnabled<PathfindingPathRequest>(agent, true);
                entityManager.SetComponentEnabled<PathfindingPathReady>(agent, false);
                entityManager.SetComponentEnabled<PathfindingInitializationPending>(agent, true);
                entityManager.SetComponentEnabled<PathfindingSearchFailed>(agent, false);
            }

            agents.Dispose();
        }


        private void MarkSingletonFailure(ref SystemState state)
        {
            if (runtimeQuery.CalculateEntityCount() != 1) return;
            var runtimeEntity = runtimeQuery.GetSingletonEntity();
            var runtimeData = state.EntityManager.GetComponentData<PathfindingTestRuntimeData>(runtimeEntity);
            runtimeData.Status = PathfindingTestRuntimeStatus.Failed;
            runtimeData.Error = PathfindingTestRuntimeError.InvalidSingletonCount;
            state.EntityManager.SetComponentData(runtimeEntity, runtimeData);
        }

        private static bool ValidateWallPrefab(EntityManager entityManager, Entity wallPrefab)
        {
            return wallPrefab != Entity.Null && entityManager.Exists(wallPrefab) && entityManager.HasComponent<LocalTransform>(wallPrefab) && entityManager.HasComponent<PathfindingWallTag>(wallPrefab) && entityManager.HasComponent<PathfindingWallState>(wallPrefab);
        }

        private static bool TryGetCellCount(int2 dimensions, out int cellCount)
        {
            var longCellCount = (long)dimensions.x * dimensions.y;
            if (dimensions.x <= 0 || dimensions.y <= 0 || longCellCount < 2L || longCellCount > int.MaxValue)
            {
                cellCount = 0;
                return false;
            }

            cellCount = (int)longCellCount;
            return true;
        }

        private static void ClearGridBuffers(EntityManager entityManager, Entity spawnerEntity)
        {
            entityManager.GetBuffer<PathfindingGridCell>(spawnerEntity).Clear();
            entityManager.GetBuffer<PathfindingRegionRange>(spawnerEntity).Clear();
            entityManager.GetBuffer<PathfindingRegionCell>(spawnerEntity).Clear();
        }

        private static void MarkFailure(EntityManager entityManager, Entity spawnerEntity, Entity runtimeEntity, PathfindingGridData gridData, PathfindingTestRuntimeData runtimeData, PathfindingTestRuntimeError error)
        {
            gridData.BuildStatus = PathfindingGridBuildStatus.Failed;
            runtimeData.Status = PathfindingTestRuntimeStatus.Failed;
            runtimeData.Error = error;
            entityManager.SetComponentData(spawnerEntity, gridData);
            entityManager.SetComponentData(runtimeEntity, runtimeData);
        }

        private static int NextPositiveVersion(int currentVersion)
        {
            return currentVersion == int.MaxValue ? 1 : currentVersion + 1;
        }

        private static uint ComposeGridSeed(uint baseSeed, int2 dimensions)
        {
            var nonzeroBaseSeed = math.max(MinimumRandomSeed, baseSeed);
            var mixedSeed = math.hash(new uint3(nonzeroBaseSeed, (uint)dimensions.x * DimensionSeedMultiplier, (uint)dimensions.y * DimensionSeedMultiplier));
            return math.max(MinimumRandomSeed, mixedSeed);
        }

        private static float3 GetCellOrigin(int2 dimensions)
        {
            return new float3(-0.5f * (dimensions.x - 1), 0f, -0.5f * (dimensions.y - 1));
        }

        private static int2 IndexToCell(int cellIndex, int width)
        {
            return new int2(cellIndex % width, cellIndex / width);
        }

        [BurstCompile]
        private struct BuildGridJob : IJob
        {
            public int2 Dimensions;
            public float WallDensity;
            public uint RandomSeed;
            public NativeArray<int> CellRegions;
            public NativeArray<int> TraversalQueue;
            public NativeList<PathfindingRegionRange> RegionRanges;
            public NativeList<PathfindingRegionCell> RegionCells;
            public NativeList<int> WallIndices;

            public void Execute()
            {
                GenerateOccupancy();
                LabelRegions();
                if (RegionRanges.Length == 0)
                {
                    CellRegions[0] = -2;
                    CellRegions[1] = -2;
                    ResetWalkableLabels();
                    LabelRegions();
                }

                WallIndices.Clear();
                for (var cellIndex = 0; cellIndex < CellRegions.Length; cellIndex++)
                {
                    if (CellRegions[cellIndex] < 0) WallIndices.Add(cellIndex);
                }
            }

            private void GenerateOccupancy()
            {
                var random = new Unity.Mathematics.Random(RandomSeed);
                for (var cellIndex = 0; cellIndex < CellRegions.Length; cellIndex++)
                {
                    CellRegions[cellIndex] = random.NextFloat() < WallDensity ? -1 : -2;
                }
            }

            private void ResetWalkableLabels()
            {
                RegionRanges.Clear();
                RegionCells.Clear();
                for (var cellIndex = 0; cellIndex < CellRegions.Length; cellIndex++)
                {
                    if (CellRegions[cellIndex] >= 0) CellRegions[cellIndex] = -2;
                }
            }

            private void LabelRegions()
            {
                for (var cellIndex = 0; cellIndex < CellRegions.Length; cellIndex++)
                {
                    if (CellRegions[cellIndex] != -2) continue;
                    var componentCellCount = TraverseComponent(cellIndex);
                    if (componentCellCount < 2)
                    {
                        CellRegions[cellIndex] = -1;
                        continue;
                    }

                    var regionIndex = RegionRanges.Length;
                    var rangeStart = RegionCells.Length;
                    for (var componentIndex = 0; componentIndex < componentCellCount; componentIndex++)
                    {
                        var componentCellIndex = TraversalQueue[componentIndex];
                        CellRegions[componentCellIndex] = regionIndex;
                        RegionCells.Add(new PathfindingRegionCell { CellIndex = componentCellIndex });
                    }

                    RegionRanges.Add(new PathfindingRegionRange { StartIndex = rangeStart, Count = componentCellCount });
                }
            }

            private int TraverseComponent(int startCellIndex)
            {
                var readIndex = 0;
                var writeIndex = 1;
                TraversalQueue[0] = startCellIndex;
                CellRegions[startCellIndex] = -3;
                while (readIndex < writeIndex)
                {
                    var currentCellIndex = TraversalQueue[readIndex++];
                    var currentCell = IndexToCell(currentCellIndex, Dimensions.x);
                    for (var yOffset = -1; yOffset <= 1; yOffset++)
                    {
                        for (var xOffset = -1; xOffset <= 1; xOffset++)
                        {
                            if (xOffset == 0 && yOffset == 0) continue;
                            var neighborCell = currentCell + new int2(xOffset, yOffset);
                            if (!IsInBounds(neighborCell)) continue;
                            var neighborIndex = neighborCell.y * Dimensions.x + neighborCell.x;
                            if (CellRegions[neighborIndex] != -2 || !CanTraverseDiagonal(currentCell, xOffset, yOffset)) continue;
                            CellRegions[neighborIndex] = -3;
                            TraversalQueue[writeIndex++] = neighborIndex;
                        }
                    }
                }

                return writeIndex;
            }

            private bool CanTraverseDiagonal(int2 currentCell, int xOffset, int yOffset)
            {
                if (xOffset == 0 || yOffset == 0) return true;
                var horizontalIndex = currentCell.y * Dimensions.x + currentCell.x + xOffset;
                var verticalIndex = (currentCell.y + yOffset) * Dimensions.x + currentCell.x;
                return CellRegions[horizontalIndex] != -1 && CellRegions[verticalIndex] != -1;
            }

            private bool IsInBounds(int2 cell)
            {
                return cell.x >= 0 && cell.y >= 0 && cell.x < Dimensions.x && cell.y < Dimensions.y;
            }
        }
    }
}
