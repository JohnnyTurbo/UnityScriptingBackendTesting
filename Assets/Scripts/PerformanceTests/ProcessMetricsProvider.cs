namespace CoreCLRTest.PerformanceTests
{
    internal interface IProcessMetricsProvider
    {
        /// <summary>Attempts to retrieve the number of logical processors available to the process.</summary>
        bool TryGetLogicalProcessorCount(out uint logicalProcessorCount);

        /// <summary>Captures the currently available cumulative process CPU time and resident memory.</summary>
        ProcessMetricsSample Capture();
    }

    internal readonly struct ProcessMetricsSample
    {
        internal bool IsTotalProcessorTimeAvailable { get; }
        internal bool IsResidentMemoryAvailable { get; }
        internal double TotalProcessorTimeSeconds { get; }
        internal ulong ResidentMemoryBytes { get; }

        internal ProcessMetricsSample(bool isTotalProcessorTimeAvailable, bool isResidentMemoryAvailable, double totalProcessorTimeSeconds, ulong residentMemoryBytes)
        {
            IsTotalProcessorTimeAvailable = isTotalProcessorTimeAvailable;
            IsResidentMemoryAvailable = isResidentMemoryAvailable;
            TotalProcessorTimeSeconds = totalProcessorTimeSeconds;
            ResidentMemoryBytes = residentMemoryBytes;
        }
    }

    internal static class ProcessMetricsProviderFactory
    {
        internal static IProcessMetricsProvider Create()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            return new WindowsProcessMetricsProvider();
#elif UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
            return new MacOSProcessMetricsProvider();
#elif UNITY_STANDALONE_LINUX || UNITY_EDITOR_LINUX
            return new LinuxProcessMetricsProvider();
#else
            return new UnavailableProcessMetricsProvider();
#endif
        }
    }

    internal sealed class UnavailableProcessMetricsProvider : IProcessMetricsProvider
    {
        /// <inheritdoc />
        public bool TryGetLogicalProcessorCount(out uint logicalProcessorCount)
        {
            logicalProcessorCount = 0;
            return false;
        }

        /// <inheritdoc />
        public ProcessMetricsSample Capture()
        {
            return default;
        }
    }
}
