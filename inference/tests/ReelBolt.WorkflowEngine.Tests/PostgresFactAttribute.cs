#nullable enable
using System;
using System.Linq;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>A <see cref="FactAttribute"/> that is skipped unless a real Postgres is configured (<see cref="PostgresTestEnvironment.SkipReason"/>).</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (PostgresTestEnvironment.SkipReason is { } reason) Skip = reason;
    }
}

/// <summary>Theory counterpart of <see cref="PostgresFactAttribute"/>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute()
    {
        if (PostgresTestEnvironment.SkipReason is { } reason) Skip = reason;
    }
}

/// <summary>
/// Marks a test class whose inherited <see cref="DatabaseFactAttribute"/> scenarios run on a real Postgres, so they
/// are skipped there while the same scenarios on another subclass (Sqlite) still run.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class RequiresPostgresAttribute : Attribute
{
}

/// <summary>
/// A <see cref="FactAttribute"/> for a scenario a base class shares between database subclasses: it runs everywhere,
/// except on a subclass marked <see cref="RequiresPostgresAttribute"/> while <c>REELBOLT_TEST_PG</c> is unset, where
/// it is skipped. A plain attribute cannot do this: it is evaluated once per method, not per test class.
/// </summary>
[XunitTestCaseDiscoverer("ReelBolt.WorkflowEngine.Tests.DatabaseFactDiscoverer", "ReelBolt.WorkflowEngine.Tests")]
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DatabaseFactAttribute : FactAttribute
{
}

/// <summary>Discovers <see cref="DatabaseFactAttribute"/> tests, skipping them per test class (see there).</summary>
public sealed class DatabaseFactDiscoverer(IMessageSink diagnosticMessageSink) : FactDiscoverer(diagnosticMessageSink)
{
    protected override IXunitTestCase CreateTestCase(
        ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod, IAttributeInfo factAttribute)
    {
        bool needsPostgres = testMethod.TestClass.Class.GetCustomAttributes(typeof(RequiresPostgresAttribute)).Any();
        if (needsPostgres && PostgresTestEnvironment.SkipReason is { } reason)
        {
            return new XunitSkippedDataRowTestCase(
                DiagnosticMessageSink, discoveryOptions.MethodDisplayOrDefault(), discoveryOptions.MethodDisplayOptionsOrDefault(),
                testMethod, reason);
        }

        return base.CreateTestCase(discoveryOptions, testMethod, factAttribute);
    }
}
