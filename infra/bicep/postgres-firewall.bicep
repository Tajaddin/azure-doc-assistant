// One firewall rule per outbound address of the apps that reach the database. A separate module because a
// resource loop must be sized at deployment start, and the apps' addresses are only known once they exist.
param serverName string

@description('IPv4 addresses the API and the Function can connect from (their possibleOutboundIpAddresses)')
param addresses array

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' existing = {
  name: serverName
}

resource rules 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = [for (address, i) in addresses: {
  parent: postgres
  name: 'app-outbound-${i}'
  properties: { startIpAddress: address, endIpAddress: address }
}]
