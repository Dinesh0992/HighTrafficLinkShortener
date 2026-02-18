using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Distributed;
using Npgsql;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// --- CONFIGURATION ---
var connectionString = builder.Configuration.GetConnectionString("Postgres");
var redisConnection = builder.Configuration.GetConnectionString("Redis");

// --- PHASE 2: DATABASE & POOLING (Singleton DataSource) ---
builder.Services.AddNpgsqlDataSource(connectionString!);

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
                PermitLimit = 10,
                Window = TimeSpan.FromSeconds(10),
                SegmentsPerWindow = 5, // 10s / 5 segments = 2s per segment
                QueueLimit = 0
            }));
});

var app = builder.Build();

// Middleware Order is critical!
app.UseRateLimiter();

// --- THE HIGH-PERFORMANCE REDIRECT ENDPOINT ---
app.MapGet("/{code}", async (string code, NpgsqlDataSource dataSource, IDistributedCache cache) =>
{
    // 1. Check Redis Cache (RAM - Fast Path)
    var cachedUrl = await cache.GetStringAsync(code);
    if (!string.IsNullOrEmpty(cachedUrl))
    {
        return Results.Redirect(cachedUrl);
    }

    // 2. Check Postgres (Disk - Slow Path)
    await using var cmd = dataSource.CreateCommand("SELECT long_url FROM urls WHERE short_code = @c");
    cmd.Parameters.AddWithValue("c", code);
    var url = await cmd.ExecuteScalarAsync();

    if (url is string longUrl)
    {
        // 3. Update Cache for future requests
        await cache.SetStringAsync(code, longUrl, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1)
        });

        return Results.Redirect(longUrl);
    }

    return Results.NotFound();
}).RequireRateLimiting("sliding-by-ip");

app.Run();