using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using ReelBolt.Inference.Api.Data;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// Structural guards for decision D-7 ("a brand change is only ever approved by a person"). They do not exercise
/// behaviour; they fail when the SHAPE that makes D-7 true is changed: no auto-approve switch exists on the
/// design-system data model, and the only code that marks a version Approved is the one approval routine the
/// person-gated routes and the assistant's gated tool all call.
/// </summary>
public class AssistantDesignSystemD7GuardTests
{
    private static string Normalise(string name) => name.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();

    [Fact]
    public void No_design_system_entity_or_column_is_an_auto_approve_switch()
    {
        using InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        string[] banned = ["autoapprove", "autoapply", "autoapproval", "autopromote"];
        IEntityType[] designEntities = db.Model.GetEntityTypes()
            .Where(e => e.ClrType.Name.StartsWith("DesignSystem", StringComparison.Ordinal)
                || e.ClrType.Name.StartsWith("DesignVersion", StringComparison.Ordinal)
                || (e.GetTableName() ?? string.Empty).StartsWith("design_system", StringComparison.Ordinal))
            .ToArray();

        designEntities.Should().NotBeEmpty("the guard must actually be looking at the design-system model");

        var offenders = new List<string>();
        foreach (IEntityType entity in designEntities)
        {
            foreach (string name in new[] { entity.ClrType.Name }.Concat(entity.GetProperties().SelectMany(p => new[] { p.Name, p.GetColumnName() })))
            {
                if (banned.Any(b => Normalise(name).Contains(b, StringComparison.Ordinal)))
                    offenders.Add($"{entity.ClrType.Name}.{name}");
            }
        }

        offenders.Should().BeEmpty("D-7: nothing on the design-system model may approve or promote a version by itself");
    }

    private static string RepoSrc()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string src = Path.Combine(dir.FullName, "src", "ReelBolt.Inference.Api");
            if (Directory.Exists(src))
                return Path.Combine(dir.FullName, "src");
        }

        throw new InvalidOperationException("inference/src was not found above the test binaries.");
    }

    /// <summary>
    /// A deliberately narrow source scan: it looks for the spelling <c>State = DesignVersionState.Approved</c> (an
    /// assignment or object initializer; comparisons use <c>==</c> or a pattern and do not match) in every
    /// non-generated C# file under inference/src. A write spelled differently (a variable, raw SQL) would not be
    /// seen: the behavioural D-7 tests cover those, this one catches the obvious regression of a second writer.
    /// </summary>
    [Fact]
    public void Only_ApplyApprovalAsync_assigns_the_Approved_state()
    {
        var assignment = new Regex(@"\bState\s*=(?!=)\s*DesignVersionState\.Approved\b", RegexOptions.Compiled);
        var hits = new List<string>();
        foreach (string file in Directory.EnumerateFiles(RepoSrc(), "*.cs", SearchOption.AllDirectories))
        {
            string normalised = file.Replace('\\', '/');
            if (normalised.Contains("/obj/") || normalised.Contains("/bin/") || normalised.Contains("/Migrations/"))
                continue;

            string text = File.ReadAllText(file);
            foreach (Match match in assignment.Matches(text))
            {
                string before = text[..match.Index];
                int method = before.LastIndexOf("Task ApplyApprovalAsync(", StringComparison.Ordinal);
                bool insideApply = method >= 0
                    && Path.GetFileName(file) == "DesignSystemService.cs"
                    && !Regex.IsMatch(before[method..][1..], @"\n    (public|private|internal|protected)\b");
                if (!insideApply)
                    hits.Add($"{Path.GetRelativePath(RepoSrc(), file)}:{before.Count(c => c == '\n') + 1}");
            }
        }

        hits.Should().BeEmpty("only DesignSystemService.ApplyApprovalAsync may mark a design-system version Approved (D-7)");

        // And the scan is alive: the one legitimate writer is found, so a refactor that moves it cannot silently blind the guard.
        string service = File.ReadAllText(Path.Combine(RepoSrc(), "ReelBolt.Inference.Api", "Services", "DesignSystems", "DesignSystemService.cs"));
        assignment.Matches(service).Should().HaveCount(1);
    }
}
