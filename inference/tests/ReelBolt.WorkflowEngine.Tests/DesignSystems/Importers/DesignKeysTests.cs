using System;
using System.Collections.Generic;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;

public sealed class DesignKeysTests
{
    [Fact]
    public void Unique_with_50k_colliding_paths_is_fast_and_yields_distinct_paths()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);

        HostileInput.RunWithin(TimeSpan.FromSeconds(10), () =>
        {
            for (int i = 0; i < 50_000; i++)
                DesignKeys.Unique(used, "color.same");

            return 0;
        });

        Assert.Equal(50_000, used.Count);
        Assert.Contains("color.same", used);
        Assert.Contains("color.same-2", used);
        Assert.Contains("color.same-50000", used);
    }

    [Fact]
    public void Unique_skips_suffixes_that_were_taken_by_other_paths()
    {
        var used = new HashSet<string>(StringComparer.Ordinal) { "a", "a-2" };

        Assert.Equal("a-3", DesignKeys.Unique(used, "a"));
        Assert.Equal("a-4", DesignKeys.Unique(used, "a"));
        Assert.Equal("b", DesignKeys.Unique(used, "b"));
    }

    [Fact]
    public void Clip_bounds_an_attacker_string_and_keeps_short_ones()
    {
        Assert.Equal("short", DesignKeys.Clip("short"));
        Assert.Equal("(none)", DesignKeys.Clip(null));
        string clipped = DesignKeys.Clip(new string('x', 5000));
        Assert.True(clipped.Length <= 64, "clipped length " + clipped.Length);
        Assert.StartsWith("xxxx", clipped);
    }
}
