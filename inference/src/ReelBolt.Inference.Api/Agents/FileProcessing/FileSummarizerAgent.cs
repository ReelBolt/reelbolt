using Microsoft.Extensions.AI;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Inference.Api.Agents.FileProcessing;

/// <summary>
/// Single-turn agent that produces concise summaries of uploaded files.
/// </summary>
public class FileSummarizerAgentImpl : ReelBoltAgentBase
{
    private const string DefaultPrompt =
        """
        You are a file analysis expert. Given a file's text content, produce a concise
        summary (max 200 words) of what the file contains and its relevance to video
        generation. Focus on:

        - What the file is (source code, config, documentation, asset, etc.)
        - Key information it contains
        - How it could be useful for generating a promotional video
        - Any notable patterns, components, or features described

        Output ONLY JSON matching FileSummaryOutput.
        """;

    public FileSummarizerAgentImpl(IAgentChatClientProvider chatClients, IConfiguration configuration)
        : base(
            chatClients,
            configuration,
            "FileSummarizer",
            "Produces concise summaries of uploaded files.",
            AgentType.FileSummarizerAgent,
            DefaultPrompt,
            outputSchemaType: typeof(FileSummaryOutput))
    {
    }
}
