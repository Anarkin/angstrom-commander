# The identity the Server's container pulls images with; keeps the registry
# admin account disabled.
resource "azurerm_user_assigned_identity" "server" {
  name                = "id-server-${var.environment_name}"
  resource_group_name = azurerm_resource_group.stamp.name
  location            = azurerm_resource_group.stamp.location
}

resource "azurerm_role_assignment" "server_acr_pull" {
  scope                = data.azurerm_container_registry.shared.id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_user_assigned_identity.server.principal_id
}

# Symmetric JWT signing key, generated per stamp so no environment can mint
# tokens for another. Never leaves Terraform state + the Container App secret.
resource "random_password" "jwt_signing_key" {
  length  = 64
  special = false
}

resource "azurerm_container_app" "server" {
  name                         = "ca-server-${var.environment_name}"
  resource_group_name          = azurerm_resource_group.stamp.name
  container_app_environment_id = azurerm_container_app_environment.stamp.id
  revision_mode                = "Single"

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.server.id]
  }

  registry {
    server   = data.azurerm_container_registry.shared.login_server
    identity = azurerm_user_assigned_identity.server.id
  }

  secret {
    name  = "pg-connection-string"
    value = "Host=${azurerm_postgresql_flexible_server.stamp.fqdn};Port=5432;Database=angstrom;Username=angstrom;Password=${random_password.postgres_admin.result};Ssl Mode=Require"
  }

  secret {
    name  = "jwt-signing-key"
    value = random_password.jwt_signing_key.result
  }

  ingress {
    external_enabled = true
    target_port      = 8080
    # "auto" negotiates HTTP/2 and WebSockets alike — the Daemon sockets depend on it.
    transport = "auto"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    # The Daemon connection registry is in-process, so a second replica would
    # own sockets the first cannot see. Hard cap at 1 until Redis exists.
    min_replicas = var.server_min_replicas
    max_replicas = 1

    container {
      name = "server"
      # Bootstrap placeholder so the app can exist before our first image push;
      # azd deploy replaces it, and ignore_changes below keeps Terraform from
      # ever putting the placeholder back.
      image  = "mcr.microsoft.com/k8se/quickstart:latest"
      cpu    = 0.25
      memory = "0.5Gi"

      env {
        name        = "ConnectionStrings__Database"
        secret_name = "pg-connection-string"
      }

      env {
        name        = "Auth__JwtSigningKey"
        secret_name = "jwt-signing-key"
      }

      env {
        name  = "Database__MigrateOnStartup"
        value = "true"
      }

      env {
        name  = "Cors__AllowedOrigins__0"
        value = "https://${local.app_hostname}"
      }
    }
  }

  lifecycle {
    ignore_changes = [template[0].container[0].image]
  }

  tags = {
    "azd-env-name"     = var.environment_name
    "azd-service-name" = "server"
  }
}
