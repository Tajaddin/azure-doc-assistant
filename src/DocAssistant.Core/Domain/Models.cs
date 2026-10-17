namespace DocAssistant.Core.Domain;

public enum DocumentStatus
{
    Uploaded,
    Processing,
    Ready,
    Failed,
}

public enum DocumentKind
{
    Unknown,
    Invoice,
    Contract,
    Manual,
}

/// <summary>A document owned by one user. Every read goes through the owner id; there is no cross-user path.</summary>
public sealed record DocumentRecord(
    Guid Id,
    string OwnerId,
    string FileName,
    string BlobName,
    long SizeBytes,
    DocumentKind Kind,
    DocumentStatus Status,
    int PageCount,
    int ChunkCount,
    DateTimeOffset UploadedAt,
    DateTimeOffset? IndexedAt,
    string? Error);

/// <summary>A page of extracted text. Lines are kept so citations can quote a snippet.</summary>
public sealed record AnalyzedPage(int Number, string Text);

public sealed record AnalyzedDocument(
    IReadOnlyList<AnalyzedPage> Pages,
    string Analyzer,
    IReadOnlyDictionary<string, string>? PrebuiltFields = null)
{
    public int PageCount => Pages.Count;
}

public sealed record Chunk(Guid Id, Guid DocumentId, string OwnerId, int Page, int Ordinal, string Text);

public sealed record SearchHit(Chunk Chunk, string FileName, double Score, double? VectorScore, double? KeywordScore);

public sealed record Citation(int Marker, Guid DocumentId, string FileName, int Page, Guid ChunkId, string Snippet);

/// <summary>Token usage and cost. Estimated is true when the provider returned no usage and tokens were approximated from text length.</summary>
public sealed record UsageInfo(int InputTokens, int OutputTokens, int Calls, decimal CostUsd, bool Estimated = false)
{
    public static readonly UsageInfo Zero = new(0, 0, 0, 0m);

    public UsageInfo Add(UsageInfo other) => new(
        InputTokens + other.InputTokens,
        OutputTokens + other.OutputTokens,
        Calls + other.Calls,
        CostUsd + other.CostUsd,
        Estimated || other.Estimated);
}

public sealed record Answer(
    string Text,
    IReadOnlyList<Citation> Citations,
    IReadOnlyList<SearchHit> Sources,
    IReadOnlyList<string> ToolCalls,
    UsageInfo Usage,
    TimeSpan Elapsed,
    string Model);

public sealed record CostEntry(
    Guid Id,
    string OwnerId,
    string Operation,
    string Model,
    int InputTokens,
    int OutputTokens,
    decimal CostUsd,
    DateTimeOffset At);

public sealed record CostSummary(
    decimal TotalUsd,
    int Calls,
    int InputTokens,
    int OutputTokens,
    IReadOnlyList<CostBucket> ByOperation,
    IReadOnlyList<CostBucket> ByDay);

public sealed record CostBucket(string Key, decimal Usd, int Calls);

public sealed record IngestionMessage(Guid DocumentId, string OwnerId, string BlobName, string FileName);

public sealed record IngestionResult(Guid DocumentId, int Pages, int Chunks, TimeSpan Analyze, TimeSpan Embed, TimeSpan Index, TimeSpan Total)
{
    public double PagesPerMinute => Total.TotalMinutes > 0 ? Pages / Total.TotalMinutes : 0;
}
