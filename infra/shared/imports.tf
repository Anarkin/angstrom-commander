# The resource group and DNS zone predate Terraform (provisioned by hand, July 2026)
# and must be imported, never recreated: the registrar's NS delegation points at this
# exact zone instance, and a recreated zone would be assigned different name servers.
# Import blocks are ignored once the resources are in state, so these stay.

import {
  to = azurerm_resource_group.shared
  id = "/subscriptions/${var.subscription_id}/resourceGroups/angstrom-commander-shared"
}

import {
  to = azurerm_dns_zone.main
  id = "/subscriptions/${var.subscription_id}/resourceGroups/angstrom-commander-shared/providers/Microsoft.Network/dnsZones/angstrom.adamlengyel.com"
}
