namespace ClaudeSessionSearch;

public record SessionMeta(string Id, string Ticket, string Topic, string Date);

public record SearchHit(
    string DocType,
    string DocId,
    string Title,
    string? Ticket,
    string? Date,
    string Snippet,
    string? Subtitle = null,
    string[]? Prs = null,
    string? Flags = null,
    string? LastActive = null,
    System.Text.Json.Nodes.JsonNode? Summary = null
);

public class IndexStatus
{
    public bool Indexing { get; set; }
    public DateTimeOffset? LastCompletedUtc { get; set; }
    public int TranscriptCount { get; set; }
    public int MemoryDocCount { get; set; }
    public string? LastError { get; set; }
}

public record AdminUpdate(string Id, string? Name, bool? Pinned, bool? Archived, string[]? Tickets, string? Note);
