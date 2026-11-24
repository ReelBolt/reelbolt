namespace ReelBolt.Inference.Api.Controllers.Dto;

public sealed record PronunciationEntryRequest(string? Term, string? Spelling, bool? CaseSensitive = null);

public sealed record PronunciationEntryResponse(Guid Id, string Term, string Spelling, bool CaseSensitive, DateTime UpdatedAt);

public sealed record PronunciationListResponse(IReadOnlyList<PronunciationEntryResponse> Entries, int Max);
