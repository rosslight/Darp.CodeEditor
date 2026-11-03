// The runner tests check deadlines and kills. They run one class at a time, so they compete for the CPU neither with each
// other nor with the NativeAOT publish of the test host.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
