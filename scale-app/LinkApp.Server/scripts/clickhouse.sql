CREATE DATABASE IF NOT EXISTS analytics_db;

CREATE TABLE IF NOT EXISTS analytics_db.link_analytics_log (
    short_code String,
    ip_address String,
    user_agent String,
    clicked_at DateTime
) ENGINE = MergeTree()
ORDER BY (short_code, clicked_at);
