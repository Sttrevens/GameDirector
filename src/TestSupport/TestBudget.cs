// Linked only into test assemblies. The ONE declared budget for a hang guard: a
// wait for an event that must happen anyway — a worker entering its install, a
// gated generator being reached, a child process writing its descriptor. It
// bounds how long a broken test may take to fail; a passing test resolves on the
// event and never pays it. It is therefore set far above any plausible load
// slowdown (xUnit 2.7.0 without a runner json runs every collection on one
// shared 10-thread MaxConcurrencySyncContext, so a continuation can queue behind
// the whole suite) and it is NEVER a speed assertion. Timing that is itself under
// test keeps its own deliberate budget and marks that line
// "// timing-under-test: <why>"; TestBudgetGuardTests enforces both rules.
public static class TestBudget
{
    public static readonly TimeSpan Hang = TimeSpan.FromSeconds(30);
}
