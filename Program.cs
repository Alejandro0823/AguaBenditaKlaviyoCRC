using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using KlaviyoCRC;

var builder = Host.CreateApplicationBuilder(args);

// Register dependencies
builder.Services.AddTransient<IKlaviyoCustomerFetcher, KlaviyoCustomerFetcher>();
builder.Services.AddTransient<ICrcEmailValidationService, CrcEmailValidationService>();
builder.Services.AddSingleton<IProcessExecutor, ProcessExecutor>();
builder.Services.AddHostedService<SchedulerService>();

var host = builder.Build();
await host.RunAsync();
