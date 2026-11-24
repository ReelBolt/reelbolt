# The 50-goal eval set

Fixture for the recipe-first assistant (spec: [`../workflow-apps.md`](../workflow-apps.md), sections 6 and 8). Machine-readable file: [`eval-goals.json`](eval-goals.json). Validate it from the repository root:

```
node docs/design/workflow-apps/check-eval-goals.mjs
```

The validator checks the shape, unique ids, route and brief vocabulary, lint rule ids (`WL001`-`WL019`) and that every `templateKey` exists in `inference/src/ReelBolt.Shared/Workflows/WorkflowTemplateCatalog.cs`.

## Entry shape

| Field | Meaning |
|---|---|
| `id`, `locale`, `goal` | Stable id, the language the person types in, the exact text typed |
| `project` | The state the stub project is built with: `code` (`none`/`react`/`blazor`/`other`), `videoClips`, `images`, `musicTrack`, `sfxFiles`, `consentedVoice`, `designSystem` (`none`/`draft`/`approved`), `providers` (configured capabilities), `plan` |
| `expected.route` | `template`, `template_with_patch`, `custom_graph`, `clarify_first`, `decline_with_alternative` |
| `expected.templateKey`, `expected.alternates`, `expected.patch` | The recipe; other template keys that are also correct answers (`alternates`, often empty); the plain-English patch (maps to a closed patch id in the spec's 6.1) |
| `expected.brief` | The run brief the assistant should produce (spec 3.1): `language`, `format`, `resolution`, `lengthSec`, `designSystem`, `stylePreset`, `voice`, `tone`, `notes`, `sourceFiles` roles. Absent = not stated or implied |
| `expected.clarify` | The one or two questions that must be asked first (`clarify_first` only) |
| `expected.lint` | Findings the naive route would produce, which the linter must flag |
| `expected.paid`, `expected.why` | Whether the run spends beyond tokens; the reason, citing the template or missing capability |

## Planned templates

`plannedTemplates` in the fixture lists decided templates that do not exist yet (today: none; `video-subtitles`, product decision Q16, shipped with lane 3e and G40 is a normal `template` goal). A goal that names one carries `expected.availability: "planned:<key>"`. **Until the template ships** the goal is scored as `decline_with_alternative` (the assistant must say it cannot do it yet and name the nearest route) and is left out of the template-accuracy metric; **after it ships** the lane deletes the entry from `plannedTemplates` and the goal's `availability`, and it scores as a normal `template` goal. The validator fails if a planned key already exists in the catalog, so the switch cannot be forgotten.

## Scoring (per run of the harness)

Drive the assistant with `goal` against a stub project built from `project`; no paid provider call, providers stubbed by capability. Record the proposal tool calls (`ProposeCreateFromTemplate`, `ProposeWorkflow`, `ExecuteWorkflow` arguments, questions asked), not rendered video. Three samples per goal; a goal passes a metric when at least two of three samples do (a single flake is not a miss).

| Metric | Definition | Target |
|---|---|---|
| Route accuracy | Chosen route equals `expected.route` | >= 85 % |
| Template accuracy | On `template` and `template_with_patch` goals, the chosen key equals `templateKey` **or is in `alternates`** | >= 80 % |
| Brief accuracy | Per field present in `expected.brief`: exact for `language`, `format`, `resolution`, `designSystem`, `voice`, `stylePreset`, `tone`; `lengthSec` within 10 %; `notes` not scored. **A field the expected brief omits but the assistant sets (a hallucinated `stylePreset` or `lengthSec`) counts as one miss for that goal** | language and format >= 95 % |
| Lint recall | Every expected rule id appears in the findings the assistant saw or the linter returns for its first proposal | >= 90 % |
| Clarification quality | A question is asked iff `route = clarify_first`, at most two; on `clarify_first` goals a judge (or hand review) also scores whether the questions are the ones in `expected.clarify` (asks the missing field, not something the project data answers) | unneeded questions on <= 2 goals |
| Paid disclosure | When `paid`, the lead-in or card states what is spent | 100 % |
| **Zero tolerance** | A template key that is not in the catalog (hallucinated); a paid step added that the goal did not call for; an id shown to the user | 0 occurrences |

**How to read the numbers.** With 50 goals, one goal is 2 points and a 95 % target allows two misses; the template metric is over about 40 goals, where a Wilson 95 % interval is roughly plus or minus 12 points. Report intervals with every score. These targets are a **regression floor** for assistant prompt and tool changes, not a launch statistic; grow the set toward about 80 goals before using any single figure as a ship gate, and treat the zero-tolerance rows as hard gates from day one. The current assistant is the baseline and is measured before it is changed.

Goals that rest on an assumption are listed in the fixture's `assumptions` where present and in spec section 11, Q14; resolve them before the goal gates anything.
