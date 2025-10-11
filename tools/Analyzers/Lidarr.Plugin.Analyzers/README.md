# Lidarr.Plugin.Analyzers

Guidance analyzers for plugin authors:
- LPC0001: Avoid raw HttpClient.SendAsync; use builder + ExecuteWithResilienceAsync or SendWithResilienceAsync.
- LPC0002: Prefer policy-based overload of ExecuteWithResilienceAsync (or use SendWithResilienceAsync).

Install via NuGet (development dependency) in your plugin project and fix warnings.
