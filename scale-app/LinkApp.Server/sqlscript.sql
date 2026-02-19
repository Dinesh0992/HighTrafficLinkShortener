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



CREATE TABLE link_analytics (
    id SERIAL PRIMARY KEY,
    short_code VARCHAR(10) NOT NULL,
    clicked_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    ip_address VARCHAR(45),
    user_agent TEXT
);

-- Index the short_code so we can generate reports quickly later
CREATE INDEX idx_analytics_code ON link_analytics(short_code);


--For checking the Analystics entry 
SELECT * FROM link_analytics ORDER BY clicked_at DESC LIMIT 10;