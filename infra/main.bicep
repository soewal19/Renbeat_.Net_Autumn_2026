targetScope = 'subscription'

param environmentName string = 'dev'
param appName string = 'roombooking'
param location string = 'westeurope'

param sqlAdminLogin string = 'sqladmin'
@secure()
param sqlAdminPassword string
param adminEmail string = 'admin@reenbeat.com'
param seedDemoData bool = false
@secure()
param adminPassword string
@secure()
param groqApiKey string = ''

param tags object = {
  project: 'reenbit-roombooking'
  environment: environmentName
}

var rgName = 'rg-${appName}-${environmentName}'
var appNameFull = 'app-${appName}-${environmentName}-${take(uniqueString(subscription().id, appName), 6)}'
var planName = 'plan-${appName}-${environmentName}'
var sqlServerName = 'sql-${appName}-${environmentName}-${take(uniqueString(subscription().id, appName), 6)}'
var sqlDbName = 'db-${appName}'
var signalrName = 'sr-${appName}-${environmentName}-${take(uniqueString(subscription().id, appName), 6)}'
var kvName = 'kv-${take(uniqueString(subscription().id, appName, environmentName), 20)}'
var appInsightsName = 'ai-${appName}-${environmentName}'
var workspaceName = 'log-${appName}-${environmentName}'

resource rg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: rgName
  location: location
  tags: tags
}

module appservice 'modules/appservice.bicep' = {
  name: 'appservice-${appName}'
  scope: resourceGroup(rgName, location)
  params: {
    location: location
    appName: appNameFull
    planName: planName
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
    keyVaultName: kvName
    adminEmail: adminEmail
    seedDemoData: seedDemoData
    groqConfigured: !empty(groqApiKey)
    tags: tags
  }
}

module sql 'modules/sql.bicep' = {
  name: 'sql-${appName}'
  scope: resourceGroup(rgName, location)
  params: {
    location: location
    sqlServerName: sqlServerName
    sqlDbName: sqlDbName
    sqlAdminLogin: sqlAdminLogin
    sqlAdminPassword: sqlAdminPassword
    allowedIpStart: '0.0.0.0'
    allowedIpEnd: '0.0.0.0'
    tags: tags
    keyVaultName: kvName
  }
  dependsOn: [keyvault]
}

module signalr 'modules/signalr.bicep' = {
  name: 'signalr-${appName}'
  scope: resourceGroup(rgName, location)
  params: {
    location: location
    name: signalrName
    keyVaultName: kvName
    tags: tags
  }
  dependsOn: [keyvault]
}

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring-${appName}'
  scope: resourceGroup(rgName, location)
  params: {
    location: location
    appInsightsName: appInsightsName
    workspaceName: workspaceName
    tags: tags
  }
}

module keyvault 'modules/keyvault.bicep' = {
  name: 'keyvault-${appName}'
  scope: resourceGroup(rgName, location)
  params: {
    location: location
    keyVaultName: kvName
    tags: tags
    principalIdAccess: appservice.outputs.principalId
    adminPassword: adminPassword
    groqApiKey: groqApiKey
  }
}

output resourceGroupName string = rg.name
output appServiceName string = appNameFull
output webAppHostname string = appservice.outputs.webAppHostname
output sqlServerFqdn string = sql.outputs.sqlServerFqdn
output signalrHostname string = signalr.outputs.hostname
output applicationInsightsName string = monitoring.outputs.appInsightsName
