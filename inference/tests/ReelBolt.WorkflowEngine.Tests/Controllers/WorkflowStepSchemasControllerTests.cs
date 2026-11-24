using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Controllers;

public class WorkflowStepSchemasControllerTests
{
    [Fact]
    public async Task Get_ReturnsListWithOneEntryPerStepType()
    {
        // Arrange
        var exporter = new StepConfigSchemaExporter();
        var controller = new WorkflowStepSchemasController(exporter);

        // Act
        var result = await controller.List(CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsAssignableFrom<List<WorkflowStepSchemaDto>>(okResult.Value);

        var expectedCount = Enum.GetValues<StepType>().Length;
        Assert.Equal(expectedCount, list.Count);
    }

    [Fact]
    public async Task Get_IncludesAtLeastOneEntryWithConfigTrue()
    {
        // Arrange
        var exporter = new StepConfigSchemaExporter();
        var controller = new WorkflowStepSchemasController(exporter);

        // Act
        var result = await controller.List(CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsAssignableFrom<List<WorkflowStepSchemaDto>>(okResult.Value);

        var withConfig = list.FirstOrDefault(e => e.HasConfig);
        Assert.NotNull(withConfig);
        Assert.NotNull(withConfig.Schema);
    }

    [Fact]
    public async Task GetByStepType_WithValidStepType_ReturnsSingleEntry()
    {
        // Arrange
        var exporter = new StepConfigSchemaExporter();
        var controller = new WorkflowStepSchemasController(exporter);

        // Act
        var result = await controller.Get("VideoAnalyze", CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var entry = Assert.IsType<WorkflowStepSchemaDto>(okResult.Value);
        Assert.Equal("VideoAnalyze", entry.StepTypeName);
        Assert.True(entry.HasConfig);
    }

    [Fact]
    public async Task GetByStepType_WithInvalidStepType_Returns404()
    {
        // Arrange
        var exporter = new StepConfigSchemaExporter();
        var controller = new WorkflowStepSchemasController(exporter);

        // Act
        var result = await controller.Get("InvalidStepType", CancellationToken.None);

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Theory]
    [InlineData("Agent")]
    [InlineData("ForEach")]
    [InlineData("Parallel")]
    public async Task GetByStepType_WithNoConfigStepType_Returns200WithNoConfigAndNullSchema(string stepTypeName)
    {
        // Arrange
        var exporter = new StepConfigSchemaExporter();
        var controller = new WorkflowStepSchemasController(exporter);

        // Act
        var result = await controller.Get(stepTypeName, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var entry = Assert.IsType<WorkflowStepSchemaDto>(okResult.Value);
        Assert.Equal(stepTypeName, entry.StepTypeName);
        Assert.False(entry.HasConfig);
        Assert.Null(entry.Schema);
    }
}
