using Xunit.v3;
using Xunit.Sdk;

// These fixtures share process-wide coordinators and observe worker lifetimes.
[assembly: Parallelization(Mode = ParallelMode.None)]
