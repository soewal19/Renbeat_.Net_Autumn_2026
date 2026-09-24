param location string
param keyVaultName string
param tags object
param principalIdAccess string
@secure()
param adminPassword string
@secure()
param groqApiKey string = ''

resource kv 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    enableRbacAuthorization: true
    publicNetworkAccess: 'Enabled'
  }
}

resource appSecretsRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(kv.id, principalIdAccess, 'KeyVaultSecretsUser')
  scope: kv
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: principalIdAccess
    principalType: 'ServicePrincipal'
  }
}

resource initialAdminPassword 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'Seed-AdminPassword'
  properties: {
    value: adminPassword
  }
}

resource groqApiKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (!empty(groqApiKey)) {
  parent: kv
  name: 'GroqApiKey'
  properties: {
    value: groqApiKey
  }
}

output keyVaultName string = kv.name
output keyVaultUri string = kv.properties.vaultUri
