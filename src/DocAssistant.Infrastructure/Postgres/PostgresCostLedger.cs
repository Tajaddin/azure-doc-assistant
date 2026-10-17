using Dapper;
using DocAssistant.Core.Abstractions;
using DocAssistant.Core.Domain;

namespace DocAssistant.Infrastructure.Postgres;

public sealed class PostgresCostLedger(Db db) : ICostLedger
{
    public async Task RecordAsync(CostEntry e, CancellationToken ct)
    {
        await using var conn = await db.Source.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            insert into cost_entries (id, owner_id, operation, model, input_tokens, output_tokens, cost_usd, at)
            values (@Id, @OwnerId, @Operation, @Model, @InputTokens, @OutputTokens, @CostUsd, @At)
            """, e, cancellationToken: ct));
    }

    public async Task<CostSummary> SummaryAsync(string ownerId, DateTimeOffset since, CancellationToken ct)
    {
        await using var conn = await db.Source.OpenConnectionAsync(ct);
        var totals = await conn.QuerySingleAsync<(decimal Usd, int Calls, int Input, int Output)>(new CommandDefinition("""
            select coalesce(sum(cost_usd), 0), count(*)::int, coalesce(sum(input_tokens), 0)::int, coalesce(sum(output_tokens), 0)::int
            from cost_entries where owner_id = @ownerId and at >= @since
            """, new { ownerId, since }, cancellationToken: ct));
        var byOp = await conn.QueryAsync<(string Key, decimal Usd, int Calls)>(new CommandDefinition("""
            select operation, sum(cost_usd), count(*)::int from cost_entries
            where owner_id = @ownerId and at >= @since group by operation order by 2 desc
            """, new { ownerId, since }, cancellationToken: ct));
        var byDay = await conn.QueryAsync<(string Key, decimal Usd, int Calls)>(new CommandDefinition("""
            select to_char(date_trunc('day', at at time zone 'utc'), 'YYYY-MM-DD'), sum(cost_usd), count(*)::int from cost_entries
            where owner_id = @ownerId and at >= @since group by 1 order by 1
            """, new { ownerId, since }, cancellationToken: ct));
        return new CostSummary(
            totals.Usd, totals.Calls, totals.Input, totals.Output,
            byOp.Select(b => new CostBucket(b.Key, b.Usd, b.Calls)).ToList(),
            byDay.Select(b => new CostBucket(b.Key, b.Usd, b.Calls)).ToList());
    }
}
