# azd reads these into its environment after provisioning; the registry
# endpoint is what `azd deploy` pushes the Server image to.
output "AZURE_CONTAINER_REGISTRY_ENDPOINT" {
  value = data.azurerm_container_registry.shared.login_server
}

output "SERVER_URI" {
  value = "https://${local.api_hostname}"
}

output "WEB_URI" {
  value = "https://${local.app_hostname}"
}

output "POSTGRES_FQDN" {
  value = azurerm_postgresql_flexible_server.stamp.fqdn
}
