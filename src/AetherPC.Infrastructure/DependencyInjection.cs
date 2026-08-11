using AetherPC.Core.Abstractions;
using AetherPC.Core.Localization;
using AetherPC.Infrastructure.Benchmarks;
using AetherPC.Infrastructure.Cleanup;
using AetherPC.Infrastructure.Data;
using AetherPC.Infrastructure.Localization;
using AetherPC.Infrastructure.Sensors;
using AetherPC.Infrastructure.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace AetherPC.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddAetherInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<ILocalizer, CatalogLocalizer>();
        services.AddSingleton<SqliteStore>();
        services.AddSingleton<IHistoryStore>(sp => sp.GetRequiredService<SqliteStore>());
        services.AddSingleton<IAppSettingsStore>(sp => sp.GetRequiredService<SqliteStore>());
        services.AddSingleton<ISensorService, LibreHardwareSensorService>();
        services.AddSingleton<ISystemScanner, WindowsSystemScanner>();
        services.AddSingleton<IProcessService, WindowsProcessService>();
        services.AddSingleton<IServiceEnumerator, WindowsServiceEnumerator>();
        services.AddSingleton<IStartupService, WindowsStartupService>();
        services.AddSingleton<IDriverService, WindowsDriverService>();
        services.AddSingleton<IPrivilegeService, WindowsPrivilegeService>();
        services.AddSingleton<IRestorePointService, WindowsRestorePointService>();
        services.AddSingleton<ICleanupService, WindowsCleanupService>();
        services.AddSingleton<IBenchmarkService, LocalBenchmarkService>();
        services.AddSingleton<IDisplayControlService, WindowsDisplayControlService>();
        services.AddSingleton<IGameLibraryService, AetherPC.Infrastructure.Games.WindowsGameLibraryService>();
        return services;
    }
}
