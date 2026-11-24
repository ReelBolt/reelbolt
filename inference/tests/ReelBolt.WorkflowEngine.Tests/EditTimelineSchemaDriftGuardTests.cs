using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Ensures the EditTimeline JSON Schema (EditTimeline.schema.json) and C# types
/// (EditTimeline.cs, Item.cs, Keyframed.cs, Provenance.cs) remain synchronized.
///
/// The schema is the single source of truth for the contract both frontend and backend
/// depend on. This test:
/// - Verifies all C# types can be round-tripped through JSON
/// - Ensures JSON discriminator values match C# JsonDerivedType attributes
/// - Confirms schema definitions contain all required properties
/// - Guards against silent drift between schema and types (which has been a recurring bug)
/// </summary>
public class EditTimelineSchemaDriftGuardTests
{
    private static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public void Schema_file_exists_and_is_valid_json()
    {
        string schemaPath = GetSchemaFilePath();
        File.Exists(schemaPath).Should().BeTrue(
            "EditTimeline.schema.json must exist at the expected path");

        string schemaContent = File.ReadAllText(schemaPath);
        using JsonDocument doc = JsonDocument.Parse(schemaContent);
        doc.RootElement.Should().NotBeNull(
            "Schema must be valid JSON");

        // The schema document and the C# type graph must agree in both directions.
        VerifySchemaPropertiesMatchCSharpProperties(doc.RootElement);
    }

    [Fact]
    public void EditTimeline_round_trips_through_json()
    {
        var timeline = CreateSampleEditTimeline();

        string json = JsonSerializer.Serialize(timeline, DefaultJsonOptions);
        var deserialized = JsonSerializer.Deserialize<EditTimeline>(json, DefaultJsonOptions);

        deserialized.Should().NotBeNull("Deserialization should succeed");
        deserialized!.Id.Should().Be(timeline.Id);
        deserialized.SchemaVersion.Should().Be(2);
        deserialized.Name.Should().Be(timeline.Name);
    }

    [Fact]
    public void Item_discriminator_value_mediaClip_exists_in_schema()
    {
        VerifyItemDiscriminator(typeof(MediaClip), "mediaClip");
    }

    [Fact]
    public void Item_discriminator_value_titleClip_exists_in_schema()
    {
        VerifyItemDiscriminator(typeof(TitleClip), "titleClip");
    }

    [Fact]
    public void Item_discriminator_value_generatorClip_exists_in_schema()
    {
        VerifyItemDiscriminator(typeof(GeneratorClip), "generatorClip");
    }

    [Fact]
    public void Item_discriminator_value_insertClip_exists_in_schema()
    {
        VerifyItemDiscriminator(typeof(InsertClip), "insertClip");
    }

    [Fact]
    public void Item_discriminator_value_caption_exists_in_schema()
    {
        VerifyItemDiscriminator(typeof(Caption), "caption");
    }

    [Fact]
    public void Item_discriminator_value_transition_exists_in_schema()
    {
        VerifyItemDiscriminator(typeof(Transition), "transition");
    }

    [Fact]
    public void Item_discriminator_value_compoundClip_exists_in_schema()
    {
        VerifyItemDiscriminator(typeof(CompoundClip), "compoundClip");
    }

    [Fact]
    public void Item_discriminator_value_multicamClip_exists_in_schema()
    {
        VerifyItemDiscriminator(typeof(MulticamClip), "multicamClip");
    }

    [Fact]
    public void Item_discriminator_value_adjustmentClip_exists_in_schema()
    {
        VerifyItemDiscriminator(typeof(AdjustmentClip), "adjustmentClip");
    }

    [Fact]
    public void MediaClip_with_all_properties_round_trips()
    {
        var assetRef = new AssetRef(AssetRefKind.ProjectFile, ProjectFileId: Guid.NewGuid());
        var clip = new MediaClip(
            Id: Guid.NewGuid(),
            Name: "Test Clip",
            RecordIn: 100,
            RecordOut: 600,
            AssetRef: assetRef,
            SourceIn: 50,
            PlaybackRate: 1.5,
            Enabled: true);

        // Serialize through the polymorphic base type: System.Text.Json only writes the
        // discriminator when the declared type is the base, and Deserialize<Item> requires it.
        string json = JsonSerializer.Serialize<Item>(clip, DefaultJsonOptions);
        var deserialized = JsonSerializer.Deserialize<Item>(json, DefaultJsonOptions);

        deserialized.Should().BeOfType<MediaClip>();
        var mediaClip = (MediaClip)deserialized!;
        mediaClip.PlaybackRate.Should().Be(1.5);
        mediaClip.SourceIn.Should().Be(50);
        mediaClip.AssetRef.Should().Be(assetRef);
        mediaClip.RecordIn.Should().Be(100);
        mediaClip.RecordOut.Should().Be(600);
    }

    [Fact]
    public void TitleClip_with_text_round_trips()
    {
        var clip = new TitleClip(
            Id: Guid.NewGuid(),
            Name: "Title",
            RecordIn: 0,
            RecordOut: 120,
            Text: "Hello World",
            FontSize: 72,
            FontFamily: "Arial",
            Color: "#FF0000");

        string json = JsonSerializer.Serialize<Item>(clip, DefaultJsonOptions);
        var deserialized = JsonSerializer.Deserialize<Item>(json, DefaultJsonOptions);

        deserialized.Should().BeOfType<TitleClip>();
        var titleClip = (TitleClip)deserialized!;
        titleClip.Text.Should().Be("Hello World");
        titleClip.FontSize.Should().Be(72);
    }

    [Fact]
    public void Track_with_mixed_items_round_trips()
    {
        var track = new Track(
            Id: Guid.NewGuid(),
            Kind: TrackKind.Video,
            Name: "V1",
            Color: "#FF5733",
            Height: 100,
            Locked: false,
            Enabled: true,
            Mute: false,
            Solo: false,
            BusId: null,
            Items: new List<Item>
            {
                new MediaClip(
                    Guid.NewGuid(),
                    "Clip1",
                    0,
                    100,
                    new AssetRef(AssetRefKind.ProjectFile, ProjectFileId: Guid.NewGuid())),
                new TitleClip(Guid.NewGuid(), "Title1", 100, 50, "Test")
            });

        string json = JsonSerializer.Serialize(track, DefaultJsonOptions);
        var deserialized = JsonSerializer.Deserialize<Track>(json, DefaultJsonOptions);

        deserialized.Should().NotBeNull();
        deserialized!.Items.Should().HaveCount(2);
        deserialized.Items[0].Should().BeOfType<MediaClip>();
        deserialized.Items[1].Should().BeOfType<TitleClip>();
    }

    [Fact]
    public void Keyframed_with_keyframes_round_trips()
    {
        var keyframed = new Keyframed<double>(
            Value: 50.0,
            Keys: new[]
            {
                new Keyframe<double>(Frame: 0, Value: 0.0, Interp: InterpKind.Linear),
                new Keyframe<double>(Frame: 100, Value: 100.0, Interp: InterpKind.EaseInQuad)
            });

        string json = JsonSerializer.Serialize(keyframed, DefaultJsonOptions);
        var deserialized = JsonSerializer.Deserialize<Keyframed<double>>(json, DefaultJsonOptions);

        deserialized.Should().NotBeNull();
        deserialized!.Value.Should().Be(50.0);
        deserialized.Keys.Should().HaveCount(2);
        deserialized.Keys![0].Interp.Should().Be(InterpKind.Linear);
    }

    [Fact]
    public void Provenance_with_all_fields_round_trips()
    {
        var provenance = new Provenance(
            Origin: ProvenanceOrigin.Generated,
            StepResultId: Guid.NewGuid(),
            AgentDefinitionId: Guid.NewGuid(),
            ProviderId: Guid.NewGuid(),
            OfferedId: "offer_123",
            Rationale: "Best match",
            EditedByUser: true);

        string json = JsonSerializer.Serialize(provenance, DefaultJsonOptions);
        var deserialized = JsonSerializer.Deserialize<Provenance>(json, DefaultJsonOptions);

        deserialized.Should().NotBeNull();
        deserialized!.Origin.Should().Be(ProvenanceOrigin.Generated);
        deserialized.EditedByUser.Should().BeTrue();
        deserialized.Rationale.Should().Be("Best match");
    }

    [Fact]
    public void AssetRef_with_projectFile_round_trips()
    {
        var projectFileId = Guid.NewGuid();
        var assetRef = new AssetRef(
            Kind: AssetRefKind.ProjectFile,
            ProjectFileId: projectFileId);

        string json = JsonSerializer.Serialize(assetRef, DefaultJsonOptions);
        var deserialized = JsonSerializer.Deserialize<AssetRef>(json, DefaultJsonOptions);

        deserialized.Should().NotBeNull();
        deserialized!.Kind.Should().Be(AssetRefKind.ProjectFile);
        deserialized.ProjectFileId.Should().Be(projectFileId);
    }

    [Fact]
    public void AssetRef_with_stepResult_round_trips()
    {
        var stepResultId = Guid.NewGuid();
        var assetRef = new AssetRef(
            Kind: AssetRefKind.StepResult,
            StepResultId: stepResultId);

        string json = JsonSerializer.Serialize(assetRef, DefaultJsonOptions);
        var deserialized = JsonSerializer.Deserialize<AssetRef>(json, DefaultJsonOptions);

        deserialized.Should().NotBeNull();
        deserialized!.Kind.Should().Be(AssetRefKind.StepResult);
        deserialized.StepResultId.Should().Be(stepResultId);
    }

    [Fact]
    public void Rational_round_trips()
    {
        var rational = new Rational(Num: 24, Den: 1);

        string json = JsonSerializer.Serialize(rational, DefaultJsonOptions);
        var deserialized = JsonSerializer.Deserialize<Rational>(json, DefaultJsonOptions);

        deserialized.Should().NotBeNull();
        deserialized!.Num.Should().Be(24);
        deserialized.Den.Should().Be(1);
    }

    [Fact]
    public void EditTimelineSettings_round_trips()
    {
        var settings = new EditTimelineSettings(
            Width: 1920,
            Height: 1080,
            Rate: new Rational(24, 1),
            SampleRate: 48000,
            PixelAspect: new Rational(1, 1),
            AspectPolicy: AspectPolicy.Fit,
            StartTimecodeFrames: 0);

        string json = JsonSerializer.Serialize(settings, DefaultJsonOptions);
        var deserialized = JsonSerializer.Deserialize<EditTimelineSettings>(json, DefaultJsonOptions);

        deserialized.Should().NotBeNull();
        deserialized!.Width.Should().Be(1920);
        deserialized.AspectPolicy.Should().Be(AspectPolicy.Fit);
    }

    [Fact]
    public void EditTimeline_schemaVersion_is_always_2()
    {
        var timeline = CreateSampleEditTimeline();
        timeline.SchemaVersion.Should().Be(2,
            "EditTimeline.SchemaVersion must always be 2");
    }

    [Fact]
    public void Schema_contains_item_discriminator_definitions()
    {
        string schemaContent = File.ReadAllText(GetSchemaFilePath());
        using JsonDocument doc = JsonDocument.Parse(schemaContent);

        // Verify the schema has a discriminator for Item types
        var itemDef = doc.RootElement.GetProperty("$defs").GetProperty("Item");
        itemDef.GetProperty("discriminator").Should().NotBeNull();
        itemDef.GetProperty("discriminator")
            .GetProperty("propertyName")
            .GetString()
            .Should().Be("type",
            "Item discriminator property must be named 'type'");
    }

    private static void VerifyItemDiscriminator(Type itemType, string expectedDiscriminatorValue)
    {
        // [JsonDerivedType] is declared on the polymorphic base, not on the derived type, so the
        // mapping has to be read from 'Item' and matched by DerivedType.
        var attributes = typeof(Item)
            .GetCustomAttributes(typeof(JsonDerivedTypeAttribute), false)
            .Cast<JsonDerivedTypeAttribute>()
            .Where(candidate => candidate.DerivedType == itemType)
            .ToList();

        attributes.Should().ContainSingle(
            $"Item must declare exactly one [JsonDerivedType] attribute for {itemType.Name}");

        // TypeDiscriminator is the public property on JsonDerivedTypeAttribute — reading it
        // through the public API is what makes this assertion actually run instead of being
        // silently skipped when a non-public field lookup returns null.
        attributes[0].TypeDiscriminator.Should().Be(expectedDiscriminatorValue,
            $"{itemType.Name} must have discriminator value '{expectedDiscriminatorValue}'");

        // Also verify the schema contains this discriminator value
        VerifySchemaContainsItemType(expectedDiscriminatorValue);
    }

    private static void VerifySchemaContainsItemType(string discriminatorValue)
    {
        string schemaContent = File.ReadAllText(GetSchemaFilePath());
        using JsonDocument doc = JsonDocument.Parse(schemaContent);

        var itemDefs = doc.RootElement.GetProperty("$defs");
        bool found = false;

        foreach (var definition in itemDefs.EnumerateObject())
        {
            if (definition.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // The discriminator lives in the definition's 'properties' as the 'type' property
            // schema { "const": <discriminator> }. The definition's own top-level 'type' is the
            // JSON Schema type keyword, not the discriminator.
            if (definition.Value.TryGetProperty("properties", out var properties) &&
                properties.ValueKind == JsonValueKind.Object &&
                properties.TryGetProperty("type", out var typeProperty) &&
                typeProperty.ValueKind == JsonValueKind.Object &&
                typeProperty.TryGetProperty("const", out var constElement) &&
                constElement.ValueKind == JsonValueKind.String &&
                constElement.GetString() == discriminatorValue)
            {
                found = true;
                break;
            }
        }

        found.Should().BeTrue(
            $"Schema must define an item type with discriminator value '{discriminatorValue}'");
    }

    /// <summary>
    /// Every schema definition, and the document root, paired with the C# type it describes.
    /// A null discriminator means the type carries no 'type' JSON marker.
    /// </summary>
    private static readonly (string DefinitionName, Type ClrType, string? DiscriminatorPropertyName)[]
        SchemaDefinitionTypes =
    {
        ("Rational", typeof(Rational), null),
        ("EditTimelineSettings", typeof(EditTimelineSettings), null),
        ("Track", typeof(Track), null),
        ("Item", typeof(Item), "type"),
        ("MediaClip", typeof(MediaClip), "type"),
        ("TitleClip", typeof(TitleClip), "type"),
        ("GeneratorClip", typeof(GeneratorClip), "type"),
        ("InsertClip", typeof(InsertClip), "type"),
        ("Caption", typeof(Caption), "type"),
        ("Transition", typeof(Transition), "type"),
        ("CompoundClip", typeof(CompoundClip), "type"),
        ("MulticamClip", typeof(MulticamClip), "type"),
        ("AdjustmentClip", typeof(AdjustmentClip), "type"),
        ("Marker", typeof(Marker), null),
        ("EditRange", typeof(EditRange), null),
        ("EditTimelineSeed", typeof(EditTimelineSeed), null),
        ("Keyframed", typeof(Keyframed<>), null),
        ("Keyframe", typeof(Keyframe<>), null),
        ("Provenance", typeof(Provenance), null),
        ("AssetRef", typeof(AssetRef), null),
        ("QuadPoint", typeof(QuadPoint), null),
        ("QuadCorners", typeof(QuadCorners), null),
        ("ClipTransform", typeof(ClipTransform), null),
        ("ClipCrop", typeof(ClipCrop), null),
        ("TrackEq", typeof(TrackEq), null),
        ("EqBand", typeof(EqBand), null),
        ("GradeWheel", typeof(GradeWheel), null),
        ("GradePrimaries", typeof(GradePrimaries), null),
        ("CurvePoint", typeof(CurvePoint), null),
        ("GradeCurves", typeof(GradeCurves), null),
        ("GradeLut", typeof(GradeLut), null),
        ("GradeNode", typeof(GradeNode), null),
        ("GradeStack", typeof(GradeStack), null),
        ("ColorGroup", typeof(ColorGroup), null),
        ("ShapeMask", typeof(ShapeMask), null),
        ("ChromaKeyer", typeof(ChromaKeyer), null),
        ("ClipEffect", typeof(ClipEffect), null),
        ("ReframeTarget", typeof(ReframeTarget), null),
        ("EditSequence", typeof(EditSequence), null),
        ("MulticamAngle", typeof(MulticamAngle), null),
        ("MulticamCut", typeof(MulticamCut), null),
        ("AudioCompressor", typeof(AudioCompressor), null),
        ("AudioLimiter", typeof(AudioLimiter), null),
        ("AudioGate", typeof(AudioGate), null),
        ("AudioDeesser", typeof(AudioDeesser), null),
        ("AudioDynamics", typeof(AudioDynamics), null),
        ("AudioDenoise", typeof(AudioDenoise), null),
        ("AudioLeveler", typeof(AudioLeveler), null),
        ("AudioBus", typeof(AudioBus), null),
        ("GradeQualifier", typeof(GradeQualifier), null),
        ("GradeWindow", typeof(GradeWindow), null)
    };

    /// <summary>
    /// Asserts that the schema and the C# types agree on properties in BOTH directions: no
    /// property is declared in the schema that no public C# property provides, and no public C#
    /// property goes undeclared in the schema. The polymorphic 'type' discriminator is a
    /// JSON-level marker with no C# property, so it is exempt on the schema side.
    /// </summary>
    private static void VerifySchemaPropertiesMatchCSharpProperties(JsonElement schemaRoot)
    {
        JsonElement definitions = schemaRoot.GetProperty("$defs");

        VerifyPropertiesAgree(schemaRoot, "EditTimeline (root)", typeof(EditTimeline), null);

        var definitionNames = definitions.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (definitionName, clrType, discriminatorPropertyName) in SchemaDefinitionTypes)
        {
            definitionNames.Should().Contain(definitionName,
                $"the schema must define '$defs/{definitionName}' for C# type {clrType.Name}");

            VerifyPropertiesAgree(
                definitions.GetProperty(definitionName),
                definitionName,
                clrType,
                discriminatorPropertyName);
        }
    }

    private static void VerifyPropertiesAgree(
        JsonElement definition,
        string definitionName,
        Type clrType,
        string? discriminatorPropertyName)
    {
        definition.TryGetProperty("properties", out JsonElement schemaProperties)
            .Should().BeTrue(
                $"schema definition '{definitionName}' must declare the properties of {clrType.Name}");

        var declaredInSchema = schemaProperties
            .EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        var declaredInCSharp = clrType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name))
            .ToHashSet(StringComparer.Ordinal);

        var allowedInSchema = new HashSet<string>(declaredInCSharp, StringComparer.Ordinal);
        if (discriminatorPropertyName != null)
        {
            allowedInSchema.Add(discriminatorPropertyName);
        }

        declaredInSchema.Should().BeSubsetOf(allowedInSchema,
            $"every property the schema declares for '{definitionName}' must exist on {clrType.Name}");

        declaredInCSharp.Should().BeSubsetOf(declaredInSchema,
            $"every public property of {clrType.Name} must be declared by the schema's " +
            $"'{definitionName}' definition");
    }

    private static string GetSchemaFilePath()
    {
        // Walk up from the assembly until the repository root is found — the directory that owns
        // the solution next to the sources it builds. A fixed number of '..' segments resolves to
        // the wrong directory as soon as the build output layout changes, which is exactly how
        // this path silently rotted before.
        string assemblyDir = Path.GetDirectoryName(typeof(EditTimelineSchemaDriftGuardTests).Assembly.Location)!;

        for (string? candidate = assemblyDir; candidate != null; candidate = Path.GetDirectoryName(candidate))
        {
            bool isRepositoryRoot =
                File.Exists(Path.Combine(candidate, "inference", "ReelBolt.sln")) ||
                (Directory.Exists(Path.Combine(candidate, "inference")) &&
                 File.Exists(Path.Combine(candidate, "ReelBolt.sln")));

            if (isRepositoryRoot)
            {
                return Path.Combine(
                    candidate,
                    "inference/src/ReelBolt.Shared/Editing/EditTimeline.schema.json");
            }
        }

        throw new InvalidOperationException(
            "Could not locate the repository root (a directory holding 'inference/ReelBolt.sln') " +
            $"by walking up from '{assemblyDir}'.");
    }

    private static EditTimeline CreateSampleEditTimeline()
    {
        return new EditTimeline(
            id: Guid.NewGuid(),
            projectId: Guid.NewGuid(),
            name: "Sample Timeline",
            settings: new EditTimelineSettings(
                Width: 1920,
                Height: 1080,
                Rate: new Rational(24, 1),
                SampleRate: 48000,
                PixelAspect: new Rational(1, 1),
                AspectPolicy: AspectPolicy.Fit,
                StartTimecodeFrames: 0),
            tracks: new List<Track>
            {
                new Track(
                    Id: Guid.NewGuid(),
                    Kind: TrackKind.Video,
                    Name: "V1",
                    Color: null,
                    Height: 100,
                    Locked: false,
                    Enabled: true,
                    Mute: false,
                    Solo: false,
                    BusId: null,
                    Items: new List<Item>())
            },
            markers: new List<Marker>());
    }
}
