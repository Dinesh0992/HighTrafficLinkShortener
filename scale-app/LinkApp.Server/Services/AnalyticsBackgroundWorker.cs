using System.Threading.Channels;

public class AnalyticsBackgroundWorker : BackgroundService
{
    private readonly Channel<ClickData> _channel;
    private readonly IServiceProvider _serviceProvider;

    public AnalyticsBackgroundWorker(Channel<ClickData> channel, IServiceProvider serviceProvider)
    {
        _channel = channel;
        _serviceProvider = serviceProvider;
    }

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
}