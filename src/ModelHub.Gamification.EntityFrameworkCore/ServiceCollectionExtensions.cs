using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ModelHub.Gamification.EntityFrameworkCore;

/// <summary>Registers the points engine and services.</summary>
public static class GamificationServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="IPointsEngine"/>, <see cref="IWalletService"/>, <see cref="IRewardService"/> and
    /// <see cref="IProgramService"/>, scoped, working through your <typeparamref name="TContext"/>.
    /// Register the DbContext as usual and call <c>modelBuilder.ApplyGamificationModel()</c> in it.
    /// </summary>
    /// <typeparam name="TContext">Your DbContext.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Default rules, time zone, levels and redemption settings.</param>
    /// <exception cref="InvalidOperationException">Thrown when a service is first resolved if the options are not valid.</exception>
    public static IServiceCollection AddGamification<TContext>(
        this IServiceCollection services, Action<GamificationOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.AddOptions<GamificationOptions>();
        if (configure is not null) options.Configure(configure);
        options.PostConfigure(o => o.Validate());

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IPointsEngine, PointsEngine<TContext>>();
        services.TryAddScoped<IWalletService, WalletService<TContext>>();
        services.TryAddScoped<IRewardService, RewardService<TContext>>();
        services.TryAddScoped<IProgramService, ProgramService<TContext>>();
        return services;
    }
}
