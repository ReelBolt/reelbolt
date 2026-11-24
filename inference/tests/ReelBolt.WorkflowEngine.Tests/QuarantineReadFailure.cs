using System;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.EntityFrameworkCore.Query;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Data;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Fails every query on the project's files while armed, which is the first read the quarantine state makes. Used to
/// prove that a state that cannot be read means "do not reuse".
/// </summary>
internal sealed class FailProjectFileReads : IQueryExpressionInterceptor
{
    public bool Armed { get; set; }

    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
    {
        if (Armed && RootFinder.ReadsProjectFiles(queryExpression))
            throw new InvalidOperationException("db down");
        return queryExpression;
    }

    /// <summary>Arms the failure for a context that has already compiled the state read: a compiled query is cached and would skip the interceptor.</summary>
    public void ArmFor(DbContext db)
    {
        ((MemoryCache)((IInfrastructure<IServiceProvider>)db).GetService<IMemoryCache>()).Compact(1.0);
        Armed = true;
    }

    /// <summary>
    /// Options over the in-memory database <paramref name="name"/> in <paramref name="root"/>: contexts built from the
    /// same root see the same data even though each has its own service provider.
    /// </summary>
    public static DbContextOptions<WorkflowEngineDbContext> Options(string name, IInterceptor interceptor, InMemoryDatabaseRoot? root = null) =>
        new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(name, root ?? new InMemoryDatabaseRoot())
            .EnableServiceProviderCaching(false)
            .AddInterceptors(interceptor)
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;

    private sealed class RootFinder : ExpressionVisitor
    {
        private bool _found;

        public static bool ReadsProjectFiles(Expression expression)
        {
            RootFinder finder = new();
            finder.Visit(expression);
            return finder._found;
        }

        protected override Expression VisitExtension(Expression node)
        {
            if (node is EntityQueryRootExpression root && root.EntityType.ClrType == typeof(ProjectFile))
                _found = true;
            return base.VisitExtension(node);
        }
    }
}
