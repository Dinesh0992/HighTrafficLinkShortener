using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Npgsql;
using ClickHouse.Client.ADO;
using ClickHouse.Client.ADO.Parameters;

public class StatsService(NpgsqlDataSource dataSource, IDistributedCache cache, ClickHouseConnection chConnection)
{
    private readonly string _chConnectionString = chConnection.ConnectionString;

    public async Task<LinkStats?> GetStatsAsync(string code)
    {
        // 1. Try Cache First
        var cacheKey = $"stats:{code}";
        var cachedData = await cache.GetStringAsync(cacheKey);
        if (cachedData != null)
            return JsonSerializer.Deserialize<LinkStats>(cachedData);

        // 2. Postgres: Get only the Totals (Metadata)
        // We removed sqlHistory from here because ClickHouse handles it better
        const string sqlStats = @"
            SELECT 
                COUNT(*) as total,
                COUNT(DISTINCT ip_address) as unique_ips,
                MAX(clicked_at) as last_click
            FROM link_analytics WHERE short_code = @c";

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sqlStats, connection);
        cmd.Parameters.AddWithValue("c", code);

        await using var reader = await cmd.ExecuteReaderAsync();

        if (!await reader.ReadAsync() || reader.GetInt64(0) == 0) return null;

        var total = reader.GetInt64(0);
        var unique = reader.GetInt64(1);
        var last = reader.IsDBNull(2) ? null : (DateTime?)reader.GetDateTime(2);

        // 3. ClickHouse: Get the 7-day History (Analytics)
        // CALLING THE METHOD HERE:
        var history = await GetHistoryFromClickHouse(code);

        var stats = new LinkStats(code, total, unique, last, history);

        // 4. Cache the result for 30 seconds
        await cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(stats), new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30)
        });

        return stats;
    }

    private async Task<List<DailyClickCount>> GetHistoryFromClickHouse(string code)
    {
        var history = new List<DailyClickCount>();
        const string chSql = @"
        SELECT 
            toDate(clicked_at) AS day, 
            count() AS count 
        FROM link_analytics_log 
        WHERE short_code = {c:String} 
          AND clicked_at >= now() - INTERVAL 7 DAY 
        GROUP BY day 
        ORDER BY day DESC";

        try
        {
            Console.WriteLine($"[ClickHouse] Connecting with: {_chConnectionString}");
            using var conn = new ClickHouseConnection(_chConnectionString);
            await conn.OpenAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = chSql;

            // Use ClickHouseDbParameter for the specific client library requirements
            cmd.Parameters.Add(new ClickHouseDbParameter
            {
                ParameterName = "c",
                Value = code
            });

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                history.Add(new DailyClickCount(
                    DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                    Convert.ToInt64(reader.GetValue(1))
                ));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ClickHouse Stats Error] {ex.Message}");
        }
        return history;
    }
}