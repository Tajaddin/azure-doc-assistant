using System.Reflection;
using Npgsql;

namespace DocAssistant.Infrastructure.Postgres;

public sealed class Db : IAsyncDisposable
{
    public NpgsqlDataSource Source { get; }
    public int EmbeddingDimensions { get; }

    public Db(string connectionString, int embeddingDimensions)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseVector();
        Source = builder.Build();
        EmbeddingDimensions = embeddingDimensions;
    }

    /// <summary>Creates the schema. Refuses to start when the existing chunks table has another vector dimension.</summary>
    public async Task MigrateAsync(CancellationToken ct)
    {
        var sql = ReadSchema().Replace("__DIM__", EmbeddingDimensions.ToString());
        await using var conn = await Source.OpenConnectionAsync(ct);
        await using (var cmd = new NpgsqlCommand(sql, conn))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using var check = new NpgsqlCommand(
            "select atttypmod from pg_attribute where attrelid = 'chunks'::regclass and attname = 'embedding'", conn);
        var typmod = (int)(await check.ExecuteScalarAsync(ct) ?? -1);
        if (typmod != EmbeddingDimensions)
        {
            throw new InvalidOperationException(
                $"chunks.embedding has dimension {typmod} but the configured embedder produces {EmbeddingDimensions}; " +
                "drop the chunks table or switch the embedder");
        }
        await conn.ReloadTypesAsync(ct);
    }

    private static string ReadSchema()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("schema.sql", StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public ValueTask DisposeAsync() => Source.DisposeAsync();
}
