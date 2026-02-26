using System.Data;
using System.Threading.Channels;
using ClickHouse.Client.ADO;
using ClickHouse.Client.Copy;
using Npgsql;
/** * This is the "Phase 5" Background Worker that reads ClickData from the Channel and saves it to PostgreSQL.
 * In "Phase 7", we will enhance this same worker to also save to ClickHouse.
 IN PHASE 8, we will comment this worker registration in program.cs  and replace it with a RabbitMQ Consumer. */

public class AnalyticsBackgroundWorker : BackgroundService
{
    private readonly Channel<ClickData> _channel;
    private readonly IServiceProvider _serviceProvider;
    private readonly ClickHouseConnection _chConnection; // Add this

    public AnalyticsBackgroundWorker(
        Channel<ClickData> channel,
        IServiceProvider serviceProvider,
        ClickHouseConnection chConnection) // Inject here
    {
        _channel = channel;
        _serviceProvider = serviceProvider;
        _chConnection = chConnection;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<ClickData>();
        var lastFlush = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            while (_channel.Reader.TryRead(out var click))
            {
                batch.Add(click);
                if (batch.Count >= 100) break;
            }

            if (batch.Count > 0 && (batch.Count >= 100 || (DateTime.UtcNow - lastFlush).TotalSeconds >= 5))
            {
                await SaveBatchToBothDbs(batch); // Updated method name
                batch.Clear();
                lastFlush = DateTime.UtcNow;
            }

            await Task.Delay(500, stoppingToken);
        }

        if (batch.Count > 0)
        {
            await SaveBatchToBothDbs(batch);
        }
    }

    private async Task SaveBatchToBothDbs(List<ClickData> clicks)
    {
        // 1. Save to PostgreSQL (Your existing logic)
        await SaveToPostgres(clicks);

        // 2. Save to ClickHouse (The new Phase 7 logic)
        await SaveToClickHouse(clicks);
    }

    private async Task SaveToPostgres(List<ClickData> clicks)
    {
        try
        {
            Console.WriteLine($"[Postgres] Saving batch of {clicks.Count} clicks.");
            using var scope = _serviceProvider.CreateScope();
            var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var batchCmd = connection.CreateBatch();

            foreach (var click in clicks)
            {
                var cmd = batchCmd.CreateBatchCommand();
                cmd.CommandText = "INSERT INTO link_analytics (short_code, ip_address, user_agent) VALUES (@c, @ip, @ua)";
                cmd.Parameters.AddWithValue("c", click.ShortCode);
                cmd.Parameters.AddWithValue("ip", click.IpAddress ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("ua", click.UserAgent ?? (object)DBNull.Value);
                batchCmd.BatchCommands.Add(cmd);
            }
            await batchCmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Postgres ERROR] {ex.Message}");
        }
    }

    private async Task SaveToClickHouse(List<ClickData> clicks)
{
    try
    {
        using var connection = new ClickHouseConnection(_chConnection.ConnectionString);
        await connection.OpenAsync();

        using var bulkCopy = new ClickHouseBulkCopy(connection)
        {
            DestinationTableName = "link_analytics_log",
            BatchSize = 1000
        };

        // 1. FIX: You MUST initialize the column metadata
        await bulkCopy.InitAsync(); 

        // 2. FIX: Match the order and count of your ClickHouse table:
        // Table: (short_code, ip_address, user_agent, clicked_at)
        var rows = clicks.Select(e => new object[] {
            e.ShortCode,               // short_code
            e.IpAddress ?? "0.0.0.0",  // ip_address
            e.UserAgent ?? "Unknown",  // user_agent
            DateTime.UtcNow            // clicked_at
        });

        await bulkCopy.WriteToServerAsync(rows);
        Console.WriteLine($"[ClickHouse] Successfully flushed {clicks.Count} rows.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ClickHouse ERROR] {ex.Message}");
    }
}
}