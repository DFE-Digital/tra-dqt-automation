using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Hosting;
using System.CommandLine.NamingConventionBinder;
using System.CommandLine.Parsing;
using Azure;
using Azure.Storage.Blobs;
using CsvHelper;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

await CreateCommandLineBuilder()
    .UseHost((IHostBuilder host) => host
        .ConfigureAppConfiguration(config => config
            .AddUserSecrets(typeof(Program).Assembly)
            .AddEnvironmentVariables())
        .ConfigureServices((hostBuilderContext, services) =>
        {
            var configuration = hostBuilderContext.Configuration;

            services.AddSingleton<ServiceClient>(_ =>
                new ServiceClient(
                    new Uri(configuration["CrmUrl"]),
                    configuration["CrmClientId"],
                    configuration["CrmClientSecret"],
                    useUniqueInstance: false));

            services.AddSingleton<BlobContainerClient>(sp =>
            {
                var containerName = configuration["BlobContainerName"];
                if (containerName == null)
                {
                    throw new Exception("Missing BlobContainerName configuration.");
                }

                var connectionString = configuration.GetConnectionString("BlobStorage");
                if (connectionString != null)
                {
                    return new BlobContainerClient(connectionString, containerName);
                }

                var storageAccountUri = configuration["BlobUri"];
                var sasToken = configuration["BlobSasToken"];
                if (storageAccountUri != null && sasToken != null)
                {
                    var blobContainerUri = new Uri(storageAccountUri.TrimEnd('/') + $"/{containerName}");
                    return new BlobContainerClient(blobContainerUri, new AzureSasCredential(sasToken));
                }

                throw new Exception("Missing configuration for blob storage.");
            });
        }))
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
    var rootCommand = new RootCommand(description: "DQT scripts");

    AddWhoAmICommand(rootCommand);
    AddBackupHusidsCommand(rootCommand);

    return rootCommand;
}

static void AddWhoAmICommand(RootCommand rootCommand)
{
    var command = new Command("whoami", description: "Prints the user ID.")
    {
        Handler = CommandHandler.Create<IHost>(async host =>
        {
            var serviceClient = host.Services.GetRequiredService<ServiceClient>();

            var response = (WhoAmIResponse)await serviceClient.ExecuteAsync(new WhoAmIRequest());
            Console.WriteLine(response.UserId);
        })
    };

    rootCommand.Add(command);
}

static void AddBackupHusidsCommand(RootCommand rootCommand)
{
    var command = new Command("backup-husids", description: "Creates a CSV backup of contacts with a dfeta_husid.")
    {
        Handler = CommandHandler.Create<IHost>(async host =>
        {
            var serviceClient = host.Services.GetRequiredService<ServiceClient>();
            var blobContainerClient = host.Services.GetRequiredService<BlobContainerClient>();

            await blobContainerClient.CreateIfNotExistsAsync();

            var backupBlobName = $"backup-husids/backup_{DateTime.Now:yyyyMMddHHmmss}.csv";
            var backupBlobClient = blobContainerClient.GetBlobClient(backupBlobName);

            // https://github.com/Azure/azure-sdk-for-net/pull/28148 - we have to pass options
            using (var blobStream = await backupBlobClient.OpenWriteAsync(overwrite: true, new Azure.Storage.Blobs.Models.BlobOpenWriteOptions()))
            using (var streamWriter = new StreamWriter(blobStream))
            using (var csvWriter = new CsvWriter(streamWriter, System.Globalization.CultureInfo.CurrentCulture))
            {
                csvWriter.WriteField("contactid");
                csvWriter.WriteField("dfeta_husid");
                csvWriter.WriteField("dfeta_trn");
                csvWriter.NextRecord();

                var query = new QueryExpression("contact");
                query.Criteria.AddCondition("dfeta_husid", ConditionOperator.NotNull);
                query.ColumnSet = new ColumnSet("contactid", "dfeta_husid", "dfeta_trn");
                query.PageInfo = new PagingInfo()
                {
                    Count = 1000,
                    PageNumber = 1
                };

                EntityCollection result;

                do
                {
                    result = await serviceClient.RetrieveMultipleAsync(query);

                    foreach (var record in result.Entities)
                    {
                        csvWriter.WriteField(record.GetAttributeValue<Guid>("contactid"));
                        csvWriter.WriteField(record.GetAttributeValue<string>("dfeta_husid"));
                        csvWriter.WriteField(record.GetAttributeValue<string>("dfeta_trn"));
                        csvWriter.NextRecord();
                    }

                    query.PageInfo.PageNumber++;
                    query.PageInfo.PagingCookie = result.PagingCookie;
                }
                while (result.MoreRecords);
            }
        })
    };

    rootCommand.Add(command);
}
