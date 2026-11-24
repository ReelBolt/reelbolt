namespace ReelBolt.Shared.Editing.Color;

/// <summary>How far a camera profile has been checked against real footage.</summary>
public enum CameraProfileStatus
{
    /// <summary>Formula taken from the maker's document, not yet checked on real footage.</summary>
    Uncalibrated,

    /// <summary>An explicitly approximate stand-in: no public maker specification exists.</summary>
    Approximate,

    /// <summary>Checked against real footage from the camera.</summary>
    Calibrated,
}

/// <summary>
/// One camera log profile: the published log curve, the gamut matrix to Rec.709 and the signal
/// conventions of the files the camera writes. Pure data plus one pure function; no I/O.
/// </summary>
/// <param name="Id">Stable camelCase identifier, persisted by name.</param>
/// <param name="DisplayName">Factual maker name of the curve, for example "Apple Log".</param>
/// <param name="Maker">The camera maker.</param>
/// <param name="Status">How far the profile has been verified.</param>
/// <param name="ExpectedRange">The signal range the profile expects, "full" or "legal"; the profile decides, not the file tag.</param>
/// <param name="ExpectedYuvMatrix">The YUV matrix the profile expects, "bt709" or "bt2020nc"; the profile decides, not the file tag.</param>
/// <param name="DecodeToLinear">Normalised code value 0..1 to scene linear, where 0.18 is 18% grey.</param>
/// <param name="GamutToRec709">Row-major 3x3 matrix taking linear camera-gamut RGB to linear Rec.709 RGB.</param>
/// <param name="SourceNote">The maker document (name and version) the constants come from.</param>
public sealed record CameraProfileDefinition(
    string Id,
    string DisplayName,
    string Maker,
    CameraProfileStatus Status,
    string ExpectedRange,
    string ExpectedYuvMatrix,
    Func<double, double> DecodeToLinear,
    double[] GamutToRec709,
    string SourceNote);
