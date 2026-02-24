#!/bin/bash
set -e

sleep 15

clickhouse-client --host 127.0.0.1 --user admin --password password123 --multiquery < /scripts/clickhouse.sql

echo "ClickHouse tables initialized"
