using MassTransit;
using LinkApp.Server.Events;
using LinkApp.Server.Services;

namespace LinkApp.Server.Consumers;

public class LinkVisitedBatchConsumer : IConsumer<Batch<LinkVisitedEvent>>
{
    private readonly ClickHouseService _chService;
    private readonly ILogger<LinkVisitedBatchConsumer> _logger;

    public LinkVisitedBatchConsumer(ClickHouseService chService, ILogger<LinkVisitedBatchConsumer> logger)
    {
        _chService = chService;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<Batch<LinkVisitedEvent>> context)
    {
        var clicks = context.Message.Select(m => new object[]
        {
            m.Message.ShortCode,
            m.Message.IpAddress ?? "0.0.0.0",
            m.Message.UserAgent ?? "Unknown",
            m.Message.ClickedAt
        }).ToList();

        try
        {
            await _chService.BulkInsertAsync(clicks);
            _logger.LogInformation("Successfully inserted {Count} rows to ClickHouse", clicks.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ClickHouse BulkInsert failed for batch of {Count} rows", clicks.Count);
            throw;
        }
    }
}