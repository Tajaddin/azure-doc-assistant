using System.Text.Json;
using Dapper;
using DocAssistant.Core.Abstractions;
using DocAssistant.Core.Domain;

namespace DocAssistant.Infrastructure.Postgres;

public sealed class PostgresDocumentRepository(Db db) : IDocumentRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task CreateAsync(DocumentRecord d, CancellationToken ct)
    {
        await using var conn = await db.Source.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            insert into documents (id, owner_id, file_name, blob_name, size_bytes, kind, status, uploaded_at)
            values (@Id, @OwnerId, @FileName, @BlobName, @SizeBytes, @Kind, @Status, @UploadedAt)
            """,
            new { d.Id, d.OwnerId, d.FileName, d.BlobName, d.SizeBytes, Kind = d.Kind.ToString(), Status = d.Status.ToString(), d.UploadedAt },
            cancellationToken: ct));
    }

    public async Task<DocumentRecord?> GetAsync(string ownerId, Guid id, CancellationToken ct)
    {
        await using var conn = await db.Source.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            "select * from documents where owner_id = @ownerId and id = @id", new { ownerId, id }, cancellationToken: ct));
        return row?.ToRecord();
    }

    public async Task<IReadOnlyList<DocumentRecord>> ListAsync(string ownerId, CancellationToken ct)
    {
        await using var conn = await db.Source.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(
            "select * from documents where owner_id = @ownerId order by uploaded_at desc limit 500", new { ownerId }, cancellationToken: ct));
        return rows.Select(r => r.ToRecord()).ToList();
    }

    public async Task SetStatusAsync(Guid id, DocumentStatus status, string? error, CancellationToken ct)
    {
        await using var conn = await db.Source.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "update documents set status = @status, error = @error where id = @id",
            new { id, status = status.ToString(), error }, cancellationToken: ct));
    }

    public async Task SetIndexedAsync(Guid id, DocumentKind kind, int pageCount, int chunkCount, CancellationToken ct)
    {
        await using var conn = await db.Source.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            update documents set status = 'Ready', kind = @kind, page_count = @pageCount, chunk_count = @chunkCount,
                                 indexed_at = now(), error = null where id = @id
            """, new { id, kind = kind.ToString(), pageCount, chunkCount }, cancellationToken: ct));
    }

    public async Task SavePagesAsync(Guid id, IReadOnlyList<AnalyzedPage> pages, CancellationToken ct)
    {
        await using var conn = await db.Source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("delete from pages where document_id = @id", new { id }, tx, cancellationToken: ct));
        foreach (var p in pages)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "insert into pages (document_id, number, text) values (@id, @number, @text)",
                new { id, number = p.Number, text = p.Text }, tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<AnalyzedPage>> GetPagesAsync(string ownerId, Guid id, CancellationToken ct)
    {
        await using var conn = await db.Source.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(int Number, string Text)>(new CommandDefinition("""
            select p.number, p.text from pages p join documents d on d.id = p.document_id
            where d.owner_id = @ownerId and d.id = @id order by p.number
            """, new { ownerId, id }, cancellationToken: ct));
        return rows.Select(r => new AnalyzedPage(r.Number, r.Text)).ToList();
    }

    public async Task SaveInvoiceFieldsAsync(Guid id, InvoiceFields fields, string method, CancellationToken ct)
    {
        await using var conn = await db.Source.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            insert into invoice_fields (document_id, fields, method) values (@id, @fields::jsonb, @method)
            on conflict (document_id) do update set fields = excluded.fields, method = excluded.method, extracted_at = now()
            """, new { id, fields = JsonSerializer.Serialize(fields, Json), method }, cancellationToken: ct));
    }

    public async Task<(InvoiceFields Fields, string Method)?> GetInvoiceFieldsAsync(string ownerId, Guid id, CancellationToken ct)
    {
        await using var conn = await db.Source.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<(string Fields, string Method)?>(new CommandDefinition("""
            select f.fields::text, f.method from invoice_fields f join documents d on d.id = f.document_id
            where d.owner_id = @ownerId and d.id = @id
            """, new { ownerId, id }, cancellationToken: ct));
        if (row is null)
        {
            return null;
        }
        var fields = JsonSerializer.Deserialize<InvoiceFields>(row.Value.Fields, Json) ?? InvoiceFields.Empty;
        return (fields, row.Value.Method);
    }

    public async Task<bool> DeleteAsync(string ownerId, Guid id, CancellationToken ct)
    {
        await using var conn = await db.Source.OpenConnectionAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(
            "delete from documents where owner_id = @ownerId and id = @id", new { ownerId, id }, cancellationToken: ct));
        return n > 0;
    }

    private sealed class Row
    {
        public Guid Id { get; set; }
        public string Owner_Id { get; set; } = "";
        public string File_Name { get; set; } = "";
        public string Blob_Name { get; set; } = "";
        public long Size_Bytes { get; set; }
        public string Kind { get; set; } = "Unknown";
        public string Status { get; set; } = "Uploaded";
        public int Page_Count { get; set; }
        public int Chunk_Count { get; set; }
        public DateTime Uploaded_At { get; set; }
        public DateTime? Indexed_At { get; set; }
        public string? Error { get; set; }

        public DocumentRecord ToRecord() => new(
            Id, Owner_Id, File_Name, Blob_Name, Size_Bytes,
            Enum.Parse<DocumentKind>(Kind), Enum.Parse<DocumentStatus>(Status),
            Page_Count, Chunk_Count,
            new DateTimeOffset(DateTime.SpecifyKind(Uploaded_At, DateTimeKind.Utc)),
            Indexed_At is null ? null : new DateTimeOffset(DateTime.SpecifyKind(Indexed_At.Value, DateTimeKind.Utc)),
            Error);
    }
}
