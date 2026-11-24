#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Promo.Plates.Motion;

/// <summary>
/// T-014 (initiative insert-tracking-smoothness): is F2 smoothing away real plate motion on the raw promo plates? Everything under this folder is measurement
/// code only (no production code). Paths: scratch and caches in <c>/tmp/insert-tracking/plate-motion</c>, the per-label artifacts of the T-011 plate harness in
/// <c>/tmp/insert-tracking/plates/&lt;label&gt;/artifact-film{A,B}.json</c> (the shipped label is the staging artifact itself).
/// </summary>
internal static class MotionEnv
{
    public static string Out => Environment.GetEnvironmentVariable("REELBOLT_MOTION_DIR") is { Length: > 0 } d ? Dir(d) : Dir("/tmp/insert-tracking/plate-motion");

    public static string Cache => Dir(Path.Combine(Out, "cache"));

    public static string PlatesRoot => Environment.GetEnvironmentVariable("REELBOLT_PLATES_DIR") is { Length: > 0 } d ? d : PlatesEnv.Root;

    public static readonly string[] Labels = ["shipped", "f1-decode-only", "f2-smoother-only", "f1f2"];

    public static string ArtifactPath(string label, PlateCase c) =>
        label == "shipped" ? c.StagingArtifact : Path.Combine(PlatesRoot, label, $"artifact-{c.Id}.json");

    /// <summary>The chroma pseudo-truth cache of the T-011 harness is read as is (it never depends on the code under test).</summary>
    public static PlatesEnv PlatesEnvironment() => new("plate-motion", Dir(Path.Combine(Out, "plates-out")), Dir(Path.Combine(Out, "plates-scratch")), Path.Combine(PlatesRoot, "cache"));

    private static string Dir(string d)
    {
        Directory.CreateDirectory(d);
        return d;
    }

    public static string Stages => Environment.GetEnvironmentVariable("REELBOLT_MOTION_STAGES") is { Length: > 0 } s ? s : "all";

    public static bool Stage(string name) => Stages == "all" || Stages.Split(',').Contains(name);
}
