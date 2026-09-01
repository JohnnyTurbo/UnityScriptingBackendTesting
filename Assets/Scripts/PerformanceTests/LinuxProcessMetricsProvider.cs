#if UNITY_STANDALONE_LINUX || UNITY_EDITOR_LINUX
using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace CoreCLRTest.PerformanceTests
{
    internal sealed class LinuxProcessMetricsProvider : IProcessMetricsProvider
    {
        private const string LibcLibrary = "libc";
        private const string ProcessStatmPath = "/proc/self/statm";
        private const int ClockProcessCpuTimeId = 2;
        private const int ResidentPageFieldIndex = 1;
        private const int MinimumStatmFieldCount = ResidentPageFieldIndex + 1;
        private const double NanosecondsPerSecond = 1_000_000_000d;
        private const long MaximumNanoseconds = 999_999_999L;

        private static readonly char[] StatmSeparators = { ' ', '\t', '\r', '\n' };

        /// <inheritdoc />
        public bool TryGetLogicalProcessorCount(out uint logicalProcessorCount)
        {
            logicalProcessorCount = 0;
            try
            {
                var onlineLogicalProcessorCount = GetOnlineProcessorCount();
                if (onlineLogicalProcessorCount <= 0) return false;

                logicalProcessorCount = (uint)onlineLogicalProcessorCount;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <inheritdoc />
        public ProcessMetricsSample Capture()
        {
            var isTotalProcessorTimeAvailable = TryCaptureTotalProcessorTime(out var totalProcessorTimeSeconds);
            var isResidentMemoryAvailable = TryCaptureResidentMemory(out var residentMemoryBytes);
            return new ProcessMetricsSample(isTotalProcessorTimeAvailable, isResidentMemoryAvailable, totalProcessorTimeSeconds, residentMemoryBytes);
        }

        private static bool TryCaptureTotalProcessorTime(out double totalProcessorTimeSeconds)
        {
            totalProcessorTimeSeconds = 0d;
            try
            {
                if (ClockGetTime(ClockProcessCpuTimeId, out var processCpuTime) != 0) return false;
                if (processCpuTime.Seconds < 0 || processCpuTime.Nanoseconds < 0 || processCpuTime.Nanoseconds > MaximumNanoseconds) return false;

                totalProcessorTimeSeconds = processCpuTime.Seconds + processCpuTime.Nanoseconds / NanosecondsPerSecond;
                return totalProcessorTimeSeconds >= 0d && !double.IsNaN(totalProcessorTimeSeconds) && !double.IsInfinity(totalProcessorTimeSeconds);
            }
            catch (Exception)
            {
                totalProcessorTimeSeconds = 0d;
                return false;
            }
        }

        private static bool TryCaptureResidentMemory(out ulong residentMemoryBytes)
        {
            residentMemoryBytes = 0;
            try
            {
                var statmContents = File.ReadAllText(ProcessStatmPath);
                var fields = statmContents.Split(StatmSeparators, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < MinimumStatmFieldCount) return false;
                if (!ulong.TryParse(fields[ResidentPageFieldIndex], NumberStyles.None, CultureInfo.InvariantCulture, out var residentPageCount)) return false;

                var pageSize = GetPageSize();
                if (pageSize <= 0) return false;

                var pageSizeBytes = (ulong)pageSize;
                if (residentPageCount > ulong.MaxValue / pageSizeBytes) return false;

                residentMemoryBytes = residentPageCount * pageSizeBytes;
                return true;
            }
            catch (Exception)
            {
                residentMemoryBytes = 0;
                return false;
            }
        }

        [DllImport(LibcLibrary, EntryPoint = "clock_gettime")]
        private static extern int ClockGetTime(int clockId, out Timespec time);

        [DllImport(LibcLibrary, EntryPoint = "getpagesize")]
        private static extern int GetPageSize();

        [DllImport(LibcLibrary, EntryPoint = "get_nprocs")]
        private static extern int GetOnlineProcessorCount();

        [StructLayout(LayoutKind.Sequential)]
        private struct Timespec
        {
            internal long Seconds;
            internal long Nanoseconds;
        }
    }
}
#endif
