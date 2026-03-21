using ClickHouse.Client.ADO;
using LinkApp.Server.Consumers;
using LinkApp.Server.Events;
using LinkApp.Server.Services;
using MassTransit;
using Microsoft.Extensions.Caching.Distributed;
using Npgsql;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Postgres");
var redisConnection = builder.Configuration.GetConnectionString("Redis");
var clickHouseConnection = builder.Configuration.GetConnectionString("ClickHouse");

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowVite", policy =>
        policy
        .WithOrigins("http://localhost:5173", "http://localhost:30082")
        .AllowAnyHeader()
        .AllowAnyMethod());
});

builder.Services.AddNpgsqlDataSource(connectionString!);

var chBuilder = new ClickHouseConnectionStringBuilder(clickHouseConnection)
{
    Username = builder.Configuration["ConnectionStrings:ClickHouse:Username"] ?? "admin",
    Password = builder.Configuration["ConnectionStrings:ClickHouse:Password"] ?? "password123"
};

builder.Services.AddSingleton(new ClickHouseConnection(chBuilder.ToString()));

builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = redisConnection;
    options.InstanceName = "Shortener_";
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.Headers["Retry-After"] = "10";
        await context.HttpContext.Response.WriteAsync(
            "Quota exceeded. Try again in 10 seconds.",
            cancellationToken: token);
    };

    options.AddPolicy("sliding-by-ip", httpContext =>
        RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            factory: _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 1000,
                Window = TimeSpan.FromSeconds(10),
                SegmentsPerWindow = 5,
                QueueLimit = 0
            }));
});

builder.Services.AddScoped<StatsService>();
builder.Services.AddSingleton<ClickHouseService>();

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<LinkVisitedBatchConsumer>(cfg =>
    {
        cfg.Options<BatchOptions>(options => options
            .SetMessageLimit(100)
            .SetTimeLimit(TimeSpan.FromSeconds(5)));
    });

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(new Uri("amqp://admin:password123@rabbitmq-service:5672/"));
        cfg.ConfigureEndpoints(context);
    });
});

var app = builder.Build();

app.UseCors("AllowVite");
app.UseRateLimiter();

app.MapPost("/api/seed", async (NpgsqlDataSource dataSource) =>
{
    await using var conn = await dataSource.OpenConnectionAsync();
    
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

app.MapGet("/{code}", async (
    string code,
    NpgsqlDataSource dataSource,
    IDistributedCache cache,
    IPublishEndpoint publishEndpoint,
    HttpContext context,
    ILogger<Program> logger) =>
{
    var cachedUrl = await cache.GetStringAsync(code);

    if (string.IsNullOrEmpty(cachedUrl))
    {
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
        try
        {
            await publishEndpoint.Publish(new LinkVisitedEvent(
                ShortCode: code,
                IpAddress: context.Connection.RemoteIpAddress?.ToString(),
                UserAgent: context.Request.Headers.UserAgent.ToString(),
                ClickedAt: DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to publish LinkVisitedEvent for {Code}", code);
            
            try
            {
                var chService = context.RequestServices.GetRequiredService<ClickHouseService>();
                _ = Task.Run(async () =>
                {
                    await chService.BulkInsertAsync(new List<object[]>
                    {
                        new object[] { code, context.Connection.RemoteIpAddress?.ToString() ?? "0.0.0.0", context.Request.Headers.UserAgent.ToString(), DateTime.UtcNow }
                    });
                });
            }
            catch { }
        }

        return Results.Redirect(cachedUrl);
    }

    return Results.NotFound();
}).RequireRateLimiting("sliding-by-ip");

app.MapGet("/api/stats/{code}", async (string code, StatsService statsService) =>
{
    var stats = await statsService.GetStatsAsync(code);
    return stats is not null ? Results.Ok(stats) : Results.NotFound();
});

app.MapGet("/ping", () => Results.Ok(new { message = "GET request is working", timestamp = DateTime.UtcNow }));

app.Run();
