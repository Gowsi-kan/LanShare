using Xunit;

// Integration tests use real sockets, TLS and disks and measure timing: run them one after another.
[assembly: CollectionBehavior(DisableTestParallelization = true)]