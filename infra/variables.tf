variable "environment_name" {
  description = "The azd environment name; becomes the stamp's resource group suffix and DNS label."
  type        = string
}

variable "location" {
  type    = string
  default = "westeurope"
}

# Pinned rather than discovered: infra must never silently apply to whatever
# subscription a CLI happens to be logged into.
variable "subscription_id" {
  type    = string
  default = "0fe745e7-c045-4129-9d7d-222f9649a7dc"
}

# Daemons hold sockets open, so any environment with a paired Daemon needs 1.
# Only Daemon-less stamps may scale to zero.
variable "server_min_replicas" {
  type    = number
  default = 1
}

variable "postgres_sku_name" {
  type    = string
  default = "B_Standard_B1ms"
}

variable "postgres_storage_mb" {
  type    = number
  default = 32768
}
