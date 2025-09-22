# Exercise 1: From App Service Web API to Azure Container Apps

(Formerly the single workshop guide.) This exercise walks you through containerizing a simple Web API, pushing the image to Azure Container Registry (ACR), and deploying it to Azure Container Apps (ACA) with ingress, scaling, and health probes — all using Azure CLI.

> Estimated time: 45–60 minutes

---

_The content is identical to the original workshop. If you later need a shorter version, you can trim sections here._

<!-- BEGIN ORIGINAL CONTENT -->

This hands-on tutorial walks you through containerizing a simple Web API, pushing the image to Azure Container Registry (ACR), and deploying it to Azure Container Apps (ACA) with ingress, scaling, and health probes — all using Azure CLI.

## Prerequisites

- Azure subscription with permission to create resource groups and container resources
- Azure CLI (>= 2.53) installed and logged in:
  ```bash
  az login
  az account show
  ```
- Docker installed and running (for local image build)
- (Optional) jq for nicer JSON parsing

## Environment Variables (Customize These First)
```bash
RESOURCE_GROUP="aca-workshop-rg"
LOCATION="westeurope"
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

## 1. Create / Containerize a Simple Web API
```bash
mkdir src && cd src
cat > Program.cs <<'EOF'
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/", () => Results.Ok(new { message = "Hello from Container Apps!" }));
app.MapGet("/healthz", () => Results.Ok("OK"));
app.MapGet("/slow", async () => { await Task.Delay(500); return Results.Ok("done"); });
app.Run();
EOF

cat > Dockerfile <<'EOF'
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /out
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "src.dll"]
EOF

cat > src.csproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
EOF

dotnet restore
cd ..
```
### Build & Test
```bash
docker build -t ${IMAGE_NAME}:${IMAGE_TAG} ./src
docker run -it --rm -p 8080:8080 ${IMAGE_NAME}:${IMAGE_TAG}
curl -s http://localhost:8080 | jq
curl -s http://localhost:8080/healthz
```
## 2. ACR
```bash
az acr create -n "$ACR_NAME" -g "$RESOURCE_GROUP" --sku Basic --admin-enabled false
az acr login -n "$ACR_NAME"
```
## 3. Push Image
```bash
docker tag ${IMAGE_NAME}:${IMAGE_TAG} ${FULL_IMAGE}
docker push ${FULL_IMAGE}
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
```bash
sed -i 's/Hello from Container Apps!/Hello from Container Apps v2!/' src/Program.cs
docker build -t ${IMAGE_NAME}:v2 ./src
docker tag ${IMAGE_NAME}:v2 ${ACR_NAME}.azurecr.io/${IMAGE_NAME}:v2
docker push ${ACR_NAME}.azurecr.io/${IMAGE_NAME}:v2
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
