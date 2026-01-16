using System;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Security.SecretProtection;
using Lidarr.Plugin.Common.Security.TokenProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Lidarr.Plugin.Common.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddTokenProtection(this IServiceCollection services)
        {
            services.AddSingleton<ITokenProtector>(_ => TokenProtectorFactory.CreateFromEnvironment());
            return services;
        }

        public static IServiceCollection AddTokenProtection(this IServiceCollection services, Func<IServiceProvider, ITokenProtector> factory)
        {
            services.AddSingleton<ITokenProtector>(factory);
            return services;
        }

        public static IServiceCollection AddSecretProtection(this IServiceCollection services)
        {
            services.AddSingleton<ISecretProtector>(sp => new SecretProtector(sp.GetRequiredService<ITokenProtector>()));
            return services;
        }

        public static IServiceCollection AddSecretProtection(this IServiceCollection services, Func<IServiceProvider, byte[]?> legacyAesGcmKeyFactory)
        {
            services.AddSingleton<ISecretProtector>(sp =>
            {
                var tokenProtector = sp.GetRequiredService<ITokenProtector>();
                var legacyKey = legacyAesGcmKeyFactory(sp);
                return new SecretProtector(tokenProtector, legacyKey);
            });
            return services;
        }
    }
}
