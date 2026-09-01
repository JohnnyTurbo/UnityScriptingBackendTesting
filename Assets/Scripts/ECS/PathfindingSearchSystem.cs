using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Transforms;

namespace TMG.CoreCLRTest
{
    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PathfindingTestSpawnSystem))]
    public partial struct PathfindingSearchSystem : ISystem
    {
        private const uint MinimumRandomSeed = 1u;
        private NativeArray<int> searchStamps;
        private NativeArray<int> gCosts;
        private NativeArray<int> parents;
        private NativeArray<int> heapPositions;
        private NativeArray<int> heapCells;
        private NativeArray<int> threadSearchStamps;
        private int scratchCellCount;
        private int scratchThreadCount;

        /// <summary>
        /// Declares the grid and control singletons required by path searches.
        /// </summary>
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<PathfindingTestSpawnerData>();
            state.RequireForUpdate<PathfindingGridData>();
            state.RequireForUpdate<PathfindingTestControlData>();
            state.RequireForUpdate<PathfindingTestRuntimeData>();
        }

        /// <summary>
        /// Resizes per-worker scratch memory when necessary and schedules fresh per-frame A* searches in parallel.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            var gridData = SystemAPI.GetSingleton<PathfindingGridData>();
            if (gridData.BuildStatus != PathfindingGridBuildStatus.Ready) return;
            var gridCells = SystemAPI.GetSingletonBuffer<PathfindingGridCell>(true).AsNativeArray();
            var regionRanges = SystemAPI.GetSingletonBuffer<PathfindingRegionRange>(true).AsNativeArray();
            var regionCells = SystemAPI.GetSingletonBuffer<PathfindingRegionCell>(true).AsNativeArray();
            var cellCountLong = (long)gridData.Dimensions.x * gridData.Dimensions.y;
            var threadCount = JobsUtility.ThreadIndexCount;
            var scratchLengthLong = cellCountLong * threadCount;
            if (cellCountLong <= 0L || cellCountLong != gridCells.Length || regionRanges.Length == 0 || scratchLengthLong <= 0L || scratchLengthLong > int.MaxValue)
            {
                MarkRuntimeFailure(ref state, PathfindingTestRuntimeError.InvalidGridMetadata);
                return;
            }

            var cellCount = (int)cellCountLong;
            if (scratchCellCount != cellCount || scratchThreadCount != threadCount)
            {
                if (!TryResizeScratch(ref state, cellCount, threadCount, (int)scratchLengthLong))
                {
                    MarkRuntimeFailure(ref state, PathfindingTestRuntimeError.ScratchAllocationFailed);
                    return;
                }
            }
            var controlData = SystemAPI.GetSingleton<PathfindingTestControlData>();
            var findPathsJob = new FindPathsJob
            {
                Dimensions = gridData.Dimensions,
                CellOrigin = gridData.CellOrigin,
                GridVersion = gridData.GridVersion,
                CellCount = cellCount,
                BaseRandomSeed = math.max(MinimumRandomSeed, controlData.BaseRandomSeed),
                GridCells = gridCells,
                RegionRanges = regionRanges,
                RegionCells = regionCells,
                SearchStamps = searchStamps,
                GCosts = gCosts,
                Parents = parents,
                HeapPositions = heapPositions,
                HeapCells = heapCells,
                ThreadSearchStamps = threadSearchStamps,
                PathReadyLookup = SystemAPI.GetComponentLookup<PathfindingPathReady>(),
                InitializationPendingLookup = SystemAPI.GetComponentLookup<PathfindingInitializationPending>(),
                SearchFailedLookup = SystemAPI.GetComponentLookup<PathfindingSearchFailed>()
            };
            state.Dependency = findPathsJob.ScheduleParallel(state.Dependency);
        }

        /// <summary>
        /// Completes outstanding searches and releases persistent per-worker scratch memory.
        /// </summary>
        public void OnDestroy(ref SystemState state)
        {
            state.Dependency.Complete();
            DisposeScratch();
        }

        private bool TryResizeScratch(ref SystemState state, int cellCount, int threadCount, int scratchLength)
        {
            state.Dependency.Complete();
            DisposeScratch();
            try
            {
                searchStamps = new NativeArray<int>(scratchLength, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                gCosts = new NativeArray<int>(scratchLength, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                parents = new NativeArray<int>(scratchLength, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                heapPositions = new NativeArray<int>(scratchLength, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                heapCells = new NativeArray<int>(scratchLength, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                threadSearchStamps = new NativeArray<int>(threadCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                scratchCellCount = cellCount;
                scratchThreadCount = threadCount;
                return true;
            }
            catch
            {
                DisposeScratch();
                return false;
            }
        }

        private void DisposeScratch()
        {
            if (searchStamps.IsCreated) searchStamps.Dispose();
            if (gCosts.IsCreated) gCosts.Dispose();
            if (parents.IsCreated) parents.Dispose();
            if (heapPositions.IsCreated) heapPositions.Dispose();
            if (heapCells.IsCreated) heapCells.Dispose();
            if (threadSearchStamps.IsCreated) threadSearchStamps.Dispose();
            scratchCellCount = 0;
            scratchThreadCount = 0;
        }

        private void MarkRuntimeFailure(ref SystemState state, PathfindingTestRuntimeError error)
        {
            var runtimeData = SystemAPI.GetSingleton<PathfindingTestRuntimeData>();
            runtimeData.Status = PathfindingTestRuntimeStatus.Failed;
            runtimeData.Error = error;
            SystemAPI.SetSingleton(runtimeData);
        }

        [BurstCompile]
        [WithAll(typeof(PathfindingAgentTag))]
        private partial struct FindPathsJob : IJobEntity
        {
            private const int CardinalCost = 10;
            private const int DiagonalCost = 14;
            private const int ClosedHeapPosition = -2;
            private const float FixedAgentHeight = 1f;
            public int2 Dimensions;
            public float3 CellOrigin;
            public int GridVersion;
            public int CellCount;
            public uint BaseRandomSeed;
            [ReadOnly] public NativeArray<PathfindingGridCell> GridCells;
            [ReadOnly] public NativeArray<PathfindingRegionRange> RegionRanges;
            [ReadOnly] public NativeArray<PathfindingRegionCell> RegionCells;
            [NativeDisableParallelForRestriction] public NativeArray<int> SearchStamps;
            [NativeDisableParallelForRestriction] public NativeArray<int> GCosts;
            [NativeDisableParallelForRestriction] public NativeArray<int> Parents;
            [NativeDisableParallelForRestriction] public NativeArray<int> HeapPositions;
            [NativeDisableParallelForRestriction] public NativeArray<int> HeapCells;
            [NativeDisableParallelForRestriction] public NativeArray<int> ThreadSearchStamps;
            [NativeDisableParallelForRestriction] public ComponentLookup<PathfindingPathReady> PathReadyLookup;
            [NativeDisableParallelForRestriction] public ComponentLookup<PathfindingInitializationPending> InitializationPendingLookup;
            [NativeDisableParallelForRestriction] public ComponentLookup<PathfindingSearchFailed> SearchFailedLookup;
            [NativeSetThreadIndex] private int threadIndex;

            private void Execute(Entity entity, ref LocalTransform localTransform, ref PathfindingAgentState agentState, DynamicBuffer<PathfindingWaypoint> waypoints, EnabledRefRW<PathfindingPathRequest> pathRequest)
            {
                var random = agentState.Random;
                var pathWasReady = PathReadyLookup.IsComponentEnabled(entity);
                var hasLiveCell = TryGetLiveCell(localTransform.Position, out var currentCell, out var currentCellIndex);
                var requiresGridInitialization = agentState.AppliedGridVersion != GridVersion || !hasLiveCell;
                var requiresDestinationSelection = !pathWasReady;
                if (requiresGridInitialization)
                {
                    var randomSeed = math.hash(new uint4(BaseRandomSeed, (uint)entity.Index + 1u, (uint)entity.Version + 1u, (uint)GridVersion));
                    random = new Random(math.max(1u, randomSeed));
                    if (!TrySelectInitialCell(ref random, out currentCell, out currentCellIndex))
                    {
                        MarkSearchFailed(entity, ref pathRequest, waypoints);
                        return;
                    }

                    localTransform.Position = CellToWorld(currentCell);
                    agentState.AppliedGridVersion = GridVersion;
                    requiresDestinationSelection = true;
                }

                agentState.CurrentCell = currentCell;
                var regionIndex = GridCells[currentCellIndex].RegionIndex;
                var destinationCell = agentState.DestinationCell;
                var destinationCellIndex = CellToIndex(destinationCell);
                if (!IsValidWalkableCell(destinationCellIndex) || GridCells[destinationCellIndex].RegionIndex != regionIndex) requiresDestinationSelection = true;
                if (requiresDestinationSelection && !TrySelectDestination(ref random, regionIndex, currentCellIndex, out destinationCell, out destinationCellIndex))
                {
                    MarkSearchFailed(entity, ref pathRequest, waypoints);
                    return;
                }

                agentState.Random = random;
                agentState.DestinationCell = destinationCell;
                if (!FindPath(currentCellIndex, destinationCellIndex, waypoints))
                {
                    MarkSearchFailed(entity, ref pathRequest, waypoints);
                    return;
                }

                agentState.NextWaypointIndex = 0;
                pathRequest.ValueRW = true;
                PathReadyLookup.SetComponentEnabled(entity, true);
                InitializationPendingLookup.SetComponentEnabled(entity, false);
                SearchFailedLookup.SetComponentEnabled(entity, false);
            }

            private bool TryGetLiveCell(float3 worldPosition, out int2 currentCell, out int currentCellIndex)
            {
                currentCell = (int2)math.round(worldPosition.xz - CellOrigin.xz);
                currentCellIndex = CellToIndex(currentCell);
                return IsValidWalkableCell(currentCellIndex);
            }

            private bool TrySelectInitialCell(ref Random random, out int2 cell, out int cellIndex)
            {
                if (RegionRanges.Length == 0)
                {
                    cell = int2.zero;
                    cellIndex = -1;
                    return false;
                }

                var regionIndex = random.NextInt(RegionRanges.Length);
                var regionRange = RegionRanges[regionIndex];
                if (!IsValidRegionRange(regionRange))
                {
                    cell = int2.zero;
                    cellIndex = -1;
                    return false;
                }

                cellIndex = RegionCells[regionRange.StartIndex + random.NextInt(regionRange.Count)].CellIndex;
                cell = IndexToCell(cellIndex);
                return IsValidWalkableCell(cellIndex);
            }

            private bool TrySelectDestination(ref Random random, int regionIndex, int currentCellIndex, out int2 destinationCell, out int destinationCellIndex)
            {
                if (regionIndex < 0 || regionIndex >= RegionRanges.Length)
                {
                    destinationCell = int2.zero;
                    destinationCellIndex = -1;
                    return false;
                }

                var regionRange = RegionRanges[regionIndex];
                if (!IsValidRegionRange(regionRange))
                {
                    destinationCell = int2.zero;
                    destinationCellIndex = -1;
                    return false;
                }

                var destinationOffset = random.NextInt(regionRange.Count);
                destinationCellIndex = RegionCells[regionRange.StartIndex + destinationOffset].CellIndex;
                if (destinationCellIndex == currentCellIndex)
                {
                    destinationOffset = (destinationOffset + 1) % regionRange.Count;
                    destinationCellIndex = RegionCells[regionRange.StartIndex + destinationOffset].CellIndex;
                }

                destinationCell = IndexToCell(destinationCellIndex);
                return destinationCellIndex != currentCellIndex && IsValidWalkableCell(destinationCellIndex);
            }

            private bool FindPath(int startCellIndex, int destinationCellIndex, DynamicBuffer<PathfindingWaypoint> waypoints)
            {
                var searchStamp = BeginSearch();
                var scratchOffset = threadIndex * CellCount;
                var heapCount = 0;
                SetNode(startCellIndex, searchStamp, 0, -1, 0);
                HeapCells[scratchOffset] = startCellIndex;
                heapCount++;
                var foundDestination = false;
                while (heapCount > 0)
                {
                    var currentCellIndex = PopHeap(ref heapCount, destinationCellIndex, scratchOffset);
                    if (currentCellIndex == destinationCellIndex)
                    {
                        foundDestination = true;
                        break;
                    }

                    var currentCell = IndexToCell(currentCellIndex);
                    for (var yOffset = -1; yOffset <= 1; yOffset++)
                    {
                        for (var xOffset = -1; xOffset <= 1; xOffset++)
                        {
                            if (xOffset == 0 && yOffset == 0) continue;
                            var neighborCell = currentCell + new int2(xOffset, yOffset);
                            if (!IsInBounds(neighborCell) || !CanTraverse(currentCell, neighborCell, xOffset, yOffset)) continue;
                            var neighborCellIndex = CellToIndex(neighborCell);
                            var neighborScratchIndex = scratchOffset + neighborCellIndex;
                            if (SearchStamps[neighborScratchIndex] == searchStamp && HeapPositions[neighborScratchIndex] == ClosedHeapPosition) continue;
                            var movementCost = xOffset == 0 || yOffset == 0 ? CardinalCost : DiagonalCost;
                            var tentativeGCost = GCosts[scratchOffset + currentCellIndex] + movementCost;
                            if (SearchStamps[neighborScratchIndex] != searchStamp)
                            {
                                SetNode(neighborCellIndex, searchStamp, tentativeGCost, currentCellIndex, heapCount);
                                HeapCells[scratchOffset + heapCount] = neighborCellIndex;
                                SiftUp(heapCount, destinationCellIndex, scratchOffset);
                                heapCount++;
                            }
                            else if (tentativeGCost < GCosts[neighborScratchIndex])
                            {
                                GCosts[neighborScratchIndex] = tentativeGCost;
                                Parents[neighborScratchIndex] = currentCellIndex;
                                SiftUp(HeapPositions[neighborScratchIndex], destinationCellIndex, scratchOffset);
                            }
                        }
                    }
                }

                if (!foundDestination) return false;
                return ReconstructCompressedPath(startCellIndex, destinationCellIndex, scratchOffset, waypoints);
            }

            private int BeginSearch()
            {
                var nextSearchStamp = ThreadSearchStamps[threadIndex] + 1;
                var scratchOffset = threadIndex * CellCount;
                if (nextSearchStamp <= 0)
                {
                    for (var cellIndex = 0; cellIndex < CellCount; cellIndex++)
                    {
                        SearchStamps[scratchOffset + cellIndex] = 0;
                    }

                    nextSearchStamp = 1;
                }

                ThreadSearchStamps[threadIndex] = nextSearchStamp;
                return nextSearchStamp;
            }

            private void SetNode(int cellIndex, int searchStamp, int gCost, int parent, int heapPosition)
            {
                var scratchIndex = threadIndex * CellCount + cellIndex;
                SearchStamps[scratchIndex] = searchStamp;
                GCosts[scratchIndex] = gCost;
                Parents[scratchIndex] = parent;
                HeapPositions[scratchIndex] = heapPosition;
            }

            private int PopHeap(ref int heapCount, int destinationCellIndex, int scratchOffset)
            {
                var minimumCellIndex = HeapCells[scratchOffset];
                heapCount--;
                if (heapCount > 0)
                {
                    var replacementCellIndex = HeapCells[scratchOffset + heapCount];
                    HeapCells[scratchOffset] = replacementCellIndex;
                    HeapPositions[scratchOffset + replacementCellIndex] = 0;
                    SiftDown(0, heapCount, destinationCellIndex, scratchOffset);
                }

                HeapPositions[scratchOffset + minimumCellIndex] = ClosedHeapPosition;
                return minimumCellIndex;
            }

            private void SiftUp(int heapPosition, int destinationCellIndex, int scratchOffset)
            {
                var currentPosition = heapPosition;
                while (currentPosition > 0)
                {
                    var parentPosition = (currentPosition - 1) / 2;
                    if (!IsHigherPriority(HeapCells[scratchOffset + currentPosition], HeapCells[scratchOffset + parentPosition], destinationCellIndex, scratchOffset)) break;
                    SwapHeapEntries(currentPosition, parentPosition, scratchOffset);
                    currentPosition = parentPosition;
                }
            }

            private void SiftDown(int heapPosition, int heapCount, int destinationCellIndex, int scratchOffset)
            {
                var currentPosition = heapPosition;
                while (true)
                {
                    var leftChildPosition = currentPosition * 2 + 1;
                    if (leftChildPosition >= heapCount) return;
                    var rightChildPosition = leftChildPosition + 1;
                    var bestChildPosition = rightChildPosition < heapCount && IsHigherPriority(HeapCells[scratchOffset + rightChildPosition], HeapCells[scratchOffset + leftChildPosition], destinationCellIndex, scratchOffset) ? rightChildPosition : leftChildPosition;
                    if (!IsHigherPriority(HeapCells[scratchOffset + bestChildPosition], HeapCells[scratchOffset + currentPosition], destinationCellIndex, scratchOffset)) return;
                    SwapHeapEntries(currentPosition, bestChildPosition, scratchOffset);
                    currentPosition = bestChildPosition;
                }
            }

            private void SwapHeapEntries(int firstPosition, int secondPosition, int scratchOffset)
            {
                var firstCellIndex = HeapCells[scratchOffset + firstPosition];
                var secondCellIndex = HeapCells[scratchOffset + secondPosition];
                HeapCells[scratchOffset + firstPosition] = secondCellIndex;
                HeapCells[scratchOffset + secondPosition] = firstCellIndex;
                HeapPositions[scratchOffset + firstCellIndex] = secondPosition;
                HeapPositions[scratchOffset + secondCellIndex] = firstPosition;
            }

            private bool IsHigherPriority(int firstCellIndex, int secondCellIndex, int destinationCellIndex, int scratchOffset)
            {
                var firstHeuristic = GetOctileHeuristic(firstCellIndex, destinationCellIndex);
                var secondHeuristic = GetOctileHeuristic(secondCellIndex, destinationCellIndex);
                var firstCost = GCosts[scratchOffset + firstCellIndex] + firstHeuristic;
                var secondCost = GCosts[scratchOffset + secondCellIndex] + secondHeuristic;
                if (firstCost != secondCost) return firstCost < secondCost;
                if (firstHeuristic != secondHeuristic) return firstHeuristic < secondHeuristic;
                return firstCellIndex < secondCellIndex;
            }

            private int GetOctileHeuristic(int firstCellIndex, int secondCellIndex)
            {
                var delta = math.abs(IndexToCell(firstCellIndex) - IndexToCell(secondCellIndex));
                var diagonalSteps = math.min(delta.x, delta.y);
                var cardinalSteps = math.max(delta.x, delta.y) - diagonalSteps;
                return diagonalSteps * DiagonalCost + cardinalSteps * CardinalCost;
            }

            private bool ReconstructCompressedPath(int startCellIndex, int destinationCellIndex, int scratchOffset, DynamicBuffer<PathfindingWaypoint> waypoints)
            {
                var pathCellCount = 0;
                var currentCellIndex = destinationCellIndex;
                while (currentCellIndex >= 0 && pathCellCount < CellCount)
                {
                    HeapCells[scratchOffset + pathCellCount++] = currentCellIndex;
                    if (currentCellIndex == startCellIndex) break;
                    currentCellIndex = Parents[scratchOffset + currentCellIndex];
                }

                if (pathCellCount < 1 || HeapCells[scratchOffset + pathCellCount - 1] != startCellIndex) return false;
                waypoints.Clear();
                var previousCell = IndexToCell(startCellIndex);
                var previousDirection = int2.zero;
                var hasPreviousDirection = false;
                for (var reversePathIndex = pathCellCount - 2; reversePathIndex >= 0; reversePathIndex--)
                {
                    var pathCell = IndexToCell(HeapCells[scratchOffset + reversePathIndex]);
                    var direction = pathCell - previousCell;
                    if (hasPreviousDirection && !direction.Equals(previousDirection)) waypoints.Add(new PathfindingWaypoint { Position = CellToWorld(previousCell) });
                    previousDirection = direction;
                    hasPreviousDirection = true;
                    previousCell = pathCell;
                }

                waypoints.Add(new PathfindingWaypoint { Position = CellToWorld(IndexToCell(destinationCellIndex)) });
                return waypoints.Length > 0;
            }

            private bool CanTraverse(int2 currentCell, int2 neighborCell, int xOffset, int yOffset)
            {
                var neighborCellIndex = CellToIndex(neighborCell);
                if (!IsValidWalkableCell(neighborCellIndex)) return false;
                if (xOffset == 0 || yOffset == 0) return true;
                var horizontalCellIndex = CellToIndex(new int2(currentCell.x + xOffset, currentCell.y));
                var verticalCellIndex = CellToIndex(new int2(currentCell.x, currentCell.y + yOffset));
                return IsValidWalkableCell(horizontalCellIndex) && IsValidWalkableCell(verticalCellIndex);
            }

            private bool IsValidRegionRange(PathfindingRegionRange regionRange)
            {
                return regionRange.Count >= 2 && regionRange.StartIndex >= 0 && regionRange.StartIndex <= RegionCells.Length - regionRange.Count;
            }

            private bool IsValidWalkableCell(int cellIndex)
            {
                return cellIndex >= 0 && cellIndex < GridCells.Length && GridCells[cellIndex].RegionIndex >= 0;
            }

            private bool IsInBounds(int2 cell)
            {
                return cell.x >= 0 && cell.y >= 0 && cell.x < Dimensions.x && cell.y < Dimensions.y;
            }

            private int CellToIndex(int2 cell)
            {
                return IsInBounds(cell) ? cell.y * Dimensions.x + cell.x : -1;
            }

            private int2 IndexToCell(int cellIndex)
            {
                return new int2(cellIndex % Dimensions.x, cellIndex / Dimensions.x);
            }

            private float3 CellToWorld(int2 cell)
            {
                return CellOrigin + new float3(cell.x, FixedAgentHeight, cell.y);
            }

            private void MarkSearchFailed(Entity entity, ref EnabledRefRW<PathfindingPathRequest> pathRequest, DynamicBuffer<PathfindingWaypoint> waypoints)
            {
                waypoints.Clear();
                pathRequest.ValueRW = false;
                PathReadyLookup.SetComponentEnabled(entity, false);
                SearchFailedLookup.SetComponentEnabled(entity, true);
            }
        }
    }
}
