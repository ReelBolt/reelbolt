#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Marketing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Marketing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketing;

/// <summary>brevo-contact-sync T-001: the DB-only name reconciler.</summary>
public sealed class MarketingNameReconcilerTests : IDisposable
{
    private readonly MarketingHarness _h = new();

    public void Dispose() => _h.Dispose();

    private Guid Seed(string email, string displayName, MarketingConsentStatus status = MarketingConsentStatus.OptedIn,
        MarketingNameSource? source = null, string? first = null, string? last = null)
    {
        using InferenceApiDbContext db = _h.NewDb();
        Guid userId = Guid.NewGuid();
        db.ApplicationUsers.Add(new ApplicationUser { Id = userId, Email = email, DisplayName = displayName });
        Guid id = Guid.NewGuid();
        db.MarketingConsents.Add(new MarketingConsent
        {
            Id = id,
            Email = email,
            UserId = userId,
            Status = status,
            WordingVersion = "v1",
            Source = MarketingConsentSource.Signup,
            FirstName = first,
            LastName = last,
            NameSource = source
        });
        db.SaveChanges();
        return id;
    }

    private async Task<int> RunAsync()
    {
        using InferenceApiDbContext db = _h.NewDb();
        return await new MarketingNameReconciler(db, _h.Clock).ReconcileAsync(CancellationToken.None);
    }

    private MarketingConsent Get(Guid id)
    {
        using InferenceApiDbContext db = _h.NewDb();
        return db.MarketingConsents.AsNoTracking().Single(c => c.Id == id);
    }

    [Fact]
    public async Task Fills_from_account_name_and_stamps_source()
    {
        Guid id = Seed("a@example.com", "Maria Rosa Bianchi");
        (await RunAsync()).Should().Be(1);
        MarketingConsent row = Get(id);
        row.FirstName.Should().Be("Maria");
        row.LastName.Should().Be("Rosa Bianchi");
        row.NameSource.Should().Be(MarketingNameSource.Account);
        row.NameUpdatedAt.Should().Be(_h.Now);
    }

    [Fact]
    public async Task Email_prefix_fallback_is_not_a_name()
    {
        Guid id = Seed("jane@example.com", "jane");
        (await RunAsync()).Should().Be(0);
        Get(id).FirstName.Should().BeNull();
    }

    [Fact]
    public async Task No_write_when_equal()
    {
        Seed("a@example.com", "Maria Rosa");
        await RunAsync();
        (await RunAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Account_rename_follows_and_unusable_name_clears_but_keeps_source()
    {
        Guid id = Seed("a@example.com", "Maria Rosa");
        await RunAsync();
        using (InferenceApiDbContext db = _h.NewDb())
        {
            db.ApplicationUsers.Single(u => u.Email == "a@example.com").DisplayName = "Anna Verdi";
            db.SaveChanges();
        }

        (await RunAsync()).Should().Be(1);
        Get(id).FirstName.Should().Be("Anna");

        using (InferenceApiDbContext db = _h.NewDb())
        {
            db.ApplicationUsers.Single(u => u.Email == "a@example.com").DisplayName = "a";
            db.SaveChanges();
        }

        // "a" is the email local part: unusable.
        (await RunAsync()).Should().Be(1);
        MarketingConsent row = Get(id);
        row.FirstName.Should().BeNull();
        row.LastName.Should().BeNull();
        row.NameSource.Should().Be(MarketingNameSource.Account);
    }

    [Fact]
    public async Task Admin_name_is_never_touched()
    {
        Guid id = Seed("a@example.com", "Account Name", source: MarketingNameSource.Admin, first: "Kept");
        (await RunAsync()).Should().Be(0);
        Get(id).FirstName.Should().Be("Kept");
    }

    [Fact]
    public async Task Account_name_overrides_a_form_name_once_the_row_has_a_user()
    {
        Guid id = Seed("a@example.com", "Maria Rossi", source: MarketingNameSource.Form, first: "Mallory");
        (await RunAsync()).Should().Be(1);
        MarketingConsent row = Get(id);
        row.FirstName.Should().Be("Maria");
        row.NameSource.Should().Be(MarketingNameSource.Account);
    }

    [Fact]
    public async Task An_unusable_account_name_clears_a_form_name()
    {
        Guid id = Seed("jane@example.com", "jane", source: MarketingNameSource.Form, first: "Mallory");
        (await RunAsync()).Should().Be(1);
        Get(id).FirstName.Should().BeNull();
    }

    [Fact]
    public async Task ClearWithdrawn_runs_on_its_own()
    {
        Guid id = Seed("a@example.com", "A", MarketingConsentStatus.Withdrawn, MarketingNameSource.Form, "Old", "Name");
        using InferenceApiDbContext db = _h.NewDb();
        (await new MarketingNameReconciler(db, _h.Clock).ClearWithdrawnAsync(CancellationToken.None)).Should().Be(1);
        Get(id).FirstName.Should().BeNull();
    }

    [Fact]
    public async Task Withdrawn_rows_lose_their_name_and_keep_the_source()
    {
        Guid id = Seed("a@example.com", "Account Name", MarketingConsentStatus.Withdrawn, MarketingNameSource.Admin, "Old", "Name");
        (await RunAsync()).Should().Be(1);
        MarketingConsent row = Get(id);
        row.FirstName.Should().BeNull();
        row.LastName.Should().BeNull();
        row.NameSource.Should().Be(MarketingNameSource.Admin);
    }
}
