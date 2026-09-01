using System;
using Unity.Entities;

namespace TMG.CoreCLRTest
{
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial class ConfigurePhysicsFrequencySystem : SystemBase
    {
        private const int MinimumPhysicsUpdatesPerSecond = 1;
        private const int MaximumPhysicsUpdatesPerSecond = 60;

        private FixedStepSimulationSystemGroup fixedStepSimulationSystemGroup;

        protected override void OnCreate()
        {
            fixedStepSimulationSystemGroup = World.GetExistingSystemManaged<FixedStepSimulationSystemGroup>();
            ConfigureForTargetFrameRate(MaximumPhysicsUpdatesPerSecond);
        }

        protected override void OnUpdate()
        {
        }

        /// <summary>
        /// Matches the physics update rate to targets below 60 FPS and otherwise uses 60 Hz.
        /// </summary>
        public void ConfigureForTargetFrameRate(int targetFrameRate)
        {
            var physicsUpdatesPerSecond = Math.Min(Math.Max(targetFrameRate, MinimumPhysicsUpdatesPerSecond), MaximumPhysicsUpdatesPerSecond);
            fixedStepSimulationSystemGroup.Timestep = 1f / physicsUpdatesPerSecond;
        }
    }
}
