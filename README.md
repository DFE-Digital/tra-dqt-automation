# tra-crm-automation

## Github Actions

The current workflow is manually triggered with **Export** and **Import** environments as inputs. A solution name
can also be passed into the workflow. Workflow expects CRM url, application ID, application client secret, and tenant ID in Azure keyvault secret in yaml format.

Each Github environment for the repo has KEYVAULT_NAME secret defined. Keyvaults are the same as the ones used for API deployment for each environment.

**Note** - Currently there is Build environment in CRM which is the development environment. Therefore Build secrets are stored in DEV keyvault.
