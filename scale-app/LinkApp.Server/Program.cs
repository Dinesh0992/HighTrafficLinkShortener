using ClickHouse.Client.ADO;
using LinkApp.Server.Consumers;
using LinkApp.Server.Events;
using LinkApp.Server.Services;
using MassTransit;
using Microsoft.Extensions.Caching.Distributed;
using Npgsql;
using System.Threading.Channels;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// --- CONFIGURATION ---
var connectionString = builder.Configuration.GetConnectionString("Postgres");
var redisConnection = builder.Configuration.GetConnectionString("Redis");
var clickHouseConnection = builder.Configuration.GetConnectionString("ClickHouse");


// 1. Define Policy
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowVite", policy =>
        policy
        .WithOrigins("http://localhost:5173")
              .AllowAnyHeader()
              .AllowAnyMethod());
});



// --- PHASE 2: DATABASE & POOLING (Singleton DataSource) ---
builder.Services.AddNpgsqlDataSource(connectionString!);

// --- PHASE 7: ANALYTICS (ClickHouse) ---
var chBuilder = new ClickHouseConnectionStringBuilder(clickHouseConnection);
builder.Services.AddSingleton(new ClickHouseConnection(chBuilder.ToString()));

// --- PHASE 3: DISTRIBUTED CACHING (Redis) ---
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = redisConnection;
    options.InstanceName = "Shortener_";
});

// --- PHASE 4: RATE LIMITING (Partitioned by IP) ---
builder.Services.AddRateLimiter(options =>
{
    // 1. Set the status code for all rejections
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // 2. Define WHAT happens when a user is rejected
    options.OnRejected = async (context, token) =>
    {
        // Add the Retry-After header (tells the client how many seconds to wait)
        context.HttpContext.Response.Headers["Retry-After"] = "10";

        // Write a custom message to the response body
        await context.HttpContext.Response.WriteAsync(
            "Quota exceeded. Try again in 10 seconds.",
            cancellationToken: token);
    };

    // 3. fixed-ip existing policy
    /*
    options.AddPolicy("fixed-by-ip", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromSeconds(10),
                QueueLimit = 0
            }));
    */
    /*liding-by-ip*/
    options.AddPolicy("sliding-by-ip", httpContext =>
        RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            factory: _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 1000, // Increased from 10 to 1000 fr rabbitmq autocannon load testing
                Window = TimeSpan.FromSeconds(10),
                SegmentsPerWindow = 5, // 10s / 5 segments = 2s per segment
                QueueLimit = 0
            }));
});

/* COMMENTING --- PHASE 5: BACKGROUND WORKER (Channel for ClickData) ---
builder.Services.AddSingleton(Channel.CreateUnbounded<ClickData>());
builder.Services.AddHostedService<AnalyticsBackgroundWorker>();*/

builder.Services.AddScoped<StatsService>();

// --- PHASE 8: ANALYTICS (ClickHouse) WITH rabbitmq ---
builder.Services.AddSingleton<ClickHouseService>();

// RabbitMQ + MassTransit Configuration
builder.Services.AddMassTransit(x =>
{
    // Register our Batch Consumer
    x.AddConsumer<LinkVisitedBatchConsumer>(cfg =>
    {
        cfg.Options<BatchOptions>(options => options
            .SetMessageLimit(100) // For testing, trigger every 100 clicks
            .SetTimeLimit(TimeSpan.FromSeconds(5))); // Or every 5 seconds
    });

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host("localhost", "/", h => {
            h.Username("admin");
            h.Password("password123");
        });
        
        // This automatically sets up the exchange and queue in RabbitMQ
        cfg.ConfigureEndpoints(context);
    });
});


var app = builder.Build();
// --- MIDDLEWARE --- CORS should be early to set headers before rate limiter can reject requests
app.UseCors("AllowVite");

// Middleware Order is critical!
app.UseRateLimiter();

// --- THE HIGH-PERFORMANCE REDIRECT ENDPOINT ---

app.MapPost("/api/seed", async (NpgsqlDataSource dataSource) =>
{
    await using var conn = await dataSource.OpenConnectionAsync();
    
    // Using Binary COPY for the highest possible ingestion speed
    await using var writer = await conn.BeginBinaryImportAsync(
        "COPY urls (long_url, short_code) FROM STDIN (FORMAT BINARY)");

    for (int i = 1; i <= 100; i++)
    {
        await writer.StartRowAsync();
        await writer.WriteAsync($"https://google.com/search?q={i}", NpgsqlTypes.NpgsqlDbType.Text);
        await writer.WriteAsync($"code{i}", NpgsqlTypes.NpgsqlDbType.Varchar);
    }

    await writer.CompleteAsync();
    return Results.Ok("10000000 links created via Binary COPY!");
});
/*
app.MapGet("/{code}", async (
    string code,
    NpgsqlDataSource dataSource,
    IDistributedCache cache,
    Channel<ClickData> channel,
    HttpContext context) =>
{
    // 1. Check Redis Cache (Fast Path)
    var cachedUrl = await cache.GetStringAsync(code);

    if (!string.IsNullOrEmpty(cachedUrl))
    {
        // PUSH TO ANALYTICS (Background)
        channel.Writer.TryWrite(new ClickData(
            code,
            context.Connection.RemoteIpAddress?.ToString(),
            context.Request.Headers.UserAgent));

        return Results.Redirect(cachedUrl);
    }

    // 2. Check Postgres (Slow Path)
    await using var cmd = dataSource.CreateCommand("SELECT long_url FROM urls WHERE short_code = @c");
    cmd.Parameters.AddWithValue("c", code);
    var dbResult = await cmd.ExecuteScalarAsync();

    if (dbResult is string longUrl)
    {
        // Hydrate Cache
        await cache.SetStringAsync(code, longUrl, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1)
        });

        // PUSH TO ANALYTICS (Background)
        channel.Writer.TryWrite(new ClickData(
            code,
            context.Connection.RemoteIpAddress?.ToString(),
            context.Request.Headers.UserAgent));

        return Results.Redirect(longUrl);
    }

    return Results.NotFound();
}).RequireRateLimiting("sliding-by-ip");
*/
/* PHASE 8: ANALYTICS (ClickHouse) WITH rabbitmq ---
 We replace the Channel with a RabbitMQ Publisher. 
 The Background Worker is replaced with a MassTransit Consumer that listens to the LinkVisitedEvent and writes to ClickHouse.
*/
app.MapGet("/{code}", async (
    string code,
    NpgsqlDataSource dataSource,
    IDistributedCache cache,
    IPublishEndpoint publishEndpoint,
    HttpContext context) =>
{
    // 1. Check Redis
    var cachedUrl = await cache.GetStringAsync(code);

    if (string.IsNullOrEmpty(cachedUrl))
    {
        // 2. Query Postgres (Table: urls, Column: long_url)
        await using var cmd = dataSource.CreateCommand("SELECT long_url FROM urls WHERE short_code = @c");
        cmd.Parameters.AddWithValue("c", code);
        var dbResult = await cmd.ExecuteScalarAsync();

        if (dbResult is string longUrl)
        {
            cachedUrl = longUrl;
            await cache.SetStringAsync(code, longUrl, new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1)
            });
        }
    }

    if (!string.IsNullOrEmpty(cachedUrl))
    {
        // 3. Publish to RabbitMQ (Phase 8 Analytics)
        await publishEndpoint.Publish(new LinkVisitedEvent(
           ShortCode: code,
            IpAddress: context.Connection.RemoteIpAddress?.ToString(),
            UserAgent: context.Request.Headers.UserAgent,
            ClickedAt: DateTime.UtcNow));

        return Results.Redirect(cachedUrl);
    }

    return Results.NotFound();
}).RequireRateLimiting("sliding-by-ip"); // <--- CRITICAL FOR 1M USER SCALE
// --- THE STATS ENDPOINT ---
app.MapGet("/api/stats/{code}", async (string code, StatsService statsService) =>
{
    var stats = await statsService.GetStatsAsync(code);
    return stats is not null ? Results.Ok(stats) : Results.NotFound();
});



app.Run();