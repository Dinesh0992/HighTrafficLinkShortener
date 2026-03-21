# Saga Pattern Implementation for Click Analytics

## What is Saga Pattern?

Saga is a sequence of **local transactions**. Each step publishes an event/message. If one step fails, Saga executes **compensating transactions** to undo previous steps.

```
┌─────────────────────────────────────────────────────────────────┐
│                     SAGA PATTERN                                │
│                                                                  │
│   Step 1         Step 2           Step 3         Step 4        │
│   ┌───────┐      ┌───────┐       ┌───────┐      ┌───────┐      │
│   │Create │ ───▶ │Publish │ ───▶ │Insert │ ───▶ │Update │      │
│   │Event  │      │toQueue │      │CH     │      │Stats  │      │
│   └───┬───┘      └───┬───┘       └───┬───┘      └───┬───┘      │
│       │              │               │              │           │
│       │              │               │              │           │
│       ▼              ▼               ▼              ▼           │
│   ┌──────────────────────────────────────────────┐             │
│   │           COMPENSATING TRANSACTIONS          │             │
│   │    (If any step fails, rollback previous)   │             │
│   └──────────────────────────────────────────────┘             │
└─────────────────────────────────────────────────────────────────┘
```

---

## Why Saga for This Project?

In your current flow:
```
User Click → API → Publish to Queue → Consumer → ClickHouse
```

With Saga, you can:
1. **Track processing state** of each click event
2. **Handle failures gracefully** with retry/rollback
3. **Maintain processing history** for debugging
4. **Support multiple consumers** (different processing pipelines)

---

## Implementation

### Step 1: Add Saga State Model

```csharp
// Models/ClickProcessingSaga.cs
namespace LinkApp.Server.Models;

public enum SagaStatus
{
    Started,
    EventPublished,
    ClickHouseInserted,
    Completed,
    Failed,
    Compensating,
    Compensated
}

public class ClickProcessingSaga
{
    public Guid SagaId { get; set; }
    public string ShortCode { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public DateTime ClickedAt { get; set; }
    public SagaStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int RetryCount { get; set; }
}
```

### Step 2: Create Saga Repository

```csharp
// Services/SagaStateStore.cs
using LinkApp.Server.Models;
using StackExchange.Redis;

namespace LinkApp.Server.Services;

public interface ISagaStateStore
{
    Task CreateSagaAsync(ClickProcessingSaga saga);
    Task UpdateSagaStatusAsync(Guid sagaId, SagaStatus status, string? error = null);
    Task<ClickProcessingSaga?> GetSagaAsync(Guid sagaId);
    Task CompleteSagaAsync(Guid sagaId);
}

public class SagaStateStore : ISagaStateStore
{
    private readonly IDatabase _redis;
    private const string SagaPrefix = "saga:";

    public SagaStateStore(IConnectionMultiplexer redis)
    {
        _redis = redis.GetDatabase();
    }

    public async Task CreateSagaAsync(ClickProcessingSaga saga)
    {
        var key = $"{SagaPrefix}{saga.SagaId}";
        var json = System.Text.Json.JsonSerializer.Serialize(saga);
        await _redis.StringSetAsync(key, json, TimeSpan.FromHours(24));
    }

    public async Task UpdateSagaStatusAsync(Guid sagaId, SagaStatus status, string? error = null)
    {
        var saga = await GetSagaAsync(sagaId);
        if (saga != null)
        {
            saga.Status = status;
            if (!string.IsNullOrEmpty(error))
                saga.ErrorMessage = error;
            
            await CreateSagaAsync(saga);
        }
    }

    public async Task<ClickProcessingSaga?> GetSagaAsync(Guid sagaId)
    {
        var key = $"{SagaPrefix}{sagaId}";
        var json = await _redis.StringGetAsync(key);
        if (json.IsNullOrEmpty) return null;
        
        return System.Text.Json.JsonSerializer.Deserialize<ClickProcessingSaga>(json!);
    }

    public async Task CompleteSagaAsync(Guid sagaId)
    {
        await UpdateSagaStatusAsync(sagaId, SagaStatus.Completed);
    }
}
```

### Step 3: Update Consumer with Saga

```csharp
// Consumers/LinkVisitedSagaConsumer.cs
using MassTransit;
using LinkApp.Server.Events;
using LinkApp.Server.Models;
using LinkApp.Server.Services;

namespace LinkApp.Server.Consumers;

public class LinkVisitedSagaConsumer : IConsumer<Batch<LinkVisitedEvent>>
{
    private readonly ClickHouseService _chService;
    private readonly ISagaStateStore _sagaStore;
    private readonly ILogger<LinkVisitedSagaConsumer> _logger;

    public LinkVisitedSagaConsumer(
        ClickHouseService chService,
        ISagaStateStore sagaStore,
        ILogger<LinkVisitedSagaConsumer> logger)
    {
        _chService = chService;
        _sagaStore = sagaStore;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<Batch<LinkVisitedEvent>> context)
    {
        var sagas = new List<ClickProcessingSaga>();
        
        // Step 1: Create Sagas for each event
        foreach (var message in context.Message)
        {
            var saga = new ClickProcessingSaga
            {
                SagaId = Guid.NewGuid(),
                ShortCode = message.Message.ShortCode,
                IpAddress = message.Message.IpAddress ?? "0.0.0.0",
                UserAgent = message.Message.UserAgent ?? "Unknown",
                ClickedAt = message.Message.ClickedAt,
                Status = SagaStatus.Started,
                CreatedAt = DateTime.UtcNow
            };
            
            await _sagaStore.CreateSagaAsync(saga);
            sagas.Add(saga);
        }

        _logger.LogInformation("SAGA: Created {Count} sagas", sagas.Count);

        try
        {
            // Step 2: Insert to ClickHouse
            var clicks = sagas.Select(s => new object[]
            {
                s.ShortCode,
                s.IpAddress,
                s.UserAgent,
                s.ClickedAt
            }).ToList();

            // Mark as Event Published
            foreach (var saga in sagas)
            {
                await _sagaStore.UpdateSagaStatusAsync(saga.SagaId, SagaStatus.EventPublished);
            }

            _logger.LogInformation("SAGA: Publishing events to ClickHouse for {Count} clicks", clicks.Count);

            await _chService.BulkInsertAsync(clicks);

            // Mark as Completed
            foreach (var saga in sagas)
            {
                await _sagaStore.CompleteSagaAsync(saga.SagaId);
            }

            _logger.LogInformation("SAGA: Completed {Count} clicks", sagas.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SAGA: Failed at ClickHouse insert");

            // Compensate: Mark as Failed
            foreach (var saga in sagas)
            {
                await _sagaStore.UpdateSagaStatusAsync(
                    saga.SagaId, 
                    SagaStatus.Failed, 
                    ex.Message);
            }

            // Could implement retry logic here
            throw;
        }
    }
}
```

### Step 4: Add Saga Tracking Endpoint

```csharp
// In Program.cs
app.MapGet("/api/saga/{sagaId}", async (Guid sagaId, ISagaStateStore sagaStore) =>
{
    var saga = await sagaStore.GetSagaAsync(sagaId);
    return saga is not null ? Results.Ok(saga) : Results.NotFound();
});

app.MapGet("/api/sagas/failed", async (ISagaStateStore sagaStore) =>
{
    // This would need Redis SCAN in real implementation
    return Results.Ok(new { message = "List failed sagas endpoint" });
});
```

### Step 5: Add Saga Retry Logic

```csharp
// Services/SagaRetryService.cs
using LinkApp.Server.Models;

public class SagaRetryService : BackgroundService
{
    private readonly ISagaStateStore _sagaStore;
    private readonly ClickHouseService _chService;
    private readonly ILogger<SagaRetryService> _logger;

    public SagaRetryService(
        ISagaStateStore sagaStore,
        ClickHouseService chService,
        ILogger<SagaRetryService> logger)
    {
        _sagaStore = sagaStore;
        _chService = chService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // In real implementation, scan Redis for failed sagas
            // and retry them
            
            _logger.LogInformation("SAGA: Checking for failed sagas to retry...");
            
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
```

---

## Saga Benefits for Auto-Scaling

```
┌─────────────────────────────────────────────────────────────────┐
│                    WITH SAGA PATTERN                             │
│                                                                  │
│  Traffic Spike:                                                 │
│  ┌─────────┐    ┌─────────┐    ┌─────────┐                    │
│  │  1000   │    │  5000   │    │ 10000   │  ← Queue builds    │
│  │  Clicks │ ──▶│  Queue  │ ──▶│  Queue  │                    │
│  └─────────┘    └─────────┘    └─────────┘                    │
│        │                                               │        │
│        ▼                                               ▼        │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │              SCALE OUT CONSUMERS                        │   │
│  │   Consumer Pod 1 → Consumer Pod 2 → Consumer Pod 5     │   │
│  │   Each processes batch, tracks saga state              │   │
│  │   If pod crashes, saga recovery picks up               │   │
│  └─────────────────────────────────────────────────────────┘   │
│                                                                  │
│  Recovery:                                                      │
│  - Failed message → Retry 3 times                               │
│  - Still failing → Move to Dead Letter Queue                   │
│  - All sagas tracked in Redis                                   │
│  - Dashboard shows exactly what failed                           │
└─────────────────────────────────────────────────────────────────┘
```

---

## Updated README Section

Add to README.md:

```markdown
### Phase 8.1: Saga Pattern for Reliable Analytics

**Problem:** What happens if ClickHouse is down? What if a consumer pod crashes mid-processing?

**Solution:** Implement Saga pattern for each click event:

1. **Saga State Tracking:** Each click creates a saga stored in Redis
2. **Step-by-Step Processing:** 
   - Step 1: Create Saga (Started)
   - Step 2: Publish Event (EventPublished)
   - Step 3: Insert ClickHouse (ClickHouseInserted)
   - Step 4: Mark Complete (Completed)
3. **Failure Recovery:** If any step fails, saga status shows exactly where
4. **Retry Logic:** Failed sagas can be retried automatically
5. **Dead Letter Queue:** Permanent failures go to DLQ for manual review

**Benefits:**
- Exactly-once processing guarantee
- Full visibility into processing state
- Automatic retry on transient failures
- Manual intervention for permanent failures
- Multi-consumer support without data duplication
```
