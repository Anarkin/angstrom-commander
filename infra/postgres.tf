resource "random_password" "postgres_admin" {
  length = 32
  # Kept out of the connection string's reserved characters.
  special = false
}

resource "azurerm_postgresql_flexible_server" "stamp" {
  name                   = "angstrom-commander-${var.environment_name}"
  resource_group_name    = azurerm_resource_group.stamp.name
  location               = azurerm_resource_group.stamp.location
  version                = "17"
  administrator_login    = "angstrom"
  administrator_password = random_password.postgres_admin.result
  sku_name               = var.postgres_sku_name
  storage_mb             = var.postgres_storage_mb
  backup_retention_days  = 7
  zone                   = "1"
}

resource "azurerm_postgresql_flexible_server_database" "angstrom" {
  name      = "angstrom"
  server_id = azurerm_postgresql_flexible_server.stamp.id
}

# The Server reaches PostgreSQL over its public endpoint; this special range means
# "Azure services only". Fine while the Container Apps environment has no VNet —
# revisit if the stamp ever gets one.
resource "azurerm_postgresql_flexible_server_firewall_rule" "azure_services" {
  name             = "allow-azure-services"
  server_id        = azurerm_postgresql_flexible_server.stamp.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}
