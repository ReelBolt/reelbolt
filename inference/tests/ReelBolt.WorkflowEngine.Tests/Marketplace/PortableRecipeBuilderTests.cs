using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Marketplace.Snapshot;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketplace;

public sealed class PortableRecipeBuilderTests
{
    private static PortableRecipeResult Build(IReadOnlyList<WorkflowStep> steps, IReadOnlyDictionary<Guid, AgentDefinition>? agents = null, AppManifest? manifest = null, WorkflowDefinition? workflow = null) =>
        PortableRecipeBuilder.Build(workflow ?? SnapshotFixtures.Workflow(), steps, agents ?? SnapshotFixtures.BuiltIns(), manifest);

    private static string[] Codes(PortableRecipeResult r) => [.. r.Refusals.Select(x => x.Code)];

    // ---------------------------------------------------------------- golden: every built-in template

    public static IEnumerable<object[]> Templates() => WorkflowTemplateCatalog.GetAll().Select(t => new object[] { t.Key });

    [Theory]
    [MemberData(nameof(Templates))]
    public void Every_shareable_template_builds_a_clean_validated_snapshot(string key)
    {
        WorkflowTemplateDefinition template = WorkflowTemplateCatalog.GetByKey(key)!;
        List<WorkflowStep> steps = SnapshotFixtures.FromTemplate(template);
        bool refused = steps.Any(s => MarketplaceStepPublishability.For(s.StepType) is StepPublishability.NotShareable);

        PortableRecipeResult result = Build(steps, manifest: template.AppManifest);

        if (refused)
        {
            result.Recipe.Should().BeNull();
            Codes(result).Should().Contain("STEP_TYPE_NOT_SHAREABLE");
            return;
        }

        result.Refusals.Should().BeEmpty(key);
        result.Recipe!.Steps.Select(s => s.Order).Should().Equal(steps.Select(s => s.StepOrder));
        result.Recipe.Steps.Select(s => s.StepType).Should().Equal(steps.Select(s => s.StepType));
        PortableRecipeValidator.Validate(result.Recipe).Should().BeEmpty(key);
        SnapshotLeakGuard.Scan(result.CanonicalJson!).Should().BeEmpty();
        PortableRecipeJson.TryParse(result.CanonicalJson)!.ToCanonicalJson().Should().Be(result.CanonicalJson);
    }

    [Fact]
    public void Template_strip_keeps_numbers_enums_and_text_but_nothing_with_an_id()
    {
        WorkflowTemplateDefinition template = WorkflowTemplateCatalog.GetAll().First(t => t.Steps.Any(x => x.StepType == StepType.VideoAnalyze));
        List<WorkflowStep> steps = SnapshotFixtures.FromTemplate(template);
        PortableRecipeResult result = Build(steps);

        result.Recipe.Should().NotBeNull();
        PortableStep analyze = result.Recipe!.Steps.First(s => s.StepType == StepType.VideoAnalyze);
        analyze.Config!["source"]!["kind"]!.GetValue<string>().Should().Be("ProjectFile");
        analyze.Config["version"].Should().NotBeNull();
        result.CanonicalJson.Should().NotContainEquivalentOf("projectFileId\":\"");
    }

    // ---------------------------------------------------------------- strip: no source id survives

    [Fact]
    public void No_planted_guid_survives_in_any_id_field_of_any_step_config()
    {
        Random random = new(20261102);
        for (int round = 0; round < 25; round++)
        {
            List<string> planted = [];
            List<WorkflowStep> steps = [];
            int order = 1;
            int slotsExpected = 0;
            foreach (StepType type in Enum.GetValues<StepType>())
            {
                if (MarketplaceStepPublishability.ConfigTypeOf(type) is not { } configType)
                    continue;
                WorkflowStep step = SnapshotFixtures.Step(order++, type);
                JsonObject config = SnapshotFixtures.Synthesize(configType, p =>
                {
                    FieldRule? rule = MarketplaceFieldCatalog.RuleFor(p);
                    if (rule is null || p.Info.Name.Contains("AgentDefinitionId", StringComparison.Ordinal) || rule.RefuseCode is not null)
                        return rule?.Class == FieldClass.Text ? JsonValue.Create("hello text") : p.ValueType == typeof(Guid) ? null : JsonValue.Create("x");
                    if (rule.Class is FieldClass.Slot or FieldClass.Strip && (p.ValueType == typeof(Guid) || rule.Class == FieldClass.Slot))
                    {
                        string id = NewGuid(random);
                        planted.Add(id);
                        if (rule.Class == FieldClass.Slot)
                            slotsExpected++;
                        return JsonValue.Create(id);
                    }

                    return rule.Class == FieldClass.Text ? JsonValue.Create("hello text") : JsonValue.Create("x");
                });
                SnapshotFixtures.SetConfig(step, config);
                steps.Add(step);
            }

            PortableRecipeResult result = Build(steps);

            result.Refusals.Select(r => $"{r.Code}:{r.StepOrder}:{r.Message}").Should().BeEmpty();
            string json = result.CanonicalJson!;
            planted.Should().NotBeEmpty();
            foreach (string id in planted)
            {
                json.Should().NotContainEquivalentOf(id);
                json.Should().NotContainEquivalentOf(id.Replace("-", string.Empty));
            }

            result.Recipe!.Slots.Count.Should().BeGreaterThan(0);
            result.Recipe.Slots.Count.Should().Be(slotsExpected);
            result.Recipe.Slots.Select(s => s.Kind).Distinct().Should().BeEquivalentTo(
                [PortableSlotKinds.Provider, PortableSlotKinds.File, PortableSlotKinds.Voice]);
        }
    }

    [Fact]
    public void Source_media_egress_consent_is_forced_off_everywhere()
    {
        WorkflowStep generate = SnapshotFixtures.Step(1, StepType.VideoGenerate);
        SnapshotFixtures.SetConfig(generate, new JsonObject { ["model"] = "m", ["allowSourceMediaEgress"] = true });
        WorkflowStep analyze = SnapshotFixtures.Step(2, StepType.VideoAnalyze);
        SnapshotFixtures.SetConfig(analyze, new JsonObject { ["version"] = 1, ["allowSourceMediaEgress"] = true });

        PortableRecipeResult result = Build([generate, analyze]);

        result.Recipe!.Steps.Select(x => x.Config!.ToJsonString()).Should().OnlyContain(c => !c.Contains("llowSourceMediaEgress"));
        result.Recipe.Removed.Should().Contain(r => r.Field == "allowSourceMediaEgress");
    }

    [Fact]
    public void An_unrecognised_setting_is_dropped_not_copied()
    {
        WorkflowStep step = SnapshotFixtures.Step(1, StepType.Conditional);
        SnapshotFixtures.SetConfig(step, new JsonObject { ["version"] = 1, ["mystery"] = "visible?", ["decisionQuestion"] = "Is it good?" });

        PortableRecipeResult result = Build([step]);

        result.CanonicalJson.Should().NotContain("mystery").And.NotContain("visible?");
        result.Recipe!.Steps[0].Config!["decisionQuestion"]!.GetValue<string>().Should().Be("Is it good?");
    }

    [Fact]
    public void Provider_voice_and_file_ids_become_slots_with_words_but_no_ids()
    {
        Guid provider = Guid.NewGuid();
        Guid voice = Guid.NewGuid();
        Guid file = Guid.NewGuid();
        WorkflowStep vo = SnapshotFixtures.Step(1, StepType.Voiceover);
        SnapshotFixtures.SetConfig(vo, new JsonObject
        {
            ["providerId"] = provider.ToString(),
            ["defaultVoiceId"] = voice.ToString(),
            ["source"] = new JsonObject { ["kind"] = "Inline", ["lines"] = new JsonArray(new JsonObject { ["text"] = "Hello", ["startSec"] = 0, ["voiceId"] = voice.ToString() }) }
        });
        WorkflowStep analyze = SnapshotFixtures.Step(2, StepType.VideoAnalyze);
        SnapshotFixtures.SetConfig(analyze, new JsonObject { ["version"] = 1, ["source"] = new JsonObject { ["kind"] = "ProjectFile", ["projectFileId"] = file.ToString() } });

        PortableRecipeResult result = Build([vo, analyze]);

        result.Recipe!.Slots.Should().Contain(s => s.Kind == PortableSlotKinds.Provider && s.StepOrder == 1 && s.Field == "providerId");
        result.Recipe.Slots.Should().Contain(s => s.Kind == PortableSlotKinds.Voice && s.Field == "defaultVoiceId");
        result.Recipe.Slots.Should().Contain(s => s.Kind == PortableSlotKinds.Voice && s.Field == "source.lines[0].voiceId");
        result.Recipe.Slots.Should().Contain(s => s.Kind == PortableSlotKinds.File && s.StepOrder == 2 && s.Field == "source.projectFileId");
        result.Recipe.Removed.Should().OnlyContain(r => !r.What.Contains(provider.ToString()) && !r.Field.Contains(file.ToString()));
        result.Recipe.Steps[0].Config!["source"]!["lines"]![0]!["text"]!.GetValue<string>().Should().Be("Hello");
    }

    // ---------------------------------------------------------------- enum allowlist

    private static JsonNode? ReviewConfig(JsonNode value, string key = "mode")
    {
        WorkflowStep step = SnapshotFixtures.Step(1, StepType.Conditional);
        SnapshotFixtures.SetConfig(step, new JsonObject { [key] = value });
        return Build([step]).Recipe!.Steps[0].Config?[key];
    }

    [Fact]
    public void A_defined_enum_number_is_rewritten_to_its_name()
    {
        ReviewConfig(JsonValue.Create(1))!.GetValue<string>().Should().Be(nameof(ConditionMode.Decision));
        ReviewConfig(JsonValue.Create("decision"))!.GetValue<string>().Should().Be(nameof(ConditionMode.Decision));
    }

    [Theory]
    [InlineData("99")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("1e30")]
    [InlineData("true")]
    public void An_undefined_fractional_or_out_of_range_enum_number_is_dropped(string literal)
    {
        ReviewConfig(JsonNode.Parse(literal)!).Should().BeNull();
    }

    [Fact]
    public void Enum_values_that_are_objects_arrays_or_odd_strings_are_dropped()
    {
        ReviewConfig(new JsonObject { ["x"] = 1 }).Should().BeNull();
        ReviewConfig(new JsonArray(1)).Should().BeNull();
        ReviewConfig(JsonValue.Create("Nonsense")).Should().BeNull();
        ReviewConfig(JsonValue.Create("1,2")).Should().BeNull();
    }

    [Fact]
    public void An_undefined_enum_number_in_a_nested_config_is_dropped()
    {
        WorkflowStep step = SnapshotFixtures.Step(1, StepType.VideoAnalyze);
        SnapshotFixtures.SetConfig(step, new JsonObject { ["source"] = new JsonObject { ["kind"] = 77 }, ["vision"] = 0 });

        PortableRecipe recipe = Build([step]).Recipe!;

        recipe.Steps[0].Config!["source"]!.AsObject().ContainsKey("kind").Should().BeFalse();
        recipe.Steps[0].Config!["vision"]!.GetValue<string>().Should().Be("Off");
    }

    // ---------------------------------------------------------------- leak guard

    [Fact]
    public void The_leak_guard_catches_an_id_planted_in_free_text()
    {
        Guid planted = Guid.NewGuid();
        WorkflowStep step = SnapshotFixtures.Step(1, StepType.VideoGenerate);
        SnapshotFixtures.SetConfig(step, new JsonObject { ["inlinePrompt"] = $"A cat, see project {planted}" });

        PortableRecipeResult result = Build([step]);

        result.Recipe.Should().BeNull();
        result.Refusals.Should().ContainSingle(r => r.Code == SnapshotLeakGuard.RefusalCode && r.StepOrder == 1);
        result.Refusals.Select(r => r.Message).Should().OnlyContain(m => !m.Contains(planted.ToString()));
    }

    [Theory]
    [InlineData("see projects/abc12345/clip.mp4 please", "a storage path")]
    [InlineData("https://x.example/f?X-Amz-Signature=abc", "a signed link")]
    [InlineData("key sk-" + "abcdefghijklmnopqrstuvwxyz0123", "a secret")]
    [InlineData("id 123e4567e89b12d3a456426614174000 here", "an id")]
    [InlineData("a perfectly ordinary prompt about cats", null)]
    public void The_leak_guard_classifies_shapes(string text, string? expected)
    {
        SnapshotLeakGuard.Classify(text).Should().Be(expected);
    }

    [Fact]
    public void The_leak_guard_scans_keys_too()
    {
        string json = "{\"steps\":[{\"order\":3,\"stepType\":\"Agent\",\"" + Guid.NewGuid() + "\":1}]}";

        IReadOnlyList<PortableRefusal> refusals = SnapshotLeakGuard.Scan(json);

        refusals.Should().ContainSingle().Which.StepOrder.Should().Be(3);
    }

    // ---------------------------------------------------------------- refusals

    [Fact]
    public void A_custom_agent_refuses_with_its_step()
    {
        WorkflowStep step = SnapshotFixtures.Step(1, StepType.Agent, AgentType.ScriptwriterAgent);
        Dictionary<Guid, AgentDefinition> agents = new(SnapshotFixtures.BuiltIns());
        agents[step.AgentDefinitionId] = new AgentDefinition { Id = step.AgentDefinitionId, AgentType = AgentType.ScriptwriterAgent, IsBuiltIn = false };

        PortableRecipeResult result = Build([step], agents);

        result.Refusals.Should().ContainSingle(r => r.Code == "CUSTOM_AGENT" && r.StepOrder == 1);
        result.Recipe.Should().BeNull();
    }

    [Fact]
    public void An_unknown_agent_refuses_like_a_custom_one()
    {
        WorkflowStep step = SnapshotFixtures.Step(1, StepType.Agent);
        step.AgentDefinitionId = Guid.NewGuid();

        Codes(Build([step])).Should().Contain("CUSTOM_AGENT");
    }

    [Fact]
    public void A_custom_agent_in_a_parallel_list_refuses()
    {
        WorkflowStep step = SnapshotFixtures.Step(1, StepType.Parallel);
        Guid custom = Guid.NewGuid();
        step.ParallelAgentIdsJson = System.Text.Json.JsonSerializer.Serialize(new[] { SnapshotFixtures.BuiltInIds[AgentType.ScriptwriterAgent], custom });
        Dictionary<Guid, AgentDefinition> agents = new(SnapshotFixtures.BuiltIns())
        {
            [custom] = new AgentDefinition { Id = custom, AgentType = AgentType.ScriptwriterAgent, IsBuiltIn = false }
        };

        Codes(Build([step], agents)).Should().Contain("CUSTOM_AGENT");
    }

    [Fact]
    public void A_built_in_parallel_list_becomes_agent_types_without_ids()
    {
        WorkflowStep step = SnapshotFixtures.Step(1, StepType.Parallel);
        step.ParallelAgentIdsJson = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            SnapshotFixtures.BuiltInIds[AgentType.ScriptwriterAgent], SnapshotFixtures.BuiltInIds[AgentType.VideoTransform]
        });

        PortableRecipeResult result = Build([step]);

        result.Recipe!.Steps[0].ParallelAgentTypes.Should().Equal(AgentType.ScriptwriterAgent, AgentType.VideoTransform);
    }

    [Theory]
    [InlineData(StepType.EditRoom, "seats")]
    [InlineData(StepType.GraphicsRoom, "director")]
    [InlineData(StepType.ColorGradeRoom, "seats")]
    [InlineData(StepType.DailiesRoom, "director")]
    public void A_custom_agent_on_a_room_seat_or_director_refuses(StepType type, string where)
    {
        Guid custom = Guid.NewGuid();
        WorkflowStep step = SnapshotFixtures.Step(1, type);
        JsonObject config = where == "seats"
            ? new JsonObject { ["seats"] = new JsonArray(new JsonObject { ["name"] = "A", ["persona"] = "p", ["agentDefinitionId"] = custom.ToString() }) }
            : new JsonObject { ["directorAgentDefinitionId"] = custom.ToString() };
        SnapshotFixtures.SetConfig(step, config);
        Dictionary<Guid, AgentDefinition> agents = new(SnapshotFixtures.BuiltIns())
        {
            [custom] = new AgentDefinition { Id = custom, AgentType = AgentType.VideoStoryEditor, IsBuiltIn = false }
        };

        Codes(Build([step], agents)).Should().Contain("CUSTOM_AGENT");
    }

    [Fact]
    public void A_built_in_room_seat_agent_is_dropped_to_its_default()
    {
        Guid builtIn = SnapshotFixtures.BuiltInIds[AgentType.VideoStoryEditor];
        WorkflowStep step = SnapshotFixtures.Step(1, StepType.EditRoom);
        SnapshotFixtures.SetConfig(step, new JsonObject { ["seats"] = new JsonArray(new JsonObject { ["name"] = "A", ["persona"] = "p", ["agentDefinitionId"] = builtIn.ToString() }) });

        PortableRecipeResult result = Build([step]);

        result.Recipe.Should().NotBeNull();
        result.CanonicalJson.Should().NotContainEquivalentOf(builtIn.ToString());
    }

    [Theory]
    [InlineData(StepType.TimelineRender)]
    [InlineData(StepType.Revise)]
    [InlineData(StepType.DesignGraphic)]
    public void Refused_step_types_name_the_step_and_say_why(StepType type)
    {
        WorkflowStep step = SnapshotFixtures.Step(4, type);
        SnapshotFixtures.SetConfig(step, new JsonObject { ["editTimelineId"] = Guid.NewGuid().ToString() });

        PortableRecipeResult result = Build([SnapshotFixtures.Step(1, StepType.Agent), step]);

        result.Recipe.Should().BeNull();
        result.Refusals.Should().ContainSingle(r => r.Code == "STEP_TYPE_NOT_SHAREABLE" && r.StepOrder == 4 && r.Message.Length > 20);
    }

    [Theory]
    [InlineData(WorkflowDefinitionKind.CompositionRevision)]
    public void Hidden_workflow_kinds_refuse(WorkflowDefinitionKind kind)
    {
        PortableRecipeResult result = Build([SnapshotFixtures.Step(1, StepType.Agent)], workflow: SnapshotFixtures.Workflow(kind));

        Codes(result).Should().Equal("HIDDEN_WORKFLOW_KIND");
    }

    [Fact]
    public void Every_non_standard_workflow_kind_refuses()
    {
        foreach (WorkflowDefinitionKind kind in Enum.GetValues<WorkflowDefinitionKind>().Where(k => k != WorkflowDefinitionKind.Standard))
            Codes(Build([SnapshotFixtures.Step(1, StepType.Agent)], workflow: SnapshotFixtures.Workflow(kind))).Should().Equal("HIDDEN_WORKFLOW_KIND");
    }

    [Fact]
    public void Run_bound_sources_refuse()
    {
        WorkflowStep compile = SnapshotFixtures.Step(1, StepType.VideoCompile);
        SnapshotFixtures.SetConfig(compile, new JsonObject { ["analysisStepResultId"] = Guid.NewGuid().ToString(), ["analysisStepOrder"] = 1 });
        WorkflowStep thumb = SnapshotFixtures.Step(2, StepType.ThumbnailGenerate);
        SnapshotFixtures.SetConfig(thumb, new JsonObject { ["analysisStepResultId"] = Guid.NewGuid().ToString() });
        WorkflowStep analyze = SnapshotFixtures.Step(3, StepType.VideoAnalyze);
        SnapshotFixtures.SetConfig(analyze, new JsonObject { ["source"] = new JsonObject { ["kind"] = "StepResult", ["stepResultId"] = Guid.NewGuid().ToString() } });
        WorkflowStep agent = SnapshotFixtures.Step(4, StepType.Agent);
        agent.ContinueCompositionJson = $"{{\"executionId\":\"{Guid.NewGuid()}\"}}";
        WorkflowStep insert = SnapshotFixtures.Step(5, StepType.PinInsert);
        SnapshotFixtures.SetConfig(insert, new JsonObject { ["pins"] = new JsonArray(new JsonObject { ["content"] = new JsonObject { ["from"] = "StepResult", ["stepResultId"] = Guid.NewGuid().ToString() } }) });

        PortableRecipeResult result = Build([compile, thumb, analyze, agent, insert]);

        result.Recipe.Should().BeNull();
        result.Refusals.Where(r => r.Code == "RUN_BOUND_SOURCE").Select(r => r.StepOrder).Should().BeEquivalentTo([1, 2, 3, 4, 5]);
    }

    [Fact]
    public void A_step_kind_that_names_a_step_result_refuses_even_without_an_id()
    {
        WorkflowStep analyze = SnapshotFixtures.Step(1, StepType.VideoAnalyze);
        SnapshotFixtures.SetConfig(analyze, new JsonObject { ["source"] = new JsonObject { ["kind"] = "StepResult" } });

        Codes(Build([analyze])).Should().Contain("RUN_BOUND_SOURCE");
    }

    // ---------------------------------------------------------------- caps and canonical form

    [Fact]
    public void More_than_forty_steps_is_too_large()
    {
        List<WorkflowStep> steps = [.. Enumerable.Range(1, 41).Select(i => SnapshotFixtures.Step(i, StepType.Agent))];

        Codes(Build(steps)).Should().Equal("TOO_LARGE");
        Codes(Build(steps.Take(40).ToList())).Should().BeEmpty();
    }

    [Fact]
    public void A_text_longer_than_4000_characters_is_too_large()
    {
        WorkflowStep step = SnapshotFixtures.Step(1, StepType.VideoGenerate);
        SnapshotFixtures.SetConfig(step, new JsonObject { ["inlinePrompt"] = new string('a', 4001) });

        PortableRecipeResult result = Build([step]);

        result.Refusals.Should().ContainSingle(r => r.Code == "TOO_LARGE" && r.StepOrder == 1);
        Codes(Build([Labelled(new string('a', 4001))])).Should().Contain("TOO_LARGE");
        Codes(Build([Labelled(new string('a', 4000))])).Should().BeEmpty();
    }

    [Fact]
    public void A_snapshot_over_256_kb_is_too_large()
    {
        List<WorkflowStep> steps = [];
        for (int i = 1; i <= 40; i++)
        {
            WorkflowStep step = SnapshotFixtures.Step(i, StepType.Agent);
            step.Label = new string((char)('a' + (i % 26)), 4000);
            step.ConditionExpression = new string('b', 1900);
            step.LoopSourceExpression = new string('c', 1900);
            steps.Add(step);
        }

        PortableRecipeResult result = Build(steps);

        result.Refusals.Should().ContainSingle(r => r.Code == "TOO_LARGE" && r.Message.Contains("256 KB"));
    }

    [Fact]
    public void The_canonical_json_is_stable_whatever_the_key_order_of_the_input()
    {
        WorkflowStep a = SnapshotFixtures.Step(1, StepType.Conditional);
        SnapshotFixtures.SetConfig(a, new JsonObject { ["version"] = 1, ["decisionQuestion"] = "Q?", ["threshold"] = 0.5 });
        WorkflowStep b = SnapshotFixtures.Step(1, StepType.Conditional);
        SnapshotFixtures.SetConfig(b, new JsonObject { ["threshold"] = 0.5, ["decisionQuestion"] = "Q?", ["version"] = 1 });
        a.Label = b.Label = "same";

        string first = Build([a]).CanonicalJson!;
        string second = Build([b]).CanonicalJson!;

        second.Should().Be(first);
        Build([a]).CanonicalJson.Should().Be(first);
        PortableRecipe.CurrentSchemaVersion.Should().Be(1);
        first.Should().Contain("\"schemaVersion\":1");
    }

    [Fact]
    public void Re_walking_a_stripped_config_changes_nothing()
    {
        WorkflowStep step = SnapshotFixtures.Step(1, StepType.EditRoom);
        SnapshotFixtures.SetConfig(step, new JsonObject { ["seats"] = new JsonArray(new JsonObject { ["name"] = "N <b>x</b>", ["persona"] = "P" }), ["rounds"] = 3 });
        PortableRecipe recipe = Build([step]).Recipe!;

        WalkResult again = new();
        JsonNode? walked = ConfigWalker.Walk(recipe.Steps[0].Config, typeof(EditRoomStepConfig), 1, again);

        PortableRecipeJson.Canonicalize(walked)!.ToJsonString().Should().Be(PortableRecipeJson.Canonicalize(recipe.Steps[0].Config)!.ToJsonString());
        again.Removed.Should().BeEmpty();
        again.Slots.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- text

    [Fact]
    public void Text_fields_list_every_prompt_label_and_manifest_text()
    {
        WorkflowStep step = SnapshotFixtures.Step(2, StepType.Conditional);
        SnapshotFixtures.SetConfig(step, new JsonObject { ["decisionQuestion"] = "Is it funny?" });
        AppManifest manifest = new(Title: "My <b>app</b>", Tagline: "Does things", Inputs: [new AppManifestInput("text", Label: "Topic")]);

        PortableRecipe recipe = Build([step], manifest: manifest).Recipe!;
        IReadOnlyList<PortableTextField> texts = recipe.TextFields();

        texts.Should().Contain(t => t.StepOrder == 2 && t.Field == "decisionQuestion" && t.Text == "Is it funny?");
        texts.Should().Contain(t => t.StepOrder == 2 && t.Field == "label");
        texts.Should().Contain(t => t.Field == "manifest.title" && t.Text == "My app");
        texts.Should().Contain(t => t.Field == "manifest.inputs[0].label" && t.Text == "Topic");
    }

    [Fact]
    public void A_request_template_is_sanitised_like_other_manifest_text()
    {
        WorkflowStep step = SnapshotFixtures.Step(2, StepType.Conditional);
        SnapshotFixtures.SetConfig(step, new JsonObject { ["decisionQuestion"] = "Is it funny?" });
        AppManifest manifest = new(
            Version: AppManifestVocabulary.Version2,
            Title: "App",
            RequestTemplate: "Write about the topic, see https://user:pw@evil.example/x [[fence]] <b>bold</b>");

        PortableRecipeResult result = Build([step], manifest: manifest);

        result.Recipe.Should().NotBeNull(string.Join(";", result.Refusals.Select(r => r.Message)));
        string template = result.Recipe!.Manifest!.RequestTemplate!;
        template.Should().NotContain("pw@").And.NotContain("evil.example").And.NotContain("<b>");
        template.Should().Contain("Write about the topic");
        result.Recipe.TextFields().Should().Contain(t => t.Field == "manifest.requestTemplate");
    }

    [Theory]
    [InlineData("mail me@corp.com please")]
    [InlineData("for run 3f2b8c1e-5a4d-4e6f-9b7a-1c2d3e4f5a6b")]
    public void A_request_template_with_an_email_or_an_id_is_refused(string template)
    {
        WorkflowStep step = SnapshotFixtures.Step(2, StepType.Conditional);
        SnapshotFixtures.SetConfig(step, new JsonObject { ["decisionQuestion"] = "Is it funny?" });

        PortableRecipeResult result = Build([step], manifest: new AppManifest(Version: AppManifestVocabulary.Version2, RequestTemplate: template));

        result.Recipe.Should().BeNull();
        result.Refusals.Should().NotBeEmpty();
    }

    [Fact]
    public void A_model_slug_is_not_mistaken_for_a_secret_but_a_random_token_still_is()
    {
        SnapshotLeakGuard.Classify("minimax/h3-max/lip-sync/image-to-video").Should().BeNull();
        SnapshotLeakGuard.Classify("aB3dE9fG7hJ2kL5mN8pQ1rS4tU6vW0xYz").Should().Be("a secret");
    }

    [Theory]
    [InlineData("vendor/a1b2c3d4e5f6g7h8i9j0k1l2m3n4o5p6")]
    [InlineData("vendor-name-prefix/k3j9x2m7q1z8w4v6n5b0")]
    [InlineData("vendor/model/k3j9x2m7q1z8w4v6n5b0r2")]
    [InlineData("k3j9x2m7q1z8/w4v6n5b0r2t4/y6u8i0p3o5a7s9d1")]
    public void A_secret_shaped_like_a_model_slug_is_still_refused(string text)
    {
        SnapshotLeakGuard.Classify(text).Should().Be("a secret");
    }

    [Fact]
    public void Every_string_member_of_a_manifest_input_is_in_the_checked_texts()
    {
        AppManifest manifest = new(
            Version: AppManifestVocabulary.Version2,
            Inputs:
            [
                new AppManifestInput(
                    Kind: AppManifestVocabulary.KindInput,
                    Id: "pick",
                    Type: "choice",
                    Options: [new AppInputOption(System.Text.Json.JsonSerializer.SerializeToElement("opt-value"), "L", "H")],
                    ShowWhen: new AppInputShowWhen("other", System.Text.Json.JsonSerializer.SerializeToElement("eq-value")))
            ]);
        PortableRecipe recipe = new(PortableRecipe.CurrentSchemaVersion, [], manifest, [], []);
        IReadOnlyList<PortableTextField> texts = recipe.TextFields();
        texts.Should().Contain(t => t.Field == "manifest.inputs[0].options[0].value" && t.Text == "opt-value");
        texts.Should().Contain(t => t.Field == "manifest.inputs[0].showWhen.equals" && t.Text == "eq-value");
        texts.Should().Contain(t => t.Field == "manifest.inputs[0].id");
    }

    [Fact]
    public void A_published_recipe_never_carries_the_media_permission_field()
    {
        // docs/design/app-inputs.md "Permission to send media as an app field": the field is derived from the copy's own steps.
        WorkflowStep step = SnapshotFixtures.Step(2, StepType.Conditional);
        SnapshotFixtures.SetConfig(step, new JsonObject { ["decisionQuestion"] = "Is it funny?" });
        AppManifest manifest = new(
            Title: "App",
            Inputs:
            [
                new AppManifestInput("text", Label: "Topic"),
                new AppManifestInput(AppManifestVocabulary.KindInput, Label: MediaEgressPermission.Label, Id: MediaEgressPermission.InputId,
                    Type: "toggle", Required: true, Permission: MediaEgressPermission.Marker),
            ]);

        PortableRecipeResult result = Build([step], manifest: manifest);

        result.Recipe.Should().NotBeNull(string.Join(";", result.Refusals.Select(r => r.Message)));
        result.Recipe!.Manifest!.Inputs.Should().ContainSingle().Which.Label.Should().Be("Topic");
        result.Recipe.Manifest.Inputs!.Should().OnlyContain(i => i.Permission == null && i.Id != MediaEgressPermission.InputId);
    }

    [Theory]
    [InlineData("Hello‮world​!", "Helloworld!")]
    [InlineData("a <script>alert(1)</script> b", "a alert(1) b")]
    [InlineData("see [docs](https://evil.example/x) now", "see docs now")]
    [InlineData("go to https://evil.example/x or www.evil.example today", "go to or today")]
    [InlineData("tabs\tand   spaces\r\n\r\n\r\n\r\nand lines", "tabs and spaces\n\nand lines")]
    [InlineData("ctl\u0001\u0007x", "ctlx")]
    [InlineData("`code` here", "code here")]
    public void The_text_sanitiser_strips_control_bidi_markup_and_links(string input, string expected)
    {
        MarketplaceText.Sanitize(input).Should().Be(expected);
    }

    [Fact]
    public void The_text_sanitiser_never_throws_on_null_or_empty()
    {
        MarketplaceText.Sanitize(null).Should().BeEmpty();
        MarketplaceText.Sanitize("   ").Should().BeEmpty();
    }

    private static WorkflowStep Labelled(string label)
    {
        WorkflowStep step = SnapshotFixtures.Step(1, StepType.Agent);
        step.Label = label;
        return step;
    }

    private static string NewGuid(Random random)
    {
        byte[] bytes = new byte[16];
        random.NextBytes(bytes);
        return new Guid(bytes).ToString();
    }
}
