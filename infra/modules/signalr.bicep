param location string
param name string
param keyVaultName string
param tags object

resource signalr 'Microsoft.SignalRService/signalR@2023-06-01-preview' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: 'Standard_S1'
    tier: 'Standard'
    capacity: 1
  }
  kind: 'SignalR'
  properties: {
    features: [
      { flag: 'ServiceMode', value: 'Default' }
      { flag: 'EnableConnectivityLogs', value: 'true' }
      { flag: 'EnableMessagingLogs', value: 'true' }
    ]
    publicNetworkAccess: 'Enabled'
    tls: {
      clientCertEnabled: false
    }
  }
}

var signalrKeys = signalr.listKeys()

resource kv 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource connectionSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'AzureSignalR'
  properties: {
    value: 'Endpoint=https://${name}.service.signalr.net;AccessKey=${signalrKeys.primaryKey};Version=1.0;'
  }
}

output hostname string = signalr.properties.hostName
