using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Groups the tests that redirect the status file through <see cref="Hypertree.Status.StatusFile"/>'s
/// <c>OverrideDirectory</c> — a <b>process-global static</b>. xUnit parallelises across test classes by
/// default, so without this each such class ran concurrently and stomped the others' redirect: one class
/// resetting the override to null (or to its own temp dir) mid-test made another read or write the wrong
/// directory, which surfaced as the occasional "leaves no temp file behind" failure.
///
/// Everything in this collection runs sequentially (a collection is never parallelised internally), and
/// <c>DisableParallelization</c> also keeps it from overlapping other collections — so the shared static is
/// only ever touched by one test at a time. No fixture is needed; this is purely a scheduling barrier.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StatusFileCollection
{
    public const string Name = "StatusFile (serial)";
}
