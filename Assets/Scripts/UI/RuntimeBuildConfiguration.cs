using Unity.Burst;

namespace CoreCLRTest.UI
{
    [BurstCompile]
    internal static class RuntimeBuildConfiguration
    {
        private const string CoreClrBackendName = "CoreCLR";
        private const string Il2CppBackendName = "IL2CPP";
        private const string MonoBackendName = "Mono";
        private const string UnknownBackendName = "Unknown";
        private const string BurstEnabledStateName = "Enabled";
        private const string BurstDisabledStateName = "Disabled";
        private const string SummaryFormat = "{0} | Burst: {1}";

        internal static string GetSummaryText()
        {
            var scriptingBackendName = GetScriptingBackendName();
            var burstStateName = GetBurstStateName();
            return string.Format(SummaryFormat, scriptingBackendName, burstStateName);
        }

        private static string GetScriptingBackendName()
        {
#if ENABLE_CORECLR
            return CoreClrBackendName;
#elif ENABLE_IL2CPP
            return Il2CppBackendName;
#elif ENABLE_MONO
            return MonoBackendName;
#else
            return UnknownBackendName;
#endif
        }

        private static string GetBurstStateName()
        {
            return BurstCompiler.IsEnabled ? BurstEnabledStateName : BurstDisabledStateName;
        }

        [BurstCompile]
        private static class BurstRuntimeProbe
        {
            [BurstCompile(CompileSynchronously = true)]
            internal static bool IsBurstEnabled()
            {
                var isBurstEnabled = true;
                SetBurstDisabledForManagedExecution(ref isBurstEnabled);
                return isBurstEnabled;
            }

            [BurstDiscard]
            private static void SetBurstDisabledForManagedExecution(ref bool isBurstEnabled)
            {
                isBurstEnabled = false;
            }
        }
    }
}
