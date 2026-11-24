using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// A nullable tool parameter with no default is REQUIRED in the schema, and the binder throws before the tool body runs
/// when the model leaves it out (how "create a workflow" failed with an empty "Function failed."). Every nullable parameter
/// of every assistant tool therefore carries a default, so omitted means exactly what an explicit null means.
/// </summary>
public class AssistantToolOptionalParameterDriftTests
{
    [Fact]
    public void No_assistant_tool_has_a_nullable_parameter_without_a_default()
    {
        using var db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Guid user = Guid.NewGuid();
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(user));
        var nullability = new NullabilityInfoContext();

        string[] offenders = [.. provider.GetTools(new AssistantToolContext(user))
            .SelectMany(tool => (tool.UnderlyingMethod?.GetParameters() ?? [])
                .Where(p => !p.HasDefaultValue && p.ParameterType != typeof(CancellationToken)
                    && (Nullable.GetUnderlyingType(p.ParameterType) is not null
                        || (!p.ParameterType.IsValueType && nullability.Create(p).WriteState == NullabilityState.Nullable)))
                .Select(p => $"{tool.Name}.{p.Name}"))];

        offenders.Should().BeEmpty("an optional argument the model omits must bind as null, not throw");
    }
}
