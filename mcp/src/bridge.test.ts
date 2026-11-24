import { strict as assert } from "node:assert";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import type {
  CatalogResponse,
  CatalogToolAnnotations,
} from "./apiClient.js";
import {
  injectContextWrapper,
  makeProxyServer,
  splitContext,
} from "./bridge.js";
import type {
  CallResult,
  ReelBoltApiClient,
} from "./apiClient.js";
import type {
  Transport,
  TransportSendOptions,
} from "@modelcontextprotocol/sdk/shared/transport.js";
import type {
  JSONRPCMessage,
  MessageExtraInfo,
  RequestId,
} from "@modelcontextprotocol/sdk/types.js";

/* ---------------------------------------------------------------------------
 * Stub API client
 * ------------------------------------------------------------------------- */

interface RecordedCall {
  name: string;
  arguments: Record<string, unknown>;
  context: Record<string, unknown> | undefined;
  token: string | undefined;
}

/**
 * In-memory ReelBoltApiClient: records every call, returns canned results, and
 * can be told to throw (simulating an unreachable / non-2xx API) so we can
 * prove a tool-call failure surfaces as a JSON-RPC error, not a crash.
 */
type StubApiClient = ReelBoltApiClient & {
  calls: RecordedCall[];
  setCallBehavior: (b: "ok" | "throw") => void;
};

function makeStubApiClient(catalog: CatalogResponse): StubApiClient {
  const calls: RecordedCall[] = [];
  let callBehavior: "ok" | "throw" = "ok";
  const apiClient: StubApiClient = {
    calls,
    setCallBehavior(b) {
      callBehavior = b;
    },
    async catalog(_token) {
      if (callBehavior === "throw") {
        throw new Error("catalog: API unreachable (simulated)");
      }
      return catalog;
    },
    async callTool(name, args, context, token): Promise<CallResult> {
      calls.push({ name, arguments: args, context, token });
      if (callBehavior === "throw") {
        throw new Error("callTool: API returned 500 (simulated)");
      }
      return {
        content: [{ type: "text", text: JSON.stringify({ echoedName: name }) }],
        isError: false,
      };
    },
  };
  return apiClient;
}

/* ---------------------------------------------------------------------------
 * In-memory transport (JSON-RPC loopback) — proves real protocol compliance
 * without the live Inference API.
 * ------------------------------------------------------------------------- */

type JSONRPCRequest = { jsonrpc: string; id: number; method: string; params?: unknown };
type JSONRPCNotification = { jsonrpc: string; method: string; params?: unknown };
// Loose internal message type so tests can read .result / .error directly.
type AnyMessage = Record<string, unknown>;

class LoopbackTransport implements Transport {
  onmessage?: <T extends JSONRPCMessage>(message: T, extra?: MessageExtraInfo) => void;
  onclose?: () => void;
  onerror?: (error: Error) => void;
  sessionId?: string;
  setProtocolVersion?: (version: string) => void;
  private sent: AnyMessage[] = [];
  private waiters = new Map<
    RequestId,
    { resolve: (m: AnyMessage) => void; reject: (e: Error) => void }
  >();

  async start(): Promise<void> {
    // no-op
  }
  async close(): Promise<void> {
    this.onclose?.();
  }
  // The server calls this to send us a message (a response).
  async send(message: JSONRPCMessage, _options?: TransportSendOptions): Promise<void> {
    const id = (message as { id?: RequestId }).id;
    if (typeof id !== "undefined" && this.waiters.has(id)) {
      const w = this.waiters.get(id)!;
      this.waiters.delete(id);
      w.resolve(message as AnyMessage);
      return;
    }
    this.sent.push(message as AnyMessage);
  }
  /** Deliver a client->server request and await the matching response. */
  request(message: JSONRPCRequest): Promise<AnyMessage> {
    const p = new Promise<AnyMessage>((resolve, reject) => {
      this.waiters.set(message.id, { resolve, reject });
      setTimeout(() => {
        if (this.waiters.has(message.id)) {
          this.waiters.delete(message.id);
          reject(new Error(`No response for request id ${message.id}`));
        }
      }, 2000);
    });
    this.onmessage?.(
      message as unknown as JSONRPCMessage,
      { requestInfo: { headers: { authorization: "Bearer test-token" } } }
    );
    return p;
  }
  notify(message: JSONRPCNotification): void {
    this.onmessage?.(
      message as unknown as JSONRPCMessage,
      { requestInfo: { headers: { authorization: "Bearer test-token" } } }
    );
  }
  getSent(): AnyMessage[] {
    return this.sent;
  }
}

/* ---------------------------------------------------------------------------
 * A representative 101-tool catalog: the MCP surface the C# API publishes.
 * It is the 40 collapsed Propose/Apply pairs (at their new MCP names), the
 * BeginUpload/CompleteUpload upload protocol, and every registered tool that is
 * neither `Propose*`, `Apply*` nor the chat-only `WatchExecution`.
 * The names are the REAL ones — see the fixture's own docstring. It includes a
 * tool declaring `workflowId` to reflect the real tools (which is why the single
 * context wrapper is used). No tool declares `context` — the precondition the
 * guard test asserts.
 * ------------------------------------------------------------------------- */

/**
 * The catalog fixture: the REAL 101 MCP tool names, derived from the C# registry and
 * `AssistantMcpToolSurface`:
 *   -39 collapsed pairs: `ApplyX` -> `X` where the MCP name is the Apply name minus
 *     its prefix, with the documented overrides (`ApplyWorkflow` -> `SaveWorkflow`);
 *   - `BeginUpload` / `CompleteUpload`, the two-phase upload protocol, kept as their
 *     own names because the client PUTs bytes between the two calls;
 *   - every registered tool that is neither `Propose*`, `Apply*` nor the chat-only
 *     `WatchExecution`.
 *   Registered: 130 (including WatchExecution). Over MCP: 129 pre-collapse.
 *   153 chat tools, 48 pairs collapsed to 47 (the 2 social pairs are not on MCP), the upload protocol under its own names = 101 (pinned in AssistantMcpCatalogTests).
 *
 * These are deliberately the real names. An earlier version of this fixture padded itself to the
 * then-current count with synthetic names (`Tool07`, `PadTool00`, ...), which made the count
 * assertion below tautological — it asserted a constant the fixture had just manufactured, and
 * would have passed even against a proxy that dropped or duplicated tools.
 *
 * The count below is therefore an independent literal rather than `realNames.length`: it is the
 * one thing in this file that can FAIL when the MCP surface changes, which is the signal to
 * re-derive the names from `AssistantMcpToolSurface.Collapsed` and the registrations. What this
 * fixture can and cannot prove: it checks the PROXY plumbing, and the count + duplicate +
 * no-retired-names checks keep it honest. True parity is asserted where it can genuinely fail, on
 * the C# side, in AssistantMcpCatalogTests (101 catalog entries equal to the derived set).
 */
function makeStubCatalog(): CatalogResponse {
  const realNames = [
    // the 47 collapsed Propose/Apply pairs (MCP names), plus WritePostText
    "AddLibraryVoice", "AddSoundEffect", "AddTextLayer", "AssignAgentSkills", "ChangePictureCanvas", "CleanAudio", "CloneWorkflow",
    "CreateAvatarVariant", "CreateFromTemplate", "CreateInferenceProvider", "CreateObjectAvatar", "CreatePictureDocument", "CreateProject",
    "DecideDesignChange", "DeleteInferenceProvider", "DeletePictureLayers", "DeleteProject",
    "DeleteProjectFile", "DeleteWorkflow", "DesignGraphic", "EditPictureLayers",
    "EditProjectFile", "EnhanceVideo", "ExtractObjectFromVideo", "GenerateSubjectClip", "GreenScreenInsert", "ImportFileFromUrl",
    "ImportLibraryAvatar", "InsertProjectFileLines",
    "ObjectEffect", "CopyMarketplaceRecipe", "PatchWorkflow", "RestoreProjectVersion", "ReviseComposition", "RunPictureTool", "SaveDesignSystem",
    "SaveOutputAsFile", "SaveProjectVersion", "SavePronunciation", "SaveStyle", "SaveWorkflow", "SetMediaOverlay", "StepChange",
    "StopExecution", "UpdateInferenceProvider", "UpdateProject", "WritePostText", "WriteProjectFile",
    // 2 upload protocol names (kept: the client PUTs bytes between them)
    "BeginUpload", "CompleteUpload",
    // the registered tools that are neither Propose*/Apply* nor WatchExecution
    "DescribePictureDocument", "DescribeVideoWindow", "EstimateWorkflowRun", "ExecuteWorkflow", "GetChangeOptions",
    "GetCreditBalance", "GetDecisionCalibration", "GetDesignSystem", "GetExecutionStatus",
    "GetMarketplaceRecipe", "GetMusicBeats", "GetOutput", "GetPostPerformance", "GetProject", "GetProjectChanges", "GetProjectFileOutline", "GetProjectReadiness", "GetStepCatalog",
    "GetWorkflow", "GetWorkflowExecution", "GetWorkflowTemplate", "ListAgents",
    "ListAgentsAdmin", "ListAvatarLibrary", "ListDesignChanges", "ListInferenceProviders", "ListPictureTools",
    "ListProjectAvatars", "ListProjectFiles", "ListProjectVersions", "ListProjectVoices", "ListProjects", "ListScheduledPosts", "ListSkills", "ListSocialAccounts", "ListStepSchemas", "ListStyles", "ListVideoEvents",
    "ListWorkflowExecutions", "ListWorkflowTemplates", "ListWorkflows", "LookAtOutput",
    "LookAtPicture", "ReadProjectFile", "ReadProjectFileLines", "SearchMarketplaceRecipes", "SearchPlatformDocs", "SearchSoundEffects", "SearchVoiceLibrary",
    "TestInferenceProvider", "ValidateWorkflow",
  ];
  // Tie the hand-written list to the C# registry: AssistantMcpToolFixtureTests keeps this JSON file equal
  // to the MCP catalog the API publishes, so adding or removing a tool fails one side until both are updated.
  const registry = JSON.parse(
    readFileSync(new URL("../test-fixtures/assistant-tool-names.json", import.meta.url), "utf8"),
  ) as string[];
  assert.deepEqual(
    [...realNames].sort(),
    [...registry].sort(),
    "the catalog fixture drifted from the C# MCP catalog (mcp/test-fixtures/assistant-tool-names.json)",
  );
  assert.equal(realNames.length, 101, "the recorded fixture must be the full 101-tool MCP set");
  assert.equal(new Set(realNames).size, 101, "the recorded fixture must have no duplicates");
  assert.ok(
    realNames.every((name) => !name.startsWith("Propose") && !name.startsWith("Apply")),
    "the MCP catalog must not expose the retired Propose*/Apply* names",
  );
  assert.ok(!realNames.includes("WatchExecution"), "WatchExecution is chat-only, never over MCP");

  const tools = realNames.map((name) => {
    // A few tools declare workflowId (reflecting the real ones); this is exactly why the context
    // wrapper is a single property rather than three top-level keys.
    const hasWorkflowId = name === "ExecuteWorkflow" || name === "SaveWorkflow";
    const annotations = stubAnnotations(name);
    return {
      name,
      description: `Stub for ${name}`,
      // The API publishes the human title twice: top-level and inside `annotations`.
      title: annotations.title,
      annotations,
      inputSchema: {
        type: "object",
        properties: hasWorkflowId
          ? { projectId: { type: "string", format: "uuid" }, workflowId: { type: "string", format: "uuid" } }
          : { projectId: { type: "string", format: "uuid" } },
        required: hasWorkflowId ? ["projectId", "workflowId"] : ["projectId"],
      },
    };
  });

  return { tools };
}

/**
 * Deterministic, per-tool annotations. Every fake entry is distinguishable: a proxy
 * that dropped the fields, renamed a key or reused another tool's object fails the
 * pass-through assertions.
 */
function stubAnnotations(name: string): CatalogToolAnnotations {
  const readOnly =
    /^(List|Get|Read|Describe|Look|Search|Estimate|Validate)/.test(name) ||
    name === "BeginUpload";
  const openWorld =
    name === "ExecuteWorkflow" ||
    name === "LookAtPicture" ||
    name === "LookAtOutput" ||
    name === "TestInferenceProvider" ||
    name === "RunPictureTool" ||
    name === "DesignGraphic" ||
    name === "ReviseComposition" ||
    name === "StepChange" ||
    name === "ImportFileFromUrl" ||
    name === "SearchVoiceLibrary" ||
    name === "AddLibraryVoice";
  return {
    title: `Stub ${name}`,
    readOnlyHint: readOnly,
    destructiveHint: !readOnly,
    idempotentHint: readOnly || name === "CompleteUpload",
    openWorldHint: openWorld,
  };
}

/* ---------------------------------------------------------------------------
 * Unit tests: injectContextWrapper
 * ------------------------------------------------------------------------- */

test("injectContextWrapper adds the context property and preserves existing props", () => {
  const schema = {
    type: "object",
    properties: { workflowId: { type: "string", format: "uuid" } },
    required: ["workflowId"],
  };
  const out = injectContextWrapper(schema);
  assert.deepEqual(out.properties?.context, {
    type: "object",
    additionalProperties: false,
    properties: {
      projectId: { type: "string", format: "uuid" },
      workflowId: { type: "string", format: "uuid" },
      imageDocumentId: { type: "string", format: "uuid" },
    },
  });
  // Existing prop preserved
  assert.deepEqual(out.properties?.workflowId, { type: "string", format: "uuid" });
  // required untouched (context NOT added)
  assert.deepEqual(out.required, ["workflowId"]);
  // original not mutated
  assert.equal((schema.properties as Record<string, unknown>).context, undefined);
});

test("injectContextWrapper creates properties when absent and does not add to required", () => {
  const out = injectContextWrapper({ type: "object" });
  assert.deepEqual(out.properties, {
    context: {
      type: "object",
      additionalProperties: false,
      properties: {
        projectId: { type: "string", format: "uuid" },
        workflowId: { type: "string", format: "uuid" },
        imageDocumentId: { type: "string", format: "uuid" },
      },
    },
  });
  assert.equal(out.required, undefined);
});

test("injectContextWrapper is defensive on a missing schema", () => {
  const out = injectContextWrapper(undefined);
  assert.equal(out.type, "object");
  assert.ok(out.properties?.context);
});

test("injectContextWrapper keeps a tool's own additionalProperties:false intact", () => {
  const out = injectContextWrapper({ type: "object", properties: { a: { type: "string" } }, additionalProperties: false });
  assert.equal(out.additionalProperties, false);
  assert.ok(out.properties?.context);
  assert.ok(out.properties?.a);
});

/* ---------------------------------------------------------------------------
 * Unit tests: splitContext
 * ------------------------------------------------------------------------- */

test("splitContext strips the wrapper and forwards the rest verbatim", () => {
  const { context, arguments: rest } = splitContext({
    projectId: "p",
    other: 1,
    context: { projectId: "p", workflowId: "w" },
  });
  assert.deepEqual(rest, { projectId: "p", other: 1 });
  assert.deepEqual(context, { projectId: "p", workflowId: "w" });
});

test("splitContext returns undefined context when absent", () => {
  const { context, arguments: rest } = splitContext({ a: 1 });
  assert.equal(context, undefined);
  assert.deepEqual(rest, { a: 1 });
});

test("splitContext ignores a non-object context value (drops it; rest forwarded)", () => {
  const { context, arguments: rest } = splitContext({ context: "not-an-object", a: 1 });
  // Only a valid object is treated as the wrapper; a malformed `context` is dropped.
  assert.equal(context, undefined);
  assert.deepEqual(rest, { a: 1 });
});

/* ---------------------------------------------------------------------------
 * Guard test: no bridged tool declares a property named `context`
 * ------------------------------------------------------------------------- */

test("GUARD: no bridged (raw catalog) tool declares a property named `context`", () => {
  const catalog = makeStubCatalog();
  const offenders = catalog.tools.filter((t) => {
    const props = t.inputSchema?.properties as Record<string, unknown> | undefined;
    return props && "context" in props;
  });
  assert.deepEqual(
    offenders.map((t) => t.name),
    [],
    `A bridged tool declares its own context property, which collides with the ` +
      `injected wrapper: ${offenders.map((t) => t.name).join(", ")}`
  );
});

test("GUARD: the 101-tool catalog contains a tool declaring workflowId (the wrapper rationale)", () => {
  const catalog = makeStubCatalog();
  const withWorkflow = catalog.tools.filter(
    (t) => "workflowId" in ((t.inputSchema?.properties as Record<string, unknown>) ?? {})
  );
  assert.ok(withWorkflow.length > 0, "expected at least one tool to declare workflowId");
});

/* ---------------------------------------------------------------------------
 * Protocol integration (via the in-memory transport)
 * ------------------------------------------------------------------------- */

interface Session {
  transport: LoopbackTransport;
  catalog: CatalogResponse;
  apiClient: ReturnType<typeof makeStubApiClient>;
  close: () => Promise<void>;
}

function startSession(catalog: CatalogResponse): Session {
  const apiClient = makeStubApiClient(catalog);
  const transport = new LoopbackTransport();
  const server = makeProxyServer({
    apiClient,
    getAuthToken: (extra) => {
      const h = extra?.requestInfo?.headers;
      const v = h?.authorization ?? h?.Authorization;
      const m = typeof v === "string" ? v.match(/^Bearer\s+(.+)$/i) : null;
      return m ? m[1] : undefined;
    },
  });
  // connect() is async but the handshake completes on first messages; fire and keep.
  server.connect(transport);
  return {
    transport,
    catalog,
    apiClient,
    close: () => server.close(),
  };
}

test("protocol: initialize, notifications/initialized, tools/list, tools/call, ping, and error codes", async () => {
  const session = startSession(makeStubCatalog());
  const { transport, apiClient } = session;

  try {
    // 1. initialize — a real client MUST be able to complete this.
    const init = await transport.request({
      jsonrpc: "2.0",
      id: 1,
      method: "initialize",
      params: {
        protocolVersion: "2024-11-05",
        capabilities: {},
        clientInfo: { name: "test-client", version: "1.0.0" },
      },
    });
    assert.equal(init.error, undefined, `initialize should not error: ${JSON.stringify(init)}`);
    const initResult = init.result as {
      serverInfo: { name: string };
      capabilities: Record<string, unknown>;
      protocolVersion: string;
    };
    assert.equal(initResult.serverInfo.name, "reelbolt");
    assert.ok(initResult.capabilities.tools, "tools capability must be advertised");
    assert.equal(typeof initResult.protocolVersion, "string");
    assert.ok(initResult.protocolVersion.length > 0);

    // 2. notifications/initialized — a notification; no response expected, no crash.
    transport.notify({ jsonrpc: "2.0", method: "notifications/initialized" });

    // 3. tools/list — full catalog, context injected, no nextCursor, count = 101.
    const list = await transport.request({
      jsonrpc: "2.0",
      id: 2,
      method: "tools/list",
      params: {},
    });
    assert.equal(list.error, undefined, `tools/list should not error: ${JSON.stringify(list)}`);
    const listResult = list.result as { tools: Array<{ name: string; inputSchema: Record<string, unknown> }>; nextCursor?: string };
    assert.equal(listResult.tools.length, 101, `expected 101 tools, got ${listResult.tools.length}`);
    for (const tool of listResult.tools) {
      const props = tool.inputSchema.properties as Record<string, unknown>;
      assert.ok(
        props && "context" in props,
        `tool ${tool.name} must have the injected context property`
      );
      // context must be optional: never in required
      const required = tool.inputSchema.required as string[] | undefined;
      assert.ok(!required || !required.includes("context"), `tool ${tool.name} must not require context`);
    }
    assert.equal(listResult.nextCursor, undefined, "tools/list must omit nextCursor");

    // 4. tools/call — forwards name + arguments (minus context) + context; token passthrough.
    const call = await transport.request({
      jsonrpc: "2.0",
      id: 3,
      method: "tools/call",
      params: {
        name: "ExecuteWorkflow",
        arguments: { projectId: "p-1", workflowId: "w-1", context: { workflowId: "w-1" } },
      },
    });
    assert.equal(call.error, undefined, `tools/call should not error: ${JSON.stringify(call)}`);
    const callResult = call.result as { content: Array<{ type: string; text: string }>; isError?: boolean };
    assert.equal(callResult.isError, false);
    assert.equal(callResult.content.length, 1);
    assert.equal(callResult.content[0].type, "text");
    // The stub records what it received — assert the forwarded body, not just the URL.
    const recorded = apiClient.calls.at(-1)!;
    assert.equal(recorded.name, "ExecuteWorkflow");
    // context stripped from arguments
    assert.equal(recorded.arguments.context, undefined, "context must be stripped from arguments");
    // remaining arguments forwarded verbatim
    assert.deepEqual(recorded.arguments, { projectId: "p-1", workflowId: "w-1" });
    // wrapper forwarded as the context field
    assert.deepEqual(recorded.context, { workflowId: "w-1" });
    // token forwarded verbatim (from the per-request header in this stub)
    assert.equal(recorded.token, "test-token");

    // 5. ping — must answer {} (contract: ping is a required method).
    const ping = await transport.request({ jsonrpc: "2.0", id: 4, method: "ping" });
    assert.equal(ping.error, undefined, `ping should not error: ${JSON.stringify(ping)}`);
    assert.deepEqual(ping.result, {});

    // 6. unknown method -> -32601 MethodNotFound.
    const unknown = await transport.request({ jsonrpc: "2.0", id: 5, method: "bogus/method" });
    assert.equal((unknown.error as { code: number }).code, -32601);

    // 7. tools/call with an unreachable API (stub throws) -> -32603, NOT a crash.
    apiClient.setCallBehavior("throw");
    const fail = await transport.request({
      jsonrpc: "2.0",
      id: 6,
      method: "tools/call",
      params: { name: "GetProject", arguments: { projectId: "p-1" } },
    });
    assert.equal((fail.error as { code: number }).code, -32603, `expected -32603, got ${JSON.stringify(fail.error)}`);
  } finally {
    await session.close();
  }
});

test("protocol: tools/list forwards title and annotations verbatim (not dropped, not renamed)", async () => {
  const catalog = makeStubCatalog();
  const session = startSession(catalog);
  const { transport } = session;

  try {
    await transport.request({
      jsonrpc: "2.0",
      id: 1,
      method: "initialize",
      params: {
        protocolVersion: "2024-11-05",
        capabilities: {},
        clientInfo: { name: "test-client", version: "1.0.0" },
      },
    });
    transport.notify({ jsonrpc: "2.0", method: "notifications/initialized" });

    const list = await transport.request({
      jsonrpc: "2.0",
      id: 2,
      method: "tools/list",
      params: {},
    });
    assert.equal(list.error, undefined, `tools/list should not error: ${JSON.stringify(list)}`);
    const tools = (
      list.result as {
        tools: Array<{
          name: string;
          title?: string;
          annotations?: Record<string, unknown>;
          inputSchema: Record<string, unknown>;
        }>;
      }
    ).tools;

    const sourceByName = new Map(catalog.tools.map((tool) => [tool.name, tool]));
    assert.equal(tools.length, 101);
    for (const tool of tools) {
      const entry = sourceByName.get(tool.name);
      assert.ok(entry, `tools/list returned an unknown tool ${tool.name}`);
      // The top-level title and the whole annotations object pass through unchanged.
      assert.equal(tool.title, entry.title, `top-level title for ${tool.name} must pass through unchanged`);
      assert.deepEqual(
        tool.annotations,
        entry.annotations,
        `annotations for ${tool.name} must pass through unchanged`
      );
      // ... alongside the injected context wrapper.
      assert.ok(
        (tool.inputSchema.properties as Record<string, unknown>).context,
        `tool ${tool.name} must keep the injected context property`
      );
    }

    // The MCP standard keys are neither renamed nor dropped.
    const beginUpload = tools.find((tool) => tool.name === "BeginUpload");
    assert.ok(beginUpload, "BeginUpload must be in the catalog");
    assert.deepEqual(beginUpload.annotations, {
      title: "Stub BeginUpload",
      readOnlyHint: true,
      destructiveHint: false,
      idempotentHint: true,
      openWorldHint: false,
    });
    assert.deepEqual(Object.keys(beginUpload.annotations ?? {}).sort(), [
      "destructiveHint",
      "idempotentHint",
      "openWorldHint",
      "readOnlyHint",
      "title",
    ]);
  } finally {
    await session.close();
  }
});

test("protocol: tools/list with an unreachable API surfaces a JSON-RPC error, not a crash", async () => {
  const catalog = makeStubCatalog();
  const apiClient = makeStubApiClient(catalog);
  apiClient.setCallBehavior("throw"); // affects catalog() too in this stub
  const transport = new LoopbackTransport();
  const server = makeProxyServer({
    apiClient,
    getAuthToken: () => "tok",
  });
  server.connect(transport);
  try {
    const list = await transport.request({ jsonrpc: "2.0", id: 1, method: "tools/list", params: {} });
    assert.equal((list.error as { code: number }).code, -32603, `expected -32603, got ${JSON.stringify(list.error)}`);
  } finally {
    await server.close();
  }
});
