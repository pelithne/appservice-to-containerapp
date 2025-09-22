using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

string openAiEndpoint = Environment.GetEnvironmentVariable("AOAI_ENDPOINT") ?? throw new("AOAI_ENDPOINT missing");
string deployment = Environment.GetEnvironmentVariable("AOAI_DEPLOYMENT") ?? throw new("AOAI_DEPLOYMENT missing");
string apiVersion = Environment.GetEnvironmentVariable("AOAI_API_VERSION") ?? "2024-08-01-preview";

TokenCredential credential = new DefaultAzureCredential();
HttpClient http = new();

app.MapPost("/api/complete", async (PromptRequest req) => {
    if (string.IsNullOrWhiteSpace(req.Prompt)) return Results.BadRequest("Prompt required");

    var token = await credential.GetTokenAsync(
        new TokenRequestContext(new[] { "https://cognitiveservices.azure.com/.default" }),
        CancellationToken.None);
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

    var responsesPayload = new {
        input = req.Prompt,
        temperature = 0.2,
        max_output_tokens = 256
    };
    var responsesUrl = $"{openAiEndpoint}openai/deployments/{deployment}/responses?api-version={apiVersion}";
    using var responsesResp = await http.PostAsync(responsesUrl, new StringContent(JsonSerializer.Serialize(responsesPayload), Encoding.UTF8, "application/json"));

    if (responsesResp.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
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
