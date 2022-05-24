resource "azurerm_resource_group" "dqt_scripts_rg" {
  name     = var.storage_resource_group_name
  location = "West Europe"
  tags     = merge(local.common_tags)
}

resource "azurerm_storage_account" "dqt_scripts_sa" {
  name                     = var.storage_account_name
  resource_group_name      = azurerm_resource_group.dqt_scripts_rg.name
  location                 = azurerm_resource_group.dqt_scripts_rg.location
  account_tier             = "Standard"
  account_replication_type = "GRS"
  blob_properties {
    versioning_enabled       = true
  }
  tags = merge(local.common_tags)
}

resource "azurerm_storage_container" "dqt_scripts_container" {
  name                  = var.storage_container_name
  storage_account_name  = azurerm_storage_account.dqt_scripts_sa.name
  container_access_type = "private"
}
