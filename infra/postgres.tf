resource "random_password" "postgres_admin" {
  length = 32
  # Kept out of the connection string's reserved characters.
  special = false
}

# No `lifecycle { prevent_destroy = true }` here, though prod needs one: the flag takes a
# literal, so it cannot be switched on by a variable, and one stamp definition serves every
# environment — turning it on unconditionally would make `azd down` fail on exactly the demo
# stamps that exist to be thrown away. Giving prod that guard means a prod-only overlay, which
# is real work and is listed as a gap rather than pretended away.
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

# The Server reaches PostgreSQL over its public endpoint, and this special range is how
# Azure spells "let Azure services in". Read it precisely: it is EVERY Azure service in
# EVERY subscription, not just this stamp's — anyone who can start a VM in Azure can reach
# this server's login prompt. What stands in front of it is the 32-character random password
# above and nothing else.
#
# It stays because Container Apps on the Consumption profile has no fixed outbound address to
# allow-list instead. Closing it properly means giving the stamp a VNet and using private
# networking, which replaces the Container Apps environment rather than editing it — a
# deliberate migration, not a line change. Tracked in ARCHITECTURE.md § Implementation status.
resource "azurerm_postgresql_flexible_server_firewall_rule" "azure_services" {
  name             = "allow-azure-services"
  server_id        = azurerm_postgresql_flexible_server.stamp.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}
