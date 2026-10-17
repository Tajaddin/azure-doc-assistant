// Everything inside the resource group. Secrets never appear in app settings: the API and the Function read
// them from Key Vault through their managed identities, and Azure OpenAI / Document Intelligence / Storage
// are called with managed-identity RBAC rather than keys wherever the SDK supports it.
param name string
param location string
param tenantId string
param apiClientId string
param spaClientId string
param githubRepository string
@secure()
param postgresPassword string
param chatModel object
param embeddingModel object
param appServiceSku string
param tags object

var suffix = toLower(uniqueString(resourceGroup().id))
var storageName = take('st${replace(name, '-', '')}${suffix}', 24)
var kvName = take('kv-${name}-${suffix}', 24)

// ----------------------------------------------------------------------------- identities

resource apiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${name}-api'
  location: location
  tags: tags
}

resource funcIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${name}-func'
  location: location
  tags: tags
}

resource deployIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${name}-deploy'
  location: location
  tags: tags
}

resource githubFederation 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: deployIdentity
  name: 'github-main'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: 'repo:${githubRepository}:ref:refs/heads/main'
    audiences: ['api://AzureADTokenExchange']
  }
}

// ----------------------------------------------------------------------------- monitoring

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${name}'
  location: location
  tags: tags
  properties: { sku: { name: 'PerGB2018' }, retentionInDays: 30, workspaceCapping: { dailyQuotaGb: 1 } }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${name}'
  location: location
  tags: tags
  kind: 'web'
  properties: { Application_Type: 'web', WorkspaceResourceId: logs.id, IngestionMode: 'LogAnalytics' }
}

// ----------------------------------------------------------------------------- storage

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  tags: tags
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    supportsHttpsTrafficOnly: true
    allowSharedKeyAccess: true // the Functions host still needs it for its own state; the app code uses identity
  }
}

resource blobServices 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource documentsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobServices
  name: 'documents'
  properties: { publicAccess: 'None' }
}

// ----------------------------------------------------------------------------- AI services

resource openAi 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: 'oai-${name}-${suffix}'
  location: location
  tags: tags
  kind: 'OpenAI'
  sku: { name: 'S0' }
  properties: { customSubDomainName: 'oai-${name}-${suffix}', publicNetworkAccess: 'Enabled', disableLocalAuth: true }
}

resource chatDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: openAi
  name: chatModel.name
  sku: { name: 'GlobalStandard', capacity: chatModel.capacity }
  properties: { model: { format: 'OpenAI', name: chatModel.name, version: chatModel.version } }
}

resource embeddingDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: openAi
  name: embeddingModel.name
  sku: { name: 'GlobalStandard', capacity: embeddingModel.capacity }
  properties: { model: { format: 'OpenAI', name: embeddingModel.name, version: embeddingModel.version } }
  dependsOn: [chatDeployment] // deployments on one account are created one at a time
}

resource docIntelligence 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: 'di-${name}-${suffix}'
  location: location
  tags: tags
  kind: 'FormRecognizer'
  sku: { name: 'F0' }
  properties: { customSubDomainName: 'di-${name}-${suffix}', publicNetworkAccess: 'Enabled', disableLocalAuth: true }
}

// ----------------------------------------------------------------------------- database

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: 'pg-${name}-${suffix}'
  location: location
  tags: tags
  sku: { name: 'Standard_B1ms', tier: 'Burstable' }
  properties: {
    version: '16'
    administratorLogin: 'docs'
    administratorLoginPassword: postgresPassword
    storage: { storageSizeGB: 32, autoGrow: 'Disabled' }
    backup: { backupRetentionDays: 7, geoRedundantBackup: 'Disabled' }
    highAvailability: { mode: 'Disabled' }
    authConfig: { passwordAuth: 'Enabled', activeDirectoryAuth: 'Disabled' }
  }
}

resource pgExtensions 'Microsoft.DBforPostgreSQL/flexibleServers/configurations@2024-08-01' = {
  parent: postgres
  name: 'azure.extensions'
  properties: { value: 'VECTOR', source: 'user-override' }
}

resource pgDatabase 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: postgres
  name: 'docs'
  properties: { charset: 'UTF8', collation: 'en_US.utf8' }
}

resource pgAllowAzure 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
  parent: postgres
  name: 'AllowAzureServices'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

// ----------------------------------------------------------------------------- key vault

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: kvName
  location: location
  tags: tags
  properties: {
    tenantId: tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    publicNetworkAccess: 'Enabled'
  }
}

var pgConnectionString = 'Host=${postgres.properties.fullyQualifiedDomainName};Port=5432;Username=docs;Password=${postgresPassword};Database=docs;SSL Mode=Require;Trust Server Certificate=true'

resource secretPostgres 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'Postgres--ConnectionString'
  properties: { value: pgConnectionString }
}

resource secretStorage 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'Storage--ConnectionString'
  properties: {
    value: 'DefaultEndpointsProtocol=https;AccountName=${storage.name};AccountKey=${storage.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'
  }
}

// ----------------------------------------------------------------------------- RBAC

var roles = {
  keyVaultSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
  storageBlobDataContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
  storageQueueDataContributor: '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
  cognitiveServicesOpenAiUser: '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
  cognitiveServicesUser: 'a97b65f3-24c7-4388-baec-2e87135dc908'
  websiteContributor: 'de139f84-1756-47ae-9be6-808fbbe84772'
}

resource apiKvRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, apiIdentity.id, roles.keyVaultSecretsUser)
  scope: keyVault
  properties: { roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsUser), principalId: apiIdentity.properties.principalId, principalType: 'ServicePrincipal' }
}

resource funcKvRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, funcIdentity.id, roles.keyVaultSecretsUser)
  scope: keyVault
  properties: { roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsUser), principalId: funcIdentity.properties.principalId, principalType: 'ServicePrincipal' }
}

resource apiOpenAiRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openAi.id, apiIdentity.id, roles.cognitiveServicesOpenAiUser)
  scope: openAi
  properties: { roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.cognitiveServicesOpenAiUser), principalId: apiIdentity.properties.principalId, principalType: 'ServicePrincipal' }
}

resource funcOpenAiRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openAi.id, funcIdentity.id, roles.cognitiveServicesOpenAiUser)
  scope: openAi
  properties: { roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.cognitiveServicesOpenAiUser), principalId: funcIdentity.properties.principalId, principalType: 'ServicePrincipal' }
}

resource funcDocIntelRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(docIntelligence.id, funcIdentity.id, roles.cognitiveServicesUser)
  scope: docIntelligence
  properties: { roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.cognitiveServicesUser), principalId: funcIdentity.properties.principalId, principalType: 'ServicePrincipal' }
}

resource apiBlobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, apiIdentity.id, roles.storageBlobDataContributor)
  scope: storage
  properties: { roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageBlobDataContributor), principalId: apiIdentity.properties.principalId, principalType: 'ServicePrincipal' }
}

resource funcBlobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, funcIdentity.id, roles.storageBlobDataContributor)
  scope: storage
  properties: { roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageBlobDataContributor), principalId: funcIdentity.properties.principalId, principalType: 'ServicePrincipal' }
}

resource deployWebRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, deployIdentity.id, roles.websiteContributor)
  scope: resourceGroup()
  properties: { roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.websiteContributor), principalId: deployIdentity.properties.principalId, principalType: 'ServicePrincipal' }
}

// ----------------------------------------------------------------------------- app settings shared by API and Function

var sharedSettings = [
  { name: 'Ai__Provider', value: 'azure' }
  { name: 'Ai__Azure__Endpoint', value: openAi.properties.endpoint }
  { name: 'Ai__Azure__ChatDeployment', value: chatModel.name }
  { name: 'Ai__Azure__EmbeddingDeployment', value: embeddingModel.name }
  { name: 'Ai__Azure__EmbeddingDimensions', value: '1536' }
  { name: 'Analyzer__Provider', value: 'azure' }
  { name: 'Analyzer__Endpoint', value: docIntelligence.properties.endpoint }
  { name: 'KeyVault__Uri', value: keyVault.properties.vaultUri }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
]

// ----------------------------------------------------------------------------- API on App Service

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: 'plan-${name}'
  location: location
  tags: tags
  kind: 'linux'
  sku: { name: appServiceSku }
  properties: { reserved: true }
}

resource api 'Microsoft.Web/sites@2023-12-01' = {
  name: 'app-${name}-${suffix}'
  location: location
  tags: tags
  kind: 'app,linux'
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${apiIdentity.id}': {} } }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    keyVaultReferenceIdentity: apiIdentity.id
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|8.0'
      alwaysOn: appServiceSku != 'F1'
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true
      healthCheckPath: '/readyz'
      cors: { allowedOrigins: ['https://${swa.properties.defaultHostname}'] }
      appSettings: concat(sharedSettings, [
        { name: 'Auth__Mode', value: 'entra' }
        { name: 'AzureAd__Instance', value: environment().authentication.loginEndpoint }
        { name: 'AzureAd__TenantId', value: tenantId }
        { name: 'AzureAd__ClientId', value: apiClientId }
        { name: 'AzureAd__Audience', value: 'api://${apiClientId}' }
        { name: 'AZURE_CLIENT_ID', value: apiIdentity.properties.clientId }
        { name: 'Cors__Origins__0', value: 'https://${swa.properties.defaultHostname}' }
        { name: 'WEBSITE_RUN_FROM_PACKAGE', value: '1' }
      ])
    }
  }
}

// ----------------------------------------------------------------------------- ingestion Function (consumption)

resource funcPlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: 'plan-${name}-func'
  location: location
  tags: tags
  kind: 'functionapp'
  sku: { name: 'Y1', tier: 'Dynamic' }
  properties: { reserved: true }
}

resource func 'Microsoft.Web/sites@2023-12-01' = {
  name: 'func-${name}-${suffix}'
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${funcIdentity.id}': {} } }
  properties: {
    serverFarmId: funcPlan.id
    httpsOnly: true
    keyVaultReferenceIdentity: funcIdentity.id
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|8.0'
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      appSettings: concat(sharedSettings, [
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'dotnet-isolated' }
        { name: 'AzureWebJobsStorage__accountName', value: storage.name }
        { name: 'AzureWebJobsStorage__credential', value: 'managedidentity' }
        { name: 'AzureWebJobsStorage__clientId', value: funcIdentity.properties.clientId }
        { name: 'Storage__ConnectionString', value: '@Microsoft.KeyVault(VaultName=${keyVault.name};SecretName=Storage--ConnectionString)' }
        { name: 'AZURE_CLIENT_ID', value: funcIdentity.properties.clientId }
        { name: 'WEBSITE_RUN_FROM_PACKAGE', value: '1' }
      ])
    }
  }
}

// ----------------------------------------------------------------------------- static web app

resource swa 'Microsoft.Web/staticSites@2023-12-01' = {
  name: 'swa-${name}'
  location: 'eastus2' // Static Web Apps are available in a short list of regions; eastus2 is one
  tags: tags
  sku: { name: 'Free', tier: 'Free' }
  properties: { stagingEnvironmentPolicy: 'Enabled', allowConfigFileUpdates: true }
}

output apiUrl string = 'https://${api.properties.defaultHostName}'
output apiAppName string = api.name
output functionAppName string = func.name
output staticWebAppUrl string = 'https://${swa.properties.defaultHostname}'
output staticWebAppName string = swa.name
output keyVaultName string = keyVault.name
output openAiEndpoint string = openAi.properties.endpoint
output deployIdentityClientId string = deployIdentity.properties.clientId
output spaClientIdEcho string = spaClientId
