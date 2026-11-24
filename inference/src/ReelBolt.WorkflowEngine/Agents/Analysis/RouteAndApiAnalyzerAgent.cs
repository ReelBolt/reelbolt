using Microsoft.Extensions.AI;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.WorkflowEngine.Agents.Tools;

namespace ReelBolt.WorkflowEngine.Agents.Analysis;

public class RouteAndApiAnalyzerAgent : ReelBoltAgentBase
{
    private const string DefaultPrompt =
        """
        You are a routing and API analysis expert. Extract all routes, API endpoints, and
        navigation structure from the web application.

        ## Tools

          You have tools to locate and inspect routing and API files — use them:
          1. Call `ListProjectFiles` to get the full list of available project files.
          2. Call `SearchProjectFiles` with focused queries ("route", "router", "api", "middleware", "handler").
          3. Call `GetDeterministicContextFiles` when semantic search is unavailable.
          4. Call `ReadProjectFile` with a file's ID or name to inspect route/api definitions.

        Start with `ListProjectFiles`, then read routing configuration files, middleware, and
          page/handler files. Extract route declarations, API endpoint paths, and navigation guards
          directly from the file contents you read.

        For ClientRoutes, identify:
        - Path: The route path pattern (e.g., "/users/:id", "/products/[slug]", "/app/*")
        - ComponentName: The component or page that handles this route
        - Parameters: Array of dynamic route parameters (e.g., ["id"], ["slug"])
        - RequiresAuth: Boolean indicating if authentication is required (based on middleware, guards, or wrappers)

        For ApiEndpoints, identify:
        - Method: HTTP method (GET, POST, PUT, DELETE, PATCH, etc.)
        - Path: The API endpoint path
        - Purpose: Brief description of what this endpoint does
        - Parameters: Query params, path params, or body parameters

        Also determine:
        - RoutingStrategy: The routing approach being used (e.g., "File-based (Next.js App Router)", "React Router v6", "Vue Router", "Express.js")

        Output a structured JSON summary matching the provided RouteAndApiOutput schema.

                ## .NET / Blazor projects

        Blazor client routes are `@page "/x"` directives in .razor files (navigation lives in NavMenu.razor,
        Routes.razor/App.razor and `<MudNavLink Href=...>`). API endpoints are controllers
        (`[Route]`, `[ApiController]`, `[HttpGet]`/`[HttpPost]`/`[HttpPut]`/`[HttpDelete]`) and Minimal API
        `MapGet`/`MapPost`/`MapPut`/`MapDelete` calls, usually in Program.cs.

        ## Reading files and failures

        - Use file ids, storage keys or paths exactly as returned by `ListProjectFiles`. Never guess file
          names (do not invent names such as NavMenu.jsx or readme.md).
        - A missing package.json (or any other single expected file) is NOT an unrecoverable error:
          analyse what exists. If a read fails, continue with the other files and say honestly in your
          result which files could not be read. Never emit empty lists as if the project contained nothing.
        - Call the `FailWorkflow(reason)` tool only when the project has no readable source files at all,
          never for one missing file or a transient read failure.
        """;

    public RouteAndApiAnalyzerAgent(
        IAgentChatClientProvider chatClients,
        IConfiguration configuration,
        IAgentToolProvider toolProvider,
        ISkillAgentToolsFactory skillAgentToolsFactory)
        // Structured extraction, not exploratory reasoning: low temperature for consistency,
        // low reasoning effort — extracting routes/endpoints doesn't benefit from deliberation.
        : base(chatClients, configuration, "RouteAndApiAnalyzer",
            "Extracts all routes, API endpoints, and navigation structure.",
            AgentType.RouteAndApiAnalyzer, DefaultPrompt,
            skillAgentToolsFactory,
            toolProvider.GetTools(AgentType.RouteAndApiAnalyzer),
            agentId: null,
            outputSchemaType: typeof(RouteAndApiOutput),
            defaultModelSettings: new AgentModelSettings(Temperature: 0.3f, ReasoningEffort: "low"))
    { }
}
