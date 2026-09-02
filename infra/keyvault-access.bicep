// Grants the web app's managed identity secret get/list on the shared Key Vault.
// Cross-RG scoped to PoShared.

@description('Name of the Key Vault')
param keyVaultName string

@description('Managed identity principal ID to grant access to')
param principalId string

@description('Tenant ID of the subscription')
param tenantId string

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource accessPolicy 'Microsoft.KeyVault/vaults/accessPolicies@2023-07-01' = {
  parent: keyVault
  name: 'add'
  properties: {
    accessPolicies: [
      {
        tenantId: tenantId
        objectId: principalId
        permissions: {
          secrets: [
            'get'
            'list'
          ]
        }
      }
    ]
  }
}
