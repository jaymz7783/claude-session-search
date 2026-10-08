using Microsoft.EntityFrameworkCore;

namespace ClaudeSessionSearch.Data;

public class SessionSearchDb(DbContextOptions<SessionSearchDb> options) : DbContext(options)
{
    public DbSet<DocumentRow> Documents => Set<DocumentRow>();
    public DbSet<SessionDetailsRow> SessionDetails => Set<SessionDetailsRow>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<DocumentRow>(e =>
        {
            e.ToTable("documents");
            // FTS5 tables only have the implicit rowid as a key. SQLite rejects RETURNING on virtual
            // tables, so EF can't read back a generated key: the indexer assigns rowids itself.
            e.HasKey(d => d.RowId);
            e.Property(d => d.RowId).HasColumnName("rowid").ValueGeneratedNever();
            e.Property(d => d.DocType).HasColumnName("doc_type");
            e.Property(d => d.DocId).HasColumnName("doc_id");
            e.Property(d => d.Title).HasColumnName("title");
            e.Property(d => d.Ticket).HasColumnName("ticket");
            e.Property(d => d.Date).HasColumnName("date");
            e.Property(d => d.Content).HasColumnName("content");
            e.Property(d => d.Subtitle).HasColumnName("subtitle");
            e.Property(d => d.Prs).HasColumnName("prs");
            e.Property(d => d.Flags).HasColumnName("flags");
            e.Property(d => d.LastActive).HasColumnName("last_active");
        });

        b.Entity<SessionDetailsRow>(e =>
        {
            e.ToTable("session_details");
            e.HasKey(d => d.Id);
            e.Property(d => d.Id).HasColumnName("id");
            e.Property(d => d.Summary).HasColumnName("summary");
            e.Property(d => d.Details).HasColumnName("details");
        });
    }

    // The index is a disposable cache rebuilt on every start, so there are no migrations: drop and
    // recreate. Kept as SQL because EF can't model an FTS5 virtual table.
    public void RecreateSchema() => Database.ExecuteSqlRaw("""
        DROP TABLE IF EXISTS documents;
        CREATE VIRTUAL TABLE documents USING fts5(
            doc_type, doc_id, title, ticket, date, content, subtitle, prs UNINDEXED, flags UNINDEXED, last_active UNINDEXED,
            tokenize = 'porter unicode61'
        );
        DROP TABLE IF EXISTS session_details;
        CREATE TABLE session_details (id TEXT PRIMARY KEY, summary TEXT NOT NULL, details TEXT NOT NULL);
        """);
}

public class DocumentRow
{
    public long RowId { get; set; }
    public string DocType { get; set; } = "";
    public string DocId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Ticket { get; set; } = "";
    public string Date { get; set; } = "";
    public string Content { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Prs { get; set; } = "";
    public string Flags { get; set; } = "";
    public string LastActive { get; set; } = "";
}

public class SessionDetailsRow
{
    public string Id { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Details { get; set; } = "";
}

// Shape of a full-text search hit; columns come from the FTS5 query, not a table.
public class SearchRow
{
    public string DocType { get; set; } = "";
    public string DocId { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Ticket { get; set; }
    public string? Date { get; set; }
    public string Snip { get; set; } = "";
    public string? Subtitle { get; set; }
    public string? Prs { get; set; }
    public string? Flags { get; set; }
    public string? LastActive { get; set; }
}
