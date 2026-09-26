# C4 — Deployment

```mermaid
flowchart TB
    browser[User browser]
    subgraph azure[Azure subscription]
      subgraph appsvc[Azure App Service\nASP.NET Core + Blazor WebAssembly]
        app[Roomly app\nManaged Identity]
      end
      sql[(Azure SQL Database)]
      sr[Azure SignalR Service]
      ai[Application Insights]
      kv[Azure Key Vault]
    end
    groq[Groq API\noptional external provider]
    browser -->|HTTPS| app
    app -->|SQL connection from Key Vault| sql
    app -->|connection from Key Vault| sr
    app -->|telemetry| ai
    app -->|optional HTTPS; key configured server-side| groq
    app -->|managed identity reads configured secrets| kv
    sr -->|schedule updates| browser
```

Infrastructure is described by `infra/main.bicep` and its modules. Groq is not provisioned by the Bicep template: configure `GROQ_API_KEY` and optionally `GROQ_MODEL` as App Service settings backed by Key Vault or GitHub environment secrets. If unset, AI endpoints report unavailable while booking remains usable. Swagger UI is Development-only. This diagram describes the deployed target; it does not imply that Azure resources have been provisioned for this repository.
