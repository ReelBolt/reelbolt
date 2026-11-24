#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>The HTTP answers of a refused hold and the closing of an assistant/MCP turn.</summary>
public class NonWorkflowChargeResultsTests
{
    [Fact]
    public void Refusal_IsA402_OnlyForInsufficientCredits()
    {
        ObjectResult refused = NonWorkflowChargeResults.Refusal(
            new ChargeHold(false, null, 5_000, 2_000, CreditLedgerErrors.InsufficientCredits), "suggestion");

        refused.StatusCode.Should().Be(StatusCodes.Status402PaymentRequired);
        JsonSerializer.SerializeToElement(refused.Value).GetProperty("code").GetString().Should().Be("INSUFFICIENT_CREDITS");
    }

    [Theory]
    [InlineData(NonWorkflowChargeCodes.GateUnavailable)]
    [InlineData(CreditLedgerErrors.InvalidAmount)]
    [InlineData(CreditLedgerErrors.ReservationNotFound)]
    [InlineData(null)]
    public void Refusal_IsA503GateUnavailable_ForEveryOtherCode(string? code)
    {
        ObjectResult refused = NonWorkflowChargeResults.Refusal(new ChargeHold(false, null, 5_000, 2_000, code), "picture edit");

        refused.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        JsonSerializer.SerializeToElement(refused.Value).GetProperty("code").GetString().Should().Be("GATE_UNAVAILABLE");
    }

    [Fact]
    public async Task CloseTurn_RetriesAFailedSettlementOnce_ThenLogsAnErrorWithTheOrganizationAndRequest()
    {
        var charges = new FakeNonWorkflowCharges { FailTurnSettlements = 2 };
        Guid org = Guid.NewGuid();
        Guid request = Guid.NewGuid();
        TurnHold turn = new(true, Guid.NewGuid(), 5_000, 0, null) { OrganizationId = org, Source = UsageSource.Assistant, RequestId = request };
        var logger = new ListLogger();

        await NonWorkflowChargeResults.CloseTurnAsync(charges, turn, 3_000, logger);

        charges.TurnSettlements.Should().HaveCount(2);
        (LogLevel Level, string Message) error = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error).Subject;
        error.Message.Should().Contain(org.ToString()).And.Contain(request.ToString());
        charges.UnsettledRecords.Should().ContainSingle().Which.MeteredMillis.Should().Be(3_000,
            "the metered usage is recorded on the open hold, so the stale sweep charges it (T-026)");
    }

    [Fact]
    public async Task CloseTurn_SettlesOnce_WhenTheFirstAttemptWorks()
    {
        var charges = new FakeNonWorkflowCharges();
        TurnHold turn = new(true, Guid.NewGuid(), 5_000, 0, null) { OrganizationId = Guid.NewGuid(), Source = UsageSource.Mcp, RequestId = Guid.NewGuid() };
        var logger = new ListLogger();

        await NonWorkflowChargeResults.CloseTurnAsync(charges, turn, 3_000, logger);

        charges.TurnSettlements.Should().ContainSingle().Which.MeteredMillis.Should().Be(3_000);
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
