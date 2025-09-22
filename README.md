# App Service to Azure Container Apps Workshop

This repository contains a progressive workshop showing how to:

1. Containerize a simple Python FastAPI Web API (Exercise 1)
2. Create an Azure Container Registry (ACR)
3. Push the container image to ACR
4. Provision an Azure Container Apps environment
5. Deploy the container with external ingress
6. Configure scaling rules and health probes
7. Perform a rolling update (new image version)
8. View logs & basic observability

All steps use only Azure CLI and Docker.

## Exercises

| Exercise | Description | Est. Time |
|----------|-------------|-----------|
| [Exercise 1](./exercise1.md) | Containerize a Python FastAPI Web API and deploy to ACA (ingress, scaling, probes, rolling update) | 45–60 min |
| [Exercise 2](./exercise2.md) | Build an ACA API that calls Azure OpenAI using a managed identity (no keys) | 40 min |
| [Exercise 3](./exercise3.md) | Secure pattern: managed identities for ACR & Key Vault, private egress, Defender for Cloud, Azure Front Door | 60–75 min |

