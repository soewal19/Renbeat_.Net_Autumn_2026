param location string
param appName string
param planName string
param appInsightsConnectionString string
param keyVaultName string
param adminEmail string
param groqConfigured bool = false
param tags object

var linuxFxVersion = 'DOTNETCORE:10.0'

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: planName
  location: location
  tags: tags
  sku: {
    name: 'B1'
    tier: 'Basic'
    capacity: 2
  }
  kind: 'linux'
  properties: {
    reserved: true
  }
}

resource app 'Microsoft.Web/sites@2023-12-01' = {
  name: appName
  location: location
  tags: tags
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    siteConfig: {
      linuxFxVersion: linuxFxVersion
      alwaysOn: true
      webSocketsEnabled: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true
      appSettings: [
        { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
        { name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED', value: 'true' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
        { name: 'ConnectionStrings__DefaultConnection', value: '@Microsoft.KeyVault(SecretUri=https://${keyVaultName}.vault.azure.net/secrets/DefaultConnection/)' }
        { name: 'ConnectionStrings__AzureSignalR', value: '@Microsoft.KeyVault(SecretUri=https://${keyVaultName}.vault.azure.net/secrets/AzureSignalR/)' }
        { name: 'Seed__AdminEmail', value: adminEmail }
        { name: 'Seed__AdminPassword', value: '@Microsoft.KeyVault(SecretUri=https://${keyVaultName}.vault.azure.net/secrets/Seed-AdminPassword/)' }
        if (groqConfigured) { name: 'GROQ_API_KEY', value: '@Microsoft.KeyVault(SecretUri=https://${keyVaultName}.vault.azure.net/secrets/GroqApiKey/)' }
        { name: 'GROQ_MODEL', value: 'llama-3.3-70b-versatile' }
        { name: 'WEBSITE_RUN_FROM_PACKAGE', value: '1' }
        { name: 'WEBSITE_ENABLE_APP_SERVICE_STORAGE', value: 'true' }
      ]
    }
    httpsOnly: true
  }
}

output principalId string = app.identity.principalId
output webAppHostname string = 'https://${app.properties.defaultHostName}'
