# 🚀 High-Traffic Link Shortener: Scaling Lab

A high-performance .NET 10 Minimal API designed to demonstrate the evolution from a basic CRUD application to a distributed system capable of handling **15,000+ Requests Per Second**.

## 📊 Performance Evolution
| Phase | Strategy | Throughput | Stability |
| :--- | :--- | :--- | :--- |
| **Phase 1** | No Index / Raw Connections | ~6,000 RPS | ❌ Crashed (Connection Exhaustion) |
| **Phase 2** | DB Indexing + Connection Pooling | ~1,800 RPS | ✅ Stable (Disk Bound) |
| **Phase 3** | Redis Caching | **15,000+ RPS** | ✅ High Performance (RAM Bound) |
| **Phase 4** | Fixed Window Rate Limiting | Controlled | 🛡️ Protected against Spam |

---

## 🛠️ Infrastructure Setup

### 1. Docker Environment
Run these commands to spin up the required infrastructure:
```bash
# Start PostgreSQL
docker run --name pg-shortener -e POSTGRES_PASSWORD=password -p 5432:5432 -d postgres

# Start Redis
docker run --name redis-shortener -p 6379:6379 -d redis



2. Database Schema & Seeding
Connect to your PostgreSQL instance and execute the following SQL:

SQL
CREATE DATABASE shortener_db;

-- Table for URL Mapping
CREATE TABLE urls (
    id SERIAL PRIMARY KEY,
    short_code VARCHAR(10) NOT NULL,
    long_url TEXT NOT NULL
);

-- Optimization: The B-Tree Index (Phase 2)
CREATE INDEX idx_short_code ON urls(short_code);

-- Seed 100,000 rows for stress testing
INSERT INTO urls (short_code, long_url)
SELECT 'code' || i, '[https://www.google.com/search?q=](https://www.google.com/search?q=)' || i
FROM generate_series(1, 100000) s(i);
🏗️ Architecture Design
Dependency Injection (DI) Lifetimes
To achieve 15k+ RPS, the application uses a strategic mix of DI lifetimes to balance performance and safety:

NpgsqlDataSource (Singleton): Acts as the Connection Pool Manager (The Reservoir). Must be a Singleton to manage shared database pipes efficiently.

IDistributedCache (Singleton): Shared Redis client to avoid the overhead of re-establishing connections.

Repositories (Scoped): Ensures business logic and data state remain isolated to a single HTTP request.

⚡ Stress Testing Commands
We use autocannon to simulate high-traffic scenarios.

Phase 1/2 Test (Direct DB Access)
Bash
autocannon -c 100 -d 10 http://localhost:5082/code99999
Phase 3 Test (Cached Redirects)
Since redirects return HTTP 302, we must tell autocannon to expect non-2xx codes:

Bash
autocannon -c 100 -d 10 --expect 302 http://localhost:5082/code99999
🛡️ Security: Rate Limiting
The application implements the Fixed Window algorithm to prevent single-user abuse:

Limit: 10 requests

Window: 10 seconds

Response: 429 Too Many Requests

🚀 How to Run
Clone the repository.

Update appsettings.json with your Docker connection strings.

Run dotnet run.

Run the autocannon commands above to verify performance.