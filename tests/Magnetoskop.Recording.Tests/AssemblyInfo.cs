using Xunit;

// Real FFmpeg integration cases share process and disk resources. Serial execution
// keeps timing/overflow assertions deterministic and avoids codec load interference.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
