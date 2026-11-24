---
title: Inference providers
sidebar_position: 2
description: How ReelBolt platform administrators connect the installation's AI services, and how a team workspace connects its own, for agents, transcription, captioning, video generation, voiceover and search.
---

# Inference providers

An **inference provider** is a connection to an AI service: the place ReelBolt sends work when an agent needs to think, when footage needs a transcript, when a clip needs to be generated, and so on. Nothing AI-powered works until at least one provider is set up, and ordinary users never have to touch these pages to *use* ReelBolt — only to change where its work is sent.

There are **two levels** of connection, and this page covers both:

| Level | Where it is managed | Who can change it |
|---|---|---|
| **Platform-managed** — the installation's own connections, shared by every workspace | **Platform → Inference** (`/app/admin/inference-providers`) | Platform administrators only |
| **Workspace (bring your own)** — a team workspace's own connections | **Organization → Providers** (`/app/org/inference-providers`) | That workspace's Owners and Admins |

The rest of this page is about the **Platform** page. The workspace page offers a reduced version of the same form, described under "A workspace's own connections" below.

## How a provider is organized

A provider is set up in up to four levels:

| Level | What it is | Example |
|---|---|---|
| **Provider** | One account with a service: its kind, address, API key and timeout. Add each account once. | "Anthropic" with your company key |
| **Model** | A model you use from that account, with the name people see when they choose it. A provider can hold many models. | `claude-sonnet-5`, shown as "Claude Sonnet" |
| **Capability** | One job a model does for ReelBolt, such as `Chat` or `Vision`. A model can do several jobs. Each capability has its own default, enabled switch and test. | "Claude Sonnet" does `Chat` and `Vision` |
| **Price** | What one unit of that job costs (platform page only). | a cost per million input tokens |

The key and the address belong to the provider, so changing either one applies to every model under it. The page says so before you save.

## Adding a provider

Click **New Provider**. The form creates the provider and its first model in one step:

- **Name**: your own label for the account, for example "Production Azure". Names must be unique.
- **Kind**: which service or vendor this is (see the next section). It cannot be changed later.
- **Capability**: the first job this model does. You can add more jobs afterwards.
- **Endpoint** / **Base URL**: the service's web address. It is filled in automatically for vendors with a fixed address.
- **Model** (or **Deployment name** for Azure OpenAI): which model to use.
- **Display name**: what people see when they choose this model. Left blank, it is the model's name.
- **API Key**: the secret that lets ReelBolt use the service. See "API keys" below.
- **Timeout (seconds)**: how long to wait for an answer before giving up. Left empty, the default is 300 seconds.
- **Set as default … provider**: makes this the model used for its capability unless something more specific is chosen.
- **Enabled**: switch off to stop using the provider without deleting it.

Use **Test connection** before saving to check the details work, then **Create**.

## Adding models and capabilities

Click a provider in the list to open its page. The page shows the account at the top and its models below, each with its capabilities.

- **Add model** adds another model under the same account, with its first capability. You do not type the key again.
- **Add capability** on a model adds another job for that model, for example `Vision` on a model that already does `Chat`.
- On each capability, **Default** makes it the default for that job, **Enabled** switches it on or off, **Test** checks it works (see "Testing a ReelBolt inference provider" below) and the bin icon removes it.
- **Edit** at the top changes the account: name, address, key, timeout and enabled switch.

## Prices and markup

Prices exist on the **Platform** page only. Work done through a workspace's own provider is never charged, so its capabilities have no prices.

Each capability on the platform page lists its prices: the unit, an optional variant, what the vendor charges (**Cost**), an optional **Credits override**, the credits it actually charges (**Effective credits**), the **Margin**, and when it applies. To change a price, click **Add price**:

- **Unit**: what is counted, for example input tokens, seconds of generated video or pictures made.
- **Variant**: optional. It separates prices of one unit, such as a video resolution (`1080p`) or a picture quality tier.
- **Cost**: what the vendor charges for one unit. The form is pre-filled from ReelBolt's list of published vendor prices when it knows the model.
- **Credits override**: optional. Leave it blank to charge cost × markup ÷ credit value.
- **Effective from**: when the price starts. Empty means now.

A new price starts at the chosen time and replaces the current price for the same unit and variant, so past usage keeps the price it was charged. **Retire** ends a price; that unit is then unpriced unless another price covers it. When you add a capability on the platform page, ReelBolt fills in its prices from the published vendor prices it knows.

The **markup** is set once for every price, on **Platform → Billing** under **Pricing**. A price with no credits override charges cost × markup ÷ credit value. **Credit value** (what one credit is worth) is shown next to it and cannot be changed there.

**Missing prices.** The same page lists every enabled platform capability that has no price for something it uses ("N capabilities have no price"). Work through such a capability is recorded but charged 0 credits, so add a price for each one listed.

## Capabilities: what each kind of ReelBolt provider connection is for

| Capability (form label) | What ReelBolt uses it for |
|---|---|
| `Chat` (**Chat**) | The thinking behind every agent: scripts, edit decisions, plans, reviews, the assistant. Required for almost everything. |
| `Transcription` (**Transcription (ASR)**) | Turning speech in your footage into a transcript, so editing agents know what is being said. |
| `Vision` (**Vision (shot captioning)**) | Describing what is on screen in each shot, as optional extra detail for editing agents. |
| `Decision` (**Decision (Jev / logprob)**) | Fast yes/no and multiple-choice judgements used by the optional decision models. See [Decision models](./decision-models.md). Never used for chat. |
| `VideoGeneration` (**Video Generation**) | Creating new video clips (b-roll, opening shots, end cards). Costs real money per clip and is budget-capped. |
| `SpeechSynthesis` (**Speech Synthesis (TTS / voiceover)**) | Turning script text into voiceover narration. |
| `WebSearch` (**Web search**) | Letting some agents look up current facts on the web, billed per search. DeepSeek, Google Gemini, and OpenAI (endpoint `https://api.openai.com/v1` only). See [Web search for agents](../workflows/web-search.md). |
| `Embedding` (**Embedding (semantic file search)**) | Searching project files by meaning, and the assistant's documentation search. See [Semantic search](./semantic-search.md). |
| `ImageGeneration`, `ImageEdit`, `ImageErase`, `ImageFill`, `ImageExpand`, `ImageBackgroundReplace`, `ImageRelight`, `ImageUpscale`, `Segmentation`, `Depth` | The AI tools of the [thumbnail editor](../editor/thumbnails.md), one capability per tool. See [Picture editor tools](#picture-editor-tools-one-capability-per-tool). |
| `VideoSegmentation` (**Video segmentation (object following)**) | Following one object through footage with a hosted model, so an effect can be applied to just that part of the picture. fal.ai only; the model is `fal-ai/sam-3-1/video-rle`. See [Blur or highlight one object](../recipes/prompted-region-effects.md). |

## Which provider kinds serve which capabilities in ReelBolt

Not every service can do every job. The form only offers the capabilities a kind supports, and ReelBolt refuses unsupported combinations when you save.

| Kind (form label) | Chat | Transcription | Vision | Decision | VideoGeneration | SpeechSynthesis | Embedding |
|---|---|---|---|---|---|---|---|
| `AzureOpenAI` (**Azure OpenAI**) | yes | yes | yes | yes | no | no | yes |
| `OpenAICompatible` (**OpenAI-compatible**) | yes | yes | yes | yes | no | yes | yes |
| `Anthropic` (**Anthropic (Claude)**) | yes | no | yes | no | no | no | no |
| `Gemini` (**Google Gemini**) | yes | no | yes | no | no | no | no |
| `DeepSeek` (**DeepSeek**) | yes | no | yes | yes | no | no | no |
| `TypeSafe` (**TypeSafe AI (Jev)**) | no | no | no | yes | no | no | no |
| `OpenJev` (self-hosted decision server) | no | no | no | yes | no | no | no |
| `MiniMax` (**MiniMax**) | no | no | no | no | yes | no | no |
| `Higgsfield` (**Higgsfield**) | no | no | no | no | yes | no | no |
| `ComfyUI` (**ComfyUI (self-hosted)**) | no | no | no | no | yes | no | no |
| `FishAudio` (**Fish Audio**) | no | yes | no | no | no | yes | no |

Notes:

- **OpenAI-compatible** covers OpenAI itself and any service that speaks the same language, including models you host yourself (for example on your own GPU server) and the two local helper services described below. To use an OpenAI model, set the **Endpoint** to `https://api.openai.com/v1`; ReelBolt then talks to OpenAI's newer interface automatically, so agents can use tools together with a reasoning effort, and it leaves out the temperature setting while reasoning is on (OpenAI refuses it).
- **OpenAI web search.** A Web search connection of the OpenAI-compatible kind works only when the endpoint is OpenAI's own (`https://api.openai.com/v1`); ReelBolt refuses any other address for this capability. It uses OpenAI's built-in search tool and bills each search plus the model's usage.
- **Anthropic, Google Gemini and DeepSeek cannot transcribe speech.** Use Azure OpenAI, an OpenAI-compatible service (such as the bundled local speech-to-text service), or **Fish Audio** for `Transcription`.
- **Fish Audio does two jobs and only two:** `SpeechSynthesis` and `Transcription`. Its transcription is Fish Audio's hosted speech-to-text service; a Fish Audio connection used for `Transcription` needs its API key filled in (an empty key is refused, because there is no no-key self-hosted speech-to-text service to fall back to). Note that Fish Audio's transcription reports what was said and when each phrase started and ended, **but not which word falls where** — so caption timing from a Fish Audio transcript is phrase-level, not word-level. Use a Whisper-compatible connection if you need per-word timings.
- **Video generation is MiniMax, Higgsfield or ComfyUI only**, and those three do nothing else. MiniMax and Higgsfield are paid cloud services. **ComfyUI is a server you run yourself** (optionally the bundled `comfyui` service, which needs a GPU and is off by default): it costs no money, so a workflow caps it in **GPU minutes** instead of dollars; its address may be a private one (for example `http://comfyui:8188`); and its **Model** must be one of the two built-in workflow names, `wan2.2-ti2v-5b-t2v` or `wan2.2-ti2v-5b-i2v` (Wan 2.2, an Apache-2.0-licensed model) — ReelBolt never sends it any other workflow. Its **API Key** is optional and only needed if a reverse proxy in front of the server expects one. **Voiceover** uses Fish Audio (its hosted service, or a Fish speech server you run yourself) or an OpenAI-compatible speech server such as the bundled local one (`http://whisper:8000/v1`, zero cost). Fish Audio is also a valid **speech-to-text** service (`Transcription`), not only a voice service. Only Fish Audio can **clone a voice** from recordings; a cloned voice is a copy of a real person's voice, and ReelBolt records who attested that person's consent, keeps the voice only for the retention period you set, and lets you revoke consent or delete the voice and its recordings at any time from the project's **Voices** tab.
- **`OpenJev`** is for a decision server you host yourself. It is not in the form's **Kind** list; a platform administrator can create it by asking the [assistant](../assistant/index.md).
- Vision connections must use a model that accepts images.

## Picture editor tools: one capability per tool

Each AI tool of the [thumbnail editor](../editor/thumbnails.md) uses its own capability, so you can give every tool a service and model that is good at that job:

| Editor tool | Capability (form label) | Who can serve it | Default fal.ai model |
|---|---|---|---|
| **Create a picture** | `ImageGeneration` (**Image Generation (text-to-image)**) | fal.ai, OpenAI-compatible, Black Forest Labs, MiniMax | `ideogram/v4.5` |
| **Edit with words** | `ImageEdit` (**Image Edit (edit from a description)**) | fal.ai, OpenAI-compatible, Black Forest Labs, MiniMax | `google/nano-banana-2.1/edit` |
| **Erase** | `ImageErase` (**Image Erase (remove objects)**) | fal.ai, OpenAI-compatible, Black Forest Labs | `fal-ai/object-removal/mask` |
| **Fill** | `ImageFill` (**Image Fill (paint in an area)**) | fal.ai, OpenAI-compatible, Black Forest Labs | `fal-ai/flux-pro/v1/fill` |
| **Expand** | `ImageExpand` (**Image Expand (outpaint)**) | fal.ai only | `fal-ai/bria/expand` (takes a description; `fal-ai/flux-2-pro/outpaint` is the alternative, without one) |
| **New background** | `ImageBackgroundReplace` (**Image Background Replace**) | fal.ai only | `fal-ai/bria/background/replace` |
| **Relight** | `ImageRelight` (**Image Relight**) | fal.ai only | `fal-ai/image-apps-v2/relighting` |
| **Upscale** | `ImageUpscale` (**Image Upscale**) | fal.ai only | `fal-ai/seedvr/upscale/image` |
| **Cut out a subject**, **Remove background** | `Segmentation` (**Segmentation (subject cut-out)**) | fal.ai only | `fal-ai/sam-3-1/image` (background removal always uses `fal-ai/birefnet/v2`) |

- **The model is the job's endpoint.** On a fal.ai connection, **Endpoint id** must be one of the endpoints listed for that tool; the form suggests them, the default first. ReelBolt refuses, when you save, a connection whose endpoint does a different job (for example an **Image Upscale** connection set to a relighting endpoint), because it would otherwise be quietly ignored.
- **Set up all picture tools in one click.** Open a fal.ai provider you already have and press **Set up all picture tools**. ReelBolt adds a model under that same provider for every picture tool that does not have one yet, each with that tool's default endpoint. They all use the provider's key, so you do not type it again. A new connection becomes the default for its tool only where the tool had no default, so nothing you already chose is replaced. Test each new connection from the list afterwards.
- **A tool with no connection of its own** uses the default `ImageEdit` connection instead, as every tool did before they were split, **but only when that connection's service can do the job**. A fal.ai `ImageEdit` connection can therefore still run every tool, each on its own default endpoint; a MiniMax `ImageEdit` connection cannot remove objects or upscale, so those tools say they are not set up rather than failing on the service's side.
- Every picture tool is a paid call, billed per picture made at the price of the endpoint that actually ran.

## Platform-managed connections and a workspace's own connections

A **platform-managed** connection belongs to the ReelBolt installation. It is available in every workspace, and it is what every workspace uses unless the workspace has provided its own. Platform administrators are the only people who can see or change one. This is the page you are reading.

A **workspace connection** (also called bring-your-own, or BYO) belongs to one team workspace. It is visible only inside that workspace, it is paid for by that workspace, and nobody else's work is affected by it. If a workspace marks one of its own connections as the default for a capability, that connection wins for that workspace — the platform default still applies everywhere else.

The two are fully separate. A member of a workspace cannot see the platform page, and a platform administrator cannot see or change a workspace's connections.

## A workspace's own connections

A team workspace's **Owners and Admins** manage its own connections on **Organization → Providers**, the page titled **Providers** under the **Organization** group in the left menu. The form works exactly like the one described above, with three deliberate differences, all enforced by ReelBolt whatever the form offers:

- **No `env:` shortcut.** A workspace connection must carry its own key; it can never be pointed at a key from the installation's environment.
- **No personal subscription mode.** The **Dev/OAuth (Personal use only)** switch, which the platform form offers for Anthropic, is not available here.
- **Public addresses only.** A workspace connection must use a public address. Self-hosted services on the installation's private network can only be platform-managed connections.

Two more rules are worth knowing:

- Adding, changing or testing a workspace connection requires the workspace's plan to include bringing your own providers. If it does not, the form says so and offers the upgrade path rather than failing obscurely. **Seeing the list and deleting a connection always work**, so a workspace that has lost the feature can still clean up after itself. Connections that are dropped by the plan are simply not used until the plan allows them again.
- **A Member cannot open the page**, but can still choose among the connections a step's settings offer, exactly as on the platform page.

The workspace page is described from the workspace's point of view in [Workspaces and teams](../workspaces.md) and [Bring your own provider](../account/bring-your-own-provider.md).

## Default providers: one per capability

Each capability has its own independent default. You can have a default `Chat` provider, a default `Transcription` provider and a default `Embedding` provider all at once; making one connection the default only replaces the previous default **for the same capability**. A workspace can also have a default of its own for a capability, and where it does, **the workspace's default wins inside that workspace** while the platform default keeps serving everyone else.

When ReelBolt needs a capability it uses, in order:

1. a connection chosen specifically for that job (for example a particular agent's override, or a provider picked in a step's settings),
2. otherwise the default connection for that capability,
3. otherwise, for `Chat` only, the Azure OpenAI details in the installation's settings file, if any were set. For `Embedding` the same settings file is also used as a last resort.

For every other capability there is no fallback: if no default exists, features that need it either skip that part (for example, an edit without a transcript) or stop with an error when the step's settings say it is required.

You cannot delete a capability that is currently the default for its job, or a model or provider that holds one; make another model the default first. Changes to providers take effect within about a minute, without restarting anything.

**Speech (narration) also looks at test results.** For `SpeechSynthesis` only, if the default connection **failed its last test** and another speech connection **passed** its test, ReelBolt narrates with the working one, and the run shows a warning ("Your default voice service failed its last connection check…"). A connection picked in a step's settings is always used as chosen, and a default that was never tested is trusted. If the run used a connection whose last test failed, it warns that narration may be missing. Re-test the default after fixing it and it is used again within about a minute. Any signed-in user can see which voice service a run would use and whether it is healthy (no addresses or keys are shown).

## Testing a ReelBolt inference provider

**Test** on a capability (or **Test connection** in the form) sends a tiny request through it and reports **Connection OK** or **Connection failed** with the reason. A capability's result is saved and shown on its row on the provider's page (hover for the time or the error). What the test does depends on the capability:

- `Chat`: asks for a one-word reply.
- `Transcription`: sends a fraction of a second of silent audio.
- `Vision`: sends a tiny image and asks for "ok".
- `Decision`: asks a fixed question ("Is 2 greater than 1?") and checks the service is clearly confident the answer is yes.
- `Embedding`: turns the word "ping" into numbers and reports the length (for example `vector length 1024`).
- `SpeechSynthesis`: synthesises the word "ok" and checks real audio came back.
- `VideoGeneration`: checks the key and service respond **without** creating a video, so the test costs nothing.
- The picture capabilities (`ImageGeneration`, `ImageEdit`, `ImageErase`, `ImageFill`, `ImageExpand`, `ImageBackgroundReplace`, `ImageRelight`, `ImageUpscale`) and `Segmentation`/`Depth`: send the service an empty request it refuses without making a picture, so the test costs nothing. On fal.ai the test also checks the connection's endpoint is one for that tool.

When editing a saved connection you can test without retyping the key; the stored key is reused.

When a test fails, the reason includes what actually went wrong — for example a refused connection with the address it tried, or the service's own error message and request number — so you can tell a wrong address from a wrong model name from an outage on the service's side. For MiniMax, a failure that comes back as a server error (500) while the key is accepted means the problem is on MiniMax's side; the message says so and includes MiniMax's request id for their support team.

## Asking the assistant to test a ReelBolt inference provider

Platform administrators can also ask the assistant to run the test, for example "test the MiniMax connection and tell me why it fails". The assistant runs exactly the same test as the **Test connection** button, saves the result in the same place, reads the error, and explains it in plain words. It sends one tiny real request, just like the button, and changes no settings — if a setting needs to change, it proposes the change and waits for your approval.

## Choosing a different provider for one agent

By default every agent uses the default `Chat` provider of the workspace it runs in. A platform administrator can point a single agent at a different chat connection everywhere, and a workspace's Owner or Admin can do the same **for that workspace only**, for example a stronger model for the director or a cheaper one for code analysis.

On the platform page:

1. Open **Agents** and click the agent (built-in agents can be overridden too).
2. In **Inference Provider** ("Overrides the global default provider for this agent."), choose a connection, or **Default provider** to go back to the default.

A workspace's Owner or Admin sees the same control on the agent's page while that workspace is active, but only for a **built-in** agent — a built-in agent is one shared row, so their choice is stored against the workspace rather than on the agent. They can pick any enabled `Chat` connection the workspace can use, whether platform-managed or the workspace's own, and it applies to that workspace alone, taking precedence over the installation-wide setting. Everyone else, and every custom agent, sees the current choice read-only.

Only enabled `Chat` connections are listed. If you later delete the chosen connection, the agent quietly goes back to the default. Members can see which provider an agent uses but cannot change it.

## API keys and the env: shortcut

API keys are stored encrypted and are never shown again after you save them: the page only shows the last four characters (for example `•••• 1a2b`), and the assistant never reads them back.

- When editing, **leave API Key blank to keep the current key**. To remove a stored key, switch on **Remove the stored key**.
- For **Anthropic**, **Google Gemini** and **DeepSeek**, you can type the literal value `env:` instead of a key. ReelBolt then stores no key at all and uses the key from the server's own environment variables: `ANTHROPIC_API_KEY` or `ANTHROPIC_AUTH_TOKEN` for Anthropic, `GEMINI_API_KEY` or `GOOGLE_API_KEY` for Gemini, `DEEPSEEK_API_KEY` for DeepSeek. Whoever runs the installation sets these.
- For those three kinds, a **blank key is an error**, not a silent fallback: a connection with no key and no `env:` fails when used.
- MiniMax and Fish Audio keys are used exactly as you enter them; `env:` has no special meaning there.

**Anthropic personal subscriptions.** The Anthropic form has a **Dev/OAuth (Personal use only)** switch for a personal Claude subscription credential instead of a Console API key. A personal subscription is licensed for its owner's own use only; an installation that serves other people must use a proper API key.

**Fish Audio licensing.** Fish Audio's hosted service, including its free tier, has no documented non-commercial restriction. Running Fish's open models on your own server is different: commercial use needs a separate paid licence from Fish Audio.

## Setting up local speech-to-text (the whisper service)

ReelBolt's standard installation includes an optional local speech-to-text service called `whisper`, which transcribes footage on your own server instead of sending audio to a cloud vendor. It is not connected automatically. To use it:

1. Make sure the `whisper` service is running (whoever runs the installation can start it).
2. Click **New Provider** and set **Kind** to **OpenAI-compatible**, **Capability** to **Transcription (ASR)**.
3. Set **Base URL** to `http://whisper:8000/v1` (the service's address inside the installation).
4. Set **Model** to one of the suggestions, for example `Systran/faster-whisper-small` (fast) or `Systran/faster-whisper-large-v3` (more accurate, slower). The model is downloaded the first time it is used, so the first transcription is slow.
5. Leave **API Key** blank, switch on **Set as default transcription provider**, click **Test connection**, then **Create**.

It runs on the processor, not a graphics card, so long footage takes a while to transcribe.

## Setting up local search (the embeddings service)

The standard installation also includes an optional local service called `embeddings`, which powers search-by-meaning without a cloud vendor. To connect it:

1. Make sure the `embeddings` service is running. The first start downloads its model and can take a few minutes.
2. Click **New Provider**: **Kind** **OpenAI-compatible**, **Capability** **Embedding (semantic file search)**.
3. **Base URL** `http://embeddings:80/v1`, **Model** `Qwen/Qwen3-Embedding-0.6B`, **API Key** blank.
4. Switch on **Set as default embedding provider**, click **Test connection** (it should report `vector length 1024`), then **Create**.
5. Reindex existing project files so they become searchable; see [Semantic search](./semantic-search.md).

## Managing inference providers through the assistant

How platform administrators can ask the [assistant](../assistant/index.md) to list connections, add one, change one or remove one. Every change is shown on a card first and only made after you press **Allow** (unless you have switched the assistant's **Ask before changes** off). The assistant never shows stored API keys, and the same rules apply as on this page (for example, it cannot delete a default connection).

The assistant's provider tools act on **platform-managed** connections only. It cannot list, read, test or change a workspace's own connections, so a workspace Owner or Admin who asks it to change one will be told the connection does not exist — use **Organization → Providers**.
