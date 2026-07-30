terraform {
  required_version = ">= 1.9"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }

  # Configured by azd from provider.conf.json (storage account, container, per-env key).
  backend "azurerm" {}
}

provider "azurerm" {
  subscription_id = var.subscription_id
  features {}
}

# The stamp: everything an environment owns lives in this one group, so
# `azd down` deletes the environment and nothing else.
resource "azurerm_resource_group" "stamp" {
  name     = "angstrom-commander-${var.environment_name}"
  location = var.location

  tags = {
    "azd-env-name" = var.environment_name
  }
}

resource "azurerm_log_analytics_workspace" "stamp" {
  name                = "log-${var.environment_name}"
  resource_group_name = azurerm_resource_group.stamp.name
  location            = azurerm_resource_group.stamp.location
  sku                 = "PerGB2018"
  retention_in_days   = 30
}

resource "azurerm_container_app_environment" "stamp" {
  name                       = "cae-${var.environment_name}"
  resource_group_name        = azurerm_resource_group.stamp.name
  location                   = azurerm_resource_group.stamp.location
  log_analytics_workspace_id = azurerm_log_analytics_workspace.stamp.id
}

# Cross-stamp resources, owned by infra/shared — read, never managed, from here.
data "azurerm_dns_zone" "shared" {
  name                = "angstrom.adamlengyel.com"
  resource_group_name = "angstrom-commander-shared"
}

data "azurerm_container_registry" "shared" {
  name                = "angstromcommander"
  resource_group_name = "angstrom-commander-shared"
}

locals {
  # prod owns the bare names (api., app.); every other stamp nests under its
  # environment name (api.test., app.test.).
  api_record_name = var.environment_name == "prod" ? "api" : "api.${var.environment_name}"
  app_record_name = var.environment_name == "prod" ? "app" : "app.${var.environment_name}"
  api_hostname    = "${local.api_record_name}.${data.azurerm_dns_zone.shared.name}"
  app_hostname    = "${local.app_record_name}.${data.azurerm_dns_zone.shared.name}"
}
