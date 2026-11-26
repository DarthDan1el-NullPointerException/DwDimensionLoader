using DwDimensionLoader;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Host genérico: nos da configuración (appsettings.json), inyección de dependencias y ILogger.
var builder = Host.CreateApplicationBuilder(args);

// Formato de log simple con hora para que las capturas se vean claras.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

builder.Services.AddSingleton<DimensionLoader>();

using var host = builder.Build();

var loader = host.Services.GetRequiredService<DimensionLoader>();
var logger = host.Services.GetRequiredService<ILogger<Program>>();

try
{
    await loader.RunAsync();
    logger.LogInformation("Proceso finalizado correctamente.");
    return 0;
}
catch (Exception ex)
{
    logger.LogCritical(ex, "El proceso terminó con errores.");
    return 1;
}