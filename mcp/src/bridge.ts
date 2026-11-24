/**
 * The MCP proxy bridge.
 *
 * This file contains the *only* tool behaviour in the TypeScript layer, and it is
 * deliberately minimal: `tools/list` proxies the Inference API catalog and `tools/call`
 * proxies the Inference API invoke. All tool logic (schema, validation, authority,
 * the 86 tools over MCP) lives in the C# `AssistantMcpToolSurface` /
 * `AssistantToolProvider` — there is no duplicate implementation here, not even the
 * Propose/Apply collapse (contract §1).
 *
 * The single non-trivial thing it does is the `context` wrapper (contract §4): one
 * optional property is injected into every bridged tool's inputSchema, stripped out
 * of the call arguments, and forwarded as the `tool-call` body's `context` field.
 * One wrapper (not three top-level keys) is used precisely because real tools
 * already declare `workflowId`; a guard test asserts no bridged tool declares a
 * property named `context`, which is what makes this safe.
 *
 * Everything else is forwarding: tool arguments pass through verbatim (no reshape,
 * no rename, no nesting), and the bearer token is forwarded verbatim (contract §9).
 */
import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import type { RequestHandlerExtra } from "@modelcontextprotocol/sdk/shared/protocol.js";
import type {
  CallToolRequest,
  CallToolResult,
  ListToolsRequest,
  ListToolsResult,
  ServerNotification,
  ServerRequest,
  Tool,
} from "@modelcontextprotocol/sdk/types.js";
import {
  CallToolRequestSchema,
  ListResourcesRequestSchema,
  ListResourceTemplatesRequestSchema,
  ListToolsRequestSchema,
  ReadResourceRequestSchema,
  SubscribeRequestSchema,
  UnsubscribeRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";
import type { ReelBoltApiClient } from "./apiClient.js";
import { RUN_URI_TEMPLATE, RunSubscriptions, type SubscriptionCounter, type SubscriptionLimits } from "./runSubscriptions.js";

/**
 * The one optional property injected into every bridged tool's inputSchema
 * (contract §4, verbatim).
 */
export const CONTEXT_WRAPPER: Record<string, unknown> = {
  type: "object",
  additionalProperties: false,
  properties: {
    projectId: { type: "string", format: "uuid" },
    workflowId: { type: "string", format: "uuid" },
    imageDocumentId: { type: "string", format: "uuid" },
  },
};

/**
 * Inject the `context` wrapper into a tool's inputSchema and return a new object
 * (never mutates the input). Preserves the original `type`, `required` and
 * `additionalProperties`; only adds `context` to `properties`. A missing or
 * non-object `properties` is created. `context` is never added to `required`.
 */
export function injectContextWrapper(
  inputSchema: unknown
): Tool["inputSchema"] {
  const base: Record<string, unknown> =
    inputSchema !== null &&
    typeof inputSchema === "object" &&
    !Array.isArray(inputSchema)
      ? { ...(inputSchema as Record<string, unknown>) }
      : {};
  if (base.type !== "object") {
    base.type = "object";
  }
  const existingProps: Record<string, unknown> =
    base.properties !== null &&
    typeof base.properties === "object" &&
    !Array.isArray(base.properties)
      ? { ...(base.properties as Record<string, unknown>) }
      : {};
  base.properties = { ...existingProps, context: CONTEXT_WRAPPER };
  return base as Tool["inputSchema"];
}

/**
 * Strip the injected `context` wrapper out of the call arguments. Returns the
 * wrapper (if present) and the remaining arguments, which are forwarded verbatim.
 * The remaining arguments are a shallow copy so the original is never mutated.
 */
export function splitContext(arguments_: Record<string, unknown>): {
  context: Record<string, unknown> | undefined;
  arguments: Record<string, unknown>;
} {
  const { context, ...rest } = arguments_;
  if (context !== null && typeof context === "object" && !Array.isArray(context)) {
    return { context: context as Record<string, unknown>, arguments: rest };
  }
  return { context: undefined, arguments: rest };
}

/** Minimal structural shape of the per-request context the handlers need. */
export interface HandlerContext {
  requestInfo?: { headers?: Record<string, string | string[] | undefined> };
}

export interface ProxyServerOptions {
  apiClient: ReelBoltApiClient;
  /**
   * Resolves the caller's bearer token for the current request. For stdio this is
   * a closure over the token read once at startup (REELBOLT_MCP_TOKEN); for HTTP
   * it reads the per-request Authorization header. It is never a tool argument.
   */
  getAuthToken: (extra?: HandlerContext) => string | undefined;
  serverInfo?: { name: string; version: string };
  /** Test seam for the run-subscription bounds; defaults to the MCP_* environment. */
  subscriptionLimits?: SubscriptionLimits;
  subscriptionCounter?: SubscriptionCounter;
  /** Filled by makeProxyServer: lets the HTTP transport ask whether the session has live subscriptions. */
  probe?: { hasLiveSubscriptions?: () => boolean };
}

/**
 * Build a protocol-complete MCP server (SDK `Server`) that proxies the two
 * Inference API endpoints. The SDK handles `initialize`, `notifications/initialized`,
 * `ping` and correct JSON-RPC error codes; we register only `tools/list` and
 * `tools/call`. The `tools` capability is advertised so those handlers are allowed.
 */
export function makeProxyServer(options: ProxyServerOptions): Server {
  const serverInfo = options.serverInfo ?? { name: "reelbolt", version: "0.4.0-alpha" };
  const server = new Server(serverInfo, {
    capabilities: { tools: {}, resources: { subscribe: true, listChanged: false } },
    instructions:
      "ReelBolt assistant tools, proxied over the Model Context Protocol. " +
      "Page context (projectId / workflowId / imageDocumentId) is supplied via the " +
      "optional `context` property; the token is never a tool argument.",
  });

  server.setRequestHandler(
    ListToolsRequestSchema,
    async (
      _request: ListToolsRequest,
      extra: RequestHandlerExtra<ServerRequest, ServerNotification>
    ) => {
      const token = options.getAuthToken(extra);
      const catalog = await options.apiClient.catalog(token);
      const result: ListToolsResult = {
        tools: catalog.tools.map((tool) => ({
          name: tool.name,
          description: tool.description,
          // Directory-facing metadata is forwarded verbatim: the C# catalog decides the
          // title and every hint; this layer never invents, renames or infers one.
          title: tool.title,
          annotations: tool.annotations,
          inputSchema: injectContextWrapper(tool.inputSchema),
        })) as Tool[],
      };
      return result;
    }
  );

  server.setRequestHandler(
    CallToolRequestSchema,
    async (
      request: CallToolRequest,
      extra: RequestHandlerExtra<ServerRequest, ServerNotification>
    ) => {
      const token = options.getAuthToken(extra);
      const rawArgs = request.params.arguments ?? {};
      const { context, arguments: forwarded } = splitContext(rawArgs);
      const result = await options.apiClient.callTool(
        request.params.name,
        forwarded,
        context,
        token
      );
      // The API returns the SDK content-block shape; map it to CallToolResult.
      return {
        content: result.content as CallToolResult["content"],
        isError: result.isError,
      };
    }
  );

  // Run notifications (see runSubscriptions.ts). Every read is an authorized
  // GetExecutionStatus call with the caller's token; resources/list never enumerates.
  const runs = new RunSubscriptions(
    server,
    options.apiClient,
    options.subscriptionLimits,
    options.subscriptionCounter
  );
  server.onclose = () => runs.closeAll();
  if (options.probe) options.probe.hasLiveSubscriptions = () => runs.size > 0;

  server.setRequestHandler(ListResourcesRequestSchema, async () => ({ resources: [] }));
  server.setRequestHandler(ListResourceTemplatesRequestSchema, async () => ({
    resourceTemplates: [
      {
        uriTemplate: RUN_URI_TEMPLATE,
        name: "ReelBolt workflow run",
        description:
          "Live status of one workflow execution (JSON). Optional ?project={projectId}. Subscribe to be notified on change.",
        mimeType: "application/json",
      },
    ],
  }));
  server.setRequestHandler(ReadResourceRequestSchema, async (request, extra) => {
    const text = await runs.read(request.params.uri, options.getAuthToken(extra));
    return { contents: [{ uri: request.params.uri, mimeType: "application/json", text }] };
  });
  server.setRequestHandler(SubscribeRequestSchema, async (request, extra) => {
    const pt = request.params._meta?.progressToken;
    await runs.subscribe(request.params.uri, options.getAuthToken(extra), pt);
    return {};
  });
  server.setRequestHandler(UnsubscribeRequestSchema, async (request, extra) => {
    runs.unsubscribe(request.params.uri, options.getAuthToken(extra));
    return {};
  });

  return server;
}

/**
 * Extract the bearer token from a per-request header map (HTTP transport). Never
 * parses, decodes or validates the token — it is forwarded verbatim (contract §9).
 */
export function readBearerToken(
  headers: Record<string, string | string[] | undefined> | undefined
): string | undefined {
  if (!headers) {
    return undefined;
  }
  const value = headers.authorization ?? headers.Authorization;
  if (typeof value !== "string") {
    return undefined;
  }
  const match = value.match(/^Bearer\s+(.+)$/i);
  return match ? match[1] : undefined;
}
