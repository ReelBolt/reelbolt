using System.Linq;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows;

/// <summary>A recipe the plan would refuse is not "ready"; and a render downloads under a readable name.</summary>
public class RecipePlanNeedsTests
{
    private static readonly OrganizationEntitlementSet Trial = new()
    {
        RoomsAllowed = false,
        VideoGenAllowed = false,
        AudioGenAllowed = false,
        SegmentationAllowed = false
    };

    [Fact]
    public void A_room_recipe_reports_a_plan_need_on_a_plan_without_rooms()
    {
        WorkflowTemplateDefinition template = WorkflowTemplateCatalog.GetByKey("video-derush-edit-room")!;

        RecipeFit.PlanNeeds(template, Trial).Should().Contain(m => m.Need == "plan:rooms" && m.Action == "upgrade-plan");
        RecipeFit.PlanNeeds(template, new OrganizationEntitlementSet()).Should().BeEmpty();
    }

    [Fact]
    public void The_region_effect_recipe_reports_a_plan_need_when_segmentation_is_not_included()
    {
        WorkflowTemplateDefinition template = WorkflowTemplateCatalog.GetByKey(WorkflowTemplateCatalog.RegionEffectTemplateKey)!;

        RecipeFit.PlanNeeds(template, Trial).Should().NotBeEmpty();
        RecipeFit.PlanNeeds(template, new OrganizationEntitlementSet()).Should().BeEmpty();
    }

    [Fact]
    public void A_plain_recipe_has_no_plan_need_on_the_most_restricted_plan()
    {
        WorkflowTemplateDefinition template = WorkflowTemplateCatalog.GetByKey("video-subtitles")!;
        RecipeFit.PlanNeeds(template, Trial).Should().BeEmpty();
    }

    [Fact]
    public void A_render_stored_under_an_id_downloads_under_the_workflow_name()
    {
        string id = "0f8fad5b-d9cb-469f-a165-70867728950e.mp4";
        OutputsController.ReadableDownloadName(id, "Product promo v2").Should().Be("Product promo v2.mp4");
        OutputsController.ReadableDownloadName(id, "a/b\"c").Should().NotContain("/").And.NotContain("\"").And.EndWith(".mp4");
        OutputsController.ReadableDownloadName(id, null).Should().Be(id);
        OutputsController.ReadableDownloadName("my-reel.mp4", "Product promo").Should().Be("my-reel.mp4");
        OutputsController.ReadableSaveAs(id, "Product promo v2.mp4").Should().Be("Product promo v2.mp4");
        OutputsController.ReadableSaveAs("my-cut.mp4", "Product promo v2.mp4").Should().Be("my-cut.mp4");
        OutputsController.ReadableSaveAs(null, "Product promo v2.mp4").Should().BeNull();
    }
}
