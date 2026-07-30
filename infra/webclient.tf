resource "azurerm_static_web_app" "webclient" {
  name                = "swa-webclient-${var.environment_name}"
  resource_group_name = azurerm_resource_group.stamp.name
  location            = azurerm_resource_group.stamp.location
  sku_tier            = "Free"
  sku_size            = "Free"

  tags = {
    "azd-env-name"     = var.environment_name
    "azd-service-name" = "webclient"
  }
}

resource "azurerm_static_web_app_custom_domain" "webclient" {
  static_web_app_id = azurerm_static_web_app.webclient.id
  domain_name       = local.app_hostname
  validation_type   = "cname-delegation"

  depends_on = [azurerm_dns_cname_record.app]
}
