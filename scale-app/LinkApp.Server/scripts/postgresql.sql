-- Create database if not exists
SELECT 'CREATE DATABASE shortener_db' WHERE NOT EXISTS (SELECT * FROM pg_database WHERE datname = 'shortener_db')\gexec

-- Connect to the database
\c shortener_db;

-- Table for URL Mapping
CREATE TABLE IF NOT EXISTS urls (
    id SERIAL PRIMARY KEY,
    short_code VARCHAR(10) NOT NULL,
    long_url TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_short_code ON urls(short_code);

-- Table for Link Analytics
CREATE TABLE IF NOT EXISTS link_analytics (
    id SERIAL PRIMARY KEY,
    short_code VARCHAR(10) NOT NULL,
    clicked_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    ip_address VARCHAR(45),
    user_agent TEXT
);

CREATE INDEX IF NOT EXISTS idx_analytics_code ON link_analytics(short_code);
CREATE INDEX IF NOT EXISTS idx_analytics_code_date ON link_analytics (short_code, clicked_at DESC);
