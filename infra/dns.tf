# Records go into the shared zone; the zone itself is owned by infra/shared.

resource "azurerm_dns_cname_record" "api" {
  name                = local.api_record_name
  zone_name           = data.azurerm_dns_zone.shared.name
  resource_group_name = data.azurerm_dns_zone.shared.resource_group_name
  ttl                 = 300
  record              = azurerm_container_app.server.ingress[0].fqdn
}

# Container Apps proves domain ownership through this TXT record before it
# will bind the hostname.
resource "azurerm_dns_txt_record" "api_verification" {
  name                = "asuid.${local.api_record_name}"
  zone_name           = data.azurerm_dns_zone.shared.name
  resource_group_name = data.azurerm_dns_zone.shared.resource_group_name
  ttl                 = 300

  record {
    value = azurerm_container_app.server.custom_domain_verification_id
  }
}

resource "azurerm_container_app_custom_domain" "api" {
  name             = local.api_hostname
  container_app_id = azurerm_container_app.server.id

  depends_on = [azurerm_dns_cname_record.api, azurerm_dns_txt_record.api_verification]

  # The managed certificate is provisioned by Azure asynchronously after the
  # domain binds (bound once via `az containerapp hostname bind`); Terraform
  # must not see that as drift and try to unbind it.
  lifecycle {
    ignore_changes = [certificate_binding_type, container_app_environment_certificate_id]
  }
}

resource "azurerm_dns_cname_record" "app" {
  name                = local.app_record_name
  zone_name           = data.azurerm_dns_zone.shared.name
  resource_group_name = data.azurerm_dns_zone.shared.resource_group_name
  ttl                 = 300
  record              = azurerm_static_web_app.webclient.default_host_name
}
