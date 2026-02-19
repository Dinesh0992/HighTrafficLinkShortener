using System.Threading.Channels;
using Npgsql;

public class AnalyticsBackgroundWorker : BackgroundService
{
    private readonly Channel<ClickData> _channel;
    private readonly IServiceProvider _serviceProvider;

    public AnalyticsBackgroundWorker(Channel<ClickData> channel, IServiceProvider serviceProvider)
    {
        _channel = channel;
        _serviceProvider = serviceProvider;
    }

    /**     * This background worker continuously reads click data from the channel and saves it to the database.
         * By offloading this work to a background service, we avoid adding latency to the redirect endpoint.
         One CLICK = ONE WRITE to Postgres, but it happens asynchronously after the redirect response is sent back to the user.
        
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Read from the channel until the app shuts down
            await foreach (var click in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                using var scope = _serviceProvider.CreateScope();
                var dataSource = scope.ServiceProvider.GetRequiredService<Npgsql.NpgsqlDataSource>();

                try
                {
                    await using var cmd = dataSource.CreateCommand(
                        "INSERT INTO link_analytics (short_code, ip_address, user_agent) VALUES (@c, @ip, @ua)");
                    cmd.Parameters.AddWithValue("c", click.ShortCode);
                    cmd.Parameters.AddWithValue("ip", click.IpAddress ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("ua", click.UserAgent ?? (object)DBNull.Value);
                    
                    await cmd.ExecuteNonQueryAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error saving analytics: {ex.Message}");
                }
            }
        }
         */

    // OPTIMIZATION: Instead of writing each click individually, we can batch them together for better performance.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<ClickData>();
        var lastFlush = DateTime.UtcNow;
        Console.WriteLine("[Analytics] Batch worker started. Batch size: 100, Flush interval: 5 seconds");

        while (!stoppingToken.IsCancellationRequested)
        {
            // 1. Try to read from the channel
            while (_channel.Reader.TryRead(out var click))
            {
                batch.Add(click);
                if (batch.Count >= 100) break; // Stop reading if we hit batch size
            }

            // 2. If we have data AND (Batch is full OR 5 seconds passed)
            if (batch.Count > 0 && (batch.Count >= 100 || (DateTime.UtcNow - lastFlush).TotalSeconds >= 5))
            {
                var batchSize = batch.Count;
                await SaveBatchToDb(batch);
                batch.Clear();
                lastFlush = DateTime.UtcNow;
            }

            // 3. Small pause to prevent CPU spinning if channel is empty
            await Task.Delay(500, stoppingToken);
        }
        
        // 4. On shutdown, flush any remaining clicks
        if (batch.Count > 0)
        {
            Console.WriteLine($"[Analytics] Flushing {batch.Count} remaining clicks on shutdown...");
            await SaveBatchToDb(batch);
        }
        
        Console.WriteLine("[Analytics] Batch worker stopped.");
    }

    private async Task SaveBatchToDb(List<ClickData> clicks)
    {
        try
        {
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
            Console.WriteLine($"[Analytics] Successfully flushed {clicks.Count} clicks to database at {DateTime.UtcNow:O}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Analytics ERROR] Failed to flush {clicks.Count} clicks. Error: {ex.Message}. Exception: {ex}");
        }
    }
}