# Exercise 1: From App Service Web API to Azure Container Apps (Python Version)

This exercise walks you through containerizing a simple Python FastAPI web API, pushing the image to Azure Container Registry (ACR), and deploying it to Azure Container Apps (ACA) with ingress, scaling, and health probes — all using Azure CLI.

> Estimated time: 45–60 minutes

---

## Prerequisites

- Azure subscription with permission to create resource groups and container resources
- Azure CLI (>= 2.53) or Azure Cloud Shell (no local Docker required)
- Logged in:
  ```bash
  az login
  az account show
  ```
- (Optional) jq for nicer JSON parsing

## Environment Variables
```bash
RESOURCE_GROUP="aca-workshop-rg"
LOCATION="swedencentral"
ACR_NAME="acaworkshop$RANDOM"
APP_NAME="aca-webapi"
ENV_NAME="aca-env"
IMAGE_NAME="webapi"
IMAGE_TAG="v1"
FULL_IMAGE="${ACR_NAME}.azurecr.io/${IMAGE_NAME}:${IMAGE_TAG}"
```
```bash
az group create -n "$RESOURCE_GROUP" -l "$LOCATION"
```

## 1. Create / Containerize a Simple Python FastAPI Web API
Project layout:
```
app/
  ├── main.py
  ├── requirements.txt
  └── Dockerfile
```

If starting from scratch you could create it with:
```bash
mkdir app
cat > app/requirements.txt <<'REQ'
fastapi==0.111.0
uvicorn==0.30.1
pydantic==2.8.2
REQ

cat > app/main.py <<'PY'
from fastapi import FastAPI
from pydantic import BaseModel
import uvicorn, asyncio

app = FastAPI(title="ACA Workshop API", version="1.0.0")

class SlowResponse(BaseModel):
    status: str
    detail: str

@app.get("/")
async def root():
    return {"message": "Hello from Container Apps (Python)!"}

@app.get("/healthz")
async def health():
    return {"status": "OK"}

@app.get("/slow", response_model=SlowResponse)
async def slow():
    await asyncio.sleep(0.5)
    return SlowResponse(status="done", detail="Completed after simulated delay")

if __name__ == "__main__":
    uvicorn.run(app, host="0.0.0.0", port=8080)
PY

cat > app/Dockerfile <<'DOCKER'
FROM python:3.12-slim AS base
ENV PYTHONDONTWRITEBYTECODE=1 \\
    PYTHONUNBUFFERED=1 \\
    PIP_NO_CACHE_DIR=1
WORKDIR /app
COPY app/requirements.txt ./requirements.txt
RUN pip install --no-cache-dir -r requirements.txt
COPY app/ .
EXPOSE 8080
CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8080"]
DOCKER
```

## 2. Create ACR
```bash
az acr create -n "$ACR_NAME" -g "$RESOURCE_GROUP" --sku Basic --admin-enabled false
```
Optional (not required for build tasks):
```bash
az acr login -n "$ACR_NAME"
```
## 3. Build Image with ACR (Cloud Shell Friendly)
Instead of using a local Docker engine we leverage ACR Tasks (`az acr build`). This uploads the build context and builds the image inside Azure. The build both builds AND pushes the image automatically.

```bash
az acr build \
  --registry "$ACR_NAME" \
  --image ${IMAGE_NAME}:${IMAGE_TAG} \
  ./app
```

Verify repository & tag:
```bash
az acr repository show-tags -n "$ACR_NAME" --repository "$IMAGE_NAME" -o table
```
Run locally (optional) only if you have Docker; otherwise skip:
```bash
docker run -it --rm -p 8080:8080 ${ACR_NAME}.azurecr.io/${IMAGE_NAME}:${IMAGE_TAG}
```
## 4. ACA Environment
```bash
az extension add --name containerapp --upgrade
az containerapp env create -n "$ENV_NAME" -g "$RESOURCE_GROUP" -l "$LOCATION"
```
## 5. Deploy App + Probes
```bash
az containerapp create \
  -n "$APP_NAME" -g "$RESOURCE_GROUP" \
  --environment "$ENV_NAME" \
  --image "$FULL_IMAGE" \
  --ingress external --target-port 8080 \
  --registry-server "${ACR_NAME}.azurecr.io" \
  --min-replicas 1 --max-replicas 5 \
  --cpu 0.25 --memory 0.5Gi \
  --revision-suffix v1
```
```bash
az containerapp update -n "$APP_NAME" -g "$RESOURCE_GROUP" --set template.containers[0].probes='[
  {"type":"liveness","httpGet":{"path":"/healthz","port":8080},"initialDelaySeconds":5,"periodSeconds":10},
  {"type":"readiness","httpGet":{"path":"/healthz","port":8080},"initialDelaySeconds":2,"periodSeconds":5}
]'
```
## 6. Scaling
```bash
az containerapp update -n "$APP_NAME" -g "$RESOURCE_GROUP" \
  --scale-rule-name http-concurrency --scale-rule-type http \
  --scale-rule-metadata concurrentRequests=50 \
  --min-replicas 1 --max-replicas 10
```
## 7. New Version
Update the message and rebuild using ACR Tasks:
```bash
sed -i "s/Hello from Container Apps (Python)!/Hello from Container Apps (Python) v2!/" app/main.py
az acr build --registry "$ACR_NAME" --image ${IMAGE_NAME}:v2 ./app
az containerapp update -n "$APP_NAME" -g "$RESOURCE_GROUP" --image ${ACR_NAME}.azurecr.io/${IMAGE_NAME}:v2 --revision-suffix v2
```
## 8. Logs
```bash
az containerapp logs show -n "$APP_NAME" -g "$RESOURCE_GROUP" --follow
```
## 9. Cleanup
```bash
az group delete -n "$RESOURCE_GROUP" --yes --no-wait
```
<!-- END ORIGINAL CONTENT -->
