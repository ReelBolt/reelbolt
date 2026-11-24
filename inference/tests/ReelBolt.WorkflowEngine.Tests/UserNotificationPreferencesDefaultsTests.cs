using System;
using System.IO;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class UserNotificationPreferencesDefaultsTests
{
    [Fact]
    public void Every_category_switch_defaults_on_including_review_activity()
    {
        var prefs = new UserNotificationPreferences();

        Assert.True(prefs.JobSucceeded);
        Assert.True(prefs.JobFailed);
        Assert.True(prefs.FileCreated);
        Assert.True(prefs.GraphicsEdited);
        Assert.True(prefs.ReviewActivity);
    }

    [Fact]
    public void Model_snapshot_maps_review_activity_with_default_true()
    {
        var dir = AppContext.BaseDirectory;
        string? path = null;
        for (var d = new DirectoryInfo(dir); d is not null && path is null; d = d.Parent)
        {
            var c = Path.Combine(d.FullName, "src", "ReelBolt.Inference.Api", "Migrations", "InferenceApiDbContextModelSnapshot.cs");
            if (File.Exists(c)) path = c;
        }
        Assert.NotNull(path);
        var text = File.ReadAllText(path!);
        Assert.Contains("b.Property<bool>(\"ReviewActivity\")", text);
        Assert.Contains("HasColumnName(\"review_activity\")", text);
    }
}
