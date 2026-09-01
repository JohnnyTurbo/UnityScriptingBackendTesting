#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System;
using System.Runtime.InteropServices;

namespace CoreCLRTest.PerformanceTests
{
    internal sealed class WindowsProcessMetricsProvider : IProcessMetricsProvider
    {
        private const ushort AllProcessorGroups = 0xFFFF;
        private const double FileTimeTicksPerSecond = 10_000_000d;

        private readonly IntPtr processHandle;
        private readonly bool isProcessHandleAvailable;

        internal WindowsProcessMetricsProvider()
        {
            try
            {
                processHandle = GetCurrentProcess();
                isProcessHandleAvailable = processHandle != IntPtr.Zero;
            }
            catch (Exception)
            {
                processHandle = IntPtr.Zero;
                isProcessHandleAvailable = false;
            }
        }

        /// <inheritdoc />
        public bool TryGetLogicalProcessorCount(out uint logicalProcessorCount)
        {
            logicalProcessorCount = 0;
            try
            {
                var activeLogicalProcessorCount = GetActiveProcessorCount(AllProcessorGroups);
                if (activeLogicalProcessorCount == 0) return false;

                logicalProcessorCount = activeLogicalProcessorCount;
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
            if (!isProcessHandleAvailable) return default;

            var isTotalProcessorTimeAvailable = TryCaptureTotalProcessorTime(out var totalProcessorTimeSeconds);
            var isResidentMemoryAvailable = TryCaptureResidentMemory(out var residentMemoryBytes);
            return new ProcessMetricsSample(isTotalProcessorTimeAvailable, isResidentMemoryAvailable, totalProcessorTimeSeconds, residentMemoryBytes);
        }

        private bool TryCaptureTotalProcessorTime(out double totalProcessorTimeSeconds)
        {
            totalProcessorTimeSeconds = 0d;
            try
            {
                if (!GetProcessTimes(processHandle, out _, out _, out var kernelFileTime, out var userFileTime)) return false;

                var kernelTimeTicks = ToUInt64(kernelFileTime);
                var userTimeTicks = ToUInt64(userFileTime);
                if (kernelTimeTicks > ulong.MaxValue - userTimeTicks) return false;

                totalProcessorTimeSeconds = (kernelTimeTicks + userTimeTicks) / FileTimeTicksPerSecond;
                return IsFiniteNonNegative(totalProcessorTimeSeconds);
            }
            catch (Exception)
            {
                totalProcessorTimeSeconds = 0d;
                return false;
            }
        }

        private bool TryCaptureResidentMemory(out ulong residentMemoryBytes)
        {
            residentMemoryBytes = 0;
            try
            {
                var memoryCounters = new ProcessMemoryCounters
                {
                    Size = (uint)Marshal.SizeOf<ProcessMemoryCounters>()
                };
                if (!K32GetProcessMemoryInfo(processHandle, out memoryCounters, memoryCounters.Size)) return false;

                residentMemoryBytes = memoryCounters.WorkingSetSize.ToUInt64();
                return true;
            }
            catch (Exception)
            {
                residentMemoryBytes = 0;
                return false;
            }
        }

        private static ulong ToUInt64(FileTime fileTime)
        {
            return ((ulong)fileTime.HighDateTime << 32) | fileTime.LowDateTime;
        }

        private static bool IsFiniteNonNegative(double value)
        {
            return value >= 0d && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessTimes(IntPtr process, out FileTime creationTime, out FileTime exitTime, out FileTime kernelTime, out FileTime userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetActiveProcessorCount(ushort groupNumber);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool K32GetProcessMemoryInfo(IntPtr process, out ProcessMemoryCounters processMemoryCounters, uint size);

        [StructLayout(LayoutKind.Sequential)]
        private struct FileTime
        {
            internal uint LowDateTime;
            internal uint HighDateTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemoryCounters
        {
            internal uint Size;
            internal uint PageFaultCount;
            internal UIntPtr PeakWorkingSetSize;
            internal UIntPtr WorkingSetSize;
            internal UIntPtr QuotaPeakPagedPoolUsage;
            internal UIntPtr QuotaPagedPoolUsage;
            internal UIntPtr QuotaPeakNonPagedPoolUsage;
            internal UIntPtr QuotaNonPagedPoolUsage;
            internal UIntPtr PagefileUsage;
            internal UIntPtr PeakPagefileUsage;
        }
    }
}
#endif
