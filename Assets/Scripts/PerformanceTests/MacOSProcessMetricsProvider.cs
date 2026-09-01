#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
using System;
using System.Runtime.InteropServices;

namespace CoreCLRTest.PerformanceTests
{
    internal sealed class MacOSProcessMetricsProvider : IProcessMetricsProvider
    {
        private const string LibProcLibrary = "libproc.dylib";
        private const string LibSystemLibrary = "libSystem.B.dylib";
        private const string LogicalProcessorCountName = "hw.logicalcpu";
        private const int RUsageInfoV0Flavor = 0;
        private const int UuidByteCount = 16;
        private const double NanosecondsPerSecond = 1_000_000_000d;

        /// <inheritdoc />
        public bool TryGetLogicalProcessorCount(out uint logicalProcessorCount)
        {
            logicalProcessorCount = 0;
            try
            {
                var nativeLogicalProcessorCount = 0;
                var valueSize = new UIntPtr(sizeof(int));
                if (SysctlByName(LogicalProcessorCountName, ref nativeLogicalProcessorCount, ref valueSize, IntPtr.Zero, UIntPtr.Zero) != 0) return false;
                if (valueSize.ToUInt64() != sizeof(int) || nativeLogicalProcessorCount <= 0) return false;

                logicalProcessorCount = (uint)nativeLogicalProcessorCount;
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
            try
            {
                var processId = GetProcessId();
                if (processId <= 0) return default;

                var resourceUsage = new RUsageInfoV0
                {
                    Uuid = new byte[UuidByteCount]
                };
                if (ProcPidRUsage(processId, RUsageInfoV0Flavor, ref resourceUsage) != 0) return default;

                var isTotalProcessorTimeAvailable = TryConvertProcessorTime(resourceUsage.UserTimeNanoseconds, resourceUsage.SystemTimeNanoseconds, out var totalProcessorTimeSeconds);
                var residentMemoryBytes = resourceUsage.ResidentSizeBytes;
                return new ProcessMetricsSample(isTotalProcessorTimeAvailable, true, totalProcessorTimeSeconds, residentMemoryBytes);
            }
            catch (Exception)
            {
                return default;
            }
        }

        private static bool TryConvertProcessorTime(ulong userTimeNanoseconds, ulong systemTimeNanoseconds, out double totalProcessorTimeSeconds)
        {
            totalProcessorTimeSeconds = 0d;
            if (userTimeNanoseconds > ulong.MaxValue - systemTimeNanoseconds) return false;

            totalProcessorTimeSeconds = (userTimeNanoseconds + systemTimeNanoseconds) / NanosecondsPerSecond;
            return totalProcessorTimeSeconds >= 0d && !double.IsNaN(totalProcessorTimeSeconds) && !double.IsInfinity(totalProcessorTimeSeconds);
        }

        [DllImport(LibSystemLibrary, EntryPoint = "getpid")]
        private static extern int GetProcessId();

        [DllImport(LibProcLibrary, EntryPoint = "proc_pid_rusage")]
        private static extern int ProcPidRUsage(int processId, int flavor, ref RUsageInfoV0 resourceUsage);

        [DllImport(LibSystemLibrary, EntryPoint = "sysctlbyname", CharSet = CharSet.Ansi)]
        private static extern int SysctlByName(string name, ref int oldValue, ref UIntPtr oldValueSize, IntPtr newValue, UIntPtr newValueSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct RUsageInfoV0
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = UuidByteCount)]
            internal byte[] Uuid;
            internal ulong UserTimeNanoseconds;
            internal ulong SystemTimeNanoseconds;
            internal ulong PackageIdleWakeups;
            internal ulong InterruptWakeups;
            internal ulong PageIns;
            internal ulong WiredSizeBytes;
            internal ulong ResidentSizeBytes;
            internal ulong PhysicalFootprintBytes;
            internal ulong ProcessStartAbsoluteTime;
            internal ulong ProcessExitAbsoluteTime;
        }
    }
}
#endif
