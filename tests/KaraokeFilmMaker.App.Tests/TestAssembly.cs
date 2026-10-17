using Xunit;

// WPF's pack resource cache is process-wide, even with one STA dispatcher per test.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
