# Kubernetes Auto-Scaling Guide for This Project

## Current Architecture for Auto-Scaling

```
                                    ┌──────────────────┐
                                    │  K8s HPA         │
                                    │  Scale on:       │
                                    │  • CPU > 70%     │
                                    │  • Queue > 100   │
                                    └────────┬─────────┘
                                             │
         ┌───────────────────────────────────┼────────────────────┐
         │                                   │                    │
         ▼                                   ▼                    ▼
┌─────────────────┐              ┌─────────────────┐    ┌─────────────────┐
│  API Pods       │              │  Queue          │    │  Consumer Pods  │
│  (scale on      │              │  RabbitMQ       │    │  (scale on      │
│   CPU + RPS)    │              │  (queue length) │    │   queue length) │
└─────────────────┘              └─────────────────┘    └─────────────────┘
```

---

## 1. Horizontal Pod Autoscaler (HPA) - CPU Based

```yaml
# k8s/hpa-api.yaml
apiVersion: autoscaling/v2
kind: HorizontalPodAutoscaler
metadata:
  name: link-server-hpa
spec:
  scaleTargetRef:
    apiVersion: apps/v1
    kind: Deployment
    name: link-server-deployment
  minReplicas: 2          # Minimum 2 pods
  maxReplicas: 100         # Max 100 pods for Black Friday
  metrics:
  - type: Resource
    resource:
      name: cpu
      target:
        type: Utilization
        averageUtilization: 70   # Scale up if CPU > 70%
  behavior:
    scaleUp:
      stabilizationWindowSeconds: 30
      policies:
      - type: Percent
        value: 100          # Double pods at a time
        periodSeconds: 15
    scaleDown:
      stabilizationWindowSeconds: 300   # Wait 5 min before scaling down
      policies:
      - type: Percent
        value: 10           # Remove 10% at a time
        periodSeconds: 60
```

**Apply:**
```bash
kubectl apply -f k8s/hpa-api.yaml
```

---

## 2. Queue-Length Based Scaling (Custom Metrics)

For scaling based on RabbitMQ queue length, you need **KEDA** (Kubernetes Event-driven Autoscaling):

```yaml
# k8s/keda-scaled-consumer.yaml
apiVersion: keda.sh/v1alpha1
kind: ScaledObject
metadata:
  name: rabbitmq-consumer-scaledobject
spec:
  scaleTargetRef:
    name: link-consumer-deployment
  pollingInterval: 15      # Check every 15 seconds
  cooldownPeriod: 300      # Wait 5 min before scaling down
  minReplicaCount: 1
  maxReplicaCount: 20
  triggers:
  - type: rabbitmq
    metadata:
      queueName: LinkVisitedBatch
      queueLength: "100"      # Scale up when queue > 100 messages
      host: amqp://admin:password123@rabbitmq-service:5672
```

---

## 3. Complete K8s Setup with Auto-Scaling

```yaml
# k8s/complete-deployment.yaml

---
# API Deployment with HPA
apiVersion: apps/v1
kind: Deployment
metadata:
  name: link-server-deployment
spec:
  replicas: 3
  selector:
    matchLabels:
      app: link-server
  template:
    metadata:
      labels:
        app: link-server
    spec:
      containers:
      - name: link-server
        image: link-app-server:latest
        resources:
          requests:
            cpu: "250m"        # Request 250 millicores
            memory: "256Mi"
          limits:
            cpu: "1000m"       # Limit to 1 core
            memory: "512Mi"
        ports:
        - containerPort: 5082

---
# API HPA
apiVersion: autoscaling/v2
kind: HorizontalPodAutoscaler
metadata:
  name: link-server-hpa
spec:
  scaleTargetRef:
    apiVersion: apps/v1
    kind: Deployment
    name: link-server-deployment
  minReplicas: 2
  maxReplicas: 50
  metrics:
  - type: Resource
    resource:
      name: cpu
      target:
        type: Utilization
        averageUtilization: 70

---
# Consumer Deployment (separate from API)
apiVersion: apps/v1
kind: Deployment
metadata:
  name: link-consumer-deployment
spec:
  replicas: 2
  selector:
    matchLabels:
      app: link-consumer
  template:
    metadata:
      labels:
        app: link-consumer
    spec:
      containers:
      - name: link-consumer
        image: link-consumer:latest
        resources:
          requests:
            cpu: "250m"
            memory: "256Mi"
          limits:
            cpu: "2000m"      # Consumer can use more CPU for bulk inserts
            memory: "1Gi"

---
# Consumer HPA (Queue-based)
apiVersion: autoscaling/v2
kind: HorizontalPodAutoscaler
metadata:
  name: link-consumer-hpa
spec:
  scaleTargetRef:
    apiVersion: apps/v1
    kind: Deployment
    name: link-consumer-deployment
  minReplicas: 1
  maxReplicas: 20
  metrics:
  - type: Pods
    pods:
      metric:
        name: rabbitmq_queue_length
      target:
        type: AverageValue
        averageValue: "100"   # Scale when avg queue length > 100
```

---

## 4. Testing Auto-Scaling

### Generate Load:
```bash
# Install hey (Go load generator)
go install github.com/rakyll/hey@latest

# Generate traffic
hey -c 100 -n 100000 http://localhost:30082/code1

# Or use autocannon
autocannon -c 100 -d 60 http://localhost:30082/code1
```

### Watch Scaling:
```bash
# Watch HPA
kubectl get hpa -w

# Watch pods
kubectl get pods -w

# Watch metrics
kubectl top pods
```

### Expected Behavior:
```
TIME         EVENT
00:00        Start with 2 API pods, 1 Consumer pod
00:10        CPU 80% → Scale to 4 API pods
00:20        Queue 150 → Scale to 3 Consumer pods
00:30        Queue 500 → Scale to 6 Consumer pods
00:40        CPU 90% → Scale to 8 API pods
01:00        Traffic drops → Start scaling down
05:00        Stable → 2 API pods, 1 Consumer pod
```

---

## 5. Metrics Server Setup

For HPA to work, you need Metrics Server:

```yaml
# k8s/metrics-server.yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: metrics-server
  namespace: kube-system
spec:
  selector:
    matchLabels:
      k8s-app: metrics-server
  template:
    metadata:
      labels:
        k8s-app: metrics-server
    spec:
      containers:
      - args:
        - --kubelet-insecure-tls
        - --kubelet-preferred-address-type=InternalIP
        image: registry.k8s.io/metrics-server/metrics-server:v0.7.0
        name: metrics-server
```

---

## 6. Auto-Scaling Learning Commands

```bash
# 1. Install metrics-server (required for HPA)
kubectl apply -f https://github.com/kubernetes-sigs/metrics-server/releases/latest/download/components.yaml

# 2. Apply HPA
kubectl apply -f k8s/hpa-api.yaml

# 3. Check HPA status
kubectl get hpa

# 4. Describe HPA details
kubectl describe hpa link-server-hpa

# 5. Generate load and watch
hey -c 50 -n 50000 http://localhost:30082/code1 &
kubectl get hpa -w
kubectl get pods -w

# 6. Check resource usage
kubectl top nodes
kubectl top pods

# 7. View HPA events
kubectl describe hpa | grep -A 10 Events
```

---

## 7. Key Learning Points

| Metric | When to Scale Up | When to Scale Down |
|--------|-----------------|-------------------|
| **CPU** | > 70% for 2 min | < 30% for 5 min |
| **Queue Length** | > 100 messages | < 10 messages |
| **Memory** | > 80% sustained | < 50% for 5 min |
| **Request Rate** | > 1000 RPS/pod | < 100 RPS/pod |

**Golden Rules:**
1. **Scale UP fast** - React quickly to traffic spikes
2. **Scale DOWN slow** - Prevent thrashing
3. **Set proper resource requests** - HPA needs baseline
4. **Use multiple metrics** - CPU + Queue for better decisions
