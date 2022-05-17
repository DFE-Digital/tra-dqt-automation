using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

class DataService
{
    private readonly ILogger<DataService> _logger;
    private readonly IConfiguration _configuration;

    public DataService(ILogger<DataService> logger, IConfiguration config)
    {
        _logger = logger;
        _configuration = config;
    }

    public async Task ExecuteAsync(string command, CancellationToken stoppingToken = default)
    {
        switch (command?.ToLower())
        {
            case "husid":
                {
                    try
                    {
                        using (var _client = new ServiceClient(
                                new Uri(_configuration["CRM_URL"]),
                                _configuration["CRM_APP_ID"],
                                _configuration["CRM_APP_SECRET"],
                                useUniqueInstance: true))
                        {
                            var query = new QueryExpression("contact")
                            {
                                ColumnSet = new ColumnSet("dfeta_husid", "dfeta_dmsid", "dfeta_hesaid", "dfeta_dttpid"),

                            };

                            var hesaId = "";
                            var dttpId   = "";
                            var dmsId = "";


                            var filter = new FilterExpression();
                            filter.AddCondition("dfeta_husid", ConditionOperator.NotNull);
                            query.Criteria.AddFilter(filter);

                            EntityCollection results = await _client.RetrieveMultipleAsync(query);
                            results.Entities.ToList().ForEach(x =>
                            {
                                var entity = new Entity("contact");
                                entity.Id = x.Id;

                                entity["dfeta_dmsid"] = dmsId;
                                entity["dfeta_hesaid"] = hesaId;
                                entity["dfeta_dttpid"] = dttpId;

                                _client.Update(entity);
                            });

                        }
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e.Message);
                    }

                    break;
                }
            default:
                break;
        }
    }
}