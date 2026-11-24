using Microsoft.AspNetCore.Mvc;
using ReelBolt.Shared.Auth;

namespace ReelBolt.Inference.Api.Services.Auth;

/// <summary>
/// Abuse control (C5): a caller whose email is not verified cannot start a workflow run, which is what
/// stops throwaway signups from spending trial credit. Every Execute entry point calls this first.
/// </summary>
public static class EmailVerificationGate
{
    public const string ErrorCode = "EMAIL_NOT_VERIFIED";
    public const string Message = "Verify your email address before running workflows. Check your inbox for the verification link.";

    /// <summary>Null when the caller may run workflows, otherwise the 403 result to return.</summary>
    public static ObjectResult? Check(ICurrentUser user) =>
        user.EmailVerified
            ? null
            : new ObjectResult(new { code = ErrorCode, message = Message }) { StatusCode = StatusCodes.Status403Forbidden };
}
