using AetherPC.Application.Diagnostics;
using AetherPC.Application.Health;
using AetherPC.Application.Recommendations;
using AetherPC.Application.Scanning;
using AetherPC.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace AetherPC.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddAetherApplication(this IServiceCollection services)
    {
        services.AddSingleton<IHealthScorer, HealthScorer>();
        services.AddSingleton<IRecommendationEngine, RuleBasedRecommendationEngine>();
        services.AddSingleton<IPerformanceDiagnosis, PerformanceDiagnosis>();
        services.AddSingleton<ScanEngine>();
        return services;
    }
}