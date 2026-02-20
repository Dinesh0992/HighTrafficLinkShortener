using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Npgsql;

public class StatsService(NpgsqlDataSource dataSource, IDistributedCache cache)
{
    public async Task<LinkStats?> GetStatsAsync(string code)
    {
        // 1. Try Cache First (Don't hit DB for stats if we just checked 30 seconds ago)
        var cacheKey = $"stats:{code}";
        var cachedData = await cache.GetStringAsync(cacheKey);
        if (cachedData != null) 
            return JsonSerializer.Deserialize<LinkStats>(cachedData);

        // 2. Complex Query: Totals + History
        const string sqlStats = @"
            SELECT 
                COUNT(*) as total,
                COUNT(DISTINCT ip_address) as unique_ips,
                MAX(clicked_at) as last_click
            FROM link_analytics WHERE short_code = @c";

        const string sqlHistory = @"
            SELECT clicked_at::date as day, COUNT(*) as count
            FROM link_analytics
            WHERE short_code = @c AND clicked_at > NOW() - INTERVAL '7 days'
            GROUP BY day ORDER BY day DESC";

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var batch = new NpgsqlBatch(connection)
        {
            BatchCommands = {
                new NpgsqlBatchCommand(sqlStats) { Parameters = { new("@c", code) } },
                new NpgsqlBatchCommand(sqlHistory) { Parameters = { new("@c", code) } }
            }
        };

        await using var reader = await batch.ExecuteReaderAsync();

        if (!await reader.ReadAsync()) return null;

        var total = reader.GetInt64(0);
        var unique = reader.GetInt64(1);
        var last = reader.IsDBNull(2) ? null : (DateTime?)reader.GetDateTime(2);

        await reader.NextResultAsync();
        var history = new List<DailyClickCount>();
        while (await reader.ReadAsync())
        {
            history.Add(new DailyClickCount(reader.GetDateTime(0), reader.GetInt64(1)));
        }

        var stats = new LinkStats(code, total, unique, last, history);

        // 3. Cache the result for 30 seconds
        await cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(stats), new DistributedCacheEntryOptions {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30)
        });

        return stats;
    }
}