using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The import worker (the only place uploaded zip and PDF bytes are parsed) runs on the control host only. The media
/// host layers docker-compose.media.yml over docker-compose.yml and runs just the render services, so every other
/// service the base file defines is switched off there with the <c>media-disabled</c> profile. A new service without
/// the line would start on the render box.
/// </summary>
public sealed class ComposeMediaProfileTests
{
    private static string RepoFile(string name)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, name);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not locate {name} above {AppContext.BaseDirectory}.");
    }

    /// <summary>
    /// The profiles of each service under the top-level <c>services:</c> key. The test project has no YAML library,
    /// and the compose files use one fixed shape (two-space service keys, a flow-sequence <c>profiles: ["a", "b"]</c>
    /// at four spaces, optionally behind a compose tag such as <c>!override</c> or <c>!reset</c>), so this reads
    /// that shape by indentation and fails loudly on anything else.
    /// </summary>
    private static Dictionary<string, List<string>> ServiceProfiles(string path)
    {
        Dictionary<string, List<string>> services = [];
        bool inServices = false;
        string? current = null;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0 || line.TrimStart().StartsWith('#')) continue;

            int indent = line.Length - line.TrimStart().Length;
            if (indent == 0)
            {
                inServices = line == "services:";
                current = null;
                continue;
            }

            if (!inServices) continue;

            if (indent == 2 && line.EndsWith(':'))
            {
                current = line.Trim().TrimEnd(':');
                services[current] = [];
            }
            else if (indent == 4 && current is not null && line.TrimStart().StartsWith("profiles:", StringComparison.Ordinal))
            {
                string flow = line.TrimStart()["profiles:".Length..].Trim();
                // A compose YAML tag prefixes the value: `!override ["a"]` replaces the list the base file
                // declared, `!reset []` empties it. Strip the tag, then read the flow sequence that follows —
                // an emptied list means the service carries no profiles here, so it runs on this host.
                if (flow.StartsWith('!'))
                {
                    int space = flow.IndexOf(' ');
                    flow = space < 0 ? string.Empty : flow[(space + 1)..].Trim();
                }

                if (flow == "[]") continue; // emptied: the service runs on this host
                flow.Should().StartWith("[").And.EndWith("]", $"{current}'s profiles in {Path.GetFileName(path)} must be a one-line flow sequence");
                services[current].AddRange(flow.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(p => p.Trim('"', '\'')));
            }
        }

        return services;
    }

    [Fact]
    public void The_media_override_lists_import_worker_under_the_media_disabled_profile()
    {
        Dictionary<string, List<string>> media = ServiceProfiles(RepoFile("docker-compose.media.yml"));

        media.Should().ContainKey("import-worker", "the media override must name the worker to switch it off");
        media["import-worker"].Should().Contain("media-disabled");
    }

    [Fact]
    public void A_tagged_profiles_line_is_read_rather_than_skipped()
    {
        Dictionary<string, List<string>> media = ServiceProfiles(RepoFile("docker-compose.media.yml"));

        // `grafana` is written as `profiles: !override ["media-disabled"]`. The tag has to be stripped and the
        // list read: a parser that ignored any tagged line would still pass the import-worker test above while
        // silently losing every service the tag is used on.
        media.Should().ContainKey("grafana", "the media override must switch grafana off");
        media["grafana"].Should().Contain("media-disabled");
    }

    [Fact]
    public void The_base_compose_file_defines_import_worker_without_the_media_disabled_profile()
    {
        Dictionary<string, List<string>> control = ServiceProfiles(RepoFile("docker-compose.yml"));

        control.Should().ContainKey("import-worker");
        control["import-worker"].Should().NotContain("media-disabled", "the worker must keep running on the control host");
    }
}
