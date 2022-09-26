using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Hosting;
using System.CommandLine.NamingConventionBinder;
using System.CommandLine.Parsing;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.RegularExpressions;
using Azure;
using Azure.Storage.Blobs;
using CsvHelper;
using CsvHelper.Configuration.Attributes;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Polly;

var result = await CreateCommandLineBuilder()
    .UseHost((IHostBuilder host) => host
        .ConfigureAppConfiguration(config => config
            .AddUserSecrets(typeof(Program).Assembly, optional: true)
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

Environment.Exit(result);

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
    AddCleanseHusidCommand(rootCommand);
    AddCleanseTraineeIdCommand(rootCommand);
    AddCombineHesaAndDmsTeacherStatusesCommand(rootCommand);
    AddCombineRenameDMSTraineeTeacherStatusCommand(rootCommand);
    AddMasterChemistryMasterPhysicsQualifications(rootCommand);
    AddBulkUpdateIttProvidersCommand(rootCommand);

    return rootCommand;
}

static void AddBulkUpdateIttProvidersCommand(RootCommand rootCommand)
{
    var command = new Command("bulk-rename-itt-accounts", description: "Bulk renames Itt Provider details from an csv file")
    {
        Handler = CommandHandler.Create<IHost, bool?>(async (host, commit) =>
        {
            var serviceClient = host.Services.GetRequiredService<ServiceClient>();
            var blobContainerClient = host.Services.GetRequiredService<BlobContainerClient>();
#if DEBUG
            await blobContainerClient.CreateIfNotExistsAsync();
#endif
            var backupBlobName = $"renameaccounts/renamedaccounts_{DateTime.Now:yyyyMMddHHmmss}.csv";
            var backupBlobClient = blobContainerClient.GetBlobClient(backupBlobName);

            // retry on failure
            var retryPolicy = Policy.Handle<Exception>().RetryAsync(retryCount: 5);
            var accountsToUpdate = new Subject<BulkUpdateIttProvider>();

            // update 10 records at a time
            var batchSubscription = accountsToUpdate.Buffer(10).Subscribe(async recordList =>
            {
                var request = new ExecuteMultipleRequest()
                {
                    Requests = new OrganizationRequestCollection(),
                    Settings = new ExecuteMultipleSettings()
                    {
                        ContinueOnError = false,
                        ReturnResponses = false
                    }
                };

                foreach (var record in recordList)
                {
                    var provider = new Entity("account");
                    provider.Id = record.Id;
                    provider["name"] = record.NewName;
                    provider["dfeta_ukprn"] = record.NewUkPrn;
                    provider["dfeta_urn"] = record.NewUrn;
                    provider["dfeta_laschoolcode"] = record.NewLaSchoolCode;

                    request.Requests.Add(new UpdateRequest()
                    {
                        Target = provider
                    });
                }
                await retryPolicy.ExecuteAsync(async () => await serviceClient.ExecuteAsync(request));
            },
            onError: ex =>
            {
                Console.Error.WriteLine(ex);
                Environment.Exit(1);
            });

            using(var blob = await blobContainerClient.GetBlobClient("accounts_to_rename.csv").OpenReadAsync())
            using (var reader = new StreamReader(blob))
            using (var csv = new CsvReader(reader, System.Globalization.CultureInfo.CurrentCulture))
            using (var blobStream = await backupBlobClient.OpenWriteAsync(overwrite: true, new Azure.Storage.Blobs.Models.BlobOpenWriteOptions()))
            using (var streamWriter = new StreamWriter(blobStream))
            using (var csvWriter = new CsvWriter(streamWriter, System.Globalization.CultureInfo.CurrentCulture))
            {

                var records = csv.GetRecords<BulkUpdateIttProvider>();
                foreach (var record in records)
                {
                    //fetch old values & write to csv file
                    var statusQuery = new QueryExpression("account");
                    statusQuery.Criteria.AddCondition("accountid", ConditionOperator.Equal, record.Id);
                    statusQuery.ColumnSet = new ColumnSet("dfeta_ukprn", "dfeta_urn", "name", "dfeta_laschoolcode");
                    statusQuery.PageInfo = new PagingInfo()
                    {
                        Count = 1,
                        PageNumber = 1
                    };
                    var prevRecords = await serviceClient.RetrieveMultipleAsync(statusQuery);
                    foreach (var prevRecord in prevRecords.Entities)
                    {
                        //always write csv file
                        csvWriter.WriteField(prevRecord.Id);
                        csvWriter.WriteField(prevRecord.Contains("dfeta_ukprn") ? prevRecord["dfeta_ukprn"] : string.Empty);
                        csvWriter.WriteField(prevRecord.Contains("dfeta_urn") ? prevRecord["dfeta_urn"] : string.Empty);
                        csvWriter.WriteField(prevRecord.Contains("name") ? prevRecord["name"] : string.Empty);
                        csvWriter.WriteField(prevRecord.Contains("dfeta_laschoolcode") ? prevRecord["dfeta_laschoolcode"] : string.Empty);
                        csvWriter.NextRecord();
                    }

                    //Update with new values
                    if (commit == true)
                    {
                        accountsToUpdate.OnNext(record);
                    }
                }
                accountsToUpdate.OnCompleted();
                batchSubscription.Dispose();
            }
        })
    };
    command.AddOption(new Option<bool>("--commit", "Commits changes to database"));
    rootCommand.Add(command);
}

static void AddMasterChemistryMasterPhysicsQualifications(RootCommand rootCommand)
{
    var command = new Command("add-physics-chemistry-qualifications", description: "Adds Master Physics & Master Chemistry & Bachelor of Arts in Combined Studies HE Qualifications")
    {
        Handler = CommandHandler.Create<IHost, bool?>(async (host, commit) =>
        {
            var serviceClient = host.Services.GetRequiredService<ServiceClient>();

            //Fetch existing
            var qualificationsQuery = new QueryExpression("dfeta_hequalification");
            qualificationsQuery.Criteria.AddCondition("dfeta_value", ConditionOperator.In, new string[] { "214", "215", "009" });
            qualificationsQuery.ColumnSet = new ColumnSet("dfeta_value", "dfeta_name");
            qualificationsQuery.PageInfo = new PagingInfo()
            {
                Count = 3,
                PageNumber = 1
            };
            var heQualifications = await serviceClient.RetrieveMultipleAsync(qualificationsQuery);
            var existingMoChemistry = heQualifications.Entities.SingleOrDefault(x => x["dfeta_value"].ToString() == "214");
            var existingMoPhysics = heQualifications.Entities.SingleOrDefault(x => x["dfeta_value"].ToString() == "215");
            var existingBoArts = heQualifications.Entities.SingleOrDefault(x => x["dfeta_value"].ToString() == "009");


            //Master of Chemistry
            if (existingMoChemistry?.Id == null)
            {
                var MoChemistry = new Entity("dfeta_hequalification");
                MoChemistry["dfeta_name"] = "Master of Chemistry";
                MoChemistry["dfeta_value"] = "214";
                MoChemistry.Id = Guid.Parse("abad6e0f-8315-410c-a9bc-b3b984030526");
                await serviceClient.CreateAsync(MoChemistry);
            }

            //Master of Physics
            if (existingMoPhysics?.Id == null)
            {
                var MoPhysics = new Entity("dfeta_hequalification");
                MoPhysics["dfeta_name"] = "Master of Physics";
                MoPhysics["dfeta_value"] = "215";
                MoPhysics.Id = Guid.Parse("ef7db3fb-e855-466d-bea5-d4b95a77b51b");
                await serviceClient.CreateAsync(MoPhysics);
            }

            //Bachelor of Arts in Combined Studies / Education of the Deaf
            if (existingBoArts?.Id == null)
            {
                var BoArts = new Entity("dfeta_hequalification");
                BoArts["dfeta_name"] = "Bachelor of Arts in Combined Studies / Education of the Deaf";
                BoArts["dfeta_value"] = "009";
                BoArts.Id = Guid.Parse("5b2d3d73-243a-4ecb-a719-525d298d3971");
                await serviceClient.CreateAsync(BoArts);
            }

        })
    };
    rootCommand.Add(command);
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

static void AddCombineRenameDMSTraineeTeacherStatusCommand(RootCommand rootCommand)
{
    var command = new Command("rename-dms-traineeteacher", description: "Renames DMS Trainee Teacher Status (211) to Trainee Teacher")
    {
        Handler = CommandHandler.Create<IHost, bool?>(async (host, commit) =>
        {
            var serviceClient = host.Services.GetRequiredService<ServiceClient>();
            var blobContainerClient = host.Services.GetRequiredService<BlobContainerClient>();

#if DEBUG
            await blobContainerClient.CreateIfNotExistsAsync();
#endif
            var backupBlobName = $"renameddmstraineeteacher/renamedteacherstatuses_{DateTime.Now:yyyyMMddHHmmss}.csv";
            var backupBlobClient = blobContainerClient.GetBlobClient(backupBlobName);

            // https://github.com/Azure/azure-sdk-for-net/pull/28148 - we have to pass options
            using (var blobStream = await backupBlobClient.OpenWriteAsync(overwrite: true, new Azure.Storage.Blobs.Models.BlobOpenWriteOptions()))
            using (var streamWriter = new StreamWriter(blobStream))
            using (var csvWriter = new CsvWriter(streamWriter, System.Globalization.CultureInfo.CurrentCulture))
            {
                csvWriter.WriteField("id");
                csvWriter.WriteField("dfeta_name");
                csvWriter.NextRecord();

                //get only active statuses with dfeta_value of 211 (Hesa Trainee Teacher)
                var statusQuery = new QueryExpression("dfeta_teacherstatus");
                statusQuery.Criteria.AddCondition("dfeta_value", ConditionOperator.Equal, "211");
                statusQuery.ColumnSet = new ColumnSet("dfeta_value", "dfeta_name");
                statusQuery.PageInfo = new PagingInfo()
                {
                    Count = 2,
                    PageNumber = 1
                };
                var results = await serviceClient.RetrieveMultipleAsync(statusQuery);

                foreach (var record in results.Entities)
                {
                    //always write csv file
                    csvWriter.WriteField(record.Id);
                    csvWriter.WriteField(record["dfeta_name"]);
                    csvWriter.NextRecord();

                    if (commit == true)
                    {
                        var teacherstatus = new Entity("dfeta_teacherstatus");
                        teacherstatus.Id = record.Id;
                        teacherstatus["dfeta_name"] = "Trainee Teacher";
                        await serviceClient.UpdateAsync(teacherstatus);
                    }
                }
            }
        })
    };
    command.AddOption(new Option<bool>("--commit", "Commits changes to database"));
    rootCommand.Add(command);
}

static void AddCombineHesaAndDmsTeacherStatusesCommand(RootCommand rootCommand)
{
    var command = new Command("combine-teacherstatuses", description: "Combines Trainee Teacher - DMS (211) & Trainee Teacher - Hesa (211) statuses into one status Trainee Teacher (211)")
    {
        Handler = CommandHandler.Create<IHost, bool?>(async (host, commit) =>
        {
            var serviceClient = host.Services.GetRequiredService<ServiceClient>();
            var blobContainerClient = host.Services.GetRequiredService<BlobContainerClient>();

#if DEBUG
            await blobContainerClient.CreateIfNotExistsAsync();
#endif
            var backupBlobName = $"combine-hesa-dms-ids/combined-hesa-dms-teacherstatuses_{DateTime.Now:yyyyMMddHHmmss}.csv";
            var backupBlobClient = blobContainerClient.GetBlobClient(backupBlobName);
            var idsToBeCleansed = new Subject<Guid>();

            //fetch 210,211 statuses
            var statusQuery = new QueryExpression("dfeta_teacherstatus");
            statusQuery.Criteria.AddCondition("dfeta_value", ConditionOperator.In, new string[] { "211", "210" });
            statusQuery.ColumnSet = new ColumnSet("dfeta_value", "dfeta_name");
            statusQuery.PageInfo = new PagingInfo()
            {
                Count = 2,
                PageNumber = 1
            };
            var statusResults = await serviceClient.RetrieveMultipleAsync(statusQuery);
            var traineeTeacherHESA = statusResults.Entities.SingleOrDefault(x => x["dfeta_value"].ToString() == "210");
            var traineeTeacherDMS = statusResults.Entities.SingleOrDefault(x => x["dfeta_value"].ToString() == "211");

            //return if status 210 or 211 is not found
            if (traineeTeacherHESA?.Id == null || traineeTeacherDMS?.Id == null)
                throw new Exception("No trainee teacher status ids found.");


            // Batch Update requests in chunks of 10 and retry on failure
            var retryPolicy = Policy.Handle<Exception>().RetryAsync(retryCount: 5);

            var batchSubscription = idsToBeCleansed.Buffer(10).Subscribe(async batch =>
            {
                var request = new ExecuteMultipleRequest()
                {
                    Requests = new OrganizationRequestCollection(),
                    Settings = new ExecuteMultipleSettings()
                    {
                        ContinueOnError = false,
                        ReturnResponses = false
                    }
                };

                foreach (var id in batch)
                {
                    var update = new Entity("dfeta_qtsregistration")
                    {
                        Id = id
                    };
                    update["dfeta_teacherstatusid"] = new EntityReference("dfeta_teacherstatus", traineeTeacherDMS.Id);

                    request.Requests.Add(new UpdateRequest()
                    {
                        Target = update
                    });
                }

                await retryPolicy.ExecuteAsync(async () => await serviceClient.ExecuteAsync(request));
            },
            onError: ex =>
            {
                Console.Error.WriteLine(ex);
                Environment.Exit(1);
            });

            // https://github.com/Azure/azure-sdk-for-net/pull/28148 - we have to pass options
            using (var blobStream = await backupBlobClient.OpenWriteAsync(overwrite: true, new Azure.Storage.Blobs.Models.BlobOpenWriteOptions()))
            using (var streamWriter = new StreamWriter(blobStream))
            using (var csvWriter = new CsvWriter(streamWriter, System.Globalization.CultureInfo.CurrentCulture))
            {
                csvWriter.WriteField("qtsregistrationid");
                csvWriter.WriteField("personid");
                csvWriter.WriteField("dfeta_teacherstatusid");
                csvWriter.NextRecord();

                //Fetch qts records with trainee teacher status : Hesa
                var query = new QueryExpression("dfeta_qtsregistration");
                query.Criteria.AddCondition("dfeta_teacherstatusid", ConditionOperator.Equal, traineeTeacherHESA?.Id);
                query.ColumnSet = new ColumnSet("dfeta_personid", "dfeta_qtsregistrationid", "dfeta_teacherstatusid");
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
                        //always write csv file
                        csvWriter.WriteField(record.GetAttributeValue<Guid>("dfeta_qtsregistrationid"));
                        csvWriter.WriteField(record.GetAttributeValue<EntityReference>("dfeta_personid").Id);
                        csvWriter.WriteField(record.GetAttributeValue<EntityReference>("dfeta_teacherstatusid").Id);
                        csvWriter.NextRecord();

#if DEBUG
                        Console.WriteLine($"{record["dfeta_qtsregistrationid"]} - {record.GetAttributeValue<EntityReference>("dfeta_personid").Id} - {record.GetAttributeValue<EntityReference>("dfeta_teacherstatusid").Id} ");
#endif

                        if (commit == true)
                        {
                            idsToBeCleansed.OnNext(record.Id);
                        }
                    }
                    query.PageInfo.PageNumber++;
                    query.PageInfo.PagingCookie = result.PagingCookie;
                }
                while (result.MoreRecords);
            }
            idsToBeCleansed.OnCompleted();
            batchSubscription.Dispose();
        })
    };
    command.AddOption(new Option<bool>("--commit", "Commits changes to database"));

    rootCommand.Add(command);
}


static void AddCleanseHusidCommand(RootCommand rootCommand)
{
    var command = new Command("cleanse-husids", description: "Nulls out HusId where Husid field contains DttpId or DmsId")
    {
        Handler = CommandHandler.Create<IHost, bool?>(async (host, commit) =>
        {
            var serviceClient = host.Services.GetRequiredService<ServiceClient>();
            var blobContainerClient = host.Services.GetRequiredService<BlobContainerClient>();

#if DEBUG
            await blobContainerClient.CreateIfNotExistsAsync();
#endif

            var backupBlobName = $"cleanse-husids/cleanse-husids_{DateTime.Now:yyyyMMddHHmmss}.csv";
            var backupBlobClient = blobContainerClient.GetBlobClient(backupBlobName);
            var validHusidPattern = @"^\d{13}$";

            var idsToBeCleansed = new Subject<Guid>();

            // Batch Update requests in chunks of 10 and retry on failure
            var retryPolicy = Policy.Handle<Exception>().RetryAsync(retryCount: 5);

            var batchSubscription = idsToBeCleansed.Buffer(10).Subscribe(async batch =>
            {
                var request = new ExecuteMultipleRequest()
                {
                    Requests = new OrganizationRequestCollection(),
                    Settings = new ExecuteMultipleSettings()
                    {
                        ContinueOnError = false,
                        ReturnResponses = false
                    }
                };

                foreach (var id in batch)
                {
                    var update = new Entity("contact")
                    {
                        Id = id
                    };
                    update["dfeta_husid"] = null;

                    request.Requests.Add(new UpdateRequest()
                    {
                        Target = update
                    });
                }

                await retryPolicy.ExecuteAsync(() => serviceClient.ExecuteAsync(request));
            });

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
                        var husId = record.GetAttributeValue<string>("dfeta_husid");

                        if (!Regex.IsMatch(husId, validHusidPattern))
                        {
#if DEBUG
                            Console.WriteLine(husId);
#endif

                            if (commit == true)
                            {
                                idsToBeCleansed.OnNext(record.Id);
                            }

                            //always write csv file
                            csvWriter.WriteField(record.GetAttributeValue<Guid>("contactid"));
                            csvWriter.WriteField(record.GetAttributeValue<string>("dfeta_husid"));
                            csvWriter.WriteField(record.GetAttributeValue<string>("dfeta_trn"));
                            csvWriter.NextRecord();
                        }
                    }

                    query.PageInfo.PageNumber++;
                    query.PageInfo.PagingCookie = result.PagingCookie;
                }
                while (result.MoreRecords);
            }

            idsToBeCleansed.OnCompleted();

            batchSubscription.Dispose();
        })
    };

    command.AddOption(new Option<bool>("--commit", "Commits changes to database"));

    rootCommand.Add(command);
}

static void AddCleanseTraineeIdCommand(RootCommand rootCommand)
{
    var command = new Command("cleanse-traineeids", description: "Nulls out HusId where Husid field contains DttpId or DmsId")
    {
        Handler = CommandHandler.Create<IHost, bool?>(async (host, commit) =>
        {
            var serviceClient = host.Services.GetRequiredService<ServiceClient>();
            var blobContainerClient = host.Services.GetRequiredService<BlobContainerClient>();

#if DEBUG
            await blobContainerClient.CreateIfNotExistsAsync();
#endif

            var backupBlobName = $"cleanse-traineeids/cleanse-traineeids_{DateTime.Now:yyyyMMddHHmmss}.csv";
            var backupBlobClient = blobContainerClient.GetBlobClient(backupBlobName);
            var validHusidPattern = @"^\d{13}$";

            var idsToBeCleansed = new Subject<Guid>();

            // Batch Update requests in chunks of 10 and retry on failure
            var retryPolicy = Policy.Handle<Exception>().RetryAsync(retryCount: 5);

            var batchSubscription = idsToBeCleansed.Buffer(10).Subscribe(async batch =>
            {
                var request = new ExecuteMultipleRequest()
                {
                    Requests = new OrganizationRequestCollection(),
                    Settings = new ExecuteMultipleSettings()
                    {
                        ContinueOnError = false,
                        ReturnResponses = false
                    }
                };

                foreach (var id in batch)
                {
                    var update = new Entity("dfeta_initialteachertraining")
                    {
                        Id = id
                    };
                    update["dfeta_traineeid"] = null;

                    request.Requests.Add(new UpdateRequest()
                    {
                        Target = update
                    });
                }

                await retryPolicy.ExecuteAsync(() => serviceClient.ExecuteAsync(request));
            });

            // https://github.com/Azure/azure-sdk-for-net/pull/28148 - we have to pass options
            using (var blobStream = await backupBlobClient.OpenWriteAsync(overwrite: true, new Azure.Storage.Blobs.Models.BlobOpenWriteOptions()))
            using (var streamWriter = new StreamWriter(blobStream))
            using (var csvWriter = new CsvWriter(streamWriter, System.Globalization.CultureInfo.CurrentCulture))
            {
                csvWriter.WriteField("id");
                csvWriter.WriteField("dfeta_initialteachertrainingid");
                csvWriter.WriteField("dfeta_traineeid");
                csvWriter.NextRecord();

                var query = new QueryExpression("dfeta_initialteachertraining");
                query.Criteria.AddCondition("dfeta_traineeid", ConditionOperator.NotNull);
                query.ColumnSet = new ColumnSet("dfeta_initialteachertrainingid", "dfeta_traineeid");
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
                        var traineeid = record.GetAttributeValue<string>("dfeta_traineeid");

                        if (!Regex.IsMatch(traineeid, validHusidPattern))
                        {
#if DEBUG
                            Console.WriteLine(traineeid);
#endif

                            if (commit == true)
                            {
                                idsToBeCleansed.OnNext(record.Id);
                            }

                            //always write csv file
                            csvWriter.WriteField(record.Id);
                            csvWriter.WriteField(record.GetAttributeValue<Guid>("dfeta_initialteachertrainingid"));
                            csvWriter.WriteField(record.GetAttributeValue<string>("dfeta_traineeid"));
                            csvWriter.NextRecord();
                        }
                    }

                    query.PageInfo.PageNumber++;
                    query.PageInfo.PagingCookie = result.PagingCookie;
                }
                while (result.MoreRecords);

                idsToBeCleansed.OnCompleted();

                batchSubscription.Dispose();
            }
        })
    };

    command.AddOption(new Option<bool>("--commit", "Commits changes to database"));

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

#if DEBUG
            await blobContainerClient.CreateIfNotExistsAsync();
#endif

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

public class BulkUpdateIttProvider
{
    [Name("guid")]
    public Guid Id { get; set; }

    [Name("new_name")]
    public string NewName { get; set; }

    [Name("new_la_school_code")]
    public string NewLaSchoolCode { get; set; }

    [Name("new_ukprn")]
    public string NewUkPrn { get; set; }

    [Name("new_urn")]
    public string NewUrn { get; set; }

}
