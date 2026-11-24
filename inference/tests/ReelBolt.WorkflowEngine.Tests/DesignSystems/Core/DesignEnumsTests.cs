using System;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Core;

public class DesignEnumsTests
{
    [Fact]
    public void DesignVersionState_IsAppendOnlyAndOrdered()
    {
        Enum.GetNames<DesignVersionState>()
            .Should().Equal("Draft", "Approved", "Superseded", "Rejected");
    }

    [Fact]
    public void DesignVersionOrigin_IsAppendOnlyAndOrdered()
    {
        Enum.GetNames<DesignVersionOrigin>()
            .Should().Equal("Import", "StaticExtract", "LlmExtract", "Manual", "Merge", "Restore");
    }

    [Fact]
    public void DesignSourceKind_IsAppendOnlyAndOrdered()
    {
        Enum.GetNames<DesignSourceKind>()
            .Should().Equal("ProjectFile", "GitHubPath", "Upload", "Url", "Figma", "Manual");
    }

    [Fact]
    public void DesignSourceStatus_IsAppendOnlyAndOrdered()
    {
        Enum.GetNames<DesignSourceStatus>()
            .Should().Equal("Current", "Changed", "Missing");
    }

    [Fact]
    public void DesignProposalStatus_IsAppendOnlyAndOrdered()
    {
        Enum.GetNames<DesignProposalStatus>()
            .Should().Equal("Pending", "Approved", "Rejected", "Superseded", "Expired");
    }

    [Fact]
    public void DesignProposalTrigger_IsAppendOnlyAndOrdered()
    {
        Enum.GetNames<DesignProposalTrigger>()
            .Should().Equal("SourceChanged", "Manual", "Scheduled", "Import");
    }

    [Fact]
    public void DesignSystemKinds_AcceptsOnlyTheExactOrdinalValues()
    {
        DesignSystemKinds.Design.Should().Be("design");
        DesignSystemKinds.ProductMap.Should().Be("product_map");

        DesignSystemKinds.IsValid("design").Should().BeTrue();
        DesignSystemKinds.IsValid("product_map").Should().BeTrue();
        DesignSystemKinds.IsValid("Design").Should().BeFalse();
        DesignSystemKinds.IsValid("").Should().BeFalse();
        DesignSystemKinds.IsValid(null).Should().BeFalse();
    }
}
