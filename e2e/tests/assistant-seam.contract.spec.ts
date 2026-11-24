import { test, expect } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import * as path from 'node:path';

/**
 * T-QA-04 — the assistant's frontend/backend SEAM, checked as a contract against ONE shared
 * artifact.
 *
 * WHY THIS FILE EXISTS
 *
 * Every other test of the assistant checks ONE half against a hand-written idea of the other:
 * `web/components/assistant/AssistantChatPanel.integration.test.tsx` mocks `fetch` and feeds the
 * component a hand-authored SSE string, and `AssistantChatControllerTests` constructs
 * `AssistantChatRequest` in C#. Neither can observe a disagreement between the two halves — and
 * that disagreement was a real, shipped defect on this branch: the client posted the AI-SDK
 * `messages[]` body while the endpoint required a flat `message`, so every assistant turn was
 * rejected with HTTP 400. No test on either side could see it.
 *
 * WHAT THE SHARED ARTIFACT IS
 *
 *   inference/tests/ReelBolt.WorkflowEngine.Tests/TestData/assistant-chat-ai-sdk-body.json
 *
 * Both halves assert against that same file:
 *
 *   - THIS spec drives the REAL client component stack
 *     (`AssistantShellProvider → AssistantDrawer → AssistantProvider → AssistantChatPanel`), via
 *     `web/components/assistant/assistantSeamCapture.tsx`, captures the body it actually posts, and
 *     asserts it deep-equals the fixture PLUS the one key the fixture cannot carry — the bound
 *     `threadId`. That is the client half.
 *   - `AssistantChatSeamContractTests` (C#) loads the same file, binds it with MVC's JSON options
 *     and drives the real controller action. That is the server half.
 *
 * WHY THE CLIENT HALF ADDS `threadId` RATHER THAN THE FIXTURE CARRYING IT
 *
 * The fixture is a SERVER-SIDE input: it pins the allocate path — the shape the endpoint reads as
 * "make me a thread" — and the C# half exercises exactly that path against it, including the
 * `X-Assistant-Thread-Id` header the allocation answers with. Giving the fixture a `threadId` would
 * turn that into a lookup of a thread that does not exist, and delete the server-side coverage of
 * allocation, which is still a live contract. So the fixture stays as it is, and THIS spec states
 * the one extra key the real client adds and pins its exact value.
 *
 * The round that fixed the ORIGINAL defect on this seam chose a PRE-THREAD turn as the scenario,
 * because that was the window the defect lived in: `threadId` was absent, the server allocated a
 * thread, and the client never learned which. That window no longer exists in the shipped UI — the
 * composer and every other send path are inert until a server thread is bound (see
 * `web/components/assistant/AssistantThreadIdentity.test.tsx`) — so a capture taken there would
 * describe a body the app cannot produce. The turn captured now is the BOUND one, which is the only
 * turn this client sends at all.
 *
 * WHY THE CLIENT HALF RENDERS COMPONENTS INSTEAD OF HAND-BUILDING ADAPTER ARGUMENTS
 *
 * An earlier version of this file imported `DataStreamRuntimeAdapter` and called
 * `adapter.run({messages, runConfig, abortSignal, context, unstable_getMessage})` BY HAND. That was
 * the `high` finding this file now closes: hand-building the adapter's arguments means the spec
 * never produces what the APP produces. `AssistantProvider` installs its runtime through
 * `useDataStreamRuntime`, and the adapter contributes `threadId`, `unstable_assistantMessageId` and
 * `parentId` from runtime state a hand-built call never had. The guard was therefore green while
 * the live client posted `threadId: "__LOCALID_…"` and took a 400 on every pre-thread turn.
 *
 * Rendering React needs a DOM, and this Playwright project runs in plain Node — there is no browser
 * in a contract check and none is started here. So the client half is delegated to the toolchain
 * that CAN render it: `npx vitest run components/assistant/assistantSeamCapture.harness.test.tsx`
 * under jsdom, and this spec compares that capture to the fixture. The harness deliberately contains
 * NO comparison of its own — it only reports what the client sent — so a change to what the app
 * posts has nothing to hide behind and reddens here.
 *
 * It needs no browser, no database and no running service: only the two source trees it is
 * comparing. Run it with:
 *
 *   cd e2e && npx playwright test tests/assistant-seam.contract.spec.ts --project=desktop
 *
 * A failure here is a real integration defect, not a flake: it means the browser and the API
 * disagree about the wire, which no unit test on either side can see.
 */

const HERE = __dirname;
const REPO = path.resolve(HERE, '..', '..');
const WEB = path.join(REPO, 'web');
const CHAT_CONTROLLER = path.join(
  REPO,
  'inference',
  'src',
  'ReelBolt.Inference.Api',
  'Controllers',
  'AssistantChatController.cs',
);

/**
 * The shared request-body fixture — the artifact that pins both halves of this seam.
 *
 * It is the SERVER's allocate-path input: `threadId` absent, so the endpoint makes a thread and
 * answers with its id. The C# half drives exactly that, header included. The client posts this same
 * body PLUS the bound `threadId` (see `readCaptureThreadId`), which is the one key the fixture
 * cannot carry without deleting that server-side coverage.
 *
 * Held in the C# test tree rather than the e2e tree because it is a *server-side input*: the C#
 * test project copies it to its output directory (`<None Update … CopyToOutputDirectory>`) and
 * binds it. This spec reads the same file straight out of the source tree, resolved exactly the way
 * `REPO` above is, so the two are guaranteed to be looking at one file and not two copies.
 */
const FIXTURE = path.join(
  REPO,
  'inference',
  'tests',
  'ReelBolt.WorkflowEngine.Tests',
  'TestData',
  'assistant-chat-ai-sdk-body.json',
);

const FIXTURE_LABEL = path.relative(REPO, FIXTURE);

/** The vitest entry point that drives the real components and reports the captured body. */
const CAPTURE_HARNESS = 'components/assistant/assistantSeamCapture.harness.test.tsx';

/** The harness module the entry point drives, read directly for the thread id it binds. */
const CAPTURE_HARNESS_SOURCE = 'components/assistant/assistantSeamCapture.tsx';

/**
 * How the fixture is regenerated from the real stack.
 *
 * TWO steps, and the second is not optional. The capture reports the body the client actually
 * posts, which carries the `threadId` it is bound to; the fixture is the SERVER's allocate-path
 * input and carries none. Copying the capture over the fixture verbatim would turn the C# half's
 * "allocate me a thread" case into a lookup of a thread that does not exist, and delete that
 * server-side coverage — so the bound thread id is dropped on the way.
 */
const REGENERATE_COMMAND =
  `cd web && ASSISTANT_SEAM_CAPTURE_OUT=../${FIXTURE_LABEL}.captured \\\n` +
  `    env -u NODE_ENV NODE_ENV=test npx vitest run ${CAPTURE_HARNESS}\n` +
  `cd web && node -e "const fs=require('fs');const p='../${FIXTURE_LABEL}';` +
  `const b=JSON.parse(fs.readFileSync(p+'.captured','utf8'));delete b.threadId;` +
  `fs.writeFileSync(p,JSON.stringify(b,null,2)+'\\n');fs.unlinkSync(p+'.captured');"`;

interface AiSdkMessage {
  role?: string;
  content?: unknown;
}

interface AiSdkFixture {
  messages: AiSdkMessage[];
  tools?: unknown;
  runConfig?: unknown;
  projectId?: string;
  [key: string]: unknown;
}

/** The shared fixture, parsed. Read on use rather than at import time, so a missing file fails loudly. */
function readFixture(): AiSdkFixture {
  let raw: string;
  try {
    raw = readFileSync(FIXTURE, 'utf8');
  } catch (error) {
    throw new Error(
      `Could not read the shared assistant request-body fixture at ${FIXTURE}.\n` +
        'It is the ONE artifact this spec and AssistantChatSeamContractTests both pin the ' +
        'assistant request-body seam to. If it was moved, update both sides — do not ' +
        'reintroduce a second copy of the body.',
      { cause: error },
    );
  }

  try {
    return JSON.parse(raw) as AiSdkFixture;
  } catch (error) {
    throw new Error(`The shared fixture at ${FIXTURE} is not valid JSON:\n${raw}`, { cause: error });
  }
}

/**
 * The text a `content` value carries: the string itself, or every `{"type":"text","text":…}` part of
 * an array concatenated in order. Mirrors `ChatMessageInput.ExtractContentText` on the server, so
 * the non-vacuity guard below cannot be satisfied by a shape the endpoint would read as empty.
 */
function textOfContent(content: unknown): string {
  if (typeof content === 'string') return content;
  if (!Array.isArray(content)) return '';
  return content
    .filter(
      (part): part is { type?: string; text?: unknown } =>
        typeof part === 'object' && part !== null,
    )
    .filter((part) => part.type === 'text' && typeof part.text === 'string')
    .map((part) => part.text as string)
    .join('');
}

/** The fixture's last `user` entry — the turn the endpoint resolves out of the body. */
function lastUserEntry(fixture: AiSdkFixture): AiSdkMessage | undefined {
  const messages = Array.isArray(fixture.messages) ? fixture.messages : [];
  for (let i = messages.length - 1; i >= 0; i -= 1) {
    if (messages[i]?.role === 'user') return messages[i];
  }
  return undefined;
}

/** The path the endpoint is mounted on, read from the controller's own route attributes. */
function readServerRoute(): string {
  const source = readFileSync(CHAT_CONTROLLER, 'utf8');
  const controllerRoute = /\[Route\("([^"]+)"\)\]/.exec(source);
  const actionRoute = /\[HttpPost\("([^"]+)"\)\]/.exec(source);
  if (!controllerRoute || !actionRoute) {
    throw new Error(`Could not read the chat route attributes from ${CHAT_CONTROLLER}.`);
  }
  return `/${controllerRoute[1]}/${actionRoute[1]}`;
}

/** The URL `AssistantProvider` posts to, read from `getAssistantChatStreamUrl`'s source. */
function readClientUrl(): string {
  const source = readFileSync(path.join(WEB, 'lib', 'api', 'assistant.ts'), 'utf8');
  const fn =
    /export\s+function\s+getAssistantChatStreamUrl\s*\([^)]*\)\s*:\s*string\s*\{([\s\S]*?)\n\}/.exec(
      source,
    );
  if (!fn) throw new Error('Could not read getAssistantChatStreamUrl from web/lib/api/assistant.ts.');
  const returned = /return\s+['"`]([^'"`]+)['"`]/.exec(fn[1]);
  if (!returned) {
    throw new Error(
      'getAssistantChatStreamUrl no longer returns a literal URL (it may interpolate a route ' +
        'parameter again). The assistant moved to a global endpoint; verify this deliberately.',
    );
  }
  return returned[1];
}

/**
 * The server thread the capture harness binds and sends with — the ONE key the captured body
 * carries that the shared fixture does not.
 *
 * Read from the harness's source rather than restated, for the same reason `readClientUrl` and
 * `readServerRoute` read their values from source: a copy here could drift from the harness and the
 * deep-equality assertion below would then be pinning the copy instead of the client.
 */
function readCaptureThreadId(): string {
  const source = readFileSync(path.join(WEB, CAPTURE_HARNESS_SOURCE), 'utf8');
  const declared = /export\s+const\s+CAPTURE_THREAD_ID\s*=\s*'([^']+)'/.exec(source);
  if (!declared) {
    throw new Error(
      `Could not read CAPTURE_THREAD_ID from web/${CAPTURE_HARNESS_SOURCE}. The client half of ` +
        'this seam asserts the captured body against the shared fixture plus exactly that thread ' +
        'id; if the harness no longer binds one, this spec has stopped pinning the bound turn.',
    );
  }
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(declared[1])) {
    throw new Error(
      `CAPTURE_THREAD_ID is '${declared[1]}', which is not a dashed GUID. The endpoint binds ` +
        '`threadId` as a Guid, so a capture taken with anything else is not a body it would accept.',
    );
  }
  return declared[1];
}

/**
 * Renders the real client component stack and returns the body it actually posted on a turn sent
 * once its server thread was bound — the only kind of turn the shipped client sends, since every
 * send path is inert until a thread is bound.
 *
 * The harness runs as a SUBPROCESS because rendering React needs a DOM and this Playwright project
 * has none. It is handed an output path so the body arrives as a file rather than being parsed back
 * out of vitest's stdout, and `NODE_ENV=test` is forced because this shell exports
 * `NODE_ENV=production`, under which React Testing Library fails with `React.act is not a function`.
 */
function captureRealClientBody(): Record<string, unknown> {
  const dir = mkdtempSync(path.join(tmpdir(), 'assistant-seam-'));
  const out = path.join(dir, 'capture.json');
  const env = { ...process.env, NODE_ENV: 'test', ASSISTANT_SEAM_CAPTURE_OUT: out };

  try {
    try {
      execFileSync('npx', ['vitest', 'run', CAPTURE_HARNESS, '--reporter=default'], {
        cwd: WEB,
        env,
        encoding: 'utf8',
        stdio: 'pipe',
        timeout: 180_000,
      });
    } catch (error) {
      const stdout = (error as { stdout?: unknown })?.stdout ?? '';
      const stderr = (error as { stderr?: unknown })?.stderr ?? '';
      throw new Error(
        'The real client component stack could not be driven to a captured request body. The ' +
          `harness at web/${CAPTURE_HARNESS} renders AssistantShellProvider → AssistantDrawer → ` +
          'AssistantProvider → AssistantChatPanel, binds a server thread and captures the body of ' +
          'the turn it then sends; a failure there is a client-side regression, not a fixture ' +
          'problem.\n' +
          `--- harness stdout ---\n${String(stdout)}\n--- harness stderr ---\n${String(stderr)}`,
        { cause: error },
      );
    }

    return JSON.parse(readFileSync(out, 'utf8')) as Record<string, unknown>;
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

/**
 * What the shipped client adds on top of the fixture besides the bound thread: the page the user is
 * on (`AssistantChatDto.PageContext` on the server, optional there, which is why the shared fixture
 * leaves it out). The harness renders at the root route with the default document title, so the
 * client sends the "other" kind with no entity ids. Pinned here so a change in what the client
 * reports about the page is noticed.
 */
const HARNESS_PAGE_CONTEXT = { path: '/', kind: 'other', title: 'ReelBolt', entityIds: {} };

test.describe('assistant seam — the client body is the shared fixture, on the shared route', () => {
  test('the client posts to the endpoint the controller actually exposes', async () => {
    expect(
      readClientUrl(),
      'The URL the client fetches and the route the controller exposes have diverged. Exactly one ' +
        'of the two halves moved: either getAssistantChatStreamUrl in web/lib/api/assistant.ts, or ' +
        `the [Route]/[HttpPost] pair in ${path.relative(REPO, CHAT_CONTROLLER)}.`,
    ).toBe(readServerRoute());
  });

  /**
   * Non-vacuity guard. The deep-equality test below is only worth anything if the fixture still
   * describes a real turn: a fixture that silently became `{}` would deep-equal a client that had
   * stopped sending everything, and the seam would look green while carrying nothing.
   */
  test('the shared fixture still describes a real turn', async () => {
    const fixture = readFixture();

    expect(
      Array.isArray(fixture.messages) && fixture.messages.length > 0,
      `The shared fixture at ${FIXTURE_LABEL} has no non-empty "messages" array, so it can no ` +
        'longer pin anything the endpoint would accept as a turn. Restore the AI-SDK body the ' +
        'client actually posts rather than letting the fixture decay into an empty object.',
    ).toBe(true);

    const turn = lastUserEntry(fixture);
    expect(
      turn,
      `The shared fixture at ${FIXTURE_LABEL} has no "user" entry in "messages", so the endpoint ` +
        'would resolve it to an empty turn and answer 400. It must carry the turn the client sends.',
    ).toBeDefined();
    expect(
      textOfContent(turn?.content),
      `The shared fixture at ${FIXTURE_LABEL} carries a "user" entry whose content has no text, so ` +
        'the endpoint would resolve it to an empty turn and answer 400.',
    ).not.toBe('');
  });

  /**
   * The contract itself: the REAL component stack's body must deep-equal the fixture PLUS the one
   * server thread it is bound to, exactly.
   *
   * Deep equality rather than "the fixture's keys are present" is deliberate — a client that starts
   * sending an extra key the server ignores is still a client whose wire has changed, and the next
   * change to that key is the one nobody notices. That is why the bound `threadId` is part of the
   * expected object rather than filtered out of the captured one: the comparison still notices an
   * EXTRA key, and it pins the thread id's exact value.
   *
   * The fixture itself stays on the allocate path, because that is a live server contract the C#
   * half exercises against the same file — see this file's header. What the client adds on top of
   * it is exactly one key, and it is asserted here.
   */
  test('the body the real component stack posts deep-equals the shared fixture plus its bound thread', async () => {
    const fixture = readFixture();
    const boundThreadId = readCaptureThreadId();
    const captured = captureRealClientBody();

    expect(
      captured,
      'The body the REAL client component stack posts no longer deep-equals the shared fixture at ' +
        `${FIXTURE_LABEL} plus the bound thread id it sends with.\n` +
        'The fixture is the artifact both halves of the assistant request-body seam are pinned to ' +
        '— this spec pins the client to it, and AssistantChatSeamContractTests pins the server to ' +
        'it — so one of the two halves has moved:\n' +
        '  - the CLIENT half: web/components/assistant/AssistantProvider.tsx, ' +
        'getAssistantChatStreamUrl in web/lib/api/assistant.ts, or an upgrade of ' +
        '@assistant-ui/react-data-stream changing what it serializes; or\n' +
        `  - the SHARED FIXTURE itself: ${FIXTURE_LABEL}.\n` +
        'Decide which half actually changed and move the OTHER one to match. Do not edit the ' +
        'fixture merely to make a client that drifted on its own go green, and do not edit the ' +
        'client to match a fixture somebody hand-edited without checking the wire.\n\n' +
        'Note that the fixture deliberately carries NO threadId: it is the SERVER\'s allocate-path ' +
        'input, and the C# half drives exactly that path against it. The client always sends the ' +
        'thread it is bound to, which is why it is added here.\n\n' +
        'To regenerate the fixture from the real stack — the only sanctioned way to change it:\n' +
        `  ${REGENERATE_COMMAND}\n\n` +
        `Captured client body: ${JSON.stringify(captured)}\n` +
        `Fixture:             ${JSON.stringify(fixture)}\n` +
        `Bound thread id:     ${boundThreadId}`,
    ).toEqual({ ...fixture, threadId: boundThreadId, pageContext: HARNESS_PAGE_CONTEXT });
  });
});
