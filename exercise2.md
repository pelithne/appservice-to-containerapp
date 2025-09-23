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
- Your account has permissions to assign roles: `Cognitive Services OpenAI User` role on the Azure OpenAI resource for the managed identity.

### Clone the repo
If you have not already, make sure to clone the repository:

```bash
git clone https://github.com/pelithne/appservice-to-containerapp.git
cd appservice-to-containerapp
ls -1 app
```

### Create Azure OpenAI Resource
Pick a region that supports the model family you want (portal lists availability). Example below uses `swedencentral` — adjust as needed.

Start by setting some environment variables

```bash
AOAI_RESOURCE_GROUP="openai-rg"
AOAI_LOCATION="swedencentral"           # Must be a region that has Azure OpenAI capacity for your target model
AOAI_ACCOUNT_NAME="aoai$RANDOM"         # Globally unique
```

Then create the Resource Group for the Azure Open AI Service

````bash

az group create -n "$AOAI_RESOURCE_GROUP" -l "$AOAI_LOCATION"

````

Now go ahead and create the cognitive services account, and validate by using ````az cognitiveservices account show ````

````bash

az cognitiveservices account create \
  -n "$AOAI_ACCOUNT_NAME" -g "$AOAI_RESOURCE_GROUP" \
  -l "$AOAI_LOCATION" --kind OpenAI --sku S0 \
  --custom-domain "$AOAI_ACCOUNT_NAME" --yes

az cognitiveservices account show -n "$AOAI_ACCOUNT_NAME" -g "$AOAI_RESOURCE_GROUP" \
  --query '{name:name,location:location,provisioning:properties.provisioningState}' -o table
```

### Deploy a Model (Create a Deployment)
First discover models and versions actually available to YOUR subscription in THIS region (they vary):
```bash
# Raw list (can be long)
az cognitiveservices account list-models -n "$AOAI_ACCOUNT_NAME" -g "$AOAI_RESOURCE_GROUP" -o table

```

Select deployment name & capacity. Azure OpenAI currently distinguishes between legacy `Standard` and pooled `GlobalStandard` SKUs. Some newer GPT-4o variants ONLY support `GlobalStandard`. If a deployment fails with `DeploymentModelNotSupported` try switching the SKU or a different version.

Create the deployment using gpt4o-mini. First define som more environment variables:
```bash
AOAI_DEPLOYMENT="gpt4o-mini"                # Your deployment handle (free-form, lowercase recommended)
BASE_MODEL=${BASE_MODEL:-gpt-4o-mini}       # Ensure set from above
MODEL_VERSION=${MODEL_VERSION:-2024-07-18}  # Fallback if auto-pick not used
DEPLOY_SKU="GlobalStandard"                 # Try GlobalStandard first for GPT-4o family
CAPACITY=30                                 # GlobalStandard often needs higher min capacity; reduce if allowed
```

The create the cognitiveservices deployment

````bash

az cognitiveservices account deployment create \
  -g "$AOAI_RESOURCE_GROUP" -n "$AOAI_ACCOUNT_NAME" \
  --deployment-name "$AOAI_DEPLOYMENT" \
  --model-name "$BASE_MODEL" \
  --model-version "$MODEL_VERSION" \
  --model-format OpenAI \
  --sku-name $DEPLOY_SKU \
  --capacity $CAPACITY

# If you get DeploymentModelNotSupported, retry with:
#   1) A version explicitly listed in the versions output above
#   2) A different SKU (Standard vs GlobalStandard)
#   3) A different region that lists the desired version

````

Make sure all looks right: 

````bash
az cognitiveservices account deployment show \
  -g "$AOAI_RESOURCE_GROUP" -n "$AOAI_ACCOUNT_NAME" \
  --deployment-name "$AOAI_DEPLOYMENT" \
  --query '{deployment:name,model:properties.model.name,version:properties.model.version,sku:sku.name,status:properties.provisioningState}' -o table
````

## Create Container App

Now its time to create the container app that will use the Azure OpenAI resource. We start out by creating yet another collection of environment variables:

```bash
RESOURCE_GROUP="aca-rg"               # Reuse or create
LOCATION="westeurope"                 # Must support both ACA and your Azure OpenAI model
ENV_NAME="aca-env"                    # Reuse from exercise 1 if desired
APP_NAME="aca-openai-api"
ACR_NAME="acaworkshop$RANDOM"         # If you need a new registry
IMAGE_NAME="openaiapi"
IMAGE_TAG="v1"
UAMI_NAME="aca-openai-uami"
AOAI_API_VERSION="2024-08-01-preview" # Use a supported api-version
```

Create resource group for container app:
```bash
az group create -n "$RESOURCE_GROUP" -l "$LOCATION"
```

Then create the Azure Container registry

```bash
az acr create -n "$ACR_NAME" -g "$RESOURCE_GROUP" --sku Basic --admin-enabled false
```

After this, create User Assigned Managed Identity

```bash
az identity create -g "$RESOURCE_GROUP" -n "$UAMI_NAME"
UAMI_ID=$(az identity show -g "$RESOURCE_GROUP" -n "$UAMI_NAME" --query id -o tsv)
UAMI_CLIENT_ID=$(az identity show -g "$RESOURCE_GROUP" -n "$UAMI_NAME" --query clientId -o tsv)
UAMI_PRINCIPAL_ID=$(az identity show -g "$RESOURCE_GROUP" -n "$UAMI_NAME" --query principalId -o tsv)
```

### Grant the Managed Identity ACR Pull Permission
Unlike AKS (where `az aks update -n CLUSTER -g RG --attach-acr ACR_NAME` wires up the cluster's kubelet identity automatically), Azure Container Apps requires you to explicitly assign the `AcrPull` role to the user‑assigned managed identity when you rely on that identity for image pulls.

1. Assign `AcrPull`:
```bash
ACR_ID=$(az acr show -n "$ACR_NAME" -g "$RESOURCE_GROUP" --query id -o tsv)
az role assignment create \
  --assignee-object-id $UAMI_PRINCIPAL_ID \
  --assignee-principal-type ServicePrincipal \
  --role AcrPull \
  --scope $ACR_ID
```
2. (Optional) Verify:
```bash
az role assignment list --assignee $UAMI_PRINCIPAL_ID --scope $ACR_ID -o table
```

> Why this step? Container Apps only auto-resolves registry credentials if: (a) admin credentials are enabled (we disabled them for security), or (b) you specify a managed identity with the proper role via `--registry-identity`. The role assignment makes the identity authorized; specifying it during create tells the platform which identity to use.

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

What we are doing here is just piping text from the commandline into a few files. Its convenient to do it this way, because we can use the environment varibles defined previously, like ````AOAI_ENDPOINT```` and ````AOAI_API_VERSION````. Less copy-paste. Please spend some time to look at the source code though, or have an LLM like copilot explain it.

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

// Prefer explicit user-assigned identity if AZURE_CLIENT_ID is set to avoid ambiguity
var azureClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
TokenCredential credential = string.IsNullOrWhiteSpace(azureClientId)
  ? new DefaultAzureCredential()
  : new ManagedIdentityCredential(azureClientId);
Console.WriteLine($"[Startup] Using credential type: {(string.IsNullOrWhiteSpace(azureClientId) ? "DefaultAzureCredential" : "ManagedIdentityCredential with clientId")}");
HttpClient http = new();

app.MapPost("/api/complete", async (PromptRequest req) => {
  if (string.IsNullOrWhiteSpace(req.Prompt)) return Results.BadRequest("Prompt required");

  // Acquire token for Azure OpenAI (Cognitive Services scope)
  AccessToken token;
  try
  {
    token = await credential.GetTokenAsync(
      new TokenRequestContext(new[] { "https://cognitiveservices.azure.com/.default" }),
      CancellationToken.None);
  }
  catch (Azure.Identity.AuthenticationFailedException authEx)
  {
    return Results.Problem(
      title: "Managed identity token acquisition failed",
      detail: authEx.Message + (authEx.InnerException != null ? " | Inner: " + authEx.InnerException.Message : string.Empty) +
              (string.IsNullOrWhiteSpace(azureClientId) ? " | Hint: Set AZURE_CLIENT_ID to the user-assigned identity clientId." : string.Empty),
      statusCode: 500);
  }
  http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

  // Try Responses endpoint first
  var responsesPayload = new {
    input = req.Prompt,
    temperature = 0.2,
    max_output_tokens = 256
  };
  var responsesUrl = $"{openAiEndpoint}openai/deployments/{deployment}/responses?api-version={apiVersion}";
  using var responsesResp = await http.PostAsync(responsesUrl, new StringContent(JsonSerializer.Serialize(responsesPayload), Encoding.UTF8, "application/json"));

  if (responsesResp.StatusCode == System.Net.HttpStatusCode.NotFound)
  {
    // Fallback: Chat Completions
    var chatPayload = new {
      messages = new object[] { new { role = "user", content = req.Prompt } },
      temperature = 0.2,
      max_tokens = 256
    };
    var chatUrl = $"{openAiEndpoint}openai/deployments/{deployment}/chat/completions?api-version={apiVersion}";
    using var chatResp = await http.PostAsync(chatUrl, new StringContent(JsonSerializer.Serialize(chatPayload), Encoding.UTF8, "application/json"));
    var chatBody = await chatResp.Content.ReadAsStringAsync();
    if (!chatResp.IsSuccessStatusCode)
      return Results.Problem(title: "OpenAI chat call failed", detail: chatBody, statusCode: (int)chatResp.StatusCode);

    using var chatDoc = JsonDocument.Parse(chatBody);
    var chatText = chatDoc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
    return Results.Ok(new { completion = chatText, endpoint = "chat" });
  }

  var respBody = await responsesResp.Content.ReadAsStringAsync();
  if (!responsesResp.IsSuccessStatusCode)
    return Results.Problem(title: "OpenAI responses call failed", detail: respBody, statusCode: (int)responsesResp.StatusCode);

  using var doc = JsonDocument.Parse(respBody);
  string? extracted = null;
  try
  {
    var output = doc.RootElement.GetProperty("output");
    if (output.ValueKind == JsonValueKind.Array && output.GetArrayLength() > 0)
    {
      var first = output[0];
      if (first.TryGetProperty("content", out var contentElem) && contentElem.ValueKind == JsonValueKind.Array && contentElem.GetArrayLength() > 0)
      {
        var c0 = contentElem[0];
        if (c0.TryGetProperty("text", out var textElem)) extracted = textElem.GetString();
        else extracted = c0.ToString();
      }
      else
      {
        extracted = first.ToString();
      }
    }
  }
  catch { }
  extracted ??= doc.RootElement.ToString();

  return Results.Ok(new { completion = extracted, endpoint = "responses" });
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

## Build & Push Image (Remote ACR Build – Cloud Shell Friendly)
Use Azure Container Registry's remote build so you don't need a local Docker daemon (ideal for Azure Cloud Shell):
```bash
# Queue a remote build from the local folder (context path) and tag the image
az acr build -r ${ACR_NAME} -t ${IMAGE_NAME}:${IMAGE_TAG} ./openai-api

# List images to verify
az acr repository show-tags -n ${ACR_NAME} --repository ${IMAGE_NAME} -o table
```



## Crate ACA Environment 
```bash
az containerapp env create -n "$ENV_NAME" -g "$RESOURCE_GROUP" -l "$LOCATION"
```

## Deploy Container App (Attach Managed Identity & Use It for ACR)
```bash
az containerapp create \
  -n "$APP_NAME" -g "$RESOURCE_GROUP" \
  --environment "$ENV_NAME" \
  --image "${ACR_NAME}.azurecr.io/${IMAGE_NAME}:${IMAGE_TAG}" \
  --ingress external --target-port 8080 \
  --registry-server "${ACR_NAME}.azurecr.io" \
  --registry-identity "$UAMI_ID" \
  --user-assigned "$UAMI_ID" \
  --cpu 0.5 --memory 1Gi \
  --min-replicas 1 --max-replicas 3 \
  --env-vars \
    AOAI_ENDPOINT="https://${AOAI_ACCOUNT_NAME}.openai.azure.com/" \
    AOAI_DEPLOYMENT="$AOAI_DEPLOYMENT" \
    AOAI_API_VERSION="$AOAI_API_VERSION" \
    AZURE_CLIENT_ID="$UAMI_CLIENT_ID" \
  --revision-suffix v1


```





## Test the Endpoint
```bash
APP_URL=$(az containerapp show -n "$APP_NAME" -g "$RESOURCE_GROUP" --query properties.configuration.ingress.fqdn -o tsv)

curl -s https://${APP_URL}/healthz

curl -s -X POST https://${APP_URL}/api/complete \
  -H "Content-Type: application/json" \
  -d '{"prompt":"Provide a fun fact about container security."}' | jq
```
If you see an authorization error ensure the role assignment has propagated (can take a minute). Retry after ~60s.

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

## Cleanup (Remove All Resources Created in This Exercise)
The exact scope depends on whether you reused existing resource groups. Choose the option that fits your scenario.

### 1. Just the App (keep env, ACR, identity)
```bash
az containerapp delete -n "$APP_NAME" -g "$RESOURCE_GROUP" --yes
```

### 2. App + Managed Identity + ACR Image Registry (keep ACA environment & RG)
```bash
az containerapp delete -n "$APP_NAME" -g "$RESOURCE_GROUP" --yes
az identity delete -g "$RESOURCE_GROUP" -n "$UAMI_NAME"
az acr delete -n "$ACR_NAME" -g "$RESOURCE_GROUP" --yes
```

### 3. Full Container Apps Stack (App + Env + Identity + ACR)
This keeps the OpenAI resource group.
```bash
az containerapp delete -n "$APP_NAME" -g "$RESOURCE_GROUP" --yes
az containerapp env delete -n "$ENV_NAME" -g "$RESOURCE_GROUP" --yes
az identity delete -g "$RESOURCE_GROUP" -n "$UAMI_NAME"
az acr delete -n "$ACR_NAME" -g "$RESOURCE_GROUP" --yes
```

### 4. Azure OpenAI Deployment Only (keep account)
```bash
az cognitiveservices account deployment delete \
  -g "$AOAI_RESOURCE_GROUP" -n "$AOAI_ACCOUNT_NAME" \
  --deployment-name "$AOAI_DEPLOYMENT" --yes
```

### 5. Remove Azure OpenAI Account (keeps its RG)
```bash
az cognitiveservices account delete -g "$AOAI_RESOURCE_GROUP" -n "$AOAI_ACCOUNT_NAME" --yes
```

### 6. Remove Resource Groups (IRREVERSIBLE)
Only do this if the groups were created solely for this exercise and are not shared.
```bash
# Container Apps workload group
az group delete -n "$RESOURCE_GROUP" --yes --no-wait

# Azure OpenAI group (if created exclusively for this exercise)
az group delete -n "$AOAI_RESOURCE_GROUP" --yes --no-wait
```
