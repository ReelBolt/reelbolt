using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class ApplicationUserLocaleModelTests
{
    [Fact]
    public void Locale_is_a_nullable_bounded_column_without_default()
    {
        using var db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only")
            .Options);
        var prop = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(ApplicationUser))!
            .FindProperty(nameof(ApplicationUser.Locale))!;

        prop.GetColumnName().Should().Be("locale");
        prop.IsNullable.Should().BeTrue();
        prop.GetMaxLength().Should().Be(16);
        prop.GetDefaultValue().Should().BeNull();
    }
}
