#nullable enable
using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Controllers.Admin;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Marketing;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models.Marketing;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketing;

/// <summary>saas-promotions T-004: the platform-admin CSV export of the sendable consent list.</summary>
public sealed class MarketingConsentExportTests : IDisposable
{
    private const string V1 = "2026-10-marketing-v1";
    private const string Header = "email,user_id,consented_at,confirmed_at,wording_version,source,unsubscribe_url";
    private readonly MarketingHarness _h = new();
    private readonly Guid _admin = Guid.NewGuid();

    public void Dispose() => _h.Dispose();

    private sealed class FakeUser(Guid id, bool admin) : ICurrentUser
    {
        public Guid UserId => id;
        public string Email => "admin@example.com";
        public bool IsAuthenticated => true;
        public Guid? OrganizationId => null;
        public OrganizationRole? OrgRole => null;
        public bool IsPlatformAdmin => admin;
    }

    private MarketingConsentAdminController Controller(InferenceApiDbContext db, bool admin = true)
    {
        MarketingConsentAdminController controller = new(_h.NewService(db), new FakeUser(_admin, admin));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private Guid Add(string email, MarketingConsentStatus status, DateTime? confirmedAt, Guid? userId = null,
        MarketingConsentSource source = MarketingConsentSource.SiteContact)
    {
        using InferenceApiDbContext db = _h.NewDb();
        Guid id = Guid.NewGuid();
        db.MarketingConsents.Add(new MarketingConsent
        {
            Id = id,
            Email = email,
            UserId = userId,
            Status = status,
            WordingVersion = V1,
            Source = source,
            ConsentedAt = status == MarketingConsentStatus.OptedIn ? new DateTime(2026, 10, 1, 8, 30, 0, DateTimeKind.Utc) : null,
            ConfirmedAt = confirmedAt,
            RequestedAt = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc),
        });
        db.SaveChanges();
        return id;
    }

    private async Task<(FileContentResult File, HttpResponse Response)> ExportAsync()
    {
        await using InferenceApiDbContext db = _h.NewDb();
        MarketingConsentAdminController controller = Controller(db);
        IActionResult result = await controller.ExportCsv(CancellationToken.None);
        return (result.Should().BeOfType<FileContentResult>().Subject, controller.HttpContext.Response);
    }

    private static string[] Lines(FileContentResult file) =>
        Encoding.UTF8.GetString(file.FileContents).TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public async Task The_csv_has_the_exact_header_row()
    {
        (FileContentResult file, _) = await ExportAsync();

        Lines(file).Should().Equal(Header);
        file.ContentType.Should().Be("text/csv; charset=utf-8");
        file.FileDownloadName.Should().MatchRegex(@"^marketing-consents-\d{8}-\d{6}\.csv$");
    }

    [Fact]
    public async Task Only_confirmed_opted_in_rows_of_verified_or_accountless_emails_that_are_not_suppressed_are_exported()
    {
        Guid verified = _h.AddUser("verified@example.com", _h.Now.AddDays(-1));
        Guid unverified = _h.AddUser("unverified@example.com", null);

        Guid anonymous = Add("anon@example.com", MarketingConsentStatus.OptedIn, _h.Now.AddDays(-2));
        Add("verified@example.com", MarketingConsentStatus.OptedIn, _h.Now.AddDays(-2), verified, MarketingConsentSource.Signup);
        Add("unverified@example.com", MarketingConsentStatus.OptedIn, _h.Now.AddDays(-2), unverified);
        Add("unconfirmed@example.com", MarketingConsentStatus.OptedIn, confirmedAt: null);
        Add("pending@example.com", MarketingConsentStatus.Pending, null);
        Add("withdrawn@example.com", MarketingConsentStatus.Withdrawn, _h.Now.AddDays(-9));
        Add("suppressed@example.com", MarketingConsentStatus.OptedIn, _h.Now.AddDays(-2));
        using (InferenceApiDbContext db = _h.NewDb())
        {
            db.MarketingSuppressions.Add(new MarketingSuppression
            {
                Id = Guid.NewGuid(),
                EmailHmac = _h.Suppression.Hash("suppressed@example.com"),
                WithdrawnAt = _h.Now,
            });
            db.SaveChanges();
        }

        (FileContentResult file, _) = await ExportAsync();

        string[] lines = Lines(file);
        lines.Skip(1).Select(l => l.Split(',')[0]).Should().BeEquivalentTo("anon@example.com", "verified@example.com");

        string anonLine = lines.Single(l => l.StartsWith("anon@example.com,", StringComparison.Ordinal));
        string[] cells = anonLine.Split(',');
        cells[1].Should().BeEmpty();
        cells[2].Should().Be("2026-10-01T08:30:00Z");
        cells[4].Should().Be(V1);
        cells[5].Should().Be("siteContact");
        cells[6].Should().StartWith(MarketingHarness.BaseUrl + "/app/unsubscribe?token=u1.k1.");
        string token = Uri.UnescapeDataString(cells[6].Split("token=")[1]);
        MarketingTokenResult verifiedToken = _h.Tokens.VerifyUnsubscribe(token);
        verifiedToken.Status.Should().Be(MarketingTokenStatus.Valid);
        verifiedToken.ConsentId.Should().Be(anonymous);

        lines.Single(l => l.StartsWith("verified@example.com,", StringComparison.Ordinal)).Split(',')[1]
            .Should().Be(verified.ToString());
    }

    [Theory]
    [InlineData("=cmd@example.com", "'=cmd@example.com")]
    [InlineData("+cmd@example.com", "'+cmd@example.com")]
    [InlineData("-cmd@example.com", "'-cmd@example.com")]
    [InlineData("@cmd@example.com", "'@cmd@example.com")]
    public async Task Formula_cells_are_escaped(string email, string expected)
    {
        Add(email, MarketingConsentStatus.OptedIn, _h.Now);

        (FileContentResult file, _) = await ExportAsync();

        Lines(file)[1].Split(',')[0].Should().Be(expected);
    }

    [Fact]
    public void Tab_and_carriage_return_leading_cells_are_escaped_and_quoted()
    {
        MarketingCsv.Cell("\tx").Should().Be("'\tx");
        MarketingCsv.Cell("\rx").Should().Be("\"'\rx\"");
        MarketingCsv.Cell("a,b").Should().Be("\"a,b\"");
        MarketingCsv.Cell("say \"hi\"").Should().Be("\"say \"\"hi\"\"\"");
        MarketingCsv.Cell(null).Should().Be("");
    }

    [Fact]
    public async Task The_export_sends_no_store_and_writes_the_export_row()
    {
        Add("one@example.com", MarketingConsentStatus.OptedIn, _h.Now);
        Add("two@example.com", MarketingConsentStatus.OptedIn, _h.Now);

        (_, HttpResponse response) = await ExportAsync();

        response.Headers.CacheControl.ToString().Should().Be("no-store");
        using InferenceApiDbContext db = _h.NewDb();
        MarketingConsentExport export = db.MarketingConsentExports.Should().ContainSingle().Subject;
        export.ExportedByUserId.Should().Be(_admin);
        export.RowCount.Should().Be(2);
        export.ExportedAt.Should().Be(_h.Now);
    }

    [Fact]
    public async Task Non_admins_are_forbidden()
    {
        await using InferenceApiDbContext db = _h.NewDb();
        MarketingConsentAdminController controller = Controller(db, admin: false);

        (await controller.ExportCsv(CancellationToken.None)).Should().BeOfType<ForbidResult>();
        (await controller.GetSummary(CancellationToken.None)).Should().BeOfType<ForbidResult>();
        db.MarketingConsentExports.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_a_public_base_url_the_export_is_refused_rather_than_shipped_without_unsubscribe_links()
    {
        _h.Options.PublicBaseUrl = null;
        Add("one@example.com", MarketingConsentStatus.OptedIn, _h.Now);
        await using InferenceApiDbContext db = _h.NewDb();

        IActionResult result = await Controller(db).ExportCsv(CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        db.MarketingConsentExports.Should().BeEmpty();
    }

    [Fact]
    public async Task The_summary_counts_states_and_reports_the_last_export()
    {
        Add("a@example.com", MarketingConsentStatus.OptedIn, _h.Now);
        Add("b@example.com", MarketingConsentStatus.OptedIn, null);
        Add("c@example.com", MarketingConsentStatus.Pending, null);
        Add("d@example.com", MarketingConsentStatus.Withdrawn, null);

        await using (InferenceApiDbContext db = _h.NewDb())
        {
            IActionResult before = await Controller(db).GetSummary(CancellationToken.None);
            string json = JsonSerializer.Serialize(before.Should().BeOfType<OkObjectResult>().Subject.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            json.Should().Be("{\"counts\":{\"pending\":1,\"optedIn\":2,\"withdrawn\":1,\"exportable\":1},\"lastExport\":null}");
        }

        await ExportAsync();

        await using (InferenceApiDbContext db = _h.NewDb())
        {
            IActionResult after = await Controller(db).GetSummary(CancellationToken.None);
            MarketingAdminSummary summary = after.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<MarketingAdminSummary>().Subject;
            summary.LastExport.Should().NotBeNull();
            summary.LastExport!.RowCount.Should().Be(1);
            summary.LastExport.ExportedAt.Should().Be(_h.Now);
        }
    }
}
