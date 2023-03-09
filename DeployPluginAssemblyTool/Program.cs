using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Hosting;
using System.CommandLine.Parsing;
using Microsoft.Extensions.Configuration;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk.Query;

const string DefaultAssemblyName = "Dqt.Dynamics";

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
    var rootCommand = new RootCommand(description: "Deploy plugin assembly tool");

    var pathArgument = new Argument<string>(name: "path", description: "Path to the plugin assembly DLL.") { Arity = ArgumentArity.ExactlyOne };
    var assemblyNameOption = new Option<string>(name: "--assembly-name", getDefaultValue: () => DefaultAssemblyName);
    var crmUrlOption = new Option<string>(name: "--crm-url", description: "URL of the CRM environment.");
    var crmClientIdOption = new Option<string>(name: "--crm-client-id", description: "Client ID for accessing the CRM environment.");
    var crmClientSecretOption = new Option<string>(name: "--crm-client-secret", description: "Client secret for accessing the CRM environment.");

    rootCommand.AddArgument(pathArgument);
    rootCommand.AddOption(assemblyNameOption);
    rootCommand.AddOption(crmUrlOption);
    rootCommand.AddOption(crmClientIdOption);
    rootCommand.AddOption(crmClientSecretOption);

    rootCommand.SetHandler(
        async (string path, string assemblyName, string? crmUrl, string? crmClientId, string? crmClientSecret) =>
        {
            var configBuilder = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .AddInMemoryCollection(new Dictionary<string, string?>()
                {
                    { "CrmUrl", crmUrl },
                    { "CrmClientId", crmClientId },
                    { "CrmClientSecret", crmClientSecret },
                });
#if DEBUG
            configBuilder.AddUserSecrets(typeof(Program).Assembly, optional: true);
#endif
            var config = configBuilder.Build();

            using var serviceClient = new ServiceClient(
                new Uri(config["CrmUrl"] ?? throw new Exception("Missing CrmUrl configuration.")),
                config["CrmClientId"] ?? throw new Exception("Missing CrmClientId configuration."),
                config["CrmClientSecret"] ?? throw new Exception("Missing CrmClientSecret configuration."),
                useUniqueInstance: false);

            var assemblyQuery = new QueryByAttribute("pluginassembly") { ColumnSet = new ColumnSet("name", "content") };
            assemblyQuery.AddAttributeValue("name", assemblyName);
            var assemblyQueryResult = await serviceClient.RetrieveMultipleAsync(assemblyQuery);

            if (assemblyQueryResult.TotalRecordCount == 0)
            {
                throw new Exception($"No assembly registered named: '{assemblyName}'.");
            }

            var pluginAssembly = assemblyQueryResult.Entities.Single();

            var newContent = Convert.ToBase64String(await File.ReadAllBytesAsync(path));
            pluginAssembly.Attributes["content"] = newContent;

            await serviceClient.UpdateAsync(pluginAssembly);
        },
        pathArgument,
        assemblyNameOption,
        crmUrlOption,
        crmClientIdOption,
        crmClientSecretOption);

    return rootCommand;
}

public partial class Program { }
