using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Hosting;
using System.CommandLine.NamingConventionBinder;
using System.CommandLine.Parsing;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.RegularExpressions;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using CsvHelper;
using CsvHelper.Configuration.Attributes;
using Dapper;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Npgsql;
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
    AddMoveIttRecordsToNewOrganisation(rootCommand);
    CreateTestUsers(rootCommand);
    AddBackfillIdentityAccountDataCommand(rootCommand);
    AddUpdateTeacherEmailAddress(rootCommand);

    return rootCommand;
}

static void AddBackfillIdentityAccountDataCommand(RootCommand rootCommand)
{
    var command = new Command("backfill-id-account-data", description: "Backfill data in DQT for contacts owned by identity")
    {
        Handler = CommandHandler.Create<IHost, bool?>(async (host, commit) =>
        {
            var configuration = host.Services.GetRequiredService<IConfiguration>();
            var identityConnectingString = configuration["IdentityConnectionString"];
            if (string.IsNullOrEmpty(identityConnectingString))
            {
                throw new Exception("Missing IdentityConnectionString configuration.");
            }

            var serviceClient = host.Services.GetRequiredService<ServiceClient>();
            var blobContainerClient = host.Services.GetRequiredService<BlobContainerClient>();

#if DEBUG
            await blobContainerClient.CreateIfNotExistsAsync();
#endif
            var backupBlobName = $"backfill-id-account-data/backfill-id-account-data_{DateTime.Now:yyyyMMddHHmmss}.csv";
            var backupBlobClient = blobContainerClient.GetBlobClient(backupBlobName);

            var contactsToUpdate = new Subject<(Guid ContactId, Guid IdentityUserId, string EmailAddress)>();

            // Batch Update requests in chunks of 10 and retry on failure
            var retryPolicy = Policy.Handle<Exception>().RetryAsync(retryCount: 5);

            var batchSubscription = contactsToUpdate.Buffer(10).Subscribe(async batch =>
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

                foreach (var contact in batch)
                {
                    var update = new Entity("contact")
                    {
                        Id = contact.ContactId
                    };
                    update["dfeta_tspersonid"] = contact.IdentityUserId.ToString();
                    update["emailaddress1"] = contact.EmailAddress;
                    update["dfeta_lastidentityupdate"] = DateTime.UtcNow;

                    request.Requests.Add(new UpdateRequest()
                    {
                        Target = update
                    });
                }

                await retryPolicy.ExecuteAsync(() => serviceClient.ExecuteAsync(request));
            },
            onError: ex =>
            {
                Console.Error.WriteLine(ex);
                Environment.Exit(1);
            });

            using var blobStream = await backupBlobClient.OpenWriteAsync(overwrite: true, new Azure.Storage.Blobs.Models.BlobOpenWriteOptions());
            using var streamWriter = new StreamWriter(blobStream);
            using var csvWriter = new CsvWriter(streamWriter, System.Globalization.CultureInfo.CurrentCulture);

            csvWriter.WriteField("contactid");
            csvWriter.WriteField("dfeta_trn");
            csvWriter.WriteField("emailaddress1");
            csvWriter.NextRecord();

            var con = new NpgsqlConnection(identityConnectingString);
            con.Open();
            var usersWithTrns = await con.QueryAsync<IdentityUser>(
@"
SELECT
    user_id as UserId,
    email_address as EmailAddress,
    trn as Trn
FROM
    users
WHERE
    trn IS NOT NULL
");

            foreach (var user in usersWithTrns)
            {
                var filter = new FilterExpression(LogicalOperator.And);
                filter.AddCondition("dfeta_trn", ConditionOperator.Equal, user.Trn);
                filter.AddCondition("dfeta_tspersonid", ConditionOperator.Null);

                var query = new QueryExpression("contact")
                {
                    ColumnSet = new ColumnSet("contactid", "emailaddress1", "dfeta_trn", "dfeta_tspersonid"),
                    Criteria = filter
                };

                var result = await serviceClient.RetrieveMultipleAsync(query);
                var existingContact = result.Entities.FirstOrDefault();
                if (existingContact != null)
                {
                    csvWriter.WriteField(existingContact.Id);
                    csvWriter.WriteField(existingContact["dfeta_trn"]);
                    csvWriter.WriteField(existingContact.Contains("emailaddress1") ? existingContact["emailaddress1"] : string.Empty);
                    csvWriter.NextRecord();

                    // Update with new values
                    if (commit == true)
                    {
                        contactsToUpdate.OnNext((existingContact.Id, user.UserId, user.EmailAddress));
                    }
                    else
                    {
#if DEBUG
                        Console.WriteLine($"DQT record for TRN {user.Trn} would get Email Address updated to {user.EmailAddress} if commit flag was set");
#endif
                    }
                }
                else
                {
#if DEBUG
                    Console.WriteLine($"No record without identity user id in DQT for TRN {user.Trn}");
#endif
                }
            }

            contactsToUpdate.OnCompleted();
            batchSubscription.Dispose();
        })
    };

    command.AddOption(new Option<bool>("--commit", "Commits changes to database"));
    rootCommand.Add(command);
}

static void CreateTestUsers(RootCommand rootCommand)
{
    var command = new Command("create-test-users", description: "Create Test user data")
    {
        Handler = CommandHandler.Create<IHost, int?>(async (host, users) =>
        {
            var serviceClient = host.Services.GetRequiredService<ServiceClient>();
            var blobContainerClient = host.Services.GetRequiredService<BlobContainerClient>();
#if DEBUG
            await blobContainerClient.CreateIfNotExistsAsync();
#endif
            var createUsersBlobName = $"testusers/testusers{DateTime.Now:yyyyMMddHHmmss}.csv";
            var createUsersBlobClient = blobContainerClient.GetBlobClient(createUsersBlobName);

            using (var blobStream = await createUsersBlobClient.OpenWriteAsync(overwrite: true, new Azure.Storage.Blobs.Models.BlobOpenWriteOptions()))
            using (var streamWriter = new StreamWriter(blobStream))
            using (var csvWriter = new CsvWriter(streamWriter, System.Globalization.CultureInfo.CurrentCulture))
            {
                for (var user = 0; user < users; user++)
                {
                    var firstName = Faker.Name.First();
                    var lastName = Faker.Name.Last();
                    var dob = Faker.DateOfBirth.Next();
                    var nino = GenerateValidNino();
                    var email = Faker.Internet.Email();

                    //Create contact request
                    var contactId = Guid.NewGuid();
                    var contact = new Entity("contact");
                    contact.Id = contactId;
                    contact["firstname"] = firstName;
                    contact["lastname"] = lastName;
                    contact["birthdate"] = dob;
                    contact["dfeta_ninumber"] = nino;
                    contact["emailaddress1"] = email;
                    var request = new ExecuteTransactionRequest()
                    {
                        Requests = new OrganizationRequestCollection()
                    };
                    request.Requests.Add(new CreateRequest()
                    {
                        Target = contact
                    });

                    //Allocate TRN request
                    var updatedContact = new Entity("contact");
                    updatedContact.Id = contactId;
                    updatedContact["dfeta_trnallocaterequest"] = DateTime.Now;
                    request.Requests.Add(new UpdateRequest()
                    {
                        Target = updatedContact
                    });

                    //ITT Record
                    var createITT = new Entity("dfeta_initialteachertraining");
                    createITT.Id = Guid.NewGuid();
                    createITT["dfeta_personid"] = new EntityReference("contact", contactId); ;
                    createITT["dfeta_countryid"] = new EntityReference("dfeta_country", Guid.Parse("e2ebefd4-c73b-e311-82ec-005056b1356a")); //uk
                    createITT["dfeta_cohortyear"] = "2021";
                    createITT["dfeta_establishmentid"] = new EntityReference("account", Guid.Parse("55948edc-c2ae-e311-b8ed-005056822391")); //Earl Spencer Primary School
                    createITT["dfeta_programmestartdate"] = DateTime.Now.AddYears(-1);
                    createITT["dfeta_programmeenddate"] = DateTime.Now.AddMonths(-1);
                    createITT["dfeta_subject1id"] = new EntityReference("dfeta_ittsubject", Guid.Parse("403c95fe-d9a3-e911-a963-000d3a28efb4")); //business studies
                    createITT["dfeta_result"] = new OptionSetValue(389040000); //pass
                    request.Requests.Add(new CreateRequest()
                    {
                        Target = createITT
                    });

                    // Retrieve the generated TRN
                    request.Requests.Add(new RetrieveRequest()
                    {
                        Target = new EntityReference("contact", contactId),
                        ColumnSet = new ColumnSet("dfeta_trn")
                    });

                    var txnResponse = (ExecuteTransactionResponse)await serviceClient.ExecuteAsync(request);
                    string trn = (string)((RetrieveResponse)txnResponse.Responses.Last()).Entity["dfeta_trn"];

                    //write csv
                    csvWriter.WriteField(firstName);
                    csvWriter.WriteField(lastName);
                    csvWriter.WriteField(dob);
                    csvWriter.WriteField(nino);
                    csvWriter.WriteField(email);
                    csvWriter.WriteField(trn);
                    csvWriter.NextRecord();
                }
            }
        })
    };
    command.AddOption(new Option<int?>(new[] { "--users", "-u" }, description: "number of users to create", getDefaultValue: () => { return 10; }));
    rootCommand.Add(command);

    string GenerateValidNino()
    {
        var nino = "";
        Regex regex = new Regex("^[ABCEGHJKLMNOPRSTWXYZ][ABCEGHJKLMNPRSTWXYZ][0-9]{6}[A-D ]$");
        while(!regex.IsMatch(nino))
        {
            nino = Faker.Identification.UKNationalInsuranceNumber();
        }
        return nino;
    }
}


static void AddMoveIttRecordsToNewOrganisation(RootCommand rootCommand)
{
    var command = new Command("move-itt-records-to-provider", description: "Change account that an itt record is associated with")
    {
        Handler = CommandHandler.Create<IHost, bool?>(async (host, commit) =>
        {
            var serviceClient = host.Services.GetRequiredService<ServiceClient>();
            var blobContainerClient = host.Services.GetRequiredService<BlobContainerClient>();
#if DEBUG
            await blobContainerClient.CreateIfNotExistsAsync();
#endif
            var backupBlobName = $"ittrecordsmovedorgs/ittrecordsmovedorgs{DateTime.Now:yyyyMMddHHmmss}.csv";
            var backupBlobClient = blobContainerClient.GetBlobClient(backupBlobName);

            // retry on failure
            var retryPolicy = Policy.Handle<Exception>().RetryAsync(retryCount: 5);
            var IttRecordsToUpdate = new Subject<Tuple<Guid, Guid>>(); //dfeta_establishmentid,IttId

            // update 10 records at a time
            var batchSubscription = IttRecordsToUpdate.Buffer(10).Subscribe(async recordList =>
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
                    var provider = new Entity("dfeta_initialteachertraining");
                    provider["dfeta_establishmentid"] = new EntityReference("account", record.Item1);
                    provider.Id = record.Item2;
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

            using (var blob = await blobContainerClient.GetBlobClient("itt_records_to_change_accounts.csv").OpenReadAsync())
            using (var reader = new StreamReader(blob))
            using (var csv = new CsvReader(reader, System.Globalization.CultureInfo.CurrentCulture))
            using (var blobStream = await backupBlobClient.OpenWriteAsync(overwrite: true, new Azure.Storage.Blobs.Models.BlobOpenWriteOptions()))
            using (var streamWriter = new StreamWriter(blobStream))
            using (var csvWriter = new CsvWriter(streamWriter, System.Globalization.CultureInfo.CurrentCulture))
            {
                var records = csv.GetRecords<MoveIttRecord>();
                foreach (var record in records)
                {
                    //fetch old values & write to csv file
                    var statusQuery = new QueryExpression("dfeta_initialteachertraining");
                    statusQuery.Criteria.AddCondition("dfeta_establishmentid", ConditionOperator.Equal, record.FromAccountId);
                    statusQuery.ColumnSet = new ColumnSet("dfeta_establishmentid");
                    statusQuery.PageInfo = new PagingInfo()
                    {
                        Count = 1000,
                        PageNumber = 1
                    };
                    var prevRecords = await serviceClient.RetrieveMultipleAsync(statusQuery);
                    foreach (var prevRecord in prevRecords.Entities)
                    {
                        //always write csv file
#if DEBUG
                        Console.WriteLine(((EntityReference)prevRecord["dfeta_establishmentid"]).Id.ToString());
#endif
                        csvWriter.WriteField(prevRecord.Id);
                        csvWriter.WriteField(prevRecord.Contains("dfeta_establishmentid") ? ((EntityReference)prevRecord["dfeta_establishmentid"]).Id.ToString() : string.Empty);
                        csvWriter.NextRecord();

                        //Update with new values
                        if (commit == true)
                        {
                            IttRecordsToUpdate.OnNext(Tuple.Create(record.ToAccountId, prevRecord.Id));
                        }
                    }
                }

                IttRecordsToUpdate.OnCompleted();
                batchSubscription.Dispose();
            }
        })
    };
    command.AddOption(new Option<bool>("--commit", "Commits changes to database"));
    rootCommand.Add(command);
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

            using (var blob = await blobContainerClient.GetBlobClient("accounts_to_rename.csv").OpenReadAsync())
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

static void AddUpdateTeacherEmailAddress(RootCommand rootCommand)
{
    var command = new Command("update-email-addresses", description: "Updates teacher email addresses from register, by providing a file")
    {
        Handler = CommandHandler.Create<IHost, bool?, string>(async (host, commit, filename) =>
        {
            var serviceClient = host.Services.GetRequiredService<ServiceClient>();
            var blobContainerClient = host.Services.GetRequiredService<BlobContainerClient>();
            if (string.IsNullOrEmpty(filename))
            {
                Console.Error.WriteLine("--filename parameter missing");
                Environment.Exit(1);
            }

#if DEBUG
            await blobContainerClient.CreateIfNotExistsAsync();
#endif

            var logBlobName = $"register-email-updates/log-{DateTime.Now:yyyyMMddHHmmss}.csv";
            var logBlobClient = blobContainerClient.GetBlobClient(logBlobName);

            var contactsToUpdate = new Subject<(Guid ContactId, string EmailAddress)>();
            var retryPolicy = Policy.Handle<Exception>().RetryAsync(retryCount: 5);
            var batchSubscription = contactsToUpdate.Buffer(10).Subscribe(async batch =>
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
                        Id = id.ContactId
                    };
                    update["emailaddress1"] = id.EmailAddress;

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


            using (var blob = await blobContainerClient.GetBlobClient(filename).OpenReadAsync())
            using (var reader = new StreamReader(blob))
            using (var csv = new CsvReader(reader, System.Globalization.CultureInfo.CurrentCulture))
            using (var blobStream = await logBlobClient.OpenWriteAsync(overwrite: true, new Azure.Storage.Blobs.Models.BlobOpenWriteOptions()))
            using (var streamWriter = new StreamWriter(blobStream))
            using (var csvWriter = new CsvWriter(streamWriter, System.Globalization.CultureInfo.CurrentCulture))
            {
                var records = csv.GetRecords<UpdateEmailAddress>();
                foreach (var record in records)
                {
                    //fetch ids of contacts to change email
                    var query = new QueryExpression("contact");
                    query.ColumnSet = new ColumnSet("contactid", "emailaddress1", "dfeta_trn", "dfeta_tspersonid");
                    query.Criteria.AddCondition("dfeta_trn", ConditionOperator.Equal, record.TRN);
                    query.Criteria.AddCondition("contactid", ConditionOperator.Equal, record.ContactId);
                    query.PageInfo = new PagingInfo()
                    {
                        Count = 1,
                        PageNumber = 1
                    };

                    EntityCollection result;
                    do
                    {
                        result = await serviceClient.RetrieveMultipleAsync(query);
                        foreach (var entity in result.Entities)
                        {
                            //batch updates
                            var id = entity.GetAttributeValue<Guid>("contactid");
                            var tspersonid = entity.GetAttributeValue<string>("dfeta_tspersonid");
                            var email = entity.GetAttributeValue<string>("emailaddress1");
                            if (commit == true && string.IsNullOrEmpty(tspersonid) && !record.EmailAddress.Equals(email, StringComparison.InvariantCultureIgnoreCase))
                            {
                                contactsToUpdate.OnNext((id, record.EmailAddress));
                            }

                            //write records that have been updated to csv
                            csvWriter.WriteField(id);
                            csvWriter.WriteField(record.TRN);
                            csvWriter.WriteField(record.EmailAddress);
                            csvWriter.NextRecord();
                        }

                        query.PageInfo.PageNumber++;
                        query.PageInfo.PagingCookie = result.PagingCookie;
                    }
                    while (result.MoreRecords);
                }
                contactsToUpdate.OnCompleted();
                contactsToUpdate.Dispose();
            }
        })
    };
    command.AddOption(new Option<bool>("--commit", "Commits changes to database"));
    command.AddOption(new Option<string>("--filename", "File that contains email updates"));
    rootCommand.Add(command);
}

public class UpdateEmailAddress
{
    [Name("TRN")]
    public string TRN { get; set; }
    [Name("EmailAddress")]
    public string EmailAddress { get; set; }

    [Name("ContactId")]
    public string ContactId { get; set; }
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

public class MoveIttRecord
{
    [Name("FromAccountId")]
    public Guid FromAccountId { get; set; }
    [Name("ToAccountId")]
    public Guid ToAccountId { get; set; }
}

public class IdentityUser
{
    public Guid UserId { get; set; }

    public string EmailAddress { get; set; } = null!;

    public string? Trn { get; set; }
}
