@description('Location for the resources in this resource group')
param location string = resourceGroup().location

@description('Location for the Web App and App Service Plan')
param webAppLocation string = 'westus3'

@description('Name of the Storage Account')
param storageAccountName string = 'stpomode'

@description('Name of the App Service (Web App)')
param webAppName string = 'app-pomode'

@description('Name of the App Service Plan on F1 Free Linux tier')
param appServicePlanName string = 'asp-pomode-f1'

@description('Name of the shared Key Vault in PoShared')
param keyVaultName string = 'kv-poshared'

@description('Name of the shared Application Insights component in PoShared')
param appInsightsName string = 'poappideinsights8f9c9a4e'

@description('Name of the shared resource group')
param sharedResourceGroupName string = 'PoShared'

// ─────────────────────────────────────────────
// Storage Account (stpomode) in PoMode RG
// ─────────────────────────────────────────────

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAccountName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-01-01' = {
  parent: storageAccount
  name: 'default'
}

resource jobsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  parent: blobService
  name: 'pomode-jobs'
  properties: {
    publicAccess: 'None'
  }
}

// ─────────────────────────────────────────────
// Shared resources referenced from PoShared RG
// ─────────────────────────────────────────────

resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightsName
  scope: resourceGroup(sharedResourceGroupName)
}

resource sharedKeyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
  scope: resourceGroup(sharedResourceGroupName)
}

// ─────────────────────────────────────────────
// App Service Plan — F1 (Free), Linux, dedicated to PoMode
// ─────────────────────────────────────────────

resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: appServicePlanName
  location: webAppLocation
  sku: {
    name: 'F1'
    tier: 'Free'
  }
  properties: {
    reserved: true
  }
}

// ─────────────────────────────────────────────
// App Service (Web App) — .NET 10 on Linux with managed identity
// ─────────────────────────────────────────────

resource webApp 'Microsoft.Web/sites@2023-12-01' = {
  name: webAppName
  location: webAppLocation
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      appCommandLine: 'dotnet PoMode.API.dll'
      alwaysOn: false
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      appSettings: [
        {
          name: 'KeyVault__Uri'
          value: sharedKeyVault.properties.vaultUri
        }
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: 'Production'
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsights.properties.ConnectionString
        }
        {
          name: 'Jobs__Storage__AccountUri'
          value: storageAccount.properties.primaryEndpoints.blob
        }
        {
          name: 'Jobs__Storage__Container'
          value: 'pomode-jobs'
        }
        {
          name: 'Jobs__Storage__Mode'
          value: 'Auto'
        }
        {
          name: 'ASPNETCORE_URLS'
          value: 'http://+:8080'
        }
      ]
    }
  }
}

// Grant Storage Blob Data Contributor on stpomode to the App Service Managed Identity
module webAppStorageBlobRole 'storage-role.bicep' = {
  name: 'storage-blob-role'
  params: {
    storageAccountId: storageAccount.id
    storageAccountName: storageAccount.name
    principalId: webApp.identity.principalId
  }
}

// Grant Key Vault secret get/list access to the App Service Managed Identity in PoShared
module webAppKeyVaultAccess 'keyvault-access.bicep' = {
  name: 'keyvault-access'
  scope: resourceGroup(sharedResourceGroupName)
  params: {
    keyVaultName: keyVaultName
    principalId: webApp.identity.principalId
    tenantId: subscription().tenantId
  }
}

// Health ping availability test in PoShared
module healthAvailabilityTest 'availability-test.bicep' = {
  name: 'availability-test'
  scope: resourceGroup(sharedResourceGroupName)
  params: {
    appInsightsId: appInsights.id
    location: appInsights.location
    webAppHostName: webApp.properties.defaultHostName
    webAppName: webAppName
  }
}

// Outputs
output storageAccountName string = storageAccount.name
output webAppUrl string = 'https://${webApp.properties.defaultHostName}'
output webAppPrincipalId string = webApp.identity.principalId
