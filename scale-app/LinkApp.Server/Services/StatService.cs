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
        // 1. Try Cache First (Crucial for 1M user scale to prevent DB hammering)
        var cacheKey = $"stats:{code}";
        var cachedData = await cache.GetStringAsync(cacheKey);
        if (cachedData != null)
            return JsonSerializer.Deserialize<LinkStats>(cachedData);

        // 2. Postgres: Just check if the link actually exists
        // We don't query 'link_analytics' in Postgres anymore because we moved to ClickHouse
        const string sqlCheck = "SELECT 1 FROM urls WHERE short_code = @c";
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var checkCmd = new NpgsqlCommand(sqlCheck, connection);
        checkCmd.Parameters.AddWithValue("c", code);
        var exists = await checkCmd.ExecuteScalarAsync();

        if (exists == null) return null;

        // 3. ClickHouse: Get BOTH Totals and 7-day History
        // This is much faster for 1 million+ records
        var history = await GetHistoryFromClickHouse(code);

        // Calculate totals from the history or a separate aggregated query if needed
        // For this simple case, we'll fetch the aggregated totals directly from ClickHouse
        var (total, unique, last) = await GetTotalsFromClickHouse(code);

        var stats = new LinkStats(code, total, unique, last, history);

        // 4. Cache the result for 30 seconds
        await cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(stats), new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30)
        });

        return stats;
    }

    private async Task<(long total, long unique, DateTime? last)> GetTotalsFromClickHouse(string code)
    {
        const string chSql = @"
            SELECT 
                count() as total, 
                uniq(ip_address) as unique_ips, 
                max(clicked_at) as last_click 
            FROM analytics_db.link_analytics_log 
            WHERE short_code = {c:String}";

        try
        {
            using var conn = new ClickHouseConnection(_chConnectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = chSql;
            cmd.Parameters.Add(new ClickHouseDbParameter { ParameterName = "c", Value = code });

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return (
                    Convert.ToInt64(reader.GetValue(0)), // total (UInt64 -> long)
                    Convert.ToInt64(reader.GetValue(1)), // unique (UInt64 -> long)
                    reader.IsDBNull(2) ? null : (DateTime?)reader.GetDateTime(2)
                );
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ClickHouse Totals Error] {ex.Message}");
        }
        return (0, 0, null);
    }

    private async Task<List<DailyClickCount>> GetHistoryFromClickHouse(string code)
    {
        var history = new List<DailyClickCount>();
        const string chSql = @"
            SELECT 
                toDate(clicked_at) AS day, 
                count() AS count 
            FROM analytics_db.link_analytics_log 
            WHERE short_code = {c:String} 
              AND clicked_at >= now() - INTERVAL 7 DAY 
            GROUP BY day 
            ORDER BY day DESC";

        try
        {
            using var conn = new ClickHouseConnection(_chConnectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = chSql;
            cmd.Parameters.Add(new ClickHouseDbParameter { ParameterName = "c", Value = code });

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                history.Add(new DailyClickCount(
                    DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                    Convert.ToInt64(reader.GetValue(1)) // count (UInt64 -> long)
                ));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ClickHouse History Error] {ex.Message}");
        }
        return history;
    }
}