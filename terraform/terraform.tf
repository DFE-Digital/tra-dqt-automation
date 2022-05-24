terraform {
  required_version = "~> 1.0"

  backend "azurerm" {
    container_name = "dqt-scripts-tfstate"
  }

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 2.84"
    }
  }
}
