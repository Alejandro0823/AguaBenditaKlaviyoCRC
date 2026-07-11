using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using KlaviyoCRC;

var builder = Host.CreateApplicationBuilder(args);

// Colorea cada línea del log de consola según la marca (BrandOptions.Code) en ejecución.
builder.Logging.AddConsole(options => options.FormatterName = BrandConsoleFormatter.FormatterName)
    .AddConsoleFormatter<BrandConsoleFormatter, ConsoleFormatterOptions>();

// Register dependencies
builder.Services.AddTransient<IKlaviyoCustomerFetcher, KlaviyoCustomerFetcher>();
builder.Services.AddTransient<ICrcApiTokenService, CrcApiTokenService>();
builder.Services.AddTransient<ICrcEmailValidationService, CrcEmailValidationService>();
builder.Services.AddTransient<ICrcPhoneValidationService, CrcPhoneValidationService>();
builder.Services.AddTransient<IKlaviyoEmailExclusionSyncService, KlaviyoEmailExclusionSyncService>();
builder.Services.AddSingleton<IProcessExecutor, ProcessExecutor>();
builder.Services.AddHostedService<SchedulerService>();

var host = builder.Build();
await host.RunAsync();
