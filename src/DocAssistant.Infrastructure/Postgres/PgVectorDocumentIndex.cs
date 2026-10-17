using Dapper;
using DocAssistant.Core.Abstractions;
using DocAssistant.Core.Domain;
using Npgsql;
using Pgvector;

namespace DocAssistant.Infrastructure.Postgres;

/// <summary>
/// Hybrid retrieval in one SQL statement: cosine similarity over pgvector and a BM25-style rank over the
/// generated tsvector, fused with reciprocal rank fusion (k = 60). Every query is bounded by owner_id.
/// </summary>
public sealed class PgVectorDocumentIndex(Db db) : IDocumentIndex
{
    public async Task UpsertAsync(IReadOnlyList<(Chunk Chunk, float[] Vector)> items, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return;
        }
        await using var conn = await db.Source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            insert into chunks (id, document_id, owner_id, page, ordinal, text, embedding)
            values (@id, @document_id, @owner_id, @page, @ordinal, @text, @embedding)
            on conflict (id) do update set text = excluded.text, embedding = excluded.embedding
            """, conn, tx);
        var pId = cmd.Parameters.Add("id", NpgsqlTypes.NpgsqlDbType.Uuid);
        var pDoc = cmd.Parameters.Add("document_id", NpgsqlTypes.NpgsqlDbType.Uuid);
        var pOwner = cmd.Parameters.Add("owner_id", NpgsqlTypes.NpgsqlDbType.Text);
        var pPage = cmd.Parameters.Add("page", NpgsqlTypes.NpgsqlDbType.Integer);
        var pOrd = cmd.Parameters.Add("ordinal", NpgsqlTypes.NpgsqlDbType.Integer);
        var pText = cmd.Parameters.Add("text", NpgsqlTypes.NpgsqlDbType.Text);
        var pVec = cmd.Parameters.Add(new NpgsqlParameter("embedding", new Vector(items[0].Vector)));
        await cmd.PrepareAsync(ct);
        foreach (var (chunk, vector) in items)
        {
            pId.Value = chunk.Id;
            pDoc.Value = chunk.DocumentId;
            pOwner.Value = chunk.OwnerId;
            pPage.Value = chunk.Page;
            pOrd.Value = chunk.Ordinal;
            pText.Value = chunk.Text;
            pVec.Value = new Vector(vector);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchRequest r, CancellationToken ct)
    {
        const string sql = """
            with vec as (
                select id, row_number() over (order by embedding <=> @vector) as rank,
                       1 - (embedding <=> @vector) as score
                from chunks
                where owner_id = @ownerId and (@documentId::uuid is null or document_id = @documentId)
                order by embedding <=> @vector
                limit @pool
            ),
            kw as (
                select id, row_number() over (order by ts_rank_cd(tsv, q) desc) as rank,
                       ts_rank_cd(tsv, q) as score
                from chunks, websearch_to_tsquery('english', @query) q
                where owner_id = @ownerId and (@documentId::uuid is null or document_id = @documentId) and tsv @@ q
                order by ts_rank_cd(tsv, q) desc
                limit @pool
            ),
            fused as (
                select coalesce(vec.id, kw.id) as id,
                       coalesce(1.0 / (60 + vec.rank), 0) + coalesce(1.0 / (60 + kw.rank), 0) as rrf,
                       vec.score as vscore, kw.score as kscore
                from vec full outer join kw on vec.id = kw.id
            )
            select c.id, c.document_id, c.owner_id, c.page, c.ordinal, c.text, d.file_name, f.rrf, f.vscore, f.kscore
            from fused f join chunks c on c.id = f.id join documents d on d.id = c.document_id
            order by f.rrf desc
            limit @top
            """;
        await using var conn = await db.Source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter("vector", new Vector(r.QueryVector)));
        cmd.Parameters.AddWithValue("ownerId", r.OwnerId);
        cmd.Parameters.AddWithValue("documentId", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)r.DocumentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("query", string.IsNullOrWhiteSpace(r.Query) ? "document" : r.Query);
        cmd.Parameters.AddWithValue("pool", Math.Max(r.Top * 4, 20));
        cmd.Parameters.AddWithValue("top", r.Top);
        var hits = new List<SearchHit>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var chunk = new Chunk(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetString(5));
            hits.Add(new SearchHit(
                chunk,
                reader.GetString(6),
                Convert.ToDouble(reader.GetValue(7)),
                reader.IsDBNull(8) ? null : Convert.ToDouble(reader.GetValue(8)),
                reader.IsDBNull(9) ? null : Convert.ToDouble(reader.GetValue(9))));
        }
        return hits;
    }

    public async Task DeleteDocumentAsync(Guid documentId, CancellationToken ct)
    {
        await using var conn = await db.Source.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("delete from chunks where document_id = @documentId", new { documentId }, cancellationToken: ct));
    }
}
