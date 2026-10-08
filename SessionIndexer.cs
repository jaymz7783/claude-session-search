using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeSessionSearch.Data;
using Microsoft.EntityFrameworkCore;

namespace ClaudeSessionSearch;

public class SessionIndexer
{
    private readonly string _dataDir;
    private readonly IDbContextFactory<SessionSearchDb> _dbFactory;
    private readonly ILogger<SessionIndexer> _logger;
    public IndexStatus Status { get; } = new();
    public string NamesPath { get; }

    // Matches a reference_sessions.md row: | `uuid` | ticket | topic (may contain stray pipes) | YYYY-MM-DD |
    private static readonly Regex SessionRowRegex = new(
        @"^\|\s*`([0-9a-fA-F-]{36})`\s*\|\s*([^|]*)\|\s*(.*)\|\s*(\d{4}-\d{2}-\d{2})\s*\|\s*$",
        RegexOptions.Compiled);

    private const int MaxContentCharsPerDoc = 6_000_000;
    private const int MaxBlockChars = 4000;

    public SessionIndexer(IConfiguration config, IDbContextFactory<SessionSearchDb> dbFactory, ILogger<SessionIndexer> logger)
    {
        _dataDir = config["DATA_DIR"] ?? "/data";
        _dbFactory = dbFactory;
        NamesPath = config["NAMES_PATH"] ?? Path.Combine(_dataDir, "memory", "session_names.json");
        ReposRoot = (config["REPOS_ROOT"] ?? "").TrimEnd('/') + "/";
        HomeDir = config["HOME_DIR"] ?? "";
        _logger = logger;
    }

    public void EnsureSchema()
    {
        using var db = _dbFactory.CreateDbContext();
        db.RecreateSchema();
    }

    public async Task ReindexAsync()
    {
        if (Status.Indexing) return;
        Status.Indexing = true;
        Status.LastError = null;
        try
        {
            await Task.Run(RunReindex);
            Status.LastCompletedUtc = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reindex failed");
            Status.LastError = ex.Message;
        }
        finally
        {
            Status.Indexing = false;
        }
    }

    private void RunReindex()
    {
        _logger.LogInformation("Starting reindex from {DataDir}", _dataDir);

        var sessionMeta = LoadSessionMeta();
        var (nameOverrides, ticketPrefixes, pinned, archived, ticketOverrides, notes) = LoadSessionNames(sessionMeta);

        using var db = _dbFactory.CreateDbContext();
        // one transaction for clear + rebuild: readers keep seeing the old index until commit
        using var tx = db.Database.BeginTransaction();
        db.Documents.ExecuteDelete();
        db.SessionDetails.ExecuteDelete();

        long nextRowId = 1;
        // save + detach per row: transcripts run to megabytes and the tracker would otherwise hold them all
        void Add(object row)
        {
            db.Add(row);
            db.SaveChanges();
            db.ChangeTracker.Clear();
        }
        DocumentRow Doc(string docType, string docId, string title, string ticket, string date, string content,
                        string subtitle, string prs, string flags, string lastActive) =>
            new()
            {
                RowId = nextRowId++, DocType = docType, DocId = docId, Title = title, Ticket = ticket, Date = date,
                Content = content, Subtitle = subtitle, Prs = prs, Flags = flags, LastActive = lastActive
            };

        int transcriptCount = 0;
        int memoryCount = 0;
        var pendingDetails = new List<(string Id, TranscriptDetails Details)>();
        var memoryDocs = new List<(string Name, string Description, string Content)>();

        {
            // --- transcripts (*.jsonl directly under the mounted projects dir) ---
            foreach (var path in Directory.EnumerateFiles(_dataDir, "*.jsonl", SearchOption.TopDirectoryOnly))
            {
                var sessionId = Path.GetFileNameWithoutExtension(path);
                TranscriptInfo info;
                try
                {
                    info = ExtractTranscript(path, ticketPrefixes);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse transcript {Path}", path);
                    continue;
                }
                var content = info.Content;
                if (string.IsNullOrWhiteSpace(content)) continue;

                sessionMeta.TryGetValue(sessionId, out var meta);
                // name precedence: /rename name > Claude Code's auto title > session-log topic > content
                // keys in session_names.json may be a full uuid or a short prefix (first 8 chars is plenty)
                var nameOverride = nameOverrides.FirstOrDefault(kv => sessionId.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase)).Value;
                var title = nameOverride ?? info.Name ?? info.AiTitle
                    ?? (meta?.Topic is { Length: > 0 } t ? Truncate(t, 140) : Truncate(CollapseWhitespace(content), 160));
                var subtitle = meta?.Topic is { Length: > 0 } topic ? Truncate(CollapseWhitespace(topic), 400) : "";
                var ticketOverride = ticketOverrides.FirstOrDefault(kv => sessionId.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase)).Value;
                var tickets = ticketOverride is not null ? string.Join(", ", ticketOverride) : MergeTickets(meta?.Ticket, info.Tickets);
                var note = notes.FirstOrDefault(kv => sessionId.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase)).Value;
                var date = meta?.Date is { Length: > 0 } d ? d : File.GetLastWriteTimeUtc(path).ToString("yyyy-MM-dd");
                var flags = string.Join(' ', new[]
                {
                    pinned.Any(p => sessionId.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ? "pinned" : null,
                    archived.Any(a => sessionId.StartsWith(a, StringComparison.OrdinalIgnoreCase)) ? "archived" : null
                }.Where(f => f != null));
                var lastActive = File.GetLastWriteTimeUtc(path).ToString("yyyy-MM-ddTHH:mm:ssZ");
                Add(Doc("transcript", sessionId, title, tickets, date, content, subtitle, string.Join(' ', info.Prs), flags, lastActive));
                pendingDetails.Add((sessionId, info.Details with { Note = note, TicketsCurated = ticketOverride is not null }));
                transcriptCount++;
            }

            // --- memory markdown files (data/memory/*.md, includes reference_sessions.md itself) ---
            var memoryDir = Path.Combine(_dataDir, "memory");
            if (Directory.Exists(memoryDir))
            {
                foreach (var path in Directory.EnumerateFiles(memoryDir, "*.md", SearchOption.TopDirectoryOnly))
                {
                    string content;
                    try
                    {
                        content = File.ReadAllText(path);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to read memory file {Path}", path);
                        continue;
                    }
                    var name = Path.GetFileNameWithoutExtension(path);
                    var title = ExtractFrontmatterDescription(content) ?? name;
                    memoryDocs.Add((name, title, content));
                    var mtime = File.GetLastWriteTimeUtc(path).ToString("yyyy-MM-dd");
                    Add(Doc("memory", name, title, "", mtime, Truncate(content, MaxContentCharsPerDoc), "", "", "", File.GetLastWriteTimeUtc(path).ToString("yyyy-MM-ddTHH:mm:ssZ")));
                    memoryCount++;
                }
            }

            foreach (var (id, details) in pendingDetails)
                Add(BuildDetails(id, details, LinkMemory(id, memoryDocs)));

            tx.Commit();
        }

        Status.TranscriptCount = transcriptCount;
        Status.MemoryDocCount = memoryCount;
        _logger.LogInformation("Reindex complete: {Transcripts} transcripts, {Memory} memory docs", transcriptCount, memoryCount);
    }

    // Memory files that mention a session (by its 8-char prefix), most mentions first. The session log
    // and the index itself mention everything, so they're excluded.
    private static List<MemoryLink> LinkMemory(string sessionId, List<(string Name, string Description, string Content)> memoryDocs)
    {
        var shortId = sessionId[..8];
        return memoryDocs
            .Where(m => m.Name is not ("MEMORY" or "reference_sessions"))
            .Select(m =>
            {
                var hits = Regex.Matches(m.Content, Regex.Escape(shortId)).Count;
                // a file born in this session (originSessionId) is about it, not just passing mention
                var origin = m.Content.Contains($"originSessionId: {sessionId}", StringComparison.OrdinalIgnoreCase);
                // project_ files are the per-workstream notes; reference_/feedback_ ones are mostly cross-cutting
                return (m, hits, rank: hits + (origin ? 2 : 0) + (m.Name.StartsWith("project_") ? 3 : 0));
            })
            .Where(x => x.hits > 0)
            .OrderByDescending(x => x.rank)
            .Select((x, i) =>
            {
                var startHere = ExtractStartHere(x.m.Content);
                // only trust a START HERE that belongs to this session: its project file, or one that names it
                // (a note naming many sessions is an index/to-do list about them, not their own next step)
                var namesFew = startHere != null && SessionIdRegex.Matches(startHere).Select(mm => mm.Value[..8].ToLowerInvariant()).Distinct().Count() <= 2;
                var owned = startHere != null && ((i == 0 && x.m.Name.StartsWith("project_"))
                                                  || (namesFew && startHere.Contains(shortId, StringComparison.OrdinalIgnoreCase)));
                return new MemoryLink(x.m.Name, x.m.Description, x.hits, startHere, owned);
            })
            .ToList();
    }

    // The "start here" block: from the line that says it to the next markdown heading (or blank-line gap
    // after content), so a "## Next session: START HERE" heading brings its list with it.
    private static string? ExtractStartHere(string content)
    {
        var lines = content.Split('\n');
        // files accumulate dated START HERE notes; the last one is the current one
        var startIdx = Array.FindLastIndex(lines, l => l.Contains("start here", StringComparison.OrdinalIgnoreCase)
                                                   && !l.TrimStart().StartsWith("description:"));
        if (startIdx < 0) return null;
        var block = new List<string> { lines[startIdx] };
        var sawBody = !lines[startIdx].TrimStart().StartsWith('#') && lines[startIdx].Length > 40;
        for (var i = startIdx + 1; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.TrimStart().StartsWith('#')) break;
            if (string.IsNullOrWhiteSpace(l) && sawBody && i + 1 < lines.Length && !lines[i + 1].TrimStart().StartsWith(('-')) && !char.IsDigit(lines[i + 1].TrimStart().FirstOrDefault())) break;
            if (!string.IsNullOrWhiteSpace(l)) sawBody = true;
            block.Add(l);
        }
        return Truncate(string.Join('\n', block).Trim(), 2000);
    }

    private static readonly Regex SessionIdRegex = new(@"\b[0-9a-f]{8}(?:-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})?\b", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static SessionDetailsRow BuildDetails(string id, TranscriptDetails d, List<MemoryLink> memory)
    {
        // summary rides along on every list row (cards); details only load when a card is opened
        var summary = new
        {
            d.Repos,
            d.ActiveDays,
            d.FirstActive,
            d.CostUsd,
            d.PromptCount,
            d.Note,
            startHere = memory.Any(m => m.Owned),
            primaryMemory = memory.FirstOrDefault()?.Name
        };
        var details = new { d.Repos, d.ActiveDays, d.FirstActive, d.Files, d.LastPrompts, d.PromptCount, d.CostUsd, d.LinesAdded, d.LinesRemoved, d.Note, d.TicketsCurated, memory };
        return new SessionDetailsRow
        {
            Id = id,
            Summary = JsonSerializer.Serialize(summary, JsonOpts),
            Details = JsonSerializer.Serialize(details, JsonOpts)
        };
    }

    private Dictionary<string, SessionMeta> LoadSessionMeta()
    {
        var result = new Dictionary<string, SessionMeta>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(_dataDir, "memory", "reference_sessions.md");
        if (!File.Exists(path)) return result;

        foreach (var line in File.ReadLines(path))
        {
            var m = SessionRowRegex.Match(line);
            if (!m.Success) continue;
            var id = m.Groups[1].Value;
            var ticket = m.Groups[2].Value.Trim();
            var topic = m.Groups[3].Value.Trim();
            var date = m.Groups[4].Value.Trim();
            result[id] = new SessionMeta(id, ticket, topic, date);
        }
        return result;
    }

    private static string? ExtractFrontmatterDescription(string content)
    {
        // very small YAML-frontmatter scrape, no external dependency needed
        var m = Regex.Match(content, @"^---\s*\n(.*?)\n---", RegexOptions.Singleline);
        if (!m.Success) return null;
        var fm = m.Groups[1].Value;
        var descMatch = Regex.Match(fm, @"^description:\s*(.+)$", RegexOptions.Multiline);
        return descMatch.Success ? descMatch.Groups[1].Value.Trim().Trim('"') : null;
    }

    private record TranscriptInfo(string Content, string? Name, string? AiTitle, List<string> Tickets, List<string> Prs, TranscriptDetails Details);

    public record TranscriptDetails(
        List<RepoCount> Repos, List<string> ActiveDays, string? FirstActive, List<RepoCount> Files,
        List<string> LastPrompts, int PromptCount, double? CostUsd, int? LinesAdded, int? LinesRemoved,
        string? Note = null, bool TicketsCurated = false);
    public record RepoCount(string Name, int Count);
    public record MemoryLink(string Name, string Description, int Mentions, string? StartHere, bool Owned);

    // host paths as recorded inside transcripts (cwd, file_path); set via REPOS_ROOT / HOME_DIR env in compose
    private static string ReposRoot = "";
    private static string HomeDir = "";
    private static readonly TimeZoneInfo LocalTz = FindTz("America/Chicago");
    private static TimeZoneInfo FindTz(string id) { try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { return TimeZoneInfo.Utc; } }

    // Repos/<name>/anything -> <name>; the Repos root itself counts as nothing
    private static string? RepoOf(string? cwd)
    {
        if (cwd is null || ReposRoot == "/" || !cwd.StartsWith(ReposRoot, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = cwd[ReposRoot.Length..];
        var slash = rest.IndexOf('/');
        return slash < 0 ? rest : rest[..slash];
    }

    // Jira-style keys (ABC-123, PROJ-4567). Only scraped from what the user typed, so tool output noise stays out.
    private static readonly Regex TicketRegex = new(@"\b([A-Z][A-Z0-9]{1,9}-\d{1,6})\b", RegexOptions.Compiled);

    // Name overrides + Jira project allowlist from memory/session_names.json. The allowlist keeps pasted
    // error text (SFTP-22, PEP-668…) out; prefixes already used in the session log are always allowed.
    private (Dictionary<string, string> Names, HashSet<string> TicketPrefixes, List<string> Pinned, List<string> Archived, Dictionary<string, string[]> Tickets, Dictionary<string, string> Notes) LoadSessionNames(Dictionary<string, SessionMeta> sessionMeta)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var prefixes = new HashSet<string>(StringComparer.Ordinal) { "KD" };
        var pinned = new List<string>();
        var archived = new List<string>();
        var ticketsMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var meta in sessionMeta.Values)
            foreach (Match m in TicketRegex.Matches(meta.Ticket))
                prefixes.Add(m.Groups[1].Value[..m.Groups[1].Value.IndexOf('-')]);

        var path = NamesPath;
        if (!File.Exists(path)) return (names, prefixes, pinned, archived, ticketsMap, notes);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("sessions", out var sessions))
                foreach (var p in sessions.EnumerateObject())
                    if (p.Value.GetString() is { Length: > 0 } n) names[p.Name] = n;
            if (doc.RootElement.TryGetProperty("ticketPrefixes", out var tp))
                foreach (var p in tp.EnumerateArray())
                    if (p.GetString() is { Length: > 0 } x) prefixes.Add(x);
            // pinned/archived: arrays of session ids (full uuid or prefix)
            if (doc.RootElement.TryGetProperty("pinned", out var pin))
                foreach (var p in pin.EnumerateArray())
                    if (p.GetString() is { Length: > 0 } x) pinned.Add(x);
            // tickets: hand-curated list replaces the scraped one entirely; notes: free text shown on the card
            if (doc.RootElement.TryGetProperty("tickets", out var tk))
                foreach (var p in tk.EnumerateObject())
                    ticketsMap[p.Name] = p.Value.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray();
            if (doc.RootElement.TryGetProperty("notes", out var nt))
                foreach (var p in nt.EnumerateObject())
                    if (p.Value.GetString() is { Length: > 0 } n) notes[p.Name] = n;
            if (doc.RootElement.TryGetProperty("archived", out var arc))
                foreach (var p in arc.EnumerateArray())
                    if (p.GetString() is { Length: > 0 } x) archived.Add(x);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read {Path}", path);
        }
        return (names, prefixes, pinned, archived, ticketsMap, notes);
    }

    private static TranscriptInfo ExtractTranscript(string path, HashSet<string> ticketPrefixes)
    {
        var sb = new StringBuilder();
        string? name = null, aiTitle = null;
        var ticketCounts = new Dictionary<string, int>();
        var prs = new List<string>();
        var repoCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var fileCounts = new Dictionary<string, int>();
        var days = new SortedSet<string>(StringComparer.Ordinal);
        var lastPrompts = new Queue<string>();
        int promptCount = 0;
        // cost-state is a running total that can restart mid-transcript (e.g. after a resume), so bank the
        // previous segment whenever the counter drops instead of keeping only the last value
        double? cost = null; int? linesAdded = null, linesRemoved = null;
        double costBanked = 0; int addedBanked = 0, removedBanked = 0;
        using var reader = new StreamReader(path);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch
            {
                continue;
            }
            using (doc)
            {
                var root = doc.RootElement;
                var lineType = root.TryGetProperty("type", out var lt) ? lt.GetString() : null;
                if (lineType == "agent-name" && root.TryGetProperty("agentName", out var an)) { name = an.GetString(); continue; }
                if (lineType == "ai-title" && root.TryGetProperty("aiTitle", out var at)) { aiTitle = at.GetString(); continue; }
                if (lineType == "cost-state")
                {
                    if (root.TryGetProperty("totalCostUSD", out var c) && c.TryGetDouble(out var cv))
                    {
                        if (cost is { } prev && cv < prev - 0.01) costBanked += prev;
                        cost = cv;
                    }
                    if (root.TryGetProperty("totalLinesAdded", out var la) && la.TryGetInt32(out var lav))
                    {
                        if (linesAdded is { } prev && lav < prev) addedBanked += prev;
                        linesAdded = lav;
                    }
                    if (root.TryGetProperty("totalLinesRemoved", out var lr) && lr.TryGetInt32(out var lrv))
                    {
                        if (linesRemoved is { } prev && lrv < prev) removedBanked += prev;
                        linesRemoved = lrv;
                    }
                    continue;
                }
                if (lineType == "pr-link" && root.TryGetProperty("prUrl", out var pu))
                {
                    var url = pu.GetString();
                    if (!string.IsNullOrEmpty(url) && !prs.Contains(url)) prs.Add(url);
                    continue;
                }

                if (!root.TryGetProperty("message", out var message)) continue;
                // isMeta user lines are harness-injected (skill bodies, caveats), not typed by the user
                var typedByUser = lineType == "user" && !(root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True);
                if (lineType == "user" && root.TryGetProperty("timestamp", out var ts) && ts.TryGetDateTimeOffset(out var when))
                    days.Add(TimeZoneInfo.ConvertTime(when, LocalTz).ToString("yyyy-MM-dd"));
                if (lineType is "user" or "assistant" && root.TryGetProperty("cwd", out var cwdProp) && RepoOf(cwdProp.GetString()) is { } repo)
                    repoCounts[repo] = repoCounts.GetValueOrDefault(repo) + 1;
                if (!message.TryGetProperty("content", out var content)) continue;

                if (content.ValueKind == JsonValueKind.String)
                {
                    var text = content.GetString();
                    AppendBounded(sb, text);
                    if (typedByUser)
                    {
                        CountTickets(ticketCounts, ticketPrefixes, text);
                        RememberPrompt(lastPrompts, text, ref promptCount);
                    }
                }
                else if (content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in content.EnumerateArray())
                    {
                        if (block.ValueKind != JsonValueKind.Object) continue;
                        if (!block.TryGetProperty("type", out var typeProp)) continue;
                        var blockType = typeProp.GetString();

                        if (blockType == "text" && block.TryGetProperty("text", out var textProp))
                        {
                            var text = textProp.GetString();
                            AppendBounded(sb, text);
                            if (typedByUser)
                            {
                                CountTickets(ticketCounts, ticketPrefixes, text);
                                RememberPrompt(lastPrompts, text, ref promptCount);
                            }
                        }
                        else if (blockType == "tool_use"
                                 && block.TryGetProperty("name", out var toolName) && toolName.GetString() is "Edit" or "Write" or "NotebookEdit" or "MultiEdit"
                                 && block.TryGetProperty("input", out var input) && input.TryGetProperty("file_path", out var fp)
                                 && fp.GetString() is { Length: > 0 } file)
                        {
                            var shown = ReposRoot != "/" && file.StartsWith(ReposRoot, StringComparison.OrdinalIgnoreCase) ? file[ReposRoot.Length..]
                                : HomeDir.Length > 0 ? file.Replace(HomeDir, "~") : file;
                            fileCounts[shown] = fileCounts.GetValueOrDefault(shown) + 1;
                        }
                        else if (blockType == "tool_result" && block.TryGetProperty("content", out var toolContent))
                        {
                            AppendToolResultText(sb, toolContent);
                        }
                    }
                }
            }

            // keep scanning past the content cap: names/titles/PRs often land late in long sessions
            if (sb.Length > MaxContentCharsPerDoc) sb.Length = MaxContentCharsPerDoc;
        }

        var tickets = ticketCounts.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).Take(8).ToList();
        static List<RepoCount> Top(Dictionary<string, int> counts, int n) =>
            counts.OrderByDescending(kv => kv.Value).Take(n).Select(kv => new RepoCount(kv.Key, kv.Value)).ToList();
        var details = new TranscriptDetails(
            Top(repoCounts, 6), days.ToList(), days.Count > 0 ? days.Min : null, Top(fileCounts, 20),
            lastPrompts.Reverse().ToList(), promptCount,
            cost + costBanked, linesAdded + addedBanked, linesRemoved + removedBanked);
        return new TranscriptInfo(sb.ToString(), name, aiTitle, tickets, prs, details);
    }

    // Keeps the last few things the user actually typed (harness wrappers stripped), newest last.
    private static void RememberPrompt(Queue<string> prompts, string? text, ref int count)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var clean = Regex.Replace(text, @"<(system-reminder|command-[a-z]+|local-command-[a-z]+)>.*?</\1>", "", RegexOptions.Singleline).Trim();
        if (clean.Length == 0 || clean.StartsWith("[Request interrupted")) return;
        count++;
        prompts.Enqueue(Truncate(CollapseWhitespace(clean), 400));
        while (prompts.Count > 6) prompts.Dequeue();
    }

    private static void CountTickets(Dictionary<string, int> counts, HashSet<string> prefixes, string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        // skip harness-injected text (system reminders, command wrappers) that rides in user messages
        text = Regex.Replace(text, @"<(system-reminder|command-[a-z]+|local-command-[a-z]+)>.*?</\1>", "", RegexOptions.Singleline);
        foreach (Match m in TicketRegex.Matches(text))
        {
            var key = m.Groups[1].Value;
            if (!prefixes.Contains(key[..key.IndexOf('-')])) continue;
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
    }

    // Session-log tickets come first (curated), then anything else mentioned in the session.
    private static string MergeTickets(string? logTickets, List<string> scraped)
    {
        var merged = new List<string>();
        foreach (var t in (logTickets ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!merged.Contains(t)) merged.Add(t);
        foreach (var t in scraped)
            if (!merged.Contains(t)) merged.Add(t);
        return string.Join(", ", merged);
    }

    private static void AppendToolResultText(StringBuilder sb, JsonElement toolContent)
    {
        if (toolContent.ValueKind == JsonValueKind.String)
        {
            AppendBounded(sb, toolContent.GetString());
        }
        else if (toolContent.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in toolContent.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("text", out var t))
                {
                    AppendBounded(sb, t.GetString());
                }
            }
        }
    }

    private static void AppendBounded(StringBuilder sb, string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        sb.Append(text.Length > MaxBlockChars ? text[..MaxBlockChars] : text);
        sb.Append('\n');
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static string CollapseWhitespace(string s) => Regex.Replace(s, @"\s+", " ").Trim();
}
