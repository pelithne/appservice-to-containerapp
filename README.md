# App Service to Azure Container Apps Workshop

This repository contains a progressive 3‑part workshop:

1. Foundations (Exercise 1) – Containerize a .NET minimal API, push to ACR, deploy to Azure Container Apps with ingress, health probes, autoscaling and rolling revisions.
2. AI Integration (Exercise 2) – Add an Azure OpenAI–backed endpoint using a user‑assigned managed identity (no secrets) and secure token acquisition.
3. Production Hardening (Exercise 3) – Introduce least‑privilege split identities (ACR vs runtime), Key Vault secret access, internal environment + VNet, (conceptual) private endpoints, Azure Front Door, and Defender for Cloud plans.

All steps use only Azure CLI and Docker.

## Exercises

Choose the track you need or progress sequentially. Each exercise builds on concepts from the previous one but can be run standalone with minor adjustments.

| Exercise | Focus | Key Outcomes | Est. Time |
|----------|-------|-------------|-----------|
| [Exercise 1](./exercise1.md) | Core container deployment | Build & containerize a .NET minimal API, push to ACR, deploy to ACA with external ingress, health probes, autoscale (HTTP), roll out new revision, view logs. | 45–60 min |
| [Exercise 2](./exercise2.md) | AI integration & identity | Call Azure OpenAI securely from ACA using a user‑assigned managed identity (no API keys), token-based auth, structured prompt endpoint, health & scaling basics. | 40 min |
| [Exercise 3](./exercise3.md) | Production hardening | Least‑privilege split identities (ACR pull vs runtime), Key Vault secret access via MI, internal ACA environment + VNet, conceptual private endpoints, Azure Front Door (global edge), Defender for Cloud enablement, security rationale. | 60–75 min |

Next ideas: add CI/CD (GitHub Actions), Bicep infra, Front Door WAF + Private Link, Dapr integrations.

