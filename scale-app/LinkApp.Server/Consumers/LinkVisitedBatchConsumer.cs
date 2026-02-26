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
        // Convert the batch of events into the List<ClickData> your service expects
        // or map them directly to the object array for BulkCopy
        /*
        While ClickData[] feels more "correct" for C# coding, we use List<object[]> because of how the ClickHouse.Client BulkCopy works.
        1. The "Performance" Reason
           The ClickHouseBulkCopy driver is designed to be extremely fast by avoiding "Reflection." 
           If you give it a ClickData object, the driver has to waste CPU cycles looking up which property matches which database column.
           By providing a primitive array (object[]), you are handing the driver a "raw" row.

        2. Avoiding "Tight Coupling"
        If you use ClickData[] in your ClickHouseService, the service becomes "locked" to that one model. 
        By using List<object[]>, the service becomes a Generic Bulk Writer.
        */
        var clicks = context.Message.Select(m => new object[]
        {
            m.Message.ShortCode,
            m.Message.IpAddress ?? "0.0.0.0",
            m.Message.UserAgent ?? "Unknown",
            m.Message.ClickedAt
        }).ToList();

        await _chService.BulkInsertAsync(clicks);

        _logger.LogInformation("Successfully processed batch of {Count} clicks from RabbitMQ", clicks.Count);
    }
}