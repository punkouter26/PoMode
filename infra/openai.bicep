// The optional cloud interpreter: one Azure OpenAI account with one small model deployment, reached
// by the web app's managed identity only (no keys: local auth is disabled). Deployed only when
// main.bicep's enableCloudInterpreter is true, because it is the one resource here that bills per use.
// See docs/ai-cost.md for what a written interpretation costs on it.

@description('Location for the Azure OpenAI account')
param location string

@description('Account name; also its custom subdomain')
param accountName string

@description('Model deployment name the app is configured with')
param deploymentName string

@description('Model to deploy')
param modelName string = 'gpt-4.1-nano'

@description('Model version')
param modelVersion string = '2025-04-14'

@description('Tokens-per-minute capacity, in thousands')
param capacity int = 20

@description('Managed identity principal ID that may call the deployment')
param principalId string

@description('Create the role assignment (needs roleAssignments/write; CI passes false)')
param assignRole bool = true

resource account 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: accountName
  location: location
  kind: 'OpenAI'
  sku: {
    name: 'S0'
  }
  properties: {
    customSubDomainName: accountName
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

resource deployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: account
  name: deploymentName
  sku: {
    name: 'GlobalStandard'
    capacity: capacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: modelName
      version: modelVersion
    }
  }
}

// Cognitive Services OpenAI User
var roleDefinitionId = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'

resource openAiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (assignRole) {
  name: guid(account.id, principalId, roleDefinitionId)
  scope: account
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleDefinitionId)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}

output endpoint string = account.properties.endpoint
