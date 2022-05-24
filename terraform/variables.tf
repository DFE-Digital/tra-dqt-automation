variable "environment_name" {
  type = string
}

variable "storage_account_name" {
  type = string
}

variable "storage_resource_group_name" {
  type = string
}

variable "azure_sp_credentials_json" {
  type    = string
  default = null
}

variable "storage_container_name" {
  type = string
}

locals {
  azure_credentials = try(jsondecode(var.azure_sp_credentials_json), null)
  common_tags = {
    "Environment"      = var.environment_name
    "Parent Business"  = "Teacher Training and Qualifications"
    "Portfolio"        = "Early Years and Schools Group"
    "Product"          = "Database of Qualified Teachers"
    "Service"          = "Teacher Training and Qualifications"
    "Service Line"     = "Teaching Workforce"
    "Service Offering" = "Database of Qualified Teachers"
  }
}
