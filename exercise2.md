# Exercise 2: Azure Container Apps + Azure OpenAI via Managed Identity

In this exercise you will deploy a minimal API that calls an Azure OpenAI GPT model using a user-assigned managed identity (UAMI) so no API keys are stored in code or configuration.

> Goal: Show a secure pattern for server-side prompt completion using Azure AD (Entra ID) authentication.
> Estimated time: 40 minutes

## Architecture Overview
- Azure Container Apps hosts a .NET 8 minimal API.
- A User Assigned Managed Identity is bound to the Container App.
- The API obtains an AAD token for `https://cognitiveservices.azure.com/.default` and calls the Azure OpenAI endpoint.
- The client sends a prompt; the server streams back the completion (simplified to single response for brevity).

## Prerequisites
- Completion of Exercise 1 (you may reuse the resource group & env) OR create new.
- Azure CLI ≥ 2.53 and `containerapp` extension.
- You have (or will create) an Azure OpenAI resource + model deployment (e.g. `gpt-4o-mini` or `gpt-4o`) in a supported region.
- Your account has permissions to assign roles: `Cognitive Services OpenAI User` role on the Azure OpenAI resource for the managed identity.

> If you do NOT yet have an Azure OpenAI resource or model deployment, follow the next two sections to provision them. If you already have both, skip to **Set Variables**.

### (Optional) Create Azure OpenAI Resource
Pick a region that supports the model family you want (portal lists availability). Example below uses `swedencentral` — adjust as needed.

```bash
AOAI_RESOURCE_GROUP="openai-rg"
AOAI_LOCATION="swedencentral"           # Must be a region that has Azure OpenAI capacity for your target model
AOAI_ACCOUNT_NAME="aoai$RANDOM"        # Globally unique

az group create -n "$AOAI_RESOURCE_GROUP" -l "$AOAI_LOCATION"

az cognitiveservices account create \
  -n "$AOAI_ACCOUNT_NAME" -g "$AOAI_RESOURCE_GROUP" \
  -l "$AOAI_LOCATION" --kind OpenAI --sku S0 \
  --custom-domain "$AOAI_ACCOUNT_NAME" --yes

az cognitiveservices account show -n "$AOAI_ACCOUNT_NAME" -g "$AOAI_RESOURCE_GROUP" \
  --query '{name:name,location:location,provisioning:properties.provisioningState}' -o table
```

### (Optional) Deploy a Model (Create a Deployment)
List available base models (filtered to GPT-ish examples):
```bash
az cognitiveservices account list-models -n "$AOAI_ACCOUNT_NAME" -g "$AOAI_RESOURCE_GROUP" \
  --query "[?contains(modelId,'gpt')].{model:modelId,format:format,capabilities:capabilities}" -o table
```

Create a deployment (example: `gpt-4o-mini`):
```bash
AOAI_DEPLOYMENT="gpt4o-mini"
BASE_MODEL="gpt-4o-mini"        # Must match a modelId from the list above
MODEL_VERSION="2024-08-06"      # Use a valid version for the selected model

az cognitiveservices account deployment create \
  -g "$AOAI_RESOURCE_GROUP" -n "$AOAI_ACCOUNT_NAME" \
  --deployment-name "$AOAI_DEPLOYMENT" \
  --model-name "$BASE_MODEL" \
  --model-version "$MODEL_VERSION" \
  --model-format OpenAI \
  --sku-name Standard \
  --capacity 1

az cognitiveservices account deployment show \
  -g "$AOAI_RESOURCE_GROUP" -n "$AOAI_ACCOUNT_NAME" \
  --deployment-name "$AOAI_DEPLOYMENT" \
  --query '{deployment:name,model:properties.model.name,status:properties.provisioningState}' -o table
```

> Provisioning tips:
> - If capacity is constrained, try a smaller model (mini) or a different region.
> - You only need one deployment for this exercise.

## Set Variables
Update placeholders beginning with `AOAI_` to match either the resource & deployment you just created above or an existing one.
```bash
RESOURCE_GROUP="aca-workshop-rg"          # Reuse or create
LOCATION="westeurope"                    # Must support both ACA and your Azure OpenAI model
ENV_NAME="aca-env"                        # Reuse from exercise 1 if desired
APP_NAME="aca-openai-api"
ACR_NAME="acaworkshop$RANDOM"            # If you need a new registry
IMAGE_NAME="openaiapi"
IMAGE_TAG="v1"
UAMI_NAME="aca-openai-uami"
AOAI_RESOURCE_GROUP="<openai-rg>"         # Resource group containing your Azure OpenAI
AOAI_ACCOUNT_NAME="<openai-account-name>" # Name of Azure OpenAI resource
AOAI_DEPLOYMENT="<model-deployment-name>" # Model deployment (e.g. gpt-4o-mini)
AOAI_API_VERSION="2024-08-01-preview"     # Use a supported api-version
```

Create (or ensure) resource group:
```bash
az group create -n "$RESOURCE_GROUP" -l "$LOCATION"
```

## (Optional) Create ACR & Login
Skip if you already have one from Exercise 1.
```bash
az acr create -n "$ACR_NAME" -g "$RESOURCE_GROUP" --sku Basic --admin-enabled false
az acr login -n "$ACR_NAME"
```

## Create User Assigned Managed Identity
```bash
az identity create -g "$RESOURCE_GROUP" -n "$UAMI_NAME"
UAMI_ID=$(az identity show -g "$RESOURCE_GROUP" -n "$UAMI_NAME" --query id -o tsv)
UAMI_CLIENT_ID=$(az identity show -g "$RESOURCE_GROUP" -n "$UAMI_NAME" --query clientId -o tsv)
```

## Assign Azure OpenAI Role (RBAC for Managed Identity)
Grant the managed identity the least-privilege role needed to invoke completions: `Cognitive Services OpenAI User`.
```bash
OPENAI_ID=$(az resource show -g "$AOAI_RESOURCE_GROUP" -n "$AOAI_ACCOUNT_NAME" --resource-type "Microsoft.CognitiveServices/accounts" --query id -o tsv)
az role assignment create \
  --assignee-object-id $(az identity show -g "$RESOURCE_GROUP" -n "$UAMI_NAME" --query principalId -o tsv) \
  --assignee-principal-type ServicePrincipal \
  --role "Cognitive Services OpenAI User" \
  --scope "$OPENAI_ID"
```
> Role assignment propagation may take 30–120 seconds. If you get 403s immediately after deployment, wait and retry.

## Create Source Code
```bash
mkdir -p openai-api && cd openai-api
cat > Program.cs <<'EOF'
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Config from env vars (injected into container app)
string openAiEndpoint = Environment.GetEnvironmentVariable("AOAI_ENDPOINT") ?? throw new("AOAI_ENDPOINT missing");
string deployment = Environment.GetEnvironmentVariable("AOAI_DEPLOYMENT") ?? throw new("AOAI_DEPLOYMENT missing");
string apiVersion = Environment.GetEnvironmentVariable("AOAI_API_VERSION") ?? "2024-08-01-preview";

// DefaultAzureCredential will leverage the managed identity inside Container Apps
TokenCredential credential = new DefaultAzureCredential();
HttpClient http = new();

app.MapPost("/api/complete", async (PromptRequest req) => {
    if (string.IsNullOrWhiteSpace(req.Prompt)) return Results.BadRequest("Prompt required");

    // Acquire token for Azure OpenAI (Cognitive Services scope)
    var token = await credential.GetTokenAsync(new TokenRequestContext(new[] { "https://cognitiveservices.azure.com/.default" }));
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

    var payload = new {
        input = req.Prompt,
        temperature = 0.2,
        max_output_tokens = 256
    };

    var url = $"{openAiEndpoint}openai/deployments/{deployment}/responses?api-version={apiVersion}";
    var json = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    using var resp = await http.PostAsync(url, json);
    if (!resp.IsSuccessStatusCode)
    {
        var err = await resp.Content.ReadAsStringAsync();
        return Results.Problem(title: "OpenAI call failed", detail: err, statusCode: (int)resp.StatusCode);
    }

    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    // Basic extraction of the first text output (schema may evolve)
    var text = doc.RootElement
        .GetProperty("output")
        .GetProperty("[0]"); // fallback simplified – adapt to latest schema if needed

    return Results.Ok(new { completion = text.ToString() });
});

app.MapGet("/healthz", () => Results.Ok("OK"));

app.Run();

record PromptRequest(string Prompt);
EOF

cat > openai-api.csproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Azure.Identity" Version="1.11.4" />
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
ENTRYPOINT ["dotnet", "openai-api.dll"]
EOF

dotnet restore
cd ..
```

## Build & Push Image
```bash
docker build -t ${IMAGE_NAME}:${IMAGE_TAG} ./openai-api
docker tag ${IMAGE_NAME}:${IMAGE_TAG} ${ACR_NAME}.azurecr.io/${IMAGE_NAME}:${IMAGE_TAG}
docker push ${ACR_NAME}.azurecr.io/${IMAGE_NAME}:${IMAGE_TAG}
```

## Ensure ACA Environment Exists
```bash
az extension add --name containerapp --upgrade
az containerapp env show -n "$ENV_NAME" -g "$RESOURCE_GROUP" >/dev/null 2>&1 || \
az containerapp env create -n "$ENV_NAME" -g "$RESOURCE_GROUP" -l "$LOCATION"
```

## Deploy Container App (Attach Managed Identity)
```bash
az containerapp create \
  -n "$APP_NAME" -g "$RESOURCE_GROUP" \
  --environment "$ENV_NAME" \
  --image ${ACR_NAME}.azurecr.io/${IMAGE_NAME}:${IMAGE_TAG} \
  --ingress external --target-port 8080 \
  --registry-server ${ACR_NAME}.azurecr.io \
  --user-assigned $UAMI_ID \
  --cpu 0.5 --memory 1Gi \
  --min-replicas 1 --max-replicas 3 \
  --env-vars AOAI_ENDPOINT="https://${AOAI_ACCOUNT_NAME}.openai.azure.com/" AOAI_DEPLOYMENT="$AOAI_DEPLOYMENT" AOAI_API_VERSION="$AOAI_API_VERSION" \
  --revision-suffix v1
```

## Add Health Probes
```bash
az containerapp update -n "$APP_NAME" -g "$RESOURCE_GROUP" --set template.containers[0].probes='[
 {"type":"liveness","httpGet":{"path":"/healthz","port":8080},"initialDelaySeconds":5,"periodSeconds":15},
 {"type":"readiness","httpGet":{"path":"/healthz","port":8080},"initialDelaySeconds":2,"periodSeconds":5}
]'
```

> NOTE: Direct `--set template.containers[0].probes=...` can fail in some CLI versions for deep objects. If you hit validation errors, export to YAML (`az containerapp show -n "$APP_NAME" -g "$RESOURCE_GROUP" -o yaml > app.yaml`), add probes under `template.containers[].probes`, then `az containerapp update -n "$APP_NAME" -g "$RESOURCE_GROUP" --yaml app.yaml`.

## Test the Endpoint
```bash
APP_URL=$(az containerapp show -n "$APP_NAME" -g "$RESOURCE_GROUP" --query properties.configuration.ingress.fqdn -o tsv)
curl -s https://${APP_URL}/healthz

curl -s -X POST https://${APP_URL}/api/complete \
  -H "Content-Type: application/json" \
  -d '{"prompt":"Provide a fun fact about container security."}' | jq
```
If you see an authorization error ensure the role assignment has propagated (can take a minute). Retry after ~60s.

## Troubleshooting
| Symptom | Likely Cause | Fix |
|---------|--------------|-----|
| 403 Forbidden from OpenAI endpoint | Role assignment not propagated | Wait 1–3 minutes, retry |
| 404 Deployment not found | Wrong `AOAI_DEPLOYMENT` or region mismatch | List deployments: `az cognitiveservices account deployment list -n "$AOAI_ACCOUNT_NAME" -g "$AOAI_RESOURCE_GROUP" -o table` |
| 429 / Throttling | Capacity or rate limits | Lower concurrency, scale out replicas, reduce token usage |
| Model not in list | Region lacks that model | Pick a supported model from `list-models` output |
| `ManagedIdentityCredential` fails locally | Running outside Azure | Use `az login` or set `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET` for a service principal (dev only) |

Quick check of effective revision & image:
```bash
az containerapp revision list -n "$APP_NAME" -g "$RESOURCE_GROUP" --query '[].{rev:name,img:properties.template.containers[0].image}' -o table
```

Log only errors (grep style):
```bash
az containerapp logs show -n "$APP_NAME" -g "$RESOURCE_GROUP" --tail 200 | grep -i "problem\|fail\|error" || true
```

## Observing Logs
```bash
az containerapp logs show -n "$APP_NAME" -g "$RESOURCE_GROUP" --follow
```

## Scaling Considerations
For chat-style traffic you might scale on HTTP concurrency:
```bash
az containerapp update -n "$APP_NAME" -g "$RESOURCE_GROUP" \
  --scale-rule-name http-conc --scale-rule-type http \
  --scale-rule-metadata concurrentRequests=20 \
  --min-replicas 1 --max-replicas 10
```

## Security Notes
- No API keys stored; access via managed identity + RBAC.
- Consider adding a front-end (Static Web App) that calls this API.
- Add rate limiting or caching layer for production scenarios.
- Use private networks / VNet integration if restricting public access.

## Cleanup (This App Only)
```bash
az containerapp delete -n "$APP_NAME" -g "$RESOURCE_GROUP" --yes
# Optionally delete identity if no longer used
az identity delete -g "$RESOURCE_GROUP" -n "$UAMI_NAME"
```

---
You have deployed a secure Azure OpenAI-backed API using Azure Container Apps and managed identity.
