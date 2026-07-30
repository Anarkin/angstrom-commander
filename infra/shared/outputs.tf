output "dns_zone_name" {
  value = azurerm_dns_zone.main.name
}

output "dns_zone_resource_group" {
  value = azurerm_resource_group.shared.name
}

output "container_registry_login_server" {
  value = azurerm_container_registry.shared.login_server
}
