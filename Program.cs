using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClaudeSessionSearch;
using ClaudeSessionSearch.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var dbPath = builder.Configuration["DB_PATH"] ?? "/db/index.db";
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
builder.Services.AddDbContextFactory<SessionSearchDb>(o => o.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddSingleton<SessionIndexer>();

var app = builder.Build();

var indexer = app.Services.GetRequiredService<SessionIndexer>();
indexer.EnsureSchema();

// kick off an initial index pass in the background so the app is responsive immediately
_ = indexer.ReindexAsync();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/status", () => Results.Ok(new
{
    indexer.Status.Indexing,
    indexer.Status.LastCompletedUtc,
    indexer.Status.TranscriptCount,
    indexer.Status.MemoryDocCount,
    indexer.Status.LastError
}));

app.MapPost("/api/reindex", async () =>
{
    _ = indexer.ReindexAsync();
    await Task.Delay(50); // let Indexing flip to true before we respond
    return Results.Ok(new { started = true });
});

app.MapGet("/api/list", (string? type, int? limit, IDbContextFactory<SessionSearchDb> dbf) =>
{
    var take = Math.Clamp(limit ?? 200, 1, 500);
    using var db = dbf.CreateDbContext();

    var docs = db.Documents.AsNoTracking();
    if (type is "transcript" or "memory") docs = docs.Where(d => d.DocType == type);

    var rows = docs
        .OrderBy(d => d.LastActive == "")
        .ThenByDescending(d => d.LastActive)
        .Take(take)
        .Select(d => new { d.DocType, d.DocId, d.Title, d.Ticket, d.Date, d.Subtitle, d.Prs, d.Flags, d.LastActive })
        .ToList();

    var summaries = LoadSummaries(db);
    return Results.Ok(rows.Select(r => new SearchHit(
        DocType: r.DocType, DocId: r.DocId, Title: r.Title, Ticket: r.Ticket, Date: r.Date, Snippet: "",
        Subtitle: r.Subtitle, Prs: SplitPrs(r.Prs), Flags: r.Flags, LastActive: r.LastActive,
        Summary: summaries.GetValueOrDefault(r.DocId))));
});

app.MapGet("/api/document", (string type, string id, IDbContextFactory<SessionSearchDb> dbf) =>
{
    if (type is not ("transcript" or "memory")) return Results.BadRequest();

    using var db = dbf.CreateDbContext();
    var doc = db.Documents.AsNoTracking()
        .Where(d => d.DocType == type && d.DocId == id)
        .Select(d => new { d.Title, d.Ticket, d.Date, d.Content })
        .FirstOrDefault();
    if (doc is null) return Results.NotFound();

    const int maxChars = 40_000;
    var truncated = doc.Content.Length > maxChars;
    return Results.Ok(new
    {
        title = doc.Title,
        ticket = doc.Ticket,
        date = doc.Date,
        content = truncated ? doc.Content[..maxChars] : doc.Content,
        truncated
    });
});

// Admin: rename / pin / archive. Edits session_names.json in place (it's bind-mounted as a single file,
// so it must be rewritten through the same inode, which File.WriteAllText does), then reindexes.
app.MapPost("/api/admin/session", async (AdminUpdate u) =>
{
    if (string.IsNullOrWhiteSpace(u.Id) || u.Id.Length < 8) return Results.BadRequest("id required");
    var path = indexer.NamesPath;
    var root = File.Exists(path)
        ? JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject()
        : new JsonObject();

    // match an existing key/entry by prefix either way so short and full ids don't duplicate
    static bool Same(string a, string b) => a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);

    if (u.Name is not null)
    {
        var sessions = root["sessions"]?.AsObject() ?? new JsonObject();
        foreach (var k in sessions.Select(kv => kv.Key).Where(k => Same(k, u.Id)).ToList()) sessions.Remove(k);
        if (u.Name.Trim().Length > 0) sessions[u.Id] = u.Name.Trim();
        root["sessions"] = sessions;
    }
    void SetFlag(string key, bool? on)
    {
        if (on is null) return;
        var arr = root[key]?.AsArray() ?? new JsonArray();
        foreach (var n in arr.Where(n => Same(n!.GetValue<string>(), u.Id)).ToList()) arr.Remove(n);
        if (on == true) arr.Add(u.Id);
        root[key] = arr;
    }
    // tickets/note are per-session maps; an empty value clears the entry
    void SetMapEntry(string key, JsonNode? value, bool clear)
    {
        var map = root[key]?.AsObject() ?? new JsonObject();
        foreach (var k in map.Select(kv => kv.Key).Where(k => Same(k, u.Id)).ToList()) map.Remove(k);
        if (!clear) map[u.Id] = value;
        root[key] = map;
    }
    if (u.Tickets is not null)
    {
        var clean = u.Tickets.Select(t => t.Trim().ToUpperInvariant()).Where(t => t.Length > 0).Distinct().ToArray();
        SetMapEntry("tickets", new JsonArray(clean.Select(t => (JsonNode)t!).ToArray()), clean.Length == 0);
    }
    if (u.Note is not null) SetMapEntry("notes", u.Note.Trim(), u.Note.Trim().Length == 0);
    SetFlag("pinned", u.Pinned);
    SetFlag("archived", u.Archived);

    await File.WriteAllTextAsync(path, root.ToJsonString(new System.Text.Json.JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    }));
    await indexer.ReindexAsync();
    return Results.Ok(new { saved = true });
});

app.MapGet("/api/session", (string id, IDbContextFactory<SessionSearchDb> dbf) =>
{
    using var db = dbf.CreateDbContext();
    var json = db.SessionDetails.AsNoTracking().Where(d => d.Id == id).Select(d => d.Details).FirstOrDefault();
    return json is not null ? Results.Content(json, "application/json") : Results.NotFound();
});

app.MapGet("/api/memory-file", (string name, IDbContextFactory<SessionSearchDb> dbf) =>
{
    using var db = dbf.CreateDbContext();
    var md = db.Documents.AsNoTracking()
        .Where(d => d.DocType == "memory" && d.DocId == name)
        .Select(d => d.Content)
        .FirstOrDefault();
    return md is not null ? Results.Text(md, "text/plain") : Results.NotFound();
});

app.MapGet("/api/search", (string? q, string? type, int? limit, IDbContextFactory<SessionSearchDb> dbf) =>
{
    if (string.IsNullOrWhiteSpace(q))
        return Results.Ok(Array.Empty<SearchHit>());

    var matchQuery = BuildMatchQuery(q);
    if (matchQuery.Length == 0)
        return Results.Ok(Array.Empty<SearchHit>());

    var take = Math.Clamp(limit ?? 40, 1, 200);
    string? typeFilter = type is "transcript" or "memory" ? type : null;

    using var db = dbf.CreateDbContext();
    // MATCH / snippet() / bm25() are FTS5-only with no LINQ translation, so this one stays SQL.
    // Interpolated values become parameters; aliases line up with SearchRow's properties.
    var rows = db.Database.SqlQuery<SearchRow>($"""
        SELECT doc_type AS DocType, doc_id AS DocId, title AS Title, ticket AS Ticket, date AS Date,
               snippet(documents, 5, '[[', ']]', '…', 12) AS Snip,
               subtitle AS Subtitle, prs AS Prs, flags AS Flags, last_active AS LastActive
        FROM documents
        WHERE documents MATCH {matchQuery} AND ({typeFilter} IS NULL OR doc_type = {typeFilter})
        ORDER BY bm25(documents)
        LIMIT {take}
        """).ToList();

    var summaries = LoadSummaries(db);
    return Results.Ok(rows.Select(r => new SearchHit(
        DocType: r.DocType, DocId: r.DocId, Title: r.Title, Ticket: r.Ticket, Date: r.Date, Snippet: r.Snip,
        Subtitle: r.Subtitle, Prs: SplitPrs(r.Prs), Flags: r.Flags, LastActive: r.LastActive,
        Summary: summaries.GetValueOrDefault(r.DocId))));
});

app.Run();

static Dictionary<string, JsonNode?> LoadSummaries(SessionSearchDb db) =>
    db.SessionDetails.AsNoTracking()
        .Select(d => new { d.Id, d.Summary })
        .AsEnumerable()
        .ToDictionary(d => d.Id, d => JsonNode.Parse(d.Summary));

static string[] SplitPrs(string? prs) =>
    string.IsNullOrEmpty(prs) ? [] : prs.Split(' ', StringSplitOptions.RemoveEmptyEntries);

// Turns free-typed user input into a safe, forgiving FTS5 MATCH expression:
// tokenizes on word characters, quotes each token (so stray punctuation never
// breaks FTS5 query syntax), appends a prefix wildcard, ANDs them implicitly.
static string BuildMatchQuery(string raw)
{
    var tokens = Regex.Matches(raw, @"[\w]+")
        .Select(m => m.Value)
        .Where(t => t.Length > 0)
        .Select(t => $"\"{t}\"*")
        .ToArray();
    return string.Join(' ', tokens);
}
