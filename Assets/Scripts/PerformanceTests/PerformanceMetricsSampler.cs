using System;
using UnityEngine;

namespace CoreCLRTest.PerformanceTests
{
    public readonly struct PerformanceMetricsSnapshot
    {
        public bool IsProcessCpuUtilizationAvailable { get; }
        public bool IsCpuFrameTimeAvailable { get; }
        public bool IsGpuFrameTimeAvailable { get; }
        public bool IsPeakWorkingSetAvailable { get; }
        public double ProcessCpuUtilizationPercent { get; }
        public double AverageCpuFrameTimeMilliseconds { get; }
        public double AverageGpuFrameTimeMilliseconds { get; }
        public ulong PeakWorkingSetBytes { get; }

        internal PerformanceMetricsSnapshot(bool isProcessCpuUtilizationAvailable, bool isCpuFrameTimeAvailable, bool isGpuFrameTimeAvailable, bool isPeakWorkingSetAvailable, double processCpuUtilizationPercent, double averageCpuFrameTimeMilliseconds, double averageGpuFrameTimeMilliseconds, ulong peakWorkingSetBytes)
        {
            IsProcessCpuUtilizationAvailable = isProcessCpuUtilizationAvailable;
            IsCpuFrameTimeAvailable = isCpuFrameTimeAvailable;
            IsGpuFrameTimeAvailable = isGpuFrameTimeAvailable;
            IsPeakWorkingSetAvailable = isPeakWorkingSetAvailable;
            ProcessCpuUtilizationPercent = processCpuUtilizationPercent;
            AverageCpuFrameTimeMilliseconds = averageCpuFrameTimeMilliseconds;
            AverageGpuFrameTimeMilliseconds = averageGpuFrameTimeMilliseconds;
            PeakWorkingSetBytes = peakWorkingSetBytes;
        }
    }

    internal sealed class PerformanceMetricsSampler
    {
        private const double PercentScale = 100d;
        private const int LatestFrameTimingCount = 1;

        private readonly FrameTiming[] latestFrameTimings = new FrameTiming[LatestFrameTimingCount];
        private IProcessMetricsProvider processMetricsProvider;
        private uint logicalProcessorCount;
        private double initialTotalProcessorTimeSeconds;
        private ulong peakWorkingSetBytes;
        private double latestFrameStartTimestamp;
        private double totalCpuFrameTimeMilliseconds;
        private double totalGpuFrameTimeMilliseconds;
        private int cpuFrameTimeSampleCount;
        private int gpuFrameTimeSampleCount;
        private bool isInitialTotalProcessorTimeAvailable;
        private bool isPeakWorkingSetAvailable;
        private bool hasLatestFrameStartTimestamp;
        private bool isSampling;

        internal bool TryBegin(out string errorMessage)
        {
            Reset();
            processMetricsProvider = ProcessMetricsProviderFactory.Create();

            var initialProcessMetrics = processMetricsProvider.Capture();
            if (initialProcessMetrics.IsTotalProcessorTimeAvailable && IsFiniteNonNegative(initialProcessMetrics.TotalProcessorTimeSeconds))
            {
                initialTotalProcessorTimeSeconds = initialProcessMetrics.TotalProcessorTimeSeconds;
                isInitialTotalProcessorTimeAvailable = true;
            }

            if (!processMetricsProvider.TryGetLogicalProcessorCount(out logicalProcessorCount) || logicalProcessorCount == 0) logicalProcessorCount = 0;
            RecordResidentMemory(initialProcessMetrics);
            SeedLatestFrameTimestamp();

            isSampling = true;
            errorMessage = string.Empty;
            return true;
        }

        internal void RequestFrameTimingCapture()
        {
            FrameTimingManager.CaptureFrameTimings();
        }

        internal bool TryRecordCompletedFrame(out string errorMessage)
        {
            if (!isSampling)
            {
                errorMessage = "Performance metrics sampling has not begun.";
                return false;
            }

            RecordResidentMemory(processMetricsProvider.Capture());

            var timingCount = FrameTimingManager.GetLatestTimings(LatestFrameTimingCount, latestFrameTimings);
            if (timingCount == 0)
            {
                errorMessage = string.Empty;
                return true;
            }

            var frameTiming = latestFrameTimings[0];
            if (!IsFinite(frameTiming.frameStartTimestamp))
            {
                errorMessage = string.Empty;
                return true;
            }

            if (hasLatestFrameStartTimestamp && frameTiming.frameStartTimestamp <= latestFrameStartTimestamp)
            {
                errorMessage = string.Empty;
                return true;
            }

            latestFrameStartTimestamp = frameTiming.frameStartTimestamp;
            hasLatestFrameStartTimestamp = true;
            AccumulateFrameTime(frameTiming.cpuFrameTime, ref totalCpuFrameTimeMilliseconds, ref cpuFrameTimeSampleCount);
            AccumulateFrameTime(frameTiming.gpuFrameTime, ref totalGpuFrameTimeMilliseconds, ref gpuFrameTimeSampleCount);

            errorMessage = string.Empty;
            return true;
        }

        internal bool TryComplete(double elapsedSeconds, out PerformanceMetricsSnapshot snapshot, out string errorMessage)
        {
            snapshot = default;
            if (!isSampling)
            {
                errorMessage = "Performance metrics sampling has not begun.";
                return false;
            }

            isSampling = false;
            if (!IsPositiveFinite(elapsedSeconds))
            {
                errorMessage = "The performance metrics window elapsed time must be positive and finite.";
                return false;
            }

            var finalProcessMetrics = processMetricsProvider.Capture();
            RecordResidentMemory(finalProcessMetrics);

            var isProcessCpuUtilizationAvailable = TryCalculateProcessCpuUtilization(finalProcessMetrics, elapsedSeconds, out var processCpuUtilizationPercent);
            var isCpuFrameTimeAvailable = TryCalculateAverageFrameTime(totalCpuFrameTimeMilliseconds, cpuFrameTimeSampleCount, out var averageCpuFrameTimeMilliseconds);
            var isGpuFrameTimeAvailable = TryCalculateAverageFrameTime(totalGpuFrameTimeMilliseconds, gpuFrameTimeSampleCount, out var averageGpuFrameTimeMilliseconds);
            snapshot = new PerformanceMetricsSnapshot(isProcessCpuUtilizationAvailable, isCpuFrameTimeAvailable, isGpuFrameTimeAvailable, isPeakWorkingSetAvailable, processCpuUtilizationPercent, averageCpuFrameTimeMilliseconds, averageGpuFrameTimeMilliseconds, peakWorkingSetBytes);
            errorMessage = string.Empty;
            return true;
        }

        private void Reset()
        {
            processMetricsProvider = null;
            logicalProcessorCount = 0;
            initialTotalProcessorTimeSeconds = 0d;
            peakWorkingSetBytes = 0;
            latestFrameStartTimestamp = 0d;
            totalCpuFrameTimeMilliseconds = 0d;
            totalGpuFrameTimeMilliseconds = 0d;
            cpuFrameTimeSampleCount = 0;
            gpuFrameTimeSampleCount = 0;
            isInitialTotalProcessorTimeAvailable = false;
            isPeakWorkingSetAvailable = false;
            hasLatestFrameStartTimestamp = false;
            isSampling = false;
        }

        private void SeedLatestFrameTimestamp()
        {
            var timingCount = FrameTimingManager.GetLatestTimings(LatestFrameTimingCount, latestFrameTimings);
            if (timingCount == 0 || !IsFinite(latestFrameTimings[0].frameStartTimestamp)) return;

            latestFrameStartTimestamp = latestFrameTimings[0].frameStartTimestamp;
            hasLatestFrameStartTimestamp = true;
        }

        private void RecordResidentMemory(ProcessMetricsSample processMetrics)
        {
            if (!processMetrics.IsResidentMemoryAvailable) return;
            if (!isPeakWorkingSetAvailable || processMetrics.ResidentMemoryBytes > peakWorkingSetBytes) peakWorkingSetBytes = processMetrics.ResidentMemoryBytes;
            isPeakWorkingSetAvailable = true;
        }

        private bool TryCalculateProcessCpuUtilization(ProcessMetricsSample finalProcessMetrics, double elapsedSeconds, out double processCpuUtilizationPercent)
        {
            processCpuUtilizationPercent = 0d;
            if (!isInitialTotalProcessorTimeAvailable || !finalProcessMetrics.IsTotalProcessorTimeAvailable || logicalProcessorCount == 0) return false;
            if (!IsFiniteNonNegative(finalProcessMetrics.TotalProcessorTimeSeconds) || finalProcessMetrics.TotalProcessorTimeSeconds < initialTotalProcessorTimeSeconds) return false;

            var processorTimeDeltaSeconds = finalProcessMetrics.TotalProcessorTimeSeconds - initialTotalProcessorTimeSeconds;
            var normalizedElapsedSeconds = elapsedSeconds * logicalProcessorCount;
            if (!IsFiniteNonNegative(processorTimeDeltaSeconds) || !IsPositiveFinite(normalizedElapsedSeconds)) return false;

            var utilizationPercent = processorTimeDeltaSeconds / normalizedElapsedSeconds * PercentScale;
            if (!IsFiniteNonNegative(utilizationPercent)) return false;

            processCpuUtilizationPercent = Math.Min(PercentScale, Math.Max(0d, utilizationPercent));
            return true;
        }

        private static void AccumulateFrameTime(double frameTimeMilliseconds, ref double totalFrameTimeMilliseconds, ref int sampleCount)
        {
            if (!IsPositiveFinite(frameTimeMilliseconds) || sampleCount == int.MaxValue) return;

            var updatedTotalFrameTimeMilliseconds = totalFrameTimeMilliseconds + frameTimeMilliseconds;
            if (!IsPositiveFinite(updatedTotalFrameTimeMilliseconds)) return;

            totalFrameTimeMilliseconds = updatedTotalFrameTimeMilliseconds;
            sampleCount++;
        }

        private static bool TryCalculateAverageFrameTime(double totalFrameTimeMilliseconds, int sampleCount, out double averageFrameTimeMilliseconds)
        {
            averageFrameTimeMilliseconds = 0d;
            if (sampleCount <= 0 || !IsPositiveFinite(totalFrameTimeMilliseconds)) return false;

            averageFrameTimeMilliseconds = totalFrameTimeMilliseconds / sampleCount;
            if (IsPositiveFinite(averageFrameTimeMilliseconds)) return true;

            averageFrameTimeMilliseconds = 0d;
            return false;
        }

        private static bool IsPositiveFinite(double value)
        {
            return value > 0d && IsFinite(value);
        }

        private static bool IsFiniteNonNegative(double value)
        {
            return value >= 0d && IsFinite(value);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
