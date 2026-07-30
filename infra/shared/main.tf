terraform {
  required_version = ">= 1.9"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
  }

  backend "azurerm" {
    resource_group_name  = "angstrom-commander-shared"
    storage_account_name = "angstromtfstate"
    container_name       = "tfstate"
    key                  = "shared.tfstate"
  }
}

provider "azurerm" {
  subscription_id = var.subscription_id
  features {}
}

# Pinned rather than discovered: infra must never silently apply to whatever
# subscription a CLI happens to be logged into.
variable "subscription_id" {
  type    = string
  default = "0fe745e7-c045-4129-9d7d-222f9649a7dc"
}

# Cross-stamp home: resources every environment shares live here, outside any
# stamp, so tearing an environment down can never take them along.
resource "azurerm_resource_group" "shared" {
  name     = "angstrom-commander-shared"
  location = "westeurope"
}

# Delegated from the external registrar (four NS records at adamlengyel.com).
# Stamps create their records inside this zone; the registrar is never touched.
resource "azurerm_dns_zone" "main" {
  name                = "angstrom.adamlengyel.com"
  resource_group_name = azurerm_resource_group.shared.name
}

# One registry serves every stamp.
resource "azurerm_container_registry" "shared" {
  name                = "angstromcommander"
  resource_group_name = azurerm_resource_group.shared.name
  location            = azurerm_resource_group.shared.location
  sku                 = "Basic"
  admin_enabled       = false
}
