using UnityEngine;

namespace CoreCLRTest.UI
{
    [CreateAssetMenu(
        fileName = "PerformanceTestMenuSettings",
        menuName = "CoreCLR Test/Performance Test Menu Settings")]
    public sealed class PerformanceTestMenuSettings : ScriptableObject
    {
        private const string MissingDescription = "No description has been configured for this performance test.";
        private const string DefaultTargetFrameRateDescription = "Configures the target FPS goal used by the performance tests. This value does not cap the application's frame rate.";
        private const string DefaultFrameRateDeltaDescription = "Configures the acceptable range around the target FPS. Lower values provide more accurate results, but tests may take longer to complete.";

        [SerializeField]
        [TextArea(2, 4)]
        private string targetFrameRateDescription = DefaultTargetFrameRateDescription;

        [SerializeField]
        [TextArea(2, 4)]
        private string frameRateDeltaDescription = DefaultFrameRateDeltaDescription;

        [SerializeField]
        [TextArea(2, 4)]
        private string performanceTest1Description =
            "Primary performance test reserved for the first benchmark scenario.";

        [SerializeField]
        [TextArea(2, 4)]
        private string performanceTest2Description =
            "Spawns Plinko balls in configurable batches, simulates and recycles them with Unity Physics, and measures the maximum entity count that sustains the configured target FPS.";

        [SerializeField]
        [TextArea(2, 4)]
        private string performanceTest3Description =
            "Additional performance test reserved for the third benchmark scenario.";

        internal string GetTargetFrameRateDescription()
        {
            return string.IsNullOrWhiteSpace(targetFrameRateDescription) ? DefaultTargetFrameRateDescription : targetFrameRateDescription;
        }


        internal string GetFrameRateDeltaDescription()
        {
            return string.IsNullOrWhiteSpace(frameRateDeltaDescription) ? DefaultFrameRateDeltaDescription : frameRateDeltaDescription;
        }


        internal string GetPerformanceTestDescription(int testIndex)
        {
            string description = testIndex switch
            {
                0 => performanceTest1Description,
                1 => performanceTest2Description,
                2 => performanceTest3Description,
                _ => string.Empty
            };

            return string.IsNullOrWhiteSpace(description) ? MissingDescription : description;
        }
    }
}
