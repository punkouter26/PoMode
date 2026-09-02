targetScope = 'subscription'

@minLength(1)
@description('Primary location for resource group and storage')
param location string = 'eastus2'

@minLength(1)
@description('Location for Web App and App Service Plan')
param webAppLocation string = 'westus3'

var resourceGroupName = 'PoMode'
var sharedResourceGroupName = 'PoShared'
var storageAccountName = 'stpomode'
var webAppName = 'app-pomode'
var appServicePlanName = 'asp-pomode-f1'
var keyVaultName = 'kv-poshared'
var appInsightsName = 'poappideinsights8f9c9a4e'

// Target Resource Group
resource poModeRg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: resourceGroupName
  location: location
}

// Deploy PoMode application infrastructure into the PoMode RG
module resources 'resources.bicep' = {
  name: 'pomode-resources'
  scope: poModeRg
  params: {
    location: location
    webAppLocation: webAppLocation
    storageAccountName: storageAccountName
    webAppName: webAppName
    appServicePlanName: appServicePlanName
    keyVaultName: keyVaultName
    appInsightsName: appInsightsName
    sharedResourceGroupName: sharedResourceGroupName
  }
}

output webAppUrl string = resources.outputs.webAppUrl
output storageAccountName string = resources.outputs.storageAccountName
output webAppPrincipalId string = resources.outputs.webAppPrincipalId
