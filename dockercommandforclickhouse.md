1. Run ClickHouse container with user, password, and database
bash
docker run -d --name clickhouse-server -p 8123:8123 -p 9000:9000 --ulimit nofile=262144:262144 -e CLICKHOUSE_USER=admin -e CLICKHOUSE_PASSWORD=password123 -e CLICKHOUSE_DB=analytics_db -v clickhouse_data:/var/lib/clickhouse clickhouse/clickhouse-server
2. Connect to ClickHouse client inside the container
bash
docker exec -it clickhouse-server clickhouse-client --user admin --password password123 --database analytics_db
(This opens an interactive client session. If you want to run queries directly, use --query as shown below.)

3. Create the analytics table directly via command
bash
docker exec -it clickhouse-server clickhouse-client --user admin --password password123 --database analytics_db --query "CREATE TABLE IF NOT EXISTS link_analytics_log (short_code String, ip_address String, user_agent String, clicked_at DateTime DEFAULT now()) ENGINE = MergeTree() ORDER BY (short_code, clicked_at)"

List all tables in the database
bash
docker exec -it clickhouse-server clickhouse-client --user admin --password password123 --database analytics_db --query "SHOW TABLES"


INSERT INTO analytics_db.link_analytics_log (short_code, ip_address, user_agent, clicked_at) VALUES ('code1', '1.1.1.1', 'Manual', now() - INTERVAL 1 DAY)