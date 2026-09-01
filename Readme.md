# About this Project
The purpose of this project is to test CPU performance of an application developed with Unity DOTS using 3 different scripting backends (Mono, IL2CPP, Experimental CoreCLR). Further, the scripting backends can also be tested with burst enabled for those that support it (Mono, IL2CPP). The primary metric of comparison is the number of entities that can be simulated at a target frame rate specified in the main menu (default 60 fps).

# The Tests
There are three tests available in the testing suite: Random Movement, Plinko Physics, and A* Pathfinding. When running a given test, the number of entities being simulated will increase and decrease automatically to hone in on the target frame rate. Once the target frame rate is sustained for a short period of time, the performance metrics will be recorded and the next test will proceed automatically.

## Random Movement
This test will spawn entities in a simulation area and they will choose a random destination in the simulation area. They will linearly travel to the destination and pick a new destination once reached. Core logic is implemented via burst compatible `IJobEntity` jobs. The goal of this test is to see how many entities can be simulated doing extremely simple logic to be used as a baseline. In my testing, it seemed as though this test was primarily GPU-bound and the CPU could likely simulate more entities at the target frame rates. Because of this, the different scripting backends produced similar results.

## Plinko Physics
This test will simulate a large amount of entities rolling down a sloped track and colliding with moving pins. This goal of this test is to tax the DOTS physics system as frequent physics interactions are taxing on the CPU. In my testing this test did a better job at taxing the CPU and I was starting to see a greater variation in entity counts for the different scripting backends. Note that if you configure to run this test at a target frame rate of below 60 fps, the physics update rate will be lowered to match the target frame rate. This is because the physics update rate is 60hz by default and running the game at frame rates below 60 fps will cause the game to slow down as the physics system attempts to perform multiple physics steps for a single frame rendered.

## A* Pathfinding
This test is similar to the random movement test, however instead of linearly traveling from one point to another, entities will need to navigate a maze using A* pathfinding. This also does a better job at taxing the CPU compared to the linear random movement test. Although the walls remain in place during the test, each entity will constantly recalculate its path to better keep a consistent load on the CPU. Despite this, the frame rate of this test is still quite turbulent and it can take a while for the test to find an entity count that satisfies the target frame rate.

# Configuration Options
The following configuration options are available in the main menu:
- Render Resolution - Resolution at which the application should be rendered.
- Fullscreen - Toggle to swap between fullscreen and windowed modes
- Target Frame Rate - This is effectively the "goal" frame rate that the application is aiming to achieve. This does not set the `Application.aargetFrameRate`, as the frame rate of the application remains unlocked. Each test will add/remove entities until the application is running stably at this target frame rate value.
- Frame Rate Delta - This value creates a wider target frame rate for the tests. This means if the Target Frame Rate was set to 60 fps and the Frame Rate Delta was set to 10, then the tests would pass if the sable frame rate is anywhere between 50 and 70 fps. Larger values will complete the tests quicker, while smaller values will take longer to find a target frame rate, but will provide more accurate results.
- Performance Tests - Toggle which tests should be executed when pressing the "Run Tests" button.

# Performance Metrics
Once the tests are complete, the following metrics will be shown:
(Note: not all metrics are available on all platforms)
- Test Name - Name of the test associated with this row of results
- Entity Count - The number of entities simulated at the target frame rate. This is the primary metric of comparison between the different scripting backends.
- Process CPU - When the test is in a stable state, the CPU is queried to determine the load percentage of the application
- CPU Frame Time - Frame time of the application while simulating at the target frame rate. The percentage is compared with the frame time of the Target Frame Rate of the application. This metric can be used to validate the results - if this percentage is around 100%, we can be confident that the application achieved a stable state at the target frame rate. If this value is not close to 100% this could indicate something went wrong with the test and the results are a bit away from the target frame rate.
- GPU Frame Time - Frame time for the GPU to render a frame. Higher values here indicate that the test is GPU bound.
- Peak App Memory - Displays the amount of memory being used by the application when simulating at the target frame rate. In my testing, I did not see much variance on this metric, but it was still interesting to keep an eye on.

# Results
Below you will find the results of my testing. Each table shows the number of entities being simulated at a target frame rate of 60 fps.

You may notice that the CoreCLR scripting backend was only executed with Burst compilation disabled. This is because at the time of testing in Unity 6000.7.0a4, the experimental desktop player does not support Burst compilation. I would expect Burst compilation to come at a later time as I do see some verbiage in the CoreCLR documentation to indicate that Burst support is intended.
## Windows Desktop
CPU: AMD 5950X (16C/32T)
GPU: RTX 4090 (24GB)
RAM: 64GB

|                     | Mono    | IL2CPP  | CoreCLR | Mono + Burst | IL2CPP + Burst |
|---------------------|---------|---------|---------|--------------|----------------|
| **Random Movement** | 176,128 | 176,128 | 188,416 | 196,608      | 172,032        |
| **Plinko Physics**  | 2,175   | 12,524  | 15,360  | 27,648       | 27,392         |
| **A* Pathfinding**  | 4,225   | 6,144   | 7,745   | 16,384       | 11,872         |
## MacBook Pro
MacBook Pro - M3 Pro
CPU: 12-Core
GPU: 18-Core
RAM: 36 GB

|                     | Mono   | IL2CPP | CoreCLR | Mono + Burst | IL2CPP + Burst |
| ------------------- | ------ | ------ | ------- | ------------ | -------------- |
| **Random Movement** | 43,008 | 43,008 | 43,008  | 43,008       | 43,008         |
| **Plinko Physics**  | 2,924  | 18,431 | 22,582  | 32,768       | 32,768         |
| **A* Pathfinding**  | 2,608  | 7,104  | 7,104   | 7,058        | 7,104          |
## SteamDeck
CPU: Zen 2 (4C/8T)
GPU: 8 RDNA 2 CUs
RAM: 16GB

|                     | Mono   | IL2CPP | CoreCLR | Mono + Burst | IL2CPP + Burst |
| ------------------- | ------ | ------ | ------- | ------------ | -------------- |
| **Random Movement** | 25,600 | 26,624 | 26,624  | 27,684       | 27,684         |
| **Plinko Physics**  | 1,408  | 6,788  | 7,785   | 15,804       | 13,640         |
| **A* Pathfinding**  | 1,208  | 2,128  | 2,336   | 4,224        | 4,225          |
# AI and Sponsorship Disclosure
This testing suite was developed using Bezi, an AI Assistant with Unity integration. The primary LLM used was GPT 5.6 SOL on "Extra High."

Linked below is the video I created to showcase this testing. This video includes a paid sponsorship from Bezi.