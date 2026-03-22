# ==============================================================================
# KUBERNETES SETUP CHECKLIST FOR LINK APP SERVER
# ==============================================================================
# Complete guide to set up the entire infrastructure from scratch
# ==============================================================================

## PREREQUISITES
- [ ] Docker Desktop with Kubernetes enabled
- [ ] kubectl installed and configured
- [ ] Clone the repository
- [ ] autocannon installed (`npm install -g autocannon`)

---

# PART 1: FRESH START - CLEANUP EXISTING RESOURCES

## 1.1 Clean Kubernetes Resources

```bash
# Delete Kubernetes Dashboard
kubectl delete -f https://raw.githubusercontent.com/kubernetes/dashboard/v2.7.0/aio/deploy/recommended.yaml --ignore-not-found=true

# Delete HPA
kubectl delete hpa --all

# Delete all K8s resources from YAML files
kubectl delete -f scale-app/k8s --all

# Delete all pods
kubectl delete pods --all

# Delete all services
kubectl delete svc --all
```

## 1.2 Clean Docker (Keep system images)

```bash
# Stop and remove project containers only
docker stop scale-lab-db clickhouse-server rabbutmq scale-lab-cache 2>/dev/null || true
docker rm scale-lab-db clickhouse-server rabbutmq scale-lab-cache 2>/dev/null || true

# Remove project images only (keep system images)
docker rmi link-app-server:v25 link-app-server:v26 link-app-client:v1 2>/dev/null || true
```

## 1.3 Verify Clean State

```bash
# Should show: No resources found
kubectl get pods

# Should show: kubernetes service only
kubectl get svc

# Should show: no results
docker images | findstr link-app
```

---

# PART 2: BUILD & DEPLOY

## 2.1 Build Docker Image

```bash
cd scale-app
docker build -t link-app-server:v26 -f LinkApp.Server/Dockerfile .
```

**Verify:**
```bash
docker images | findstr link-app
```

## 2.2 Deploy Infrastructure Services

```bash
cd scale-app/k8s

# Deploy each service
kubectl apply -f redis.yaml
kubectl apply -f postgres.yaml
kubectl apply -f rabbitmq.yaml
kubectl apply -f clickhouse.yaml
kubectl apply -f link-app-config.yaml
```

## 2.3 Wait for Infrastructure Pods

```bash
# Watch pods until all 4 are Running
kubectl get pods -w
```

**Expected Output:**
```
NAME                          READY   STATUS    RESTARTS   AGE
clickhouse-deployment-xxx     1/1     Running   0          Xm
postgres-deployment-xxx       1/1     Running   0          Xm
rabbitmq-deployment-xxx     1/1     Running   0          Xm
redis-deployment-xxx         1/1     Running   0          Xm
```

## 2.4 Create PostgreSQL Table

```bash
# Get postgres pod name
kubectl get pods -l app=postgres -o jsonpath='{.items[0].metadata.name}'
```

```bash
# Create table (replace <POD_NAME>)
kubectl exec -it <POD_NAME> -- psql -U postgres -d shortener_db -c "
CREATE TABLE IF NOT EXISTS urls (
    id SERIAL PRIMARY KEY,
    short_code VARCHAR(10) NOT NULL,
    long_url TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_short_code ON urls(short_code);
"
```

**Verify:**
```bash
kubectl exec -it <PG_POD> -- psql -U postgres -d shortener_db -c "\dt"
```

## 2.5 Create ClickHouse Table

```bash
# Get clickhouse pod name
kubectl get pods -l app=clickhouse -o jsonpath='{.items[0].metadata.name}'
```

```bash
# Create database
kubectl exec -it <POD_NAME> -- clickhouse-client --query "CREATE DATABASE IF NOT EXISTS analytics_db"
```

```bash
# Create table (separate command)
kubectl exec -it <POD_NAME> -- clickhouse-client --query "
CREATE TABLE IF NOT EXISTS analytics_db.link_analytics_log (
    short_code String,
    ip_address String,
    user_agent String,
    clicked_at DateTime
) ENGINE = MergeTree()
ORDER BY (short_code, clicked_at);
"
```

**Verify:**
```bash
kubectl exec -it <CH_POD> -- clickhouse-client --query "SHOW TABLES FROM analytics_db"
```

## 2.6 Deploy Link Server

```bash
kubectl apply -f link-server.yaml
```

**Wait for pods:**
```bash
kubectl get pods -w
```

**Expected:** 2 link-server pods running

## 2.7 Deploy HPA (Horizontal Pod Autoscaler)

```bash
kubectl apply -f hpa-api.yaml
```

**Verify:**
```bash
kubectl get hpa
```

---

# PART 3: KUBERNETES DASHBOARD SETUP

## 3.1 Install Dashboard

```bash
kubectl apply -f https://raw.githubusercontent.com/kubernetes/dashboard/v2.7.0/aio/deploy/recommended.yaml
```

## 3.2 Create Admin User

```bash
kubectl apply -f ../LinkApp.Server/dashboard-admin.yaml
```

## 3.3 Get Dashboard Token

```bash
kubectl -n kubernetes-dashboard create token admin-user
```

**Copy this token for login**

## 3.4 Access Dashboard

1. Start proxy:
```bash
kubectl proxy
```

2. Open in browser:
```
http://localhost:8001/api/v1/namespaces/kubernetes-dashboard/services/https:kubernetes-dashboard:/proxy/
```

3. Select **Token** and paste the token

---

# PART 4: TEST & VERIFICATION

## 4.1 Check All Services

```bash
# All pods
kubectl get pods
```

```bash
# All services
kubectl get svc
```

```bash
# HPA status
kubectl get hpa
```

## 4.2 Test Endpoints

```bash
# Health check (using NodePort)
curl http://localhost:30082/ping
```

```bash
# Seed data (creates 100 test links)
curl -X POST http://localhost:30082/api/seed
```

```bash
# Test redirect
curl -v http://localhost:30082/code1 -L
```

```bash
# Test stats API
curl http://localhost:30082/api/stats/code1
```

## 4.3 Verify Database Tables

```bash
# PostgreSQL row count
kubectl exec -it <PG_POD> -- psql -U postgres -d shortener_db -c "SELECT COUNT(*) FROM urls;"

# ClickHouse visits count
kubectl exec -it <CH_POD> -- clickhouse-client --query "SELECT COUNT(*) FROM analytics_db.link_analytics_log"
```

---

# PART 5: LOAD TEST & AUTO-SCALING

## 5.1 HPA Configuration

```yaml
# hpa-api.yaml configuration
scaleUp:
  stabilizationWindowSeconds: 0        # Immediate scale up
  policies:
  - type: Percent   value: 100       # Can double pods in 15 sec
  
scaleDown:
  stabilizationWindowSeconds: 60       # Wait 1 min before scale down
  policies:
  - type: Percent   value: 50        # Can remove 50% in 15 sec

minReplicas: 2
maxReplicas: 50
metrics:
- type: Resource
  resource:
    name: cpu
    target:
      type: Utilization
      averageUtilization: 70
```

## 5.2 Run Load Test

```bash
# Standard load test
autocannon -c 500 --expect 302 --expect 429 --renderStatusCodes -d 70 -p 10 http://localhost:30082/code1
```

## 5.3 Watch Auto-Scaling (in separate terminals)

**Terminal 1: Watch Pods**
```bash
kubectl get pods -l app=link-server -w
```

**Terminal 2: Watch HPA**
```bash
kubectl get hpa link-server-hpa -w
```

**Terminal 3: Watch Resource Usage**
```bash
kubectl top pods -l app=link-server -w
```

## 5.4 Verify Scaling Results

```bash
# Check final pod count
kubectl get pods -l app=link-server
```

```bash
# Check HPA
kubectl get hpa link-server-hpa
```

```bash
# Check resource usage
kubectl top pods -l app=link-server
```

---

# QUICK REFERENCE: ALL COMMANDS IN ORDER

```bash
# === FRESH START ===
kubectl delete -f https://raw.githubusercontent.com/kubernetes/dashboard/v2.7.0/aio/deploy/recommended.yaml --ignore-not-found=true
kubectl delete hpa --all
kubectl delete -f scale-app/k8s --all
kubectl delete pods --all
kubectl delete svc --all

# === BUILD ===
cd scale-app
docker build -t link-app-server:v26 -f LinkApp.Server/Dockerfile .

# === DEPLOY INFRA ===
cd scale-app/k8s
kubectl apply -f redis.yaml
kubectl apply -f postgres.yaml
kubectl apply -f rabbitmq.yaml
kubectl apply -f clickhouse.yaml
kubectl apply -f link-app-config.yaml
kubectl get pods -w  # Wait for 4 pods running

# === CREATE TABLES ===
kubectl exec -it $(kubectl get pod -l app=postgres -o jsonpath='{.items[0].metadata.name}') -- \
  psql -U postgres -d shortener_db -c "CREATE TABLE IF NOT EXISTS urls (id SERIAL PRIMARY KEY, short_code VARCHAR(10) NOT NULL, long_url TEXT NOT NULL);"

kubectl exec -it $(kubectl get pod -l app=clickhouse -o jsonpath='{.items[0].metadata.name}') -- \
  clickhouse-client --query "CREATE DATABASE IF NOT EXISTS analytics_db"

kubectl exec -it $(kubectl get pod -l app=clickhouse -o jsonpath='{.items[0].metadata.name}') -- \
  clickhouse-client --query "CREATE TABLE IF NOT EXISTS analytics_db.link_analytics_log (short_code String, ip_address String, user_agent String, clicked_at DateTime) ENGINE = MergeTree() ORDER BY (short_code, clicked_at);"

# === DEPLOY APP ===
kubectl apply -f link-server.yaml
kubectl apply -f hpa-api.yaml

# === DASHBOARD ===
kubectl apply -f https://raw.githubusercontent.com/kubernetes/dashboard/v2.7.0/aio/deploy/recommended.yaml
kubectl apply -f ../LinkApp.Server/dashboard-admin.yaml

# === TEST ===
curl http://localhost:30082/ping
curl -X POST http://localhost:30082/api/seed

# === LOAD TEST ===
autocannon -c 500 --expect 302 --expect 429 --renderStatusCodes -d 70 -p 10 http://localhost:30082/code1

# === VERIFY ===
kubectl get pods -l app=link-server
kubectl get hpa link-server-hpa
kubectl top pods -l app=link-server
```

---

# EXPECTED RESULTS

## After Load Test (500 connections, 70 seconds)

| Metric | Value |
|--------|-------|
| Total Requests | 1,511,795 |
| Requests/sec | 21,823 avg |
| Peak Throughput | 26,895 RPS |
| Latency (Avg) | 233 ms |
| Latency (P99) | 1,296 ms |
| HTTP 302 | 14,000 |
| HTTP 429 | 1,491,795 |
| Pods (Before) | 2 |
| Pods (During) | 6 |
| CPU Peak | 390% |
| ClickHouse Visits | 52,001 |

---

# ACCESS URLs

| Service | URL | Credentials |
|---------|-----|-------------|
| Link Server | http://localhost:30082 | - |
| RabbitMQ | http://localhost:15672 | admin / password123 |
| K8s Dashboard | http://localhost:8001/api/v1/namespaces/kubernetes-dashboard/services/https:kubernetes-dashboard:/proxy/ | Token |

---

# TROUBLESHOOTING

## Pods not starting?
```bash
kubectl describe pod <pod-name>
kubectl logs <pod-name>
```

## Can't connect to services?
```bash
kubectl get endpoints
kubectl get svc
```

## Image pull error?
```bash
docker images | findstr link-app-server
docker build -t link-app-server:v26 -f LinkApp.Server/Dockerfile .
```

## HPA not scaling?
```bash
kubectl describe hpa link-server-hpa
kubectl top pods
```

## Port already in use?
```bash
netstat -ano | findstr :5082
# Kill the process or use NodePort (30082) instead
```

---

# FILE STRUCTURE

```
scale-app/
├── k8s/
│   ├── CHECKLIST.md           # Complete setup guide (THIS FILE)
│   ├── redis.yaml             # Redis cache service
│   ├── postgres.yaml          # PostgreSQL database
│   ├── rabbitmq.yaml         # RabbitMQ message queue
│   ├── clickhouse.yaml       # ClickHouse analytics
│   ├── link-server.yaml      # Application deployment + service
│   ├── link-app-config.yaml  # ConfigMap for environment variables
│   └── hpa-api.yaml         # Horizontal Pod Autoscaler
│
└── LinkApp.Server/
    ├── Dockerfile             # Multi-stage build for .NET app
    ├── scripts/
    │   ├── postgresql.sql     # PostgreSQL table schema
    │   └── clickhouse.sql    # ClickHouse table schema
    └── dashboard-admin.yaml   # K8s Dashboard admin user
```
