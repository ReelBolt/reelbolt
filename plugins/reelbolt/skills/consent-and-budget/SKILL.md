---
description: The rules Claude must follow over the ReelBolt MCP - the footage-sharing consent belongs to the customer and is given in the app, never by Claude; writes are single MCP calls and your own confirmation is the approval; approval cards and auto mode in the dashboard chat; paid-clip questions; credit refusals; per-turn budgets; sign-in scope. Use before any write, any run, or when a call is refused for consent, credits or permission.
---

# Consent, approvals, budget and sign-in

## The consent Claude never gives

Some steps can send the customer's footage to an outside service. That permission is the flag
`allowSourceMediaEgress` on the step, and it is **the customer's own, given in the app** by opening
the step and switching on the option that allows sending their footage.

- Never set, pass, copy in, fake or guess it, in any workflow, patch or recipe config.
- The server enforces this: a write that turns it on is refused with the code
  `egress_consent_is_the_customers`. Relay that message and send the customer to the app. Do not
  rephrase the request to get around it.
- A consent that was already on is kept when the edit does not touch what that step sends: the
  step itself (labels aside), the steps it reads, its place in the order and any review loop are
  unchanged. Editing another step (a later compile, say) keeps it. An edit that does touch it (its
  source, prompt, provider or target, a step it reads, an insert or delete before it, a reorder)
  switches it off in the saved workflow and adds a `WL015` note naming that step; the customer must
  allow it again in the app. Relay that note.
- Linter finding `WL015` and `needsBeforeRunning` entries about consent are the customer's to clear.
- That flag is the step's saved switch. A single run can be given the permission another way: an app whose steps send
  media has a field `allowMediaEgress` (shown by `GetWorkflow`). Pass `brief.inputs.allowMediaEgress: true` to
  `ExecuteWorkflow` only when the customer asked for a run that needs it. Over MCP this works only after the customer
  switched on "Let connected tools (MCP) allow sending my media to AI services" in ReelBolt's Settings; otherwise the
  tool answers `media_egress_not_allowed` and starts nothing. Relay that message; do not look for another way. Never
  create, bind or remove that field, and never say media was not sent for a run that allowed it: the result's
  `mediaEgress` lists what was allowed to leave.
- A cloned voice (made from someone's recordings) needs that person's consent, attested by the customer
  in the project's Voices tab; there is no tool for it. A library voice needs none: `AddLibraryVoice`.

## Writes are one MCP call

Over MCP each write is a single tool — `CreateFromTemplate`, `SaveWorkflow`, `WriteProjectFile`,
`CreateProject`, `SaveDesignSystem`, `DecideDesignChange`, and the rest. The dashboard assistant's
`Propose*` / `Apply*` pairs are not MCP tools; the server runs both halves in one request and mints
and consumes the confirmation token inside it. Never send a `confirmationToken`: it is not an MCP
argument and the server's own token wins.

Show the user what the call will do and get an explicit yes first. A refusal is a normal result
(`isError:false`) saying what is wrong; fix the arguments rather than retrying the same call.

## Approval cards and auto mode

In the dashboard chat, each `Apply*` tool and `ExecuteWorkflow` waits behind an approval card the
user clicks; "auto mode" skips the cards. **Over MCP (Claude Code) there is no card and no gate**:
the single write tool runs when you call it, so you are the user's only check. Do not rely on a card.
The one exception is `DecideDesignChange` with `decision: "approve"`, which the server always refuses
over MCP (see the `design-system` skill); `reject` is allowed.

## Paid work

- `VideoGenerate` clips are paid. Before `ExecuteWorkflow` on a workflow that has one, ask the user
  whether to reuse earlier clips (free) or regenerate (paid), then pass `regenerateVideoClips`.
  With it omitted the tool refuses and returns a cost estimate.
- Price a run first with `EstimateWorkflowRun`; check `GetCreditBalance` before a large one.
- Every built-in recipe's review step may re-make earlier steps inside the run (a narration line, a
  generated shot or take, the script) or change a setting; each redo is charged to the run. Its
  worst case is in the estimate's "up to" figure (reason `review_step_reruns`); say so when you
  quote a price. Running out of credits during a redo ends the review with the best version, not a
  failed run. The reviewer never changes prompts, models, providers, spend caps or footage consent.
- `INSUFFICIENT_CREDITS` comes back as a result with `message`, `missing` (credits still needed),
  `required` (the hold), `balance`, `low`, `high` and `typical`. Relay `message`; do not retry.

Read [`reference.md`](./reference.md) for the budget limits and sign-in details.

## Budget and sign-in in one line each

- The dashboard assistant stops a turn at 80 tool calls; Claude Code's own loop is not counted by
  that limit. Each assistant turn or MCP tool call also has a 100-credit ceiling on metered model
  usage. Both are deployment settings; see the reference.
- OAuth sign-in grants the scope `assistant.tools`; a refusal that says you may not use a tool is an
  answer. Admin tools are listed only if the account has them enabled.
