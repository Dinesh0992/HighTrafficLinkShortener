# 🚀 Project Setup Guide

**Quick reference for Local, Docker, and Kubernetes environments**

---

## 📋 Prerequisites

| Component | Local | Docker | Kubernetes |
|-----------|-------|--------|------------|
| .NET 10 SDK | ✅ Required | ❌ Not needed | ❌ Not needed |
| Docker Desktop | ❌ Optional | ✅ Required | ✅ Required |
| Kubernetes | ❌ Not needed | ❌ Not needed | ✅ Enabled in Docker Desktop |
| Node.js (for dashboard) | ✅ Optional | ❌ Optional | ❌ Optional |

---

## 🖥️ OPTION 1: Local Development (No Containers)

### Start Infrastructure
```powershell
# Start Docker containers for databases only
cd scale-app/LinkApp.Server
docker compose up -d
```

### Initialize Databases
```powershell
# Wait 10 seconds for containers to start
Start-Sleep 10

# Test PostgreSQL
docker exec scale-lab-db psql -U postgres -d shortener_db -c "SELECT 1"

# Test ClickHouse
docker exec clickhouse-server clickhouse-client --query "SELECT 1"
```

### Run API Server
```powershell
cd scale-app/LinkApp.Server
dotnet run
```

### Run Dashboard (Optional)
```powershell
cd scale-app/LinkApp.Client
npm install
npm run dev
```

### Test Endpoints
```powershell
# Seed data
curl http://localhost:5082/api/seed

# Test redirect
curl http://localhost:5082/code1

# Test stats
curl http://localhost:5082/api/stats/code1

# Health check
curl http://localhost:5082/ping
```

### Stop Services
```powershell
# Stop containers
cd scale-app/LinkApp.Server
docker compose down

# Or keep running for next time (data persists in volumes)
```

---

## 🐳 OPTION 2: Docker Compose (Containerized)

### Build & Run Everything
```powershell
cd scale-app/LinkApp.Server

# Build the API server image
docker build -t link-app-server:v1 ../..

# Start all services
docker compose up -d

# Check status
docker compose ps
```

### Verify Services
```powershell
# API Server
curl http://localhost:5082/ping

# PostgreSQL
docker exec scale-lab-db psql -U postgres -d shortener_db -c "\dt"

# ClickHouse
docker exec clickhouse-server clickhouse-client --query "SELECT 1 FROM system.tables"

# RabbitMQ Dashboard
# Open: http://localhost:15672 (admin/password123)

# Redis
docker exec scale-lab-cache redis-cli ping
```

### Load Test
```powershell
# Install autocannon if not installed
npm install -g autocannon

# Run load test
autocannon -c 10 -d 5 http://localhost:5082/code1
```

### Scale API Container
```powershell
# Scale to 3 API instances
docker compose up -d --scale link-server=3
```

### Stop Everything
```powershell
cd scale-app/LinkApp.Server
docker compose down

# Remove volumes (deletes data)
docker compose down -v
```

---

## ☸️ OPTION 3: Kubernetes (Auto-Scaling)

### Pre-requisites
```powershell
# Enable Kubernetes in Docker Desktop
# Settings → Kubernetes → Enable Kubernetes → Apply & Restart

# Verify Kubernetes is running
kubectl get nodes
```

### Deploy Everything
```powershell
cd scale-app

# 1. Deploy databases and message broker
kubectl apply -f k8s/postgres.yaml
kubectl apply -f k8s/redis.yaml
kubectl apply -f k8s/clickhouse.yaml
kubectl apply -f k8s/rabbitmq.yaml

# 2. Wait for pods to be ready
kubectl get pods -w

# 3. Deploy ConfigMap
kubectl apply -f k8s/link-app-config.yaml

# 4. Deploy API Server
kubectl apply -f k8s/link-server.yaml

# 5. Deploy HPA (Horizontal Pod Autoscaler)
kubectl apply -f k8s/hpa-api.yaml
```

### Verify Deployment
```powershell
# Check all pods
kubectl get pods

# Check services
kubectl get svc

# Check HPA
kubectl get hpa

# View API logs
kubectl logs -l app=link-server --tail=20
```

### Get API Endpoint
```powershell
# NodePort is 30082
$apiUrl = "http://localhost:30082"

# Test health
curl "$apiUrl/ping"

# Seed data
curl -X POST "$apiUrl/api/seed"

# Test redirect
curl -L "$apiUrl/code1"
```

### Load Test & Auto-Scaling
```powershell
# Monitor HPA in real-time
kubectl get hpa link-server-hpa -w

# Monitor pods scaling
kubectl get pods -l app=link-server -w

# Run load test (use hey or autocannon)
hey -c 100 -n 100000 http://localhost:30082/code1

# Or with autocannon
autocannon -c 100 -d 60 http://localhost:30082/code1
```

### Watch Auto-Scaling
```powershell
# Watch pod count increase
kubectl get pods -l app=link-server --watch

# Watch CPU metrics
kubectl top pods -l app=link-server

# Describe HPA for scaling events
kubectl describe hpa link-server-hpa
```

### Stop Kubernetes Deployment
```powershell
cd scale-app

# Delete all deployments
kubectl delete -f k8s/link-server.yaml
kubectl delete -f k8s/hpa-api.yaml
kubectl delete -f k8s/rabbitmq.yaml
kubectl delete -f k8s/clickhouse.yaml
kubectl delete -f k8s/redis.yaml
kubectl delete -f k8s/postgres.yaml
kubectl delete -f k8s/link-app-config.yaml

# Verify cleanup
kubectl get all
```

---

## 🔄 Quick Resume (Tomorrow)

### If you stopped Docker Desktop
```powershell
# 1. Start Docker Desktop
# 2. Wait for Kubernetes to be ready

# 3. For Local Development:
cd scale-app/LinkApp.Server
docker compose up -d
dotnet run

# 4. For Kubernetes:
cd scale-app
kubectl apply -f k8s/postgres.yaml
kubectl apply -f k8s/redis.yaml
kubectl apply -f k8s/clickhouse.yaml
kubectl apply -f k8s/rabbitmq.yaml
kubectl apply -f k8s/link-app-config.yaml
kubectl apply -f k8s/link-server.yaml
kubectl apply -f k8s/hpa-api.yaml

# 5. Test
curl http://localhost:30082/ping
```

---

## 📊 Service Ports Reference

| Service | Local | Docker | Kubernetes |
|---------|-------|--------|------------|
| **API Server** | 5082 | 5082 | 30082 |
| **PostgreSQL** | 5432 | 5432 | 5432 (internal) |
| **Redis** | 6379 | 6379 | 6379 (internal) |
| **ClickHouse** | 8123 | 8123 | 8123 (internal) |
| **RabbitMQ** | 5672, 15672 | 5672, 15672 | 5672 (internal) |
| **Dashboard** | 5173 | - | - |

---

## 🗄️ Data Persistence

| Environment | Data Location | Survives Restart |
|-------------|---------------|------------------|
| **Local** | Docker volumes | ✅ Yes |
| **Docker Compose** | Docker volumes | ✅ Yes |
| **Kubernetes** | hostPath volumes | ⚠️ WSL specific |

### Kubernetes Data Warning
The current setup uses `hostPath` volumes which are stored at:
- PostgreSQL: `/mnt/wsl/postgres-data`

If you restart Docker Desktop, data may be lost. For production, use PersistentVolumeClaims (PVC).

---

## 🔧 Common Commands

### Docker
```powershell
# View logs
docker compose logs -f

# Restart a service
docker compose restart postgres

# Shell into container
docker exec -it scale-lab-db psql -U postgres
```

### Kubernetes
```powershell
# Restart deployment
kubectl rollout restart deployment/link-server-deployment

# Scale manually
kubectl scale deployment link-server-deployment --replicas=5

# Get detailed pod info
kubectl describe pod <pod-name>

# View events
kubectl get events --sort-by='.lastTimestamp'
```

---

## 🆘 Troubleshooting

### Docker Compose Issues
```powershell
# Remove and recreate
docker compose down -v
docker compose up -d
```

### Kubernetes Issues
```powershell
# Check pod logs
kubectl logs <pod-name>

# Describe problematic pod
kubectl describe pod <pod-name>

# Check resource usage
kubectl top nodes
kubectl top pods
```

### Reset Everything
```powershell
# Kubernetes - delete all resources
kubectl delete all --all --namespace=default

# Docker - stop everything
docker compose down -v
docker system prune -f
```

---

## 📁 Key Files

| File | Purpose |
|------|---------|
| `scale-app/LinkApp.Server/docker-compose.yml` | Docker infrastructure |
| `scale-app/LinkApp.Server/Dockerfile` | API container image |
| `scale-app/k8s/*.yaml` | Kubernetes manifests |
| `scale-app/LinkApp.Server/appsettings.json` | Local config |
| `scale-app/k8s/link-app-config.yaml` | K8s config map |

---

**Last Updated:** March 21, 2026
