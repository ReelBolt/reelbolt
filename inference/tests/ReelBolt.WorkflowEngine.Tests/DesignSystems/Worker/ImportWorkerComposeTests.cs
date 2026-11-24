#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Worker;

/// <summary>
/// SEC-2: the import worker parses hostile PDFs and zips next to a bearer token, so it must not share a network
/// with the datastores. Asserted on the shipped <c>docker-compose.yml</c> (read as text; CI runs
/// <c>docker compose config</c> for the schema): no published port, only the internal <c>import-net</c>, shared
/// with <c>inference</c> and nobody else, and a non-executable /tmp.
/// </summary>
public class ImportWorkerComposeTests
{
    private static readonly string Compose = ReadCompose();

    private static string ReadCompose()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "docker-compose.yml");
            if (File.Exists(candidate) && Directory.Exists(Path.Combine(dir.FullName, "inference")))
                return File.ReadAllText(candidate);
            dir = dir.Parent;
        }

        throw new FileNotFoundException("docker-compose.yml was not found above " + AppContext.BaseDirectory);
    }

    /// <summary>The lines of one top-level service (2-space indented key) up to the next service or section.</summary>
    private static string[] Service(string name)
    {
        string[] lines = Compose.Split('\n');
        int start = Array.FindIndex(lines, l => l.TrimEnd() == $"  {name}:");
        start.Should().BeGreaterThanOrEqualTo(0, $"service '{name}' must exist");
        List<string> block = new();
        for (int i = start + 1; i < lines.Length; i++)
        {
            if (Regex.IsMatch(lines[i], "^  [A-Za-z0-9_-]+:\\s*$") || Regex.IsMatch(lines[i], "^[A-Za-z]"))
                break;
            block.Add(lines[i]);
        }

        return block.ToArray();
    }

    private static string[] NetworksOf(string[] service)
    {
        int at = Array.FindIndex(service, l => l.TrimEnd() == "    networks:");
        if (at < 0)
            return Array.Empty<string>();

        List<string> names = new();
        for (int i = at + 1; i < service.Length; i++)
        {
            string line = service[i];
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#'))
                continue;

            Match item = Regex.Match(line, "^      - ([A-Za-z0-9_-]+)\\s*(#.*)?$");
            if (!item.Success)
                break;
            names.Add(item.Groups[1].Value);
        }

        return names.ToArray();
    }

    [Fact]
    public void Import_worker_is_only_on_the_internal_import_net()
        => NetworksOf(Service("import-worker")).Should().Equal("import-net");

    [Fact]
    public void Import_worker_publishes_no_port_and_exposes_nothing_extra()
    {
        string[] worker = Service("import-worker");

        worker.Should().NotContain(l => Regex.IsMatch(l, "^    ports:"));
        worker.Should().NotContain(l => Regex.IsMatch(l, "^    expose:"));
        worker.Should().NotContain(l => Regex.IsMatch(l, "^    network_mode:"));
    }

    [Fact]
    public void Import_net_is_declared_internal()
    {
        string[] lines = Compose.Split('\n');
        int networks = Array.FindIndex(lines, l => l.TrimEnd() == "networks:");
        networks.Should().BeGreaterThanOrEqualTo(0);
        int net = Array.FindIndex(lines, networks, l => l.TrimEnd() == "  import-net:");
        net.Should().BeGreaterThan(networks);

        lines[net + 1].Trim().Should().Be("internal: true");
    }

    [Fact]
    public void Only_inference_shares_import_net_with_the_worker()
    {
        string[] lines = Compose.Split('\n');
        List<string> members = new();
        foreach (string line in lines.Where(l => Regex.IsMatch(l, "^  [A-Za-z0-9_-]+:\\s*$")))
        {
            string name = line.Trim().TrimEnd(':');
            if (name is "reelbolt" or "import-net" or "caddy_certs")
                continue;

            string[] service;
            try
            {
                service = Service(name);
            }
            catch (Exception)
            {
                continue;
            }

            if (NetworksOf(service).Contains("import-net"))
                members.Add(name);
        }

        members.Should().BeEquivalentTo(new[] { "import-worker", "inference" });
        NetworksOf(Service("inference")).Should().Contain("reelbolt", "inference still reaches the datastores");
    }

    [Fact]
    public void Import_worker_tmpfs_is_not_executable()
    {
        string tmpfs = Service("import-worker").Single(l => l.Contains("/tmp:", StringComparison.Ordinal));

        tmpfs.Should().Contain("noexec").And.Contain("nosuid");
    }

    [Fact]
    public void Import_worker_keeps_its_isolation_flags_and_holds_no_volumes_or_env_files()
    {
        string[] worker = Service("import-worker");

        worker.Should().Contain(l => l.Trim() == "read_only: true");
        worker.Should().Contain(l => l.Trim() == "- ALL");
        worker.Should().Contain(l => l.Trim() == "- no-new-privileges:true");
        worker.Should().NotContain(l => Regex.IsMatch(l, "^    volumes:"));
        worker.Should().NotContain(l => Regex.IsMatch(l, "^    env_file:"));
    }
}
