# Exercise 3: Secure Container Apps – Managed Identity to ACR & Key Vault, Private Egress, Defender for Cloud, Azure Front Door

This exercise layers production‑grade security and networking features onto an Azure Container Apps (ACA) workload. You will:

1. Use a managed identity (no admin / no ACR password) for image pulls from ACR
2. Store a secret in Azure Key Vault and access it from the container at runtime via managed identity
3. Restrict outbound (egress) and make the app private behind an internal environment with a workload profile + VNet (conceptual steps) and expose safely via Azure Front Door
4. Enable Microsoft Defender for Cloud plans relevant to containers & Key Vault
5. Validate everything with test commands

> NOTE: Some steps require specific regional support and/or existing virtual networks. Adjust names & regions accordingly. Where full creation would be long, concise CLI snippets + rationale are provided.

Estimated time: 60–75 minutes (depending on existing network & Front Door readiness)

---
## Prerequisites
- Completion of Exercise 1 (base ACA environment) OR create fresh resources
- Azure CLI ≥ 2.53 and `containerapp` + `front-door` extensions:
  ```bash
  az extension add --name containerapp --upgrade
  az extension add --name front-door --upgrade
  ```
- Permissions: Role assignments (Owner or appropriate RBAC to create identities, networks, Key Vault, Front Door, Defender plans)

## Variables
```bash
RESOURCE_GROUP="aca-secure-rg"
LOCATION="westeurope"                 # Must support ACA + Front Door Standard/Premium
ACR_NAME="acasecacr$RANDOM"          # globally unique
ENV_NAME="aca-secure-env"
APP_NAME="aca-secure-api"
IMAGE_NAME="secureapi"
IMAGE_TAG="v1"
UAMI_PULL_NAME="aca-acr-pull-uami"    # For ACR pull
UAMI_APP_NAME="aca-app-uami"          # For Key Vault secret retrieval
VNET_NAME="aca-secure-vnet"
VNET_CIDR="10.50.0.0/16"
SUBNET_ENV="aca-env-subnet"
SUBNET_ENV_CIDR="10.50.1.0/24"
KV_NAME="kvacasec$RANDOM"             # globally unique (alphanumeric)
SECRET_NAME="SampleMessage"
FRONTDOOR_NAME="fd-aca-secure-$RANDOM"
FD_ENDPOINT_NAME="fd-ep-secure"
APP_FQDN_VAR="APP_FQDN"               # local var to capture ingress FQDN if needed
```

Create resource group:
```bash
az group create -n "$RESOURCE_GROUP" -l "$LOCATION"
```

## 1. Create ACR (No Admin User)
```bash
az acr create -n "$ACR_NAME" -g "$RESOURCE_GROUP" --sku Standard --admin-enabled false
```

## 2. Managed Identities
Two identities: one solely for pulling from ACR (principle of least privilege), another for application data access (Key Vault).
```bash
az identity create -g "$RESOURCE_GROUP" -n "$UAMI_PULL_NAME"
az identity create -g "$RESOURCE_GROUP" -n "$UAMI_APP_NAME"
PULL_ID=$(az identity show -g "$RESOURCE_GROUP" -n "$UAMI_PULL_NAME" --query id -o tsv)
PULL_PRINCIPAL=$(az identity show -g "$RESOURCE_GROUP" -n "$UAMI_PULL_NAME" --query principalId -o tsv)
APP_IDENTITY_ID=$(az identity show -g "$RESOURCE_GROUP" -n "$UAMI_APP_NAME" --query id -o tsv)
APP_PRINCIPAL=$(az identity show -g "$RESOURCE_GROUP" -n "$UAMI_APP_NAME" --query principalId -o tsv)
```

### Grant ACR Pull Role
Use built‑in `AcrPull` role at the minimal scope.
```bash
ACR_ID=$(az acr show -n "$ACR_NAME" -g "$RESOURCE_GROUP" --query id -o tsv)
az role assignment create --assignee-object-id $PULL_PRINCIPAL --assignee-principal-type ServicePrincipal --scope $ACR_ID --role "AcrPull"
```

## 3. Key Vault + Secret
```bash
az keyvault create -n "$KV_NAME" -g "$RESOURCE_GROUP" -l "$LOCATION" --enable-rbac-authorization true
KV_ID=$(az keyvault show -n "$KV_NAME" -g "$RESOURCE_GROUP" --query id -o tsv)
# Add secret
az keyvault secret set --vault-name "$KV_NAME" -n "$SECRET_NAME" --value "Hello from Key Vault secured by MI!"
```

Assign `Key Vault Secrets User` to app identity (data-plane minimal secret get/list):
```bash
az role assignment create \
  --assignee-object-id $APP_PRINCIPAL \
  --assignee-principal-type ServicePrincipal \
  --scope $KV_ID \
  --role "Key Vault Secrets User"
```

(If using purge protection / soft delete defaults, retention is already enabled.)

## 4. (Optional) Private Networking & Restricted Egress
This section shows creating a VNet and placing the Container Apps Environment in an internal subnet (no public ingress). Later we publish through Azure Front Door.

> NOTE: Some networking features may require a dedicated workload profile or specific SKUs; ensure your subscription has necessary providers registered.

```bash
az network vnet create -g "$RESOURCE_GROUP" -n "$VNET_NAME" --address-prefixes $VNET_CIDR \
  --subnet-name $SUBNET_ENV --subnet-prefixes $SUBNET_ENV_CIDR
SUBNET_ID=$(az network vnet subnet show -g "$RESOURCE_GROUP" --vnet-name "$VNET_NAME" -n "$SUBNET_ENV" --query id -o tsv)
```

Create the *internal* ACA environment (internal only ingress):
```bash
az containerapp env create \
  -n "$ENV_NAME" -g "$RESOURCE_GROUP" -l "$LOCATION" \
  --infrastructure-subnet-resource-id $SUBNET_ID \
  --internal-only true
```

At this point outbound egress still defaults via the environment. To fully restrict egress you can use a NAT gateway, Azure Firewall, or Private Endpoints for ACR & Key Vault. Below we illustrate private endpoints conceptually (commands abbreviated)

### (Conceptual) Private Endpoints
```
# Key Vault Private Endpoint (abbreviated pattern)
az network private-endpoint create -g $RESOURCE_GROUP -n kv-pe --vnet-name $VNET_NAME --subnet $SUBNET_ENV \
  --private-connection-resource-id $KV_ID --group-ids vault --connection-name kv-pe-conn

# ACR Private Endpoint
az network private-endpoint create -g $RESOURCE_GROUP -n acr-pe --vnet-name $VNET_NAME --subnet $SUBNET_ENV \
  --private-connection-resource-id $ACR_ID --group-ids registry --connection-name acr-pe-conn
```
You would also manage private DNS zone links. For brevity these are omitted—add zones for `privatelink.vaultcore.azure.net` and `privatelink.azurecr.io` and link to the VNet.

## 5. Application Source Code
Simple API retrieving a secret each request (cached would be better in prod) + returns build info.
```bash
mkdir -p secure-api && cd secure-api
cat > Program.cs <<'EOF'
using Azure.Identity; 
using Azure.Security.KeyVault.Secrets;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

string kvUrl = Environment.GetEnvironmentVariable("KEYVAULT_URL") ?? throw new("KEYVAULT_URL missing");
string secretName = Environment.GetEnvironmentVariable("SECRET_NAME") ?? "SampleMessage";

app.MapGet("/", async () => {
    var client = new SecretClient(new Uri(kvUrl), new DefaultAzureCredential());
    var secret = await client.GetSecretAsync(secretName);
    return Results.Ok(new {
        message = secret.Value.Value,
        time = DateTime.UtcNow,
        hostname = Environment.MachineName
    });
});

app.MapGet("/healthz", () => Results.Ok("OK"));
app.Run();
EOF

cat > secure-api.csproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Azure.Identity" Version="1.11.4" />
    <PackageReference Include="Azure.Security.KeyVault.Secrets" Version="4.7.0" />
  </ItemGroup>
</Project>
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
ENTRYPOINT ["dotnet", "secure-api.dll"]
EOF

dotnet restore
cd ..
```

## 6. Build & Push Image (Using ACR Pull Identity)
```bash
docker build -t ${IMAGE_NAME}:${IMAGE_TAG} ./secure-api
docker tag ${IMAGE_NAME}:${IMAGE_TAG} ${ACR_NAME}.azurecr.io/${IMAGE_NAME}:${IMAGE_TAG}
docker push ${ACR_NAME}.azurecr.io/${IMAGE_NAME}:${IMAGE_TAG}
```

## 7. Deploy Container App
Because environment is internal-only, ingress must be *internal*. We'll front it later with Front Door via an **insecure placeholder** public revision if needed or through a reverse proxy. (Front Door requires a publicly reachable origin; for strict private, use Azure Front Door Premium with Private Link origin – commands simplified.)

```bash
az containerapp create \
  -n "$APP_NAME" -g "$RESOURCE_GROUP" \
  --environment "$ENV_NAME" \
  --image ${ACR_NAME}.azurecr.io/${IMAGE_NAME}:${IMAGE_TAG} \
  --registry-server ${ACR_NAME}.azurecr.io \
  --registry-identity $PULL_ID \
  --user-assigned $APP_IDENTITY_ID \
  --ingress internal --target-port 8080 \
  --min-replicas 1 --max-replicas 3 \
  --cpu 0.25 --memory 0.5Gi \
  --env-vars KEYVAULT_URL="https://$KV_NAME.vault.azure.net/" SECRET_NAME="$SECRET_NAME" \
  --revision-suffix v1
```

Add probes:
```bash
az containerapp update -n "$APP_NAME" -g "$RESOURCE_GROUP" --set template.containers[0].probes='[
 {"type":"liveness","httpGet":{"path":"/healthz","port":8080},"initialDelaySeconds":5,"periodSeconds":15},
 {"type":"readiness","httpGet":{"path":"/healthz","port":8080},"initialDelaySeconds":2,"periodSeconds":5}
]'
```

## 8. Azure Front Door (Standard/Premium)
For simplicity we create Front Door Standard pointing to the app's *internal* FQDN if exposed; in reality for fully private you would use Private Link origin (Premium) or expose via an Azure Application Gateway + Front Door chain.

Get (or temporarily enable) external ingress if you need a reachable origin. (Optional workaround):
```bash
# OPTIONAL: switch to external ingress just to register Front Door origin, then revert
az containerapp update -n "$APP_NAME" -g "$RESOURCE_GROUP" --ingress external --target-port 8080
APP_FQDN=$(az containerapp show -n "$APP_NAME" -g "$RESOURCE_GROUP" --query properties.configuration.ingress.fqdn -o tsv)
```

Create Front Door:
```bash
az network front-door profile create -n "$FRONTDOOR_NAME" -g "$RESOURCE_GROUP" --sku Standard_AzureFrontDoor

# Endpoint
az network front-door endpoint create -g "$RESOURCE_GROUP" --profile-name "$FRONTDOOR_NAME" -n "$FD_ENDPOINT_NAME"

# Origin group
az network front-door origin-group create -g "$RESOURCE_GROUP" --profile-name "$FRONTDOOR_NAME" -n og-aca \
  --probe-request-type GET --probe-protocol HTTPS --probe-path /healthz --probe-interval-in-seconds 30

# Origin (using public FQDN of container app)
az network front-door origin create -g "$RESOURCE_GROUP" --profile-name "$FRONTDOOR_NAME" \
  --origin-group-name og-aca -n aca-origin \
  --host-name $APP_FQDN --http-port 80 --https-port 443 --priority 1 --weight 100

# Route
az network front-door route create -g "$RESOURCE_GROUP" --profile-name "$FRONTDOOR_NAME" \
  -n route-api --endpoint-name "$FD_ENDPOINT_NAME" --origin-group og-aca \
  --supported-protocols Http Https --patterns-to-match "/*" --forwarding-protocol MatchRequest
```

Now (optionally) revert container app ingress to internal-only + use Premium + Private Link for production (not fully scripted here due to complexity).

Retrieve Front Door endpoint:
```bash
FD_HOST=$(az network front-door endpoint show -g "$RESOURCE_GROUP" --profile-name "$FRONTDOOR_NAME" -n "$FD_ENDPOINT_NAME" --query hostName -o tsv)
```

Test:
```bash
curl -s https://${FD_HOST}/ | jq
```

## 9. Microsoft Defender for Cloud Enablement
Enable plan(s) for Container Registries, Container Apps (via App Service plan coverage) and Key Vault.
```bash
SUBSCRIPTION_ID=$(az account show --query id -o tsv)
# Enable container registry protection
az security pricing create --name ContainerRegistry --tier Standard
# Key Vault
az security pricing create --name KeyVaults --tier Standard
# App Services (covers Container Apps runtime layer)
az security pricing create --name AppServices --tier Standard
```
(Plans may already exist; commands are idempotent.)

## 10. Validation
```bash
# Check identities on the app
az containerapp show -n "$APP_NAME" -g "$RESOURCE_GROUP" --query properties.template.identity -o json

# Try secret retrieval through service endpoint
curl -s https://${FD_HOST}/ | jq
```

## 11. (Optional) Lock Down After Front Door
- Revert ACA to internal ingress only.
- Use Front Door Premium + Private Link origin to keep origin private.
- Add WAF policy: `az network front-door waf-policy create ...` and link to route.

## 12. Cleanup
```bash
# Danger: removes all created components
az group delete -n "$RESOURCE_GROUP" --yes --no-wait
```

---
## Design Rationale & Best Practices Summary
- Separate identities for image pull and app operations = least privilege.
- RBAC (AcrPull, Key Vault Secrets User) instead of vault access policies.
- No admin credentials on ACR; all pulls via workload identity.
- Key Vault accessed dynamically; could add local in-memory cache.
- Private networking + Front Door provides global entry + future WAF.
- Defender plans surface image scanning, secret misuse, configuration drift.

## Next Steps
- Add CI/CD pipeline generating image & auto-redeploy (GitHub Actions with OIDC).
- Implement Key Vault secret rotation + caching.
- Use Front Door Premium + Private Link for fully private origin.
- Add rate limiting + WAF custom rules.
- Add Bicep or Terraform infra for declarative reproducibility.

Enjoy securing your containerized workloads!
