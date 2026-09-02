// Storage Blob Data Contributor for the web app's managed identity.
//
// This lives in its own module because a resource `name` must be computable before the deployment starts,
// and `webApp.identity.principalId` exists only after the site is created. Passing it in as a module
// PARAMETER makes it a plain string by the time this template is evaluated, so it can seed the guid().

@description('Resource ID of the Storage Account')
param storageAccountId string

@description('Storage Account name')
param storageAccountName string

@description('Managed identity principal ID to grant the role to')
param principalId string

// Storage Blob Data Contributor: ba92f5b4-2d11-453d-a403-e96b0029c9fe
var storageBlobDataContributorRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
)

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' existing = {
  name: storageAccountName
}

resource roleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccountId, principalId, storageBlobDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: storageBlobDataContributorRoleId
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}
