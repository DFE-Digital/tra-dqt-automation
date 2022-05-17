# Description
This console application is intended to be used with a git workflow to run adhoc to run data migrations against a CRM instance

# Usage:

dotnet run DataMigrator.dll {command}

e.g.
dotnet run DataMigrator.dll husid

or from github workflows tab:

enter command name that will be passed in as an input argument

Each command requires a corresponding piece of code to handle it's action.

# Adding new columns in CRM

- Login to Build CRM which is used as a seed environent for others. (https://ent-dqt-build.crm4.dynamics.com/)
- Click Components 
- Select Entity that requires new fields
- Select fields
- Click new button
- Fill in new field name details (name, length etc...)
- Click save and close
- Export solution
- Check exported solution into source control