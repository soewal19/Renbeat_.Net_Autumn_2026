param location string
param sqlServerName string
param sqlDbName string
param sqlAdminLogin string
@secure()
param sqlAdminPassword string
param allowedIpStart string
param allowedIpEnd string
param tags object
param keyVaultName string

resource server 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlServerName
  location: location
  tags: tags
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    version: '12.0'
    publicNetworkAccess: 'Enabled'
    minimalTlsVersion: '1.2'
  }
}

resource allowAllAzureIps 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: server
  name: 'AllowAllAzureServices'
  properties: {
    startIpAddress: allowedIpStart
    endIpAddress: allowedIpEnd
  }
}

resource database 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: server
  name: sqlDbName
  location: location
  tags: tags
  sku: {
    name: 'GP_Gen5_2'
    tier: 'GeneralPurpose'
    capacity: 2
  }
  properties: {
    zoneRedundant: false
  }
}

resource kv 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
  scope: resourceGroup()
}

resource adminSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'sql-admin-password'
  properties: {
    value: sqlAdminPassword
  }
}

resource appConnectionSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'DefaultConnection'
  properties: {
    value: 'Server=tcp:${server.name}.${environment().suffixes.sqlServerHostname},1433;Initial Catalog=${database.name};Persist Security Info=False;User ID=${sqlAdminLogin};Password=${sqlAdminPassword};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
  }
}

output sqlServerFqdn string = '${server.name}.${environment().suffixes.sqlServerHostname}'
