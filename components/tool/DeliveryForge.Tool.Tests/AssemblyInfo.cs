using Xunit;
// Native process-affinity fixture lease is process-wide; no concurrent test body
// may observe its temporary two-bit restriction/restoration.
[assembly: CollectionBehavior(DisableTestParallelization=true)]
