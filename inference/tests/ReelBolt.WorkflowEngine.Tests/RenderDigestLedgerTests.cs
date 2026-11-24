using System;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Agents.Tools;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// SEC-8: the unchanged-render guard's ledger keeps at most 1024 executions and evicts the least
/// recently used one. Exercised through the only public surface, <c>RecordAndCompare</c>.
/// </summary>
public class RenderDigestLedgerTests
{
    private const int Capacity = 1024;

    [Fact]
    public void Ledger_evicts_the_least_recently_used_execution_once_it_holds_1024()
    {
        var ledger = new ReactRemotionSandboxTools.RenderDigestLedger();
        Guid[] executions = new Guid[Capacity + 1];
        for (int i = 0; i < executions.Length; i++)
            executions[i] = Guid.NewGuid();

        ledger.RecordAndCompare(executions[0], 0, "A").Should().BeFalse();
        for (int i = 1; i < Capacity; i++)
            ledger.RecordAndCompare(executions[i], 0, "X").Should().BeFalse();

        // Exactly 1024 executions are held: execution 0 is still there, so iteration 1 with the same digest is unchanged.
        ledger.RecordAndCompare(executions[0], 1, "A").Should().BeTrue();

        // A 1025th execution evicts the least recently used one (execution 0 was just touched, so execution 1 goes).
        ledger.RecordAndCompare(executions[Capacity], 0, "Z").Should().BeFalse();
        ledger.RecordAndCompare(executions[1], 1, "X").Should().BeFalse();
        ledger.RecordAndCompare(executions[0], 2, "A").Should().BeTrue();
    }

    [Fact]
    public void Ledger_forgets_an_execution_evicted_at_capacity()
    {
        var ledger = new ReactRemotionSandboxTools.RenderDigestLedger();
        Guid oldest = Guid.NewGuid();
        ledger.RecordAndCompare(oldest, 0, "A").Should().BeFalse();

        for (int i = 0; i < Capacity; i++)
            ledger.RecordAndCompare(Guid.NewGuid(), 0, "X");

        // The oldest execution was evicted, so its earlier digest is gone and nothing is reported unchanged.
        ledger.RecordAndCompare(oldest, 1, "A").Should().BeFalse();
    }
}
