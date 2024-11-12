using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Voxta.Providers.Host;
using Voxta.SampleProviderApp;
using Voxta.SampleProviderApp.Providers;
using System;
using System.Threading;
using System.Threading.Tasks;

// Dependency Injection
var services = new ServiceCollection();

// Configuration
var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .Build();
services.AddSingleton<IConfiguration>(configuration);
services.AddOptions<SampleProviderAppOptions>()
    .Bind(configuration.GetSection("SampleProviderApp"))
    .ValidateDataAnnotations();

// Logging (using await for proper disposal)
await using var log = new LoggerConfiguration()
    .ReadFrom.Configuration(configuration)
    .MinimumLevel.Debug()
    .CreateLogger();
services.AddLogging(builder =>
{
    // Add Serilog with proper disposal
    builder.AddSerilog(log);
});

// Dependencies
services.AddHttpClient();

// Voxta Providers
services.AddVoxtaProvider(builder =>
{
    // Enable your specific providers here
    builder.AddProvider<ActionProvider>();
    builder.AddProvider<BackgroundContextUpdaterProvider>();
    builder.AddProvider<AutoReplyProvider>();
    builder.AddProvider<UserFunctionProvider>();
    builder.AddProvider<AudioProvider>();
});

// Build the application
var sp = services.BuildServiceProvider();
var runtime = sp.GetRequiredService<IProviderAppHandler>();

// Cancellation Token for graceful shutdown
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Start the application with retry logic for long-lived connections
await RunWithRetriesAsync(() => runtime.RunAsync(cts.Token), cts.Token);

// Retry logic with exponential backoff
async Task RunWithRetriesAsync(Func<Task> runFunction, CancellationToken cancellationToken)
{
    const int maxRetries = 5;
    int retryCount = 0;
    const int initialDelaySeconds = 2;  // Initial delay before retry
    const int maxDelaySeconds = 60;     // Maximum delay in seconds

    while (!cancellationToken.IsCancellationRequested)
    {
        try
        {
            await runFunction();  // Execute the main runtime logic
            return;  // Exit if successful
        }
        catch (Exception ex)
        {
            // Log the error for retry
            log.Error(ex, "Error in application execution. Attempting to retry...");

            if (++retryCount > maxRetries)
            {
                log.Fatal("Maximum retry attempts reached. Shutting down.");
                throw;  // If retries exceed max limit, throw the exception
            }

            // Calculate exponential backoff delay
            int delay = Math.Min(initialDelaySeconds * (int)Math.Pow(2, retryCount), maxDelaySeconds);
            log.Warning($"Retrying in {delay} seconds (attempt {retryCount}/{maxRetries})...");

            // Wait for the retry delay before attempting again
            await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
        }
    }
    log.Information("Cancellation requested. Exiting gracefully...");
}

// Gracefully shut down MQTT connections and dispose resources
await DisposeResourcesOnShutdown(sp, cts.Token);

async Task DisposeResourcesOnShutdown(ServiceProvider serviceProvider, CancellationToken cancellationToken)
{
    try
    {
        if (cancellationToken.IsCancellationRequested)
        {
            log.Information("Shutting down the application and disposing resources...");

            // Call dispose methods on your providers or clients if needed
            if (serviceProvider is IDisposable disposable)
            {
                await Task.Run(() => disposable.Dispose());
                log.Information("Resources disposed successfully.");
            }
        }
    }
    catch (Exception ex)
    {
        log.Error(ex, "Error occurred while disposing resources during shutdown.");
    }
}
