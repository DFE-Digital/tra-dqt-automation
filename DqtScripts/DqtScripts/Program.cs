using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

class Program
{
    static async Task Main(string[] args)
    {
        var host = Host.CreateDefaultBuilder()
            .ConfigureServices((hostContext, services) =>
            {
                services.AddTransient<DataService>();
            }).Build();
        
        var dataservice = host.Services.GetRequiredService<DataService>();
        var command = args.Length > 0 ? args[0] : String.Empty;
        await dataservice.ExecuteAsync(command);
    }
}

