/*phase one - the basics with no DI or pooling */
/*using Npgsql;
var builder = WebApplication.CreateBuilder(args);

// Enable CORS so Vite can talk to us
builder.Services.AddCors(options => {
    options.AddPolicy("VitePolicy", p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();
app.UseCors("VitePolicy");

string connString = "Host=localhost;Username=postgres;Password=password123;Database=shortener_db";

// Endpoint to find a link
app.MapGet("/{code}", async (string code) => {
    using var conn = new NpgsqlConnection(connString);
    await conn.OpenAsync();
    using var cmd = new NpgsqlCommand("SELECT long_url FROM urls WHERE short_code = @c", conn);
    cmd.Parameters.AddWithValue("c", code);
    var result = await cmd.ExecuteScalarAsync();
    return result is string url ? Results.Redirect(url) : Results.NotFound();
});

// Endpoint to seed data (The heavy lifter)
app.MapPost("/seed", async () => {
    using var conn = new NpgsqlConnection(connString);
    await conn.OpenAsync();
    using var writer = await conn.BeginBinaryImportAsync("COPY urls (long_url, short_code) FROM STDIN (FORMAT BINARY)");
    for (int i = 1; i <= 100000; i++) {
        await writer.StartRowAsync();
        await writer.WriteAsync($"https://google.com/search?q={i}", NpgsqlTypes.NpgsqlDbType.Text);
        await writer.WriteAsync($"code{i}", NpgsqlTypes.NpgsqlDbType.Varchar);
    }
    await writer.CompleteAsync();
    return "100,000 links created!";
});

app.Run();
*/
/* phase two with DI and connection pooling */
/*
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// 1. Setup Connection String (Ensure this matches your Docker setup)
var connectionString = "Host=localhost;Username=postgres;Password=password123;Database=shortener_db;Pooling=true;Minimum Pool Size=10;Maximum Pool Size=100;";

// 2. Register NpgsqlDataSource as a Singleton (The Pool Manager)
builder.Services.AddNpgsqlDataSource(connectionString);

// 3. Configure CORS (Allows your Vite frontend to talk to this API)
builder.Services.AddCors(options => {
    options.AddPolicy("VitePolicy", policy => 
        policy.WithOrigins("http://localhost:5173") // The default Vite port
              .AllowAnyMethod()
              .AllowAnyHeader());
});

var app = builder.Build();

// 4. Use Middleware
app.UseCors("VitePolicy");

// --- ENDPOINTS ---

// Root check
app.MapGet("/", () => "API is running. Ready to scale!");

// ⚡ The Redirect Endpoint (The one you'll stress test)
app.MapGet("/{code}", async (string code, NpgsqlDataSource dataSource) =>
{
    await using var cmd = dataSource.CreateCommand("SELECT long_url FROM urls WHERE short_code = @c");
    cmd.Parameters.AddWithValue("c", code);
    
    var result = await cmd.ExecuteScalarAsync();
    
    // 302 Redirect to the original URL
    return result is string url ? Results.Redirect(url) : Results.NotFound();
});

// 🚀 The Seeder Endpoint (To pump 100k rows for testing)
app.MapPost("/seed", async (NpgsqlDataSource dataSource) =>
{
    await using var connection = await dataSource.OpenConnectionAsync();
    await using var writer = await connection.BeginBinaryImportAsync(
        "COPY urls (long_url, short_code) FROM STDIN (FORMAT BINARY)");

    for (int i = 1; i <= 100000; i++)
    {
        await writer.StartRowAsync();
        await writer.WriteAsync($"https://google.com/search?q={i}", NpgsqlTypes.NpgsqlDbType.Text);
        await writer.WriteAsync($"code{i}", NpgsqlTypes.NpgsqlDbType.Varchar);
    }

    await writer.CompleteAsync();
    return Results.Ok("100,000 rows successfully seeded!");
});

app.Run();
*/
/* phase three with Redis caching */
using Microsoft.Extensions.Caching.Distributed;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Register DB
builder.Services.AddNpgsqlDataSource("Host=localhost;Username=postgres;Password=password123;Database=shortener_db;Pooling=true;");

// Register Redis
builder.Services.AddStackExchangeRedisCache(options => {
    options.Configuration = "localhost:6379";
});

var app = builder.Build();

app.MapGet("/{code}", async (string code, NpgsqlDataSource dataSource, IDistributedCache cache) => {
    // 1. Try to get the URL from the Redis RAM
    var cachedUrl = await cache.GetStringAsync(code);
    
    if (!string.IsNullOrEmpty(cachedUrl)) {
        return Results.Redirect(cachedUrl);
    }

    // 2. If not in RAM, go to the slow Database
    await using var cmd = dataSource.CreateCommand("SELECT long_url FROM urls WHERE short_code = @c");
    cmd.Parameters.AddWithValue("c", code);
    var result = await cmd.ExecuteScalarAsync();

    if (result is string url) {
        // 3. Save it to RAM so the NEXT 1 million users don't hit the DB
        await cache.SetStringAsync(code, url, new DistributedCacheEntryOptions {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10)
        });
        return Results.Redirect(url);
    }
    
    return Results.NotFound();
});

app.Run();