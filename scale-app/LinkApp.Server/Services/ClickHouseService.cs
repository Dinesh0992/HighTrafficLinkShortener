using ClickHouse.Client.ADO;
using ClickHouse.Client.Copy;
using LinkApp.Server.Events;

namespace LinkApp.Server.Services;

public class ClickHouseService
{
    private readonly string _connectionString;
    private readonly ILogger<ClickHouseService> _logger;

    public ClickHouseService(IConfiguration config, ILogger<ClickHouseService> logger)
    {
        // Get the string from your existing appsettings.json
        _connectionString = config.GetConnectionString("ClickHouse") 
            ?? "Host=localhost;Protocol=http;Port=8123;Database=analytics_db";
        _logger = logger;
    }

    public async Task BulkInsertAsync(List<object[]> rows)
    {
        try
        {
            using var connection = new ClickHouseConnection(_connectionString);
            await connection.OpenAsync();

            using var bulkCopy = new ClickHouseBulkCopy(connection)
            {
                DestinationTableName = "analytics_db.link_analytics_log",
                BatchSize = 1000
            };
            // Added to fix error : Column names not initialized. Call InitAsync once to load column data from the database.
            await bulkCopy.InitAsync();
            await bulkCopy.WriteToServerAsync(rows);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ClickHouse Bulk Insert Failed");
            throw; // Let the RabbitMQ Consumer handle the retry
        }
    }
}