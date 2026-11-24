using Microsoft.AspNetCore.Mvc;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// The REST answer to <see cref="ModelUnpricedException"/> (docs/metering-and-billing.md, "Unpriced models fail closed"):
/// <c>409 {code: "MODEL_UNPRICED", message}</c>. A catalog gap is a state the caller can act on (choose another model,
/// ask an administrator for a price), never a server fault, so it is never a 5xx.
/// </summary>
internal static class ModelUnpricedResults
{
    public static ConflictObjectResult Conflict(ModelUnpricedException refusal) =>
        new(new { code = ModelUnpricedException.FailureCode, message = refusal.Message });
}
