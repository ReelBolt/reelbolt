using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.OpenApi;
using ReelBolt.Shared.Auth;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace ReelBolt.Inference.Api.Services.PublicApi;

/// <summary>
/// The <c>public-v1</c> OpenAPI document ("ReelBolt Public API"): only controllers in the <c>public-v1</c>
/// group, the ApiKey bearer scheme, per-operation <c>x-required-scopes</c>, the <c>Idempotency-Key</c>
/// header on <see cref="IdempotentAttribute"/> actions and the rate-limit headers.
/// </summary>
public static class PublicApiOpenApi
{
    public const string DocumentName = "public-v1";
    public const string SecuritySchemeName = "ApiKey";

    public static bool InPublicGroup(ApiDescription api) => api.GroupName == DocumentName;

    /// <summary>The committed/served form: OpenAPI 3.0 JSON.</summary>
    public static async Task<string> RenderAsync(Swashbuckle.AspNetCore.Swagger.ISwaggerProvider swagger)
    {
        OpenApiDocument doc = swagger.GetSwagger(DocumentName);
        return await doc.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0);
    }

    public static void Configure(SwaggerGenOptions options)
    {
        options.SwaggerDoc(DocumentName, new OpenApiInfo { Title = "ReelBolt Public API", Version = "1.0.0" });
        options.DocInclusionPredicate((doc, api) => doc == DocumentName ? InPublicGroup(api) : !InPublicGroup(api));
        options.AddSecurityDefinition(SecuritySchemeName, new OpenApiSecurityScheme
        {
            Description = "API key as a bearer token (rbk_...).",
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "rbk_..."
        });
        options.AddOperationFilterInstance(new PublicApiOperationFilter());
        options.DocumentFilter<WebhookSchemasDocumentFilter>();
        options.DocumentFilter<ApiKeySecurityDocumentFilter>();
    }
}

/// <summary>Applies the public-only operation details; a no-op for the internal document.</summary>
public sealed class PublicApiOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (!PublicApiOpenApi.InPublicGroup(context.ApiDescription)) return;

        // The document itself is served anonymously and is not part of the document; everything else requires the key.
        if (context.ApiDescription.RelativePath?.EndsWith("openapi.json", StringComparison.OrdinalIgnoreCase) == true)
            return;

        IEnumerable<object> metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;
        string[] scopes = metadata.OfType<RequireScopeAttribute>().SelectMany(a => a.AnyOf).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray();
        operation.Extensions ??= new Dictionary<string, IOpenApiExtension>();
        operation.Extensions["x-required-scopes"] = new JsonNodeExtension(
            new System.Text.Json.Nodes.JsonArray(scopes.Select(s => (System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(s)).ToArray()));

        operation.Parameters ??= new List<IOpenApiParameter>();
        if (metadata.OfType<IdempotentAttribute>().Any(a => a.RedactFields.Length > 0))
        {
            operation.Description = (operation.Description is { Length: > 0 } d ? d + "\n\n" : "")
                + "A replay does not repeat the secret (it is null and `secretRedacted` is true); rotate it if lost.";
        }

        if (metadata.OfType<IdempotentAttribute>().Any())
        {
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "Idempotency-Key",
                In = ParameterLocation.Header,
                Required = false,
                Description = "1-200 printable ASCII characters. A repeat with the same key and body replays the first response (Idempotent-Replayed: true).",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = 200, MinLength = 1 },
            });
        }

        operation.Responses ??= new OpenApiResponses();
        foreach ((string code, string description) in new[]
                 {
                     ("401", "api_key_required: not an API key."),
                     ("429", "rate_limited: carries Retry-After and error.resetsAt."),
                 })
        {
            if (!operation.Responses.ContainsKey(code))
                operation.Responses[code] = new OpenApiResponse { Description = description };
        }

        foreach (OpenApiResponse response in operation.Responses.Values.OfType<OpenApiResponse>())
        {
            response.Headers ??= new Dictionary<string, IOpenApiHeader>();
            foreach (string header in new[] { "RateLimit-Limit", "RateLimit-Remaining", "RateLimit-Reset", "X-Request-Id" })
                response.Headers.TryAdd(header, new OpenApiHeader { Schema = new OpenApiSchema { Type = JsonSchemaType.String } });
        }

        // Q-2: every non-2xx response is the one error envelope, so no refusal is documented as an untyped body.
        IOpenApiSchema errorSchema = context.SchemaGenerator.GenerateSchema(
            typeof(PublicApiErrorEnvelope), context.SchemaRepository);
        foreach ((string status, IOpenApiResponse response) in operation.Responses)
        {
            if (!int.TryParse(status, out int code) || code < 400) continue;
            if (response is not OpenApiResponse typed) continue;
            typed.Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new OpenApiMediaType { Schema = errorSchema }
            };
        }
    }
}

/// <summary>
/// Publishes the webhook payload (<see cref="Webhooks.WebhookEventPayload"/>, its <c>data</c> and the event-type list)
/// as components of the public document: the payloads are not the response of any operation, so nothing else would
/// reference them.
/// </summary>
public sealed class WebhookSchemasDocumentFilter : IDocumentFilter
{
    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        if (swaggerDoc.Info?.Title != "ReelBolt Public API") return;

        context.SchemaGenerator.GenerateSchema(typeof(Webhooks.WebhookEventPayload), context.SchemaRepository);
        swaggerDoc.Components ??= new OpenApiComponents();
        swaggerDoc.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        foreach ((string name, IOpenApiSchema schema) in context.SchemaRepository.Schemas)
            swaggerDoc.Components.Schemas.TryAdd(name, schema);
    }
}

/// <summary>
/// Q-2: every public operation carries the <c>ApiKey</c> bearer requirement (the document endpoint, served
/// anonymously, does not). It is applied here, not per operation, because a scheme reference only serializes once it
/// is bound to the document that defines the scheme; one made inside an operation filter has no host and wrote <c>{}</c>.
/// </summary>
public sealed class ApiKeySecurityDocumentFilter : IDocumentFilter
{
    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        if (swaggerDoc.Info?.Title != "ReelBolt Public API" || swaggerDoc.Paths is null) return;

        // Only the ApiKey scheme belongs to this document: drop the dashboard's JWT scheme and the root requirement.
        swaggerDoc.Security = null;
        if (swaggerDoc.Components?.SecuritySchemes is { } schemes)
            foreach (string name in schemes.Keys.Where(n => n != PublicApiOpenApi.SecuritySchemeName).ToArray())
                schemes.Remove(name);

        // A refusal passes every extra of the source gate through (required, balance, plan, ...), so the error
        // body is open: the declared members are documented, any other member is allowed.
        if (swaggerDoc.Components?.Schemas is { } all
            && all.TryGetValue(nameof(PublicApiErrorBody), out IOpenApiSchema? body) && body is OpenApiSchema errorBody)
        {
            errorBody.AdditionalPropertiesAllowed = true;
            errorBody.AdditionalProperties = new OpenApiSchema();
        }

        foreach ((string path, IOpenApiPathItem item) in swaggerDoc.Paths)
        {
            if (path.EndsWith("openapi.json", StringComparison.OrdinalIgnoreCase) || item.Operations is null) continue;
            foreach (OpenApiOperation operation in item.Operations.Values)
            {
                operation.Security =
                [
                    new OpenApiSecurityRequirement
                    {
                        { new OpenApiSecuritySchemeReference(PublicApiOpenApi.SecuritySchemeName, swaggerDoc), new List<string>() }
                    }
                ];
            }
        }
    }
}
