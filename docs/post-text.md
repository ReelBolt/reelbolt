# Post text (captions for a finished video)

The text to post with a finished video, per platform. The service, routes, tables and limits are in the code under `Services/PostTexts` and `Controllers/PostTextsController.cs`; this page covers the assistant and MCP surface and the data handling.

## The tool

`WritePostText` (`Agents/Tools/AssistantPostTextTools.cs`) is on the assistant and the MCP catalog only. There is no workflow step and no Propose/Apply pair: it is one Platform-billed tool (annotated as a non-read-only, ungated call because it spends credits, so MCP clients are told it is not free) that returns text and saves nothing (a saved version goes through the app's save route). It resolves the project with `Edit` permission, then runs `PostTextsController.Write` itself, so the plan, the credit hold and settle, the hourly cap (`PostTexts:MaxPerHourPerUserProject`, 20), the timeout and the moderation screen are the app's, not a copy. The result is `{ httpStatus, result }`; a refusal keeps the route's status (`402`, `409`, `429`, `503`, `424`, `422`).

Arguments: `outputId`, `platforms`, optional `tone`, `language`, `about`, `hashtagSource` (`web` or omitted), `projectId`.

## Reading the video

Not in the tool. The assistant has no price estimate and cannot start a transcript, so the tool is text only (it always sends `readVideo=false`, and the argument is not offered). The app's Write post text dialog has "also read the video", priced first, with the 202 `preparing` flow.

## Data handling

Plain tool request: the project text goes to the workspace's chat provider. `hashtagSource=web`: the search query built from the project name and the about text goes to the web search provider (the app dialog's `readVideo` path, which also sends frames and audio, is described by the route's `disclosure`); search result text is never given to a model, tags are pulled out by code and the source pages are cited. The route's `disclosure` field states exactly this.

## Claims

Hashtags are `ModelSuggested` or `web search`, never measured. No text, tool description or prompt may call them "trending".

## Registry

The tool is in `AssistantPlatformTools.Tools` (Billed, Chat), `AssistantToolAnnotations` (non-read-only and ungated because it spends credits, open world), `AssistantToolProgress` captions and the assistant prompt (`AssistantAgent` and `DatabaseSeeder`, kept identical). The chat set is 117 tools and the MCP catalog publishes 79.
