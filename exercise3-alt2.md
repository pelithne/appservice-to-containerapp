# Exercise 3 (Alt 2): Secure Container Apps – Managed Identity, Public Ingress, Azure Front Door (Standard), Defender for Cloud

This alternative version uses a **public (external) Container App ingress** instead of an internal-only environment + Private Link. It is suitable for simpler demos or when private networking is not required. For production, prefer the Private Link model in `exercise3.md`.

## Objectives
1. Managed identity (no admin / no ACR password) for image pulls from ACR
2. Store a secret in Azure Key Vault and access at runtime via managed identity
3. Public ingress (external) Container App fronted by Azure Front Door (no Private Link)
4. Enable Microsoft Defender for Cloud plans relevant to ACR, App Services (Container Apps), and Key Vault
5. Validate with curl + identity checks

> NOTE: This variant omits VNet integration and Private Link. Add them later if you need private egress control or internal-only exposure.

Estimated time: 40–55 minutes

---
## Prerequisites
- Azure CLI ≥ 2.53; extensions:
```bash
az extension add --name containerapp --upgrade
az extension add --name front-door --upgrade
```
- RBAC: Ability to create identities, ACR, Key Vault, Front Door, role assignments
- Providers (usually already registered):
```bash
SUBSCRIPTION_ID=$(az account show --query id -o tsv)
az provider register --namespace Microsoft.Network --subscription $SUBSCRIPTION_ID
az provider register --namespace Microsoft.Cdn
```

## Variables
```bash
RESOURCE_GROUP="aca-semisecure-rg"
LOCATION="swedencentral"
ACR_NAME="acasecacr$RANDOM"
ENV_NAME="aca-public-env-alt"        # Public (no --internal-only)
APP_NAME="aca-public-api-alt"
IMAGE_NAME="secureapi"
UAMI_PULL_NAME="aca-acr-pull-uami-alt"
UAMI_APP_NAME="aca-app-uami-alt"
KV_NAME="kvacasec$RANDOM"
SECRET_NAME="SampleSecret-alt"
FRONTDOOR_NAME="fd-aca-public-$RANDOM"
FD_ENDPOINT_NAME="fd-ep-public-alt"
```

Create resource group:
```bash
az group create -n "$RESOURCE_GROUP" -l "$LOCATION"
```

## 1. Container Registry (No Admin User)
```bash
az acr create -n "$ACR_NAME" -g "$RESOURCE_GROUP" --sku Standard --admin-enabled false
```

## 2. Managed Identities
We need two separate identities: one solely for pulling from ACR (principle of least privilege), another for application data access in Key Vault. The commands below create the identities and populates envirnment variables with principal IDs.

```bash
az identity create -g "$RESOURCE_GROUP" -n "$UAMI_PULL_NAME"
az identity create -g "$RESOURCE_GROUP" -n "$UAMI_APP_NAME"
PULL_ID=$(az identity show -g "$RESOURCE_GROUP" -n "$UAMI_PULL_NAME" --query id -o tsv)
PULL_PRINCIPAL=$(az identity show -g "$RESOURCE_GROUP" -n "$UAMI_PULL_NAME" --query principalId -o tsv)
APP_IDENTITY_ID=$(az identity show -g "$RESOURCE_GROUP" -n "$UAMI_APP_NAME" --query id -o tsv)
APP_PRINCIPAL=$(az identity show -g "$RESOURCE_GROUP" -n "$UAMI_APP_NAME" --query principalId -o tsv)
```
Grant AcrPull:
```bash
ACR_ID=$(az acr show -n "$ACR_NAME" -g "$RESOURCE_GROUP" --query id -o tsv)
az role assignment create --assignee-object-id $PULL_PRINCIPAL \
  --assignee-principal-type ServicePrincipal --scope $ACR_ID --role "AcrPull"
```

Create the vault with RBAC (no legacy access policies) and grant a write-capable role to *you* (the operator) **before** seeding the first secret. The application identity will only get read access.

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


## 3. Key Vault + Secret
```bash
az keyvault create -n "$KV_NAME" -g "$RESOURCE_GROUP" -l "$LOCATION" --enable-rbac-authorization true
az keyvault secret set --vault-name "$KV_NAME" -n "$SECRET_NAME" --value "Hello from Key Vault secured by MI!"
az role assignment create --assignee-object-id $APP_PRINCIPAL --assignee-principal-type ServicePrincipal \
  --scope $KV_ID --role "Key Vault Secrets User"
```

## 4. Application Source Code
```bash
mkdir -p secure-api && cd secure-api
cat > Program.cs <<'EOF'
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Azure.Core;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

string kvUrl = Environment.GetEnvironmentVariable("KEYVAULT_URL") ?? throw new("KEYVAULT_URL missing");
string secretName = Environment.GetEnvironmentVariable("SECRET_NAME") ?? "SampleMessage";

var clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
TokenCredential credential = string.IsNullOrEmpty(clientId)
    ? new ManagedIdentityCredential()
    : new ManagedIdentityCredential(clientId);

var secretClient = new SecretClient(new Uri(kvUrl), credential);

app.MapGet("/mi-debug", async () => {
    try {
        var scopes = new[] { "https://vault.azure.net/.default" };
    // Explicitly pass CancellationToken (default) to satisfy method signature in certain Azure.Identity versions
    var token = await credential.GetTokenAsync(new TokenRequestContext(scopes), default);
        return Results.Ok(new { acquired = true, token.ExpiresOn });
    }
    catch (Exception ex)
    {
        return Results.Json(new {
            acquired = false,
            type = ex.GetType().FullName,
            ex.Message,
            stack = ex.StackTrace
        }, statusCode: 500);
    }
});

app.MapGet("/", async () => {
    var secret = await secretClient.GetSecretAsync(secretName);
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

## 5. Remote Build (ACR)
```bash
az acr build -r $ACR_NAME -t ${IMAGE_NAME}:v1 ./secure-api
```

## 6. Create Public Container App Environment
Public environment (no VNet, no internal-only flag):
```bash
az containerapp env create -n "$ENV_NAME" -g "$RESOURCE_GROUP" -l "$LOCATION"
```

## 7. Deploy Container App (External Ingress)
```bash
az containerapp create \
  -n "$APP_NAME" -g "$RESOURCE_GROUP" \
  --environment "$ENV_NAME" \
  --image ${ACR_NAME}.azurecr.io/${IMAGE_NAME}:v1 \
  --registry-server ${ACR_NAME}.azurecr.io \
  --user-assigned $APP_IDENTITY_ID \
  --ingress external --target-port 8080 \
  --min-replicas 1 --max-replicas 3 \
  --cpu 0.25 --memory 0.5Gi \
  --env-vars KEYVAULT_URL="https://$KV_NAME.vault.azure.net/" SECRET_NAME="$SECRET_NAME" \
  --revision-suffix v1
```
Capture FQDN:
```bash
APP_EXTERNAL_FQDN=$(az containerapp show -n "$APP_NAME" -g "$RESOURCE_GROUP" --query properties.configuration.ingress.fqdn -o tsv)
echo $APP_EXTERNAL_FQDN
curl -s https://$APP_EXTERNAL_FQDN/healthz
```
### Dual Managed Identity Configuration (Applied Pattern)

This exercise uses a **dual identity** model:

| Purpose | Identity | Roles |
|---------|----------|-------|
| Image pulls from ACR | Pull identity (`$UAMI_PULL_NAME`) | `AcrPull` on the ACR scope |
| Key Vault secret access | App identity (`$UAMI_APP_NAME`) | `Key Vault Secrets User` on the vault scope |

Both identities are attached to the Container App. The application code sets `ManagedIdentityCredential` with an explicit client ID via the `AZURE_CLIENT_ID` environment variable so the Key Vault calls always use the app identity (and not the pull identity).

Add after identity creation (Step 2) once roles are assigned:
```bash
# Capture IDs (if not already)
PULL_ID=$(az identity show -g "$RESOURCE_GROUP" -n "$UAMI_PULL_NAME" --query id -o tsv)
APP_IDENTITY_ID=$(az identity show -g "$RESOURCE_GROUP" -n "$UAMI_APP_NAME" --query id -o tsv)
APP_CLIENT_ID=$(az identity show -g "$RESOURCE_GROUP" -n "$UAMI_APP_NAME" --query clientId -o tsv)

# Attach / ensure both identities on the Container App (idempotent)
az containerapp identity assign -n "$APP_NAME" -g "$RESOURCE_GROUP" \
  --user-assigned $APP_IDENTITY_ID $PULL_ID

# Designate the pull identity for ACR (post-create)
az containerapp registry set -n "$APP_NAME" -g "$RESOURCE_GROUP" \
  --server ${ACR_NAME}.azurecr.io --identity $PULL_ID

# Inject AZURE_CLIENT_ID so the app selects the Key Vault identity
az containerapp update -n "$APP_NAME" -g "$RESOURCE_GROUP" \
  --set-env-vars AZURE_CLIENT_ID=$APP_CLIENT_ID \
  --revision-suffix setappid
```

After the update, verify:
```bash
curl -s https://$APP_EXTERNAL_FQDN/mi-debug | jq    # should show acquired:true
curl -s https://$APP_EXTERNAL_FQDN/ | jq            # should return secret JSON
```

> If you modify `Program.cs` (e.g., add new routes), you must rebuild with a new image tag and update the Container App image.


## 8. Azure Front Door (Standard/Premium) – Public Origin
Create profile + endpoint:
```bash
az afd profile create -n "$FRONTDOOR_NAME" -g "$RESOURCE_GROUP" --sku Standard_AzureFrontDoor
az afd endpoint create -g "$RESOURCE_GROUP" --profile-name "$FRONTDOOR_NAME" -n "$FD_ENDPOINT_NAME"
```
Origin group + origin (no Private Link required):
```bash
az afd origin-group create -g "$RESOURCE_GROUP" --profile-name "$FRONTDOOR_NAME" \
  --name og-aca --probe-request-type GET --probe-protocol Https --probe-path /healthz \
  --probe-interval-in-seconds 30 --sample-size 1 --successful-samples-required 1 \
  --additional-latency-in-milliseconds 0

az afd origin create -g "$RESOURCE_GROUP" --profile-name "$FRONTDOOR_NAME" \
  --origin-group-name og-aca -n aca-origin \
  --host-name $APP_EXTERNAL_FQDN \
  --https-port 443 --http-port 80 --priority 1 --weight 100
```
Route:
```bash
az afd route create -g "$RESOURCE_GROUP" --profile-name "$FRONTDOOR_NAME" \
  --endpoint-name "$FD_ENDPOINT_NAME" -n route-api \
  --origin-group og-aca \
  --supported-protocols Http Https \
  --patterns-to-match "/*" \
  --forwarding-protocol MatchRequest \
  --link-to-default-domain Enabled
```
Test:
```bash
FD_HOST=$(az afd endpoint show -g "$RESOURCE_GROUP" --profile-name "$FRONTDOOR_NAME" -n "$FD_ENDPOINT_NAME" --query hostName -o tsv)
curl -s https://${FD_HOST}/ | jq
```

(Optional) WAF:
```bash
az afd waf-policy create -g "$RESOURCE_GROUP" -n fd-waf-public --mode Prevention --sku Standard_AzureFrontDoor
az afd route update -g "$RESOURCE_GROUP" --profile-name "$FRONTDOOR_NAME" \
  --endpoint-name "$FD_ENDPOINT_NAME" -n route-api --waf-policy fd-waf-public
```

## 9. Defender for Cloud Plans
```bash
az security pricing create --name ContainerRegistry --tier Standard
az security pricing create --name KeyVaults --tier Standard
az security pricing create --name AppServices --tier Standard
```

(Optional) Vulnerable image demo:
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

List Container Apps assessments:
```bash
az security assessment list \
  --query "[?contains(resources[0].id, 'Microsoft.App/containerApps')].{name:name,status:status.code}" -o table
```

## 10. Validation
```bash
# App identities
az containerapp show -n "$APP_NAME" -g "$RESOURCE_GROUP" --query properties.template.identity -o json

# Direct app call
curl -s https://$APP_EXTERNAL_FQDN/ | jq

# Through Front Door
curl -s https://${FD_HOST}/ | jq
```

## 11. Cleanup
```bash
az group delete -n "$RESOURCE_GROUP" --yes --no-wait
```

---
For a production-grade private pattern, switch to the Private Link workflow in the original exercise.
