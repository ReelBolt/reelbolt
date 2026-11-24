# Consent, approvals, budget and sign-in: reference

## Footage-sharing consent

The refusal text is: "The permission to send the footage to an outside service is given by you in
the app, not by the assistant ...". Its JSON form is `{ "error": "...", "code":
"egress_consent_is_the_customers" }`. A write is refused when it turns the flag on where the stored
workflow had it off or absent. A recipe that ships the consent on is created with it off and
reported as `WL015`. A consent that was already on is kept, step by step, when nothing that
decides what THAT step sends changes: the step itself (every setting but its label: sources,
segmentation prompts and targets, provider choices, keyframes, cleanup target, limits), every step
it reads (directly or through other steps; a step written by an agent counts as reading every step
before it), its position, and every review loop, ForEach or Conditional step of the workflow. An
edit elsewhere (a later compile's framing, a step appended or deleted after it) keeps it. An edit
that touches it switches it off in the saved workflow and reports a `needsBeforeRunning` error
`WL015` for that step only, which the customer clears in the app. A copy into another project
always starts with it off.

What you can do: build the workflow without the flag, tell the customer which step needs it, and let
them switch it on in the app before the run.

## Approval cards and auto mode

- Dashboard chat: every `Apply*` tool and `ExecuteWorkflow` is held behind a card. A decline or an
  expiry means the tool does not run, and the model is told not to try again. Auto mode is a
  per-user preference that skips cards, deletions and paid runs included.
- Over MCP: the tools are built with no gate and each write is one collapsed tool. The external
  client asks its own user, so ask yours before every write (`CreateFromTemplate`, `SaveWorkflow`,
  `WriteProjectFile`, `DecideDesignChange`, …) and `ExecuteWorkflow`, and before anything that costs
  credits.
- Lint errors never reach a card; over MCP they refuse the write and return the findings.
- Authority never widens: the tool still checks its confirmation token (minted and consumed inside
  the one call), ownership, role and plan on every call.
  A refusal for permissions, plan or rate limit is a normal result (`isError: false`); relay it.

## Budget

| Limit | Applies to | Default |
|---|---|---|
| Tool calls per turn (`Assistant:MaxToolIterations`) | One turn of the dashboard assistant (chat). Not applied over MCP. | 80 |
| Credit ceiling per turn (`Billing:NonWorkflow:AssistantTurnCeilingCredits`) | The model usage metered for one assistant turn or one MCP tool call. It is not the cost of a workflow run. | 100 |
| Runs per rolling hour | `ExecuteWorkflow`, per person and project, shared by the dashboard assistant and Claude Code. | Set by the deployment |

A deployment can change these, so treat the defaults as typical. When a turn or call hits the
ceiling it is cut off. Over MCP the client gets plain text with `isError: true` saying the request reached
the most one assistant request may use; split the work or tell the user. Read tools such as
`ListProjects` stay free even when the monthly allowance is used up and the balance is 0; tools
that can spend are refused with a plain credits message.

## Credits

- `GetCreditBalance`: `{ billingEnabled, balance, reserved, available, plan, renewsAt? }`, in whole
  credits; `{ "billingEnabled": false }` when billing is off.
- `INSUFFICIENT_CREDITS` (from `ExecuteWorkflow`, `ReviseComposition`): `code`, `message`
  (plain words, includes how many more credits are needed), `missing`, `required`, `balance`, `low`,
  `high`, `typical`. `required` is the hold taken at start, not the usual cost; credits not used are
  returned when the run finishes.

## Sign-in

- **OAuth (the plugin's path).** Claude Code registers itself, opens the browser, the customer
  signs in and approves on a consent screen. The access token is a normal ReelBolt token with a
  `scope` claim; the scope `assistant.tools` allows use of the assistant's tools, and the
  separate `assistant.admin` scope mirrors the account's admin-tools setting at consent time.
  The token is bound to the organization it was issued in; switching organization needs a new
  sign-in. Permissions are re-checked on the server on every call.
- **Admin tools.** Thirteen registered tools manage the deployment's own AI provider settings,
  agent skills and decision calibration; the four Propose/Apply pairs collapse, so they publish as
  nine MCP entries. They are absent from the catalog unless the account's admin-tools setting is on; when
  absent, a call is refused with `MCP_ADMIN_TOOLS_DISABLED`. Claude cannot enable it.
- **Stdio token.** Self-hosted tooling can run the ReelBolt server's `mcp` package over stdio with a
  bearer token. That is separate from this plugin, which has no token to paste. The plugin's run
  watcher signs in on its own, with a read-only credential (see the `run-and-watch` skill).
- Every MCP call is recorded (tool, time, success, duration). A record is not a charge.
