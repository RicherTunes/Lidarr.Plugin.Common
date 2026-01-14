using System;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Security.TokenProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Lidarr.Plugin.Common.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddTokenProtection(this IServiceCollection services)
        {
            services.AddSingleton<ITokenProtector>(_ => TokenProtectorFactory.CreateFromEnvironment());
            services.AddSingleton<IStringTokenProtector, StringTokenProtector>();
            return services;
        }

        public static IServiceCollection AddTokenProtection(this IServiceCollection services, Func<IServiceProvider, ITokenProtector> factory)
        {
            services.AddSingleton<ITokenProtector>(factory);
            services.AddSingleton<IStringTokenProtector, StringTokenProtector>();
            return services;
        }
    }
}
