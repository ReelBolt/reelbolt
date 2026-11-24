using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Tenancy;

/// <summary>
/// The design-system half of the cross-tenant matrix. Unlike the hand-listed surfaces in
/// <see cref="CrossTenantControllerMatrixTests"/>, the actions here are <b>discovered by reflection</b>
/// from both controllers, so a new design-system or library action cannot ship without being driven
/// against a foreign project / foreign library (404, never 403), and every project action must have
/// consulted <see cref="IProjectAccessService"/> exactly once. The DbContext carries a real
/// <see cref="ITenantContext"/> so the named Tenant filter is live.
/// </summary>
public sealed class DesignSystemCrossTenantMatrixTests : IDisposable
{
    private static readonly Guid HomeOrg = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000d1");
    private static readonly Guid ForeignOrg = Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000d2");

    private readonly TestTenancy.MutableTenant _tenant = new();
    private readonly InferenceApiDbContext _db;
    private readonly Project _mine;
    private readonly Project _theirs;
    private readonly DesignSystemLibrary _theirLibrary;
    private readonly DesignSystemVersion _theirVersion;

    public DesignSystemCrossTenantMatrixTests()
    {
        _db = new InferenceApiDbContext(
            new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase($"design-cross-tenant-{Guid.NewGuid():N}")
                .Options,
            _tenant);

        Guid owner = Guid.NewGuid();
        _mine = new Project { Id = Guid.NewGuid(), Name = "Mine", OwnerId = owner, OrganizationId = HomeOrg };
        _theirs = new Project { Id = Guid.NewGuid(), Name = "Theirs", OwnerId = Guid.NewGuid(), OrganizationId = ForeignOrg };
        var theirContainer = new DesignSystem
        {
            Id = Guid.NewGuid(),
            ProjectId = null,
            Kind = DesignSystemKinds.Design,
            Name = "Theirs",
            CreatedByUserId = owner,
        };
        _theirLibrary = new DesignSystemLibrary
        {
            Id = Guid.NewGuid(),
            OrganizationId = ForeignOrg,
            Name = "Theirs",
            DesignSystemId = theirContainer.Id,
            CreatedByUserId = owner,
        };
        _theirVersion = new DesignSystemVersion
        {
            Id = Guid.NewGuid(),
            DesignSystemId = theirContainer.Id,
            VersionNumber = 1,
            State = DesignVersionState.Draft,
            DocumentJson = "{}",
            DocumentDigest = "x",
            Origin = DesignVersionOrigin.Manual,
            CreatedByUserId = owner,
        };
        _db.Projects.AddRange(_mine, _theirs);
        _db.DesignSystems.Add(theirContainer);
        _db.DesignSystemLibraries.Add(_theirLibrary);
        _db.DesignSystemVersions.Add(_theirVersion);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private sealed class FakeUser(Guid? organizationId, OrganizationRole? role) : ICurrentUser
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public string Email { get; } = "u@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole { get; } = role;
    }

    private sealed class CountingAccess(IProjectAccessService inner) : IProjectAccessService
    {
        public int Calls { get; private set; }

        public Task<ProjectAccessResult> GetAsync(Guid projectId, ProjectPermission permission, CancellationToken ct)
        {
            Calls++;
            return inner.GetAsync(projectId, permission, ct);
        }

        public ProjectAccessResult Get(Guid projectId, ProjectPermission permission)
        {
            Calls++;
            return inner.Get(projectId, permission);
        }

        public ProjectAccessOutcome AuthorizeOrganization(ProjectPermission permission) =>
            inner.AuthorizeOrganization(permission);
    }

    private static List<MethodInfo> Actions(Type controller) => controller
        .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any())
        .OrderBy(m => m.Name, StringComparer.Ordinal)
        .ToList();

    private static object? DefaultFor(Type type) =>
        type == typeof(CancellationToken) ? CancellationToken.None
        : type.IsValueType ? Activator.CreateInstance(type)
        : null;

    private static async Task<object?> InvokeAsync(object controller, MethodInfo action, Func<ParameterInfo, object?> argument)
    {
        object?[] args = action.GetParameters().Select(argument).ToArray();
        object? task = action.Invoke(controller, args);
        if (task is not Task awaitable)
            return task;

        await awaitable;
        object? result = task.GetType().GetProperty("Result")?.GetValue(task);
        object? inner = result?.GetType().GetProperty("Result")?.GetValue(result);
        return inner ?? result;
    }

    private static bool IsNotFound(object? result) =>
        result is NotFoundResult or NotFoundObjectResult
        || (result is ObjectResult o && o.StatusCode == StatusCodes.Status404NotFound);

    private DesignSystemsController ProjectController(ICurrentUser user, IProjectAccessService access) => new(
        _db, user, access,
        new DesignSystemService(
            _db, Mock.Of<IFileStorageService>(), Mock.Of<IPlanGateService>(), NullLogger<DesignSystemService>.Instance),
        Mock.Of<IDesignSystemIngestService>(),
        NullLogger<DesignSystemsController>.Instance)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
    };

    [Fact]
    public async Task Every_design_system_action_answers_a_foreign_project_404_after_one_access_check()
    {
        List<MethodInfo> actions = Actions(typeof(DesignSystemsController));
        actions.Count.Should().BeGreaterThanOrEqualTo(24, "the matrix must not silently shrink");

        var failures = new List<string>();
        foreach (MethodInfo action in actions)
        {
            ICurrentUser user = new FakeUser(HomeOrg, OrganizationRole.Owner);
            _tenant.ActAsOrganization(HomeOrg);
            var access = new CountingAccess(new ProjectAccessService(_db, user));
            DesignSystemsController controller = ProjectController(user, access);

            action.GetParameters().Should().Contain(p => p.Name == "projectId", $"{action.Name} is project-scoped");

            object? result;
            try
            {
                result = await InvokeAsync(controller, action, p =>
                    p.Name == "projectId" ? _theirs.Id
                    : p.ParameterType == typeof(Guid) ? Guid.NewGuid()
                    : DefaultFor(p.ParameterType));
            }
            catch (Exception ex)
            {
                failures.Add($"{action.Name}: threw {ex.InnerException?.GetType().Name ?? ex.GetType().Name} before answering");
                continue;
            }

            if (!IsNotFound(result))
                failures.Add($"{action.Name}: foreign project answered {result?.GetType().Name ?? "null"}, expected 404");
            if (access.Calls != 1)
                failures.Add($"{action.Name}: consulted IProjectAccessService {access.Calls} times, expected exactly 1");
        }

        failures.Should().BeEmpty();
    }

    [Fact]
    public async Task A_low_role_on_the_callers_own_project_is_403_on_every_write_action()
    {
        var failures = new List<string>();
        foreach (MethodInfo action in Actions(typeof(DesignSystemsController)))
        {
            // Billing may not even view project content; the point is the 403 (not 404) distinction.
            ICurrentUser user = new FakeUser(HomeOrg, OrganizationRole.Billing);
            _tenant.ActAsOrganization(HomeOrg);
            DesignSystemsController controller = ProjectController(user, new ProjectAccessService(_db, user));

            object? result;
            try
            {
                result = await InvokeAsync(controller, action, p =>
                    p.Name == "projectId" ? _mine.Id
                    : p.ParameterType == typeof(Guid) ? Guid.NewGuid()
                    : DefaultFor(p.ParameterType));
            }
            catch (Exception ex)
            {
                failures.Add($"{action.Name}: threw {ex.InnerException?.GetType().Name ?? ex.GetType().Name}");
                continue;
            }

            bool forbidden = result is ForbidResult || (result is ObjectResult o && o.StatusCode == StatusCodes.Status403Forbidden);
            if (!forbidden)
                failures.Add($"{action.Name}: Billing role on the own project answered {result?.GetType().Name ?? "null"}, expected 403");
        }

        failures.Should().BeEmpty();
    }

    [Fact]
    public async Task Every_library_action_naming_a_foreign_library_is_404_never_403()
    {
        List<MethodInfo> actions = Actions(typeof(OrgDesignLibrariesController));
        actions.Count.Should().BeGreaterThanOrEqualTo(12, "the matrix must not silently shrink");

        var failures = new List<string>();
        int driven = 0;
        foreach (MethodInfo action in actions)
        {
            if (action.GetParameters().All(p => p.Name != "libraryId"))
                continue;

            ICurrentUser user = new FakeUser(HomeOrg, OrganizationRole.Owner);
            _tenant.ActAsOrganization(HomeOrg);
            var controller = new OrgDesignLibrariesController(
                _db, user,
                new DesignSystemService(
                    _db, Mock.Of<IFileStorageService>(), Mock.Of<IPlanGateService>(), NullLogger<DesignSystemService>.Instance),
                NullLogger<OrgDesignLibrariesController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            };

            driven++;
            object? result;
            try
            {
                result = await InvokeAsync(controller, action, p =>
                    p.Name == "libraryId" ? _theirLibrary.Id
                    : p.Name == "versionId" ? _theirVersion.Id
                    : p.ParameterType == typeof(Guid) ? Guid.NewGuid()
                    : p.ParameterType == typeof(bool) ? true
                    : DefaultFor(p.ParameterType));
            }
            catch (Exception ex)
            {
                failures.Add($"{action.Name}: threw {ex.InnerException?.GetType().Name ?? ex.GetType().Name}");
                continue;
            }

            if (!IsNotFound(result))
                failures.Add($"{action.Name}: foreign library answered {result?.GetType().Name ?? "null"}, expected 404");
        }

        driven.Should().BeGreaterThanOrEqualTo(10);
        failures.Should().BeEmpty();
    }
}
