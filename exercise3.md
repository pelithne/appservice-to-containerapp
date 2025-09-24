# Exercise 3: Secure Container Apps – Managed Identity to ACR & Key Vault, Private ingress, Azure Front Door, Defender for Cloud

This exercise layers production‑grade security and networking features onto an Azure Container Apps (ACA) workload. You will:

1. Use a managed identity (no admin / no ACR password) for image pulls from ACR
2. Store a secret in Azure Key Vault and access it from the container at runtime via managed identity
3. Restrict outbound (egress) and make the app private and expose safely via Azure Front Door
4. Enable Microsoft Defender for Cloud plans relevant to containers & Key Vault
5. Validate everything with test commands

---
## Prerequisites
- Completion of Exercise 1 (base ACA environment) OR create fresh resources
- Azure CLI ≥ 2.53 and `containerapp` + `front-door` extensions:
  ```bash
  az extension add --name containerapp --upgrade
  az extension add --name front-door --upgrade
  ```
- Permissions: Role assignments (Owner or appropriate RBAC to create identities, networks, Key Vault, Front Door, Defender plans)

- Feature Registration: Microsoft.Network/AllowPrivateEndpoints
For a later step, you will need the ````Microsoft.Cdn```` and ````Microsoft.Network```` providers registered, as well as the ````AllowPrivateEndpoints```` feature. To avoid waiting time (registration can take a while), go ahead and register it right away. 

```bash
SUBSCRIPTION_ID=$(az account show --query id -o tsv)
az provider register --namespace Microsoft.Network --subscription $SUBSCRIPTION_ID
az provider register --namespace Microsoft.Cdn --subscription $SUBSCRIPTION_ID
```

## Variables

Create some environment variables, to use in subsequent commands.
```bash
RESOURCE_GROUP="aca-secure-rg"
LOCATION="swedencentral"            # Must support ACA + Front Door Standard/Premium
ACR_NAME="acasecacr$RANDOM"         # globally unique
ENV_NAME="aca-secure-env"           # Container app environment name
APP_NAME="aca-secure-api"           # Name of the container app
IMAGE_NAME="secureapi"              # Image name to use in ACR
UAMI_PULL_NAME="aca-acr-pull-uami"  # For ACR pull
UAMI_APP_NAME="aca-app-uami"        # For Key Vault secret retrieval
VNET_NAME="aca-secure-vnet"         # Vnet name for container app vnet
VNET_CIDR="10.50.0.0/16"            # Network range for container app vnet
SUBNET_ENV="aca-env-subnet"         # Container app dedicated subnet name
SUBNET_ENV_CIDR="10.50.1.0/24"      # Container app dedicated subnet range
KV_NAME="kvacasec$RANDOM"           # globally unique and alphanumeric
SECRET_NAME="SampleSecret"          # Name of example secret
FRONTDOOR_NAME="fd-aca-secure-$RANDOM" # Unique name for frontdoor
FD_ENDPOINT_NAME="fd-ep-secure"    # Frontdoor endpoint name
APP_FQDN_VAR="APP_FQDN"            # local var to capture ingress FQDN if 
AFD_ORIGIN_GROUP="og-aca"          # Frontdoor origin group
```

Create resource group
```bash
az group create -n "$RESOURCE_GROUP" -l "$LOCATION"
```

## 1. Create ACR (No Admin User)
Create a new standard tier Azure Container Registry. Disable admin access (username/password based access)

```bash
az acr create -n "$ACR_NAME" -g "$RESOURCE_GROUP" --sku Standard --admin-enabled false
```

## 2. Managed Identities
Create identity for the container app to fetch secret from Key Vault
```bash
az identity create -g "$RESOURCE_GROUP" -n "$UAMI_APP_NAME"
APP_IDENTITY_ID=$(az identity show -g "$RESOURCE_GROUP" -n "$UAMI_APP_NAME" --query id -o tsv)
APP_PRINCIPAL=$(az identity show -g "$RESOURCE_GROUP" -n "$UAMI_APP_NAME" --query principalId -o tsv)
APP_CLIENT_ID=$(az identity show -g "$RESOURCE_GROUP" -n "$UAMI_APP_NAME" --query clientId -o tsv)
```

### Key Vault + Secret
Create the vault with RBAC (no legacy access policies) and grant a write-capable role to *you* (the operator) **before** seeding the first secret. This is to make you able to create a secret in the key vault

The application identity will only get read access, with the ````Key Vault Secrets User```` role.

First create the Key Vault and populate $KV_ID with the keyvault identity:
```bash
az keyvault create -n "$KV_NAME" -g "$RESOURCE_GROUP" -l "$LOCATION" --enable-rbac-authorization true
KV_ID=$(az keyvault show -n "$KV_NAME" -g "$RESOURCE_GROUP" --query id -o tsv)
```
Grant yourself (signed-in user) least-privilege write (Secrets Officer) so you can set the seed secret

````bash
MY_OID=$(az ad signed-in-user show --query id -o tsv)
az role assignment create \
  --assignee-object-id $MY_OID \
  --assignee-principal-type User \
  --role "Key Vault Secrets Officer" \
  --scope $KV_ID
````

Create the secret in keyvault. If the command fails, it is most likely because the role assignment has not propagated. If that happens, wait a minute and try again.

````bash
az keyvault secret set --vault-name "$KV_NAME" -n "$SECRET_NAME" --value "Hello from Key Vault secured by MI!"
````

Role purpose quick reference:

| Role | Primary Use | Key Capabilities | Avoid Using When |
|------|-------------|------------------|------------------|
| Key Vault Secrets User | Application runtime (read) | Get, List secrets | You need to set / delete secrets |
| Key Vault Secrets Officer | Seeding & rotation ops | Get, List, Set, Delete secrets (no RBAC mgmt) | You must manage RBAC / purge | 
| Key Vault Administrator | Break-glass / full admin | All secret/keys/certs ops + RBAC assignments & purge | Routine secret rotation (over-privileged) |

> Recommendation: Grant yourself *Secrets Officer* temporarily and remove it after seeding/rotation if adhering to just-in-time access.

Assign `Key Vault Secrets User` to app identity (data-plane minimal secret get/list):
```bash
az role assignment create \
  --assignee-object-id $APP_PRINCIPAL \
  --assignee-principal-type ServicePrincipal \
  --scope $KV_ID \
  --role "Key Vault Secrets User"
```


Create the  ACA environment (internal only ingress). If this fails, it could be that the subnet delegation has not completed. If so, wait a minute or two and try again.

````bash
az containerapp env create \
  -n "$ENV_NAME" \
  -g "$RESOURCE_GROUP" \
  -l "$LOCATION"
````

Retrieve the environment ID. You use this ID to configure the environment.

````bash
ENV_ID=$(az containerapp env show \
    --resource-group $RESOURCE_GROUP \
    --name $ENV_NAME \
    --query "id" \
    --output tsv)
````

Disable public network access for the environment

This command requires that you have preview enabled on the containerapp commands

````bash
az extension add --name containerapp --upgrade --allow-preview true
````

Now, go ahead and disable public network access
````bash
az containerapp env update \
    --id $ENV_ID \
    --public-network-access Disabled
````

At this point outbound egress still defaults via the environment. To fully restrict egress you could use a NAT gateway, Azure Firewall, or Private Endpoints for ACR & Key Vault. This is for another exercise though.


## 5. Application Source Code
Before crating the caontainer app, we need to have some code. Below is a simple API that retrieves a secret for each request coming in (cached would be better in prod) + returns that info. Notice that all the code is inline, so there is no need to clone a repo to get files into your file system.
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

## 6. Build & Push Image (Using ACR Remote Build)
Instead of building locally and pushing with Docker, leverage ACR’s cloud build service so you avoid needing a local Docker daemon (useful for CI or constrained dev boxes). 

```bash
az acr build \
  -r $ACR_NAME \
  -t ${IMAGE_NAME}:v1 \
  ./secure-api
```
This uploads the context in `./secure-api`, builds in Azure, and stores the image at:
`${ACR_NAME}.azurecr.io/${IMAGE_NAME}:v1`



## 7. Deploy Container App
Because environment is internal-only, we'll front it later with Front Door to expose the app to the public internet.

```bash
az containerapp create \
  -n "$APP_NAME" -g "$RESOURCE_GROUP" \
  --environment "$ENV_NAME" \
  --image ${ACR_NAME}.azurecr.io/${IMAGE_NAME}:v1 \
  --registry-server ${ACR_NAME}.azurecr.io \
  --user-assigned $APP_CLIENT_ID \
  --ingress external \
  --target-port 8080 \
  --min-replicas 1 --max-replicas 3 \
  --cpu 0.25 --memory 0.5Gi \
  --env-vars KEYVAULT_URL="https://$KV_NAME.vault.azure.net/" SECRET_NAME="$SECRET_NAME" \
  --revision-suffix v1
```


Retrive the container app endpoint. This will be used to create an origin for Azure Front Door.
````bash
ACA_ENDPOINT=$(az containerapp show \
    --name $APP_NAME \
    --resource-group $RESOURCE_GROUP \
    --query properties.configuration.ingress.fqdn \
    --output tsv)
````




## 8. Azure Front Door Premium with Private Link (No Public Ingress Required)
The Container App ingress is internal-only, so in order to expose it we will use **Azure Front Door Premium** using a Private Link origin. This also means that we give our app a global reach through the CDN capabilities of Azure Front Door.

> Prerequisites: Front Door Premium SKU, region support for Private Link to Container Apps, and your subscription registered with `Microsoft.Network` & `Microsoft.Cdn` providers.


### 8.1 Create Front Door Premium Profile & Endpoint
Use the modern `az afd` (Azure Front Door) command group (the older `az network front-door` form is deprecated and not installed by default).

This step can take up to 5 minutes, so maybe take a leg-strecher.

```bash
az afd profile create -n "$FRONTDOOR_NAME" -g "$RESOURCE_GROUP" --sku Premium_AzureFrontDoor

```

Next, create a **Front Door Endpoint**
```bash
az afd endpoint create \
  -g "$RESOURCE_GROUP" --profile-name "$FRONTDOOR_NAME" -n "$FD_ENDPOINT_NAME"
```

Create Origin Group (Health Probe over HTTPS)
```bash
az afd origin-group create \
  --resource-group "$RESOURCE_GROUP" \
  --profile-name "$FRONTDOOR_NAME" \
  --name $AFD_ORIGIN_GROUP \
  --probe-request-type GET \
  --probe-protocol Https \
  --probe-path /healthz \
  --probe-interval-in-seconds 30 \
  --sample-size 1 \
  --successful-samples-required 1 \
  --additional-latency-in-milliseconds 0
```

Add Private Link Origin for Container App. We need the internal FQDN for the app and we also need the ID of the container app environment.
```bash
APP_INTERNAL_FQDN=$(az containerapp show -n "$APP_NAME" -g "$RESOURCE_GROUP" --query properties.configuration.ingress.fqdn -o tsv)
ENV_ID=$(az containerapp env show --resource-group $RESOURCE_GROUP --name $ENV_NAME --query "id" --output tsv)

```

Now, create the origin with Private Link enabled. This step usually takes a bit of time. Leg-stretcher?
```bash
az afd origin create \
  --resource-group "$RESOURCE_GROUP" \
  --profile-name "$FRONTDOOR_NAME" \
  --origin-name "bapporigin" \
  --origin-group-name $AFD_ORIGIN_GROUP \
  --host-name $APP_INTERNAL_FQDN \
  --origin-host-header $APP_INTERNAL_FQDN \
  --priority 1 --weight 100 \
  --enable-private-link true \
  --private-link-resource $ENV_ID \
  --private-link-sub-resource managedEnvironments \
  --private-link-location "$LOCATION" \
  --private-link-request-message "Front Door access to Container App"
```

Now, you need to approve the Private Endpoint Connection, so that the container app allows connections from front door.
````bash
PEC_ID=$(az network private-endpoint-connection list --id $ENV_ID --query "[0].id" -o tsv)
az network private-endpoint-connection approve --id $PEC_ID --description "Approve Front Door"
````

### 8.5 Create Route

Azure front door needs a route to the backend. In this case, anything on http and https is routed to the container app.
```bash
az afd route create \
  --resource-group "$RESOURCE_GROUP" \
  --profile-name "$FRONTDOOR_NAME" \
  --endpoint-name "$FD_ENDPOINT_NAME" \
  --route-name "route-api2" \
  --origin-group $AFD_ORIGIN_GROUP \
  --supported-protocols Http Https \
  --https-redirect Enabled \
  --forwarding-protocol MatchRequest \
  --link-to-default-domain Enabled
```

### 8.6 Retrieve Front Door Host & Test
```bash
FD_HOST=$(az afd endpoint show -g "$RESOURCE_GROUP" --profile-name "$FRONTDOOR_NAME" -n "$FD_ENDPOINT_NAME" --query hostName -o tsv)

```




curl the endpoint: 
````bash
curl -s https://${FD_HOST}/healthz | jq
````


If you receive 502 initially, the private endpoint may still be provisioning—wait 1–2 minutes and retry.

### Optional: WAF Policy (Recommended)
```bash
az afd waf-policy create -g "$RESOURCE_GROUP" -n fd-waf-secure --mode Prevention --sku Premium_AzureFrontDoor
az afd route update -g "$RESOURCE_GROUP" --profile-name "$FRONTDOOR_NAME" \
  --endpoint-name "$FD_ENDPOINT_NAME" -n route-api --waf-policy fd-waf-secure
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



#### E. Simulate a Vulnerable Image (Optional Demo)
Add (intentionally) an outdated package to produce medium/high CVEs:
```bash
cat > secure-api/Dockerfile.vuln <<'EOF'
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet add package Newtonsoft.Json --version 12.0.1 >/dev/null 2>&1 || true
RUN dotnet publish -c Release -o /out
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "secure-api.dll"]
EOF

az acr build -r $ACR_NAME -t ${IMAGE_NAME}:vuln-demo -f secure-api/Dockerfile.vuln ./secure-api
```
After build completes, wait ~5–10 min then re-run the manifest metadata step for `vuln-demo` tag to see extra findings.

#### F. Container App Runtime Posture
Container Apps runtime (control plane / underlying App Service) inherits recommendations under the `AppServices` coverage. Look at any active assessments for configuration drift, TLS, or diagnostic logging:
```bash
az security assessment list \
  --query "[?contains(resources[0].id, 'Microsoft.App/containerApps')].{name:name,status:status.code}" -o table
```
(If empty, no immediate posture recommendations yet.)



#### I. Cleaning Up Demo Artifacts
If you built a vulnerable demo image and want to remove it:
```bash
az acr repository delete -n $ACR_NAME --image ${IMAGE_NAME}:vuln-demo --yes
```

---
At this point you have: image vulnerability insights, Key Vault hardening signals, and (optionally) a simulated vulnerable image to illustrate remediation workflow.


## 10. Validation
```bash
# Check identities on the app
az containerapp show -n "$APP_NAME" -g "$RESOURCE_GROUP" --query properties.template.identity -o json

# Try secret retrieval through service endpoint
curl -s https://${FD_HOST}/ | jq
```


## 12. Cleanup
```bash
# Danger: removes all created components
az group delete -n "$RESOURCE_GROUP" --yes --no-wait
```


