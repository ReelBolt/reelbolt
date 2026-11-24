using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.DesignSystems;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Engine;

/// <summary>
/// A pin whose members really load: design and product versions seeded into an in-memory engine
/// database, loaded the way the executor service loads them (<see cref="DesignSystemRunState.LoadAsync"/>).
/// Used by the Require tests, which pass only for a loadable pin.
/// </summary>
internal static class LoadedPinFixture
{
    private static readonly TokenProvenance Prov = new("manual", null, null, null);

    private const string ProductJson =
        """
        {
          "schema": "reelbolt.product-map/1",
          "framework": { "name": "next", "version": "15.1", "language": "typescript", "styling": [], "packageManager": "pnpm", "projectType": "web app" },
          "routes": [{ "path": "/", "file": "app/page.tsx", "kind": "page", "requiresAuth": false }],
          "components": [],
          "dependencies": { "runtime": [], "dev": [], "ui": [] }
        }
        """;

    internal sealed record Loaded(DesignSystemRunState? State, string PinJson);

    public static async Task<Loaded> LoadAsync(
        bool enabled = true,
        bool corruptDesignDigest = false,
        bool includeDesign = true,
        bool includeProduct = true,
        int designNumber = 1,
        int productNumber = 1,
        Guid? projectId = null,
        JsonObject? design = null,
        IEnumerable<ProjectFile>? files = null)
    {
        var db = new WorkflowEngineDbContext(
            new DbContextOptionsBuilder<WorkflowEngineDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

        JsonObject document = design ?? DefaultDesign();
        string designJson = CanonicalJson.Serialize(document);
        string designDigest = CanonicalJson.Digest(document);
        string mapJson = CanonicalJson.Serialize(JsonNode.Parse(ProductJson));
        string mapDigest = CanonicalJson.Sha256Hex(mapJson);

        Guid designId = Guid.NewGuid();
        Guid productId = Guid.NewGuid();
        db.DesignSystemVersions.Add(Version(designId, designNumber, designJson, designDigest));
        db.DesignSystemVersions.Add(Version(productId, productNumber, mapJson, mapDigest));
        if (files is not null)
            db.ProjectFiles.AddRange(files);
        await db.SaveChangesAsync();

        var pin = new DesignSystemPin(
            1,
            includeDesign
                ? new DesignSystemPinMember(designId, designNumber, corruptDesignDigest ? new string('f', 64) : designDigest)
                : null,
            includeProduct ? new DesignSystemPinMember(productId, productNumber, mapDigest) : null);
        string pinJson = DesignSystemPinJson.Serialize(pin);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => db);
        services.AddScoped<PinnedDocumentLoader>();
        services.Configure<DesignSystemAmbientOptions>(options =>
        {
            options.Enabled = enabled;
            options.DesignMaxChars = 2400;
            options.ProductMaxChars = 3200;
        });
        ServiceProvider sp = services.BuildServiceProvider();

        DesignSystemRunState? state = await DesignSystemRunState.LoadAsync(
            new WorkflowExecution { ProjectId = projectId ?? Guid.NewGuid(), DesignSystemPinJson = pinJson },
            sp,
            CancellationToken.None);
        return new Loaded(state, pinJson);
    }

    private static DesignSystemVersion Version(Guid id, int number, string json, string digest) => new()
    {
        Id = id,
        DesignSystemId = Guid.NewGuid(),
        VersionNumber = number,
        State = DesignVersionState.Approved,
        DocumentJson = json,
        DocumentDigest = digest,
        Origin = DesignVersionOrigin.Manual,
        CreatedByUserId = Guid.NewGuid(),
        CreatedAt = DateTime.UtcNow,
    };

    private static JsonObject DefaultDesign()
    {
        JsonObject doc = DesignDocument.New("Acme");
        DesignDocument.SetToken(doc, "color.brand.primary", "color", new JsonObject { ["hex"] = "#6d28d9" }, Prov, 0.95);
        return doc;
    }
}
