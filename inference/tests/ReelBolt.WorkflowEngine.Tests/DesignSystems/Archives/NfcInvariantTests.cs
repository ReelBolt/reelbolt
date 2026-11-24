#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using ReelBolt.Shared.Archives;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Archives;

/// <summary>
/// The import worker ships ICU (T-021), so canonically-equal spellings are merged after NFC. These cases are
/// mode-aware: the [IcuFact] ones need normalisation data, the [InvariantFact] one proves the reader fails closed
/// when a misconfigured image runs without it. <see cref="ForcedInvariantRunTests"/> runs the class in both modes.
/// </summary>
public class NfcInvariantTests
{
    private static SafeZipResult R(byte[] zip) => SafeZipReader.Read(new MemoryStream(zip), new SafeZipLimits());

    [IcuFact]
    public void Nfc_and_nfd_spellings_land_once_with_one_duplicate()
    {
        SafeZipResult result = R(ZipFixtures.Build(
            ("caf\u00e9.css", Encoding.UTF8.GetBytes("precomposed")),
            ("cafe\u0301.css", Encoding.UTF8.GetBytes("decomposed"))));

        result.Entries.Should().ContainSingle().Which.Path.Should().Be("caf\u00e9.css");
        result.Rejected.Should().ContainSingle().Which.Reason.Should().Be("DUPLICATE_PATH");
    }

    [IcuFact]
    public void Singleton_canonical_equivalents_land_once()
    {
        // U+212B ANGSTROM SIGN canonically equals U+00C5; no combining mark is involved at all.
        SafeZipResult result = R(ZipFixtures.Build(
            ("\u212b.txt", Encoding.UTF8.GetBytes("angstrom")),
            ("\u00c5.txt", Encoding.UTF8.GetBytes("a-ring"))));

        result.Entries.Should().ContainSingle();
        result.Rejected.Should().ContainSingle().Which.Reason.Should().Be("DUPLICATE_PATH");
    }

    [IcuFact]
    public void Hangul_jamo_and_syllable_land_once()
    {
        SafeZipResult result = R(ZipFixtures.Build(
            ("\u1112\u1161\u11ab.txt", Encoding.UTF8.GetBytes("jamo")),
            ("\ud55c.txt", Encoding.UTF8.GetBytes("syllable"))));

        result.Entries.Should().ContainSingle();
        result.Rejected.Should().ContainSingle().Which.Reason.Should().Be("DUPLICATE_PATH");
    }

    [InvariantFact]
    public void Without_normalisation_data_every_non_ascii_name_is_refused_fail_closed()
    {
        SafeZipResult result = R(ZipFixtures.Build(
            ("caf\u00e9.css", Encoding.UTF8.GetBytes("precomposed")),
            ("cafe\u0301.css", Encoding.UTF8.GetBytes("decomposed")),
            ("plain.css", Encoding.UTF8.GetBytes("a{}"))));

        result.Entries.Should().ContainSingle().Which.Path.Should().Be("plain.css");
        result.Rejected.Should().HaveCount(2);
        result.Rejected.Should().OnlyContain(r => r.Reason == "NORMALIZATION_UNAVAILABLE");
    }

    [Fact]
    public void Ascii_names_are_unaffected()
    {
        SafeZipResult result = R(ZipFixtures.Build(("a/Theme.css", Encoding.UTF8.GetBytes("a{}"))));

        result.Entries.Should().ContainSingle().Which.Path.Should().Be("a/Theme.css");
    }
}

public class ForcedInvariantRunTests
{
    private static async System.Threading.Tasks.Task<string> RunChildAsync(string invariantValue, string filter = "DesignSystems.Archives.NfcInvariantTests")
    {
        string dll = typeof(NfcInvariantTests).Assembly.Location;
        ProcessStartInfo start = new("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("test");
        start.ArgumentList.Add(dll);
        start.ArgumentList.Add("--filter");
        start.ArgumentList.Add("FullyQualifiedName~" + filter);
        start.Environment["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = invariantValue;

        using Process process = Process.Start(start)!;
        System.Threading.Tasks.Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        System.Threading.Tasks.Task<string> stderr = process.StandardError.ReadToEndAsync();
        using System.Threading.CancellationTokenSource limit = new(TimeSpan.FromSeconds(180));
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("the child test host did not finish in 180 s");
        }

        string output = await stdout + await stderr;
        process.ExitCode.Should().Be(0, output);
        return output;
    }

    [Fact]
    public async System.Threading.Tasks.Task Forced_invariant_mode_refuses_non_ascii_names_fail_closed()
    {
        string output = await RunChildAsync("1");

        // fail-closed case + ASCII case ran; the three ICU cases were skipped, not silently passed.
        output.Should().MatchRegex(@"Passed:\s+2\b", "the fail-closed case and the ASCII case must really have run");
        output.Should().MatchRegex(@"Skipped:\s+3\b");
    }

    [Fact]
    public async System.Threading.Tasks.Task With_the_workers_real_settings_nfc_and_nfd_no_longer_both_land()
    {
        // The worker image sets DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false and ships ICU.
        string output = await RunChildAsync("false");

        output.Should().MatchRegex(@"Passed:\s+4\b", "the three NFC cases and the ASCII case must really have run");
        output.Should().MatchRegex(@"Skipped:\s+1\b");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("false")]
    public async System.Threading.Tasks.Task The_api_side_check_and_the_worker_name_handling_pass_in_both_modes(string invariantValue)
    {
        // The API image runs invariant: its check of the worker answer must pass there. The worker side runs with ICU.
        foreach (string filter in new[] { "DesignSystems.Worker.ImportWorkerClientResponseTests", "DesignSystems.Worker.OrdinalNameDecisionTests", "DesignSystems.Worker.ExtractZipHandlerTests" })
        {
            string output = await RunChildAsync(invariantValue, filter);
            output.Should().MatchRegex(@"Passed:\s+[1-9]", filter);
        }
    }
}
