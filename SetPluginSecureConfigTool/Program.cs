using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Hosting;
using System.CommandLine.Parsing;
using Microsoft.Extensions.Configuration;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

const string DefaultPluginAssemblyName = "Dqt.Dynamics";

return await CreateCommandLineBuilder()
    .UseHost()
    .UseDefaults()
    .Build()
    .InvokeAsync(args);

static CommandLineBuilder CreateCommandLineBuilder()
{
    var rootCommand = CreateRootCommand();
    return new CommandLineBuilder(rootCommand);
}

static RootCommand CreateRootCommand()
{
    var rootCommand = new RootCommand(description: "Update plugin secure config tool");

    var pluginTypeNameOption = new Option<string>(name: "--plugin-type", description: "The type name of the plugin.") { IsRequired = true };
    var secureConfigOption = new Option<string>(name: "--secure-config", description: "The secure config value.") { IsRequired = true };
    var pluginAssemblyNameOption = new Option<string>(name: "--assembly-name", getDefaultValue: () => DefaultPluginAssemblyName);
    var crmUrlOption = new Option<string>(name: "--crm-url", description: "URL of the CRM environment.");
    var crmClientIdOption = new Option<string>(name: "--crm-client-id", description: "Client ID for accessing the CRM environment.");
    var crmClientSecretOption = new Option<string>(name: "--crm-client-secret", description: "Client secret for accessing the CRM environment.");

    rootCommand.AddOption(pluginTypeNameOption);
    rootCommand.AddOption(secureConfigOption);
    rootCommand.AddOption(pluginAssemblyNameOption);
    rootCommand.AddOption(crmUrlOption);
    rootCommand.AddOption(crmClientIdOption);
    rootCommand.AddOption(crmClientSecretOption);

    rootCommand.SetHandler(
        async (string pluginTypeName, string secureConfig, string pluginAssemblyName, string? crmUrl, string? crmClientId, string? crmClientSecret) =>
        {
            var configFromOptions = new Dictionary<string, string?>();
            void AddConfigIfSet(string? option, string key)
            {
                if (!string.IsNullOrEmpty(option))
                {
                    configFromOptions.Add(key, option);
                }
            }

            AddConfigIfSet(crmUrl, "CrmUrl");
            AddConfigIfSet(crmClientId, "CrmClientId");
            AddConfigIfSet(crmClientSecret, "CrmClientSecret");

            var configBuilder = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .AddInMemoryCollection(configFromOptions);
#if DEBUG
            configBuilder.AddUserSecrets(typeof(Program).Assembly, optional: true);
#endif
            var config = configBuilder.Build();

            using var serviceClient = new ServiceClient(
                new Uri(config["CrmUrl"] ?? throw new Exception("Missing CrmUrl configuration.")),
                config["CrmClientId"] ?? throw new Exception("Missing CrmClientId configuration."),
                config["CrmClientSecret"] ?? throw new Exception("Missing CrmClientSecret configuration."),
                useUniqueInstance: false);

            var pluginTypeQuery = new QueryByAttribute("plugintype");
            pluginTypeQuery.AddAttributeValue("name", pluginTypeName);
            pluginTypeQuery.AddAttributeValue("assemblyname", pluginAssemblyName);
            var pluginTypeQueryResult = await serviceClient.RetrieveMultipleAsync(pluginTypeQuery);

            if (pluginTypeQueryResult.Entities.Count == 0)
            {
                throw new Exception($"Could not find plugin: '{pluginTypeName}' in assembly '{pluginAssemblyName}'.");
            }

            var pluginTypeId = pluginTypeQueryResult.Entities.Single().Id;

            var pluginStepsQuery = new QueryByAttribute("sdkmessageprocessingstep") { ColumnSet = new ColumnSet("sdkmessageprocessingstepsecureconfigid") };
            pluginStepsQuery.AddAttributeValue("plugintypeid", pluginTypeId);
            var pluginStepsQueryResult = await serviceClient.RetrieveMultipleAsync(pluginStepsQuery);

            foreach (var step in pluginStepsQueryResult.Entities)
            {
                if (step.Attributes.ContainsKey("sdkmessageprocessingstepsecureconfigid"))
                {
                    var stepSecureConfig = await serviceClient.RetrieveAsync(
                        "sdkmessageprocessingstepsecureconfig",
                        step.GetAttributeValue<EntityReference>("sdkmessageprocessingstepsecureconfigid").Id,
                        new ColumnSet(allColumns: true));

                    stepSecureConfig.Attributes["secureconfig"] = secureConfig;

                    await serviceClient.UpdateAsync(stepSecureConfig);
                }
                else
                {
                    var stepSecureConfig = new Entity("sdkmessageprocessingstepsecureconfig");
                    stepSecureConfig.Attributes["secureconfig"] = secureConfig;
                    stepSecureConfig.Id = await serviceClient.CreateAsync(stepSecureConfig);

                    step.Attributes["sdkmessageprocessingstepsecureconfigid"] = stepSecureConfig.ToEntityReference();
                    await serviceClient.UpdateAsync(step);
                }
            }

            Console.WriteLine($"Successfully updated {pluginStepsQueryResult.Entities.Count} steps");
        },
        pluginTypeNameOption,
        secureConfigOption,
        pluginAssemblyNameOption,
        crmUrlOption,
        crmClientIdOption,
        crmClientSecretOption);

    return rootCommand;
}

public partial class Program { }
