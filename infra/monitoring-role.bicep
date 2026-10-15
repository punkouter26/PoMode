// Grants the web app's managed identity "Monitoring Metrics Publisher" on the shared Application
// Insights component, so the app's OpenTelemetry exporter authenticates with Entra ID instead of the
// connection string's instrumentation key. Cross-RG scoped to PoShared.

@description('Name of the Application Insights component')
param appInsightsName string

@description('Managed identity principal ID to grant the role to')
param principalId string

resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightsName
}

// Monitoring Metrics Publisher
var roleDefinitionId = '3913510d-42f4-4e42-8a64-420c390055eb'

resource publisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(appInsights.id, principalId, roleDefinitionId)
  scope: appInsights
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleDefinitionId)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}
