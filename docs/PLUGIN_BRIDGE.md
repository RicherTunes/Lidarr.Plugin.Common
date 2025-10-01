# Streaming Plugin Bridge

`StreamingPlugin<TModule, TSettings>` lets a plugin focus on service wiring while the common library handles host interactions. Pair it with a module (for DI) and a typed settings record.

## Quickstart

```csharp file=../examples/PluginBridgeSample/MyServicePlugin.cs#bridge-plugin
```

The base class registers plugin metadata and exposes helper methods (logging, settings bridge, host info). Choose a human-readable service name to appear in logs.

## Register services

```csharp file=../examples/PluginBridgeSample/MyServicePlugin.cs#bridge-module
```

Use standard `IServiceCollection` registrations. The module can implement `IAsyncStartable`/`IAsyncStoppable` for warm-up or graceful shutdown hooks.

## Typed settings

```csharp file=../examples/PluginBridgeSample/MyServicePlugin.cs#bridge-settings
```

The library injects `ISettingsProviderBridge<TSettings>` so settings arrive as strongly typed instances. See [SETTINGS_PROVIDER.md](SETTINGS_PROVIDER.md) for dot-notation rules.

## Contract

- Derive exactly one plugin class per assembly from `StreamingPlugin<TModule,TSettings>`.
- Modules configure dependency injection and may implement lifecycle hooks.
- Settings types should be POCOs with get/set properties; defaults belong in the constructor or auto-property initialisers.
- Avoid capturing the host service provider; rely on constructor injection from your module.
- Keep HTTP clients named (e.g., `AddHttpClient("myservice")`) to reuse resilience policies.
