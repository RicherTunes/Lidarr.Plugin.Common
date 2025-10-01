# Settings Provider Bridge

Plugins receive settings from the host as a flat dictionary (`IDictionary<string, object?>`). The common library maps those values to strongly typed POCOs using dot notation and culture-invariant parsing.

## Dot-notation mapping

```csharp file=../examples/PluginBridgeSample/SettingsDotNotationSample.cs#settings-dot-notation
```

Conversion rules:

- Nested properties use `.` (e.g., `OAuth.ClientId`).
- Arrays/lists use zero-based indexes (e.g., `Qualities[0]`).
- Enums parse by name (case-insensitive).
- Numbers, `DateTime`, `TimeSpan`, and `Guid` use invariant culture.

## Typed settings example

```csharp file=../examples/PluginBridgeSample/SettingsDotNotationSample.cs#settings-roundtrip
```

Example dictionary payload:

```json
{
  "ApiKey": "abc123",
  "OAuth.ClientId": "my-client",
  "OAuth.ClientSecret": "super-secret",
  "OAuth.Scope": "ReadWrite",
  "Retry.Attempts": 4,
  "Retry.Backoff": "00:00:05"
}
```

Use the bridge inside your module or orchestrator:

```csharp
var current = Services.GetRequiredService<MyServiceSettings>();
DotNotation.Apply(settingsDictionary, current);
```

## Pitfalls handled for you

- **Culture variance:** Parsing always uses `CultureInfo.InvariantCulture` to avoid `,` vs `.` issues.
- **Nullables:** Missing keys leave default values; explicit `null` clears nullable properties.
- **Enums:** Invalid enum strings raise `InvalidOperationException` so the host can surface a friendly validation message.
- **Collections:** The bridge expects dense indexes; validate gaps before saving.

## Contract

- Settings classes must expose public get/set properties; fields are ignored.
- Use simple types, enums, `Guid`, `DateTime`, `TimeSpan`, or nested POCOs.
- Provide defaults either via auto-property initialisers or an explicit configuration method.
- Avoid mutable static state; settings objects are per-plugin instances resolved from DI.
- Always validate user input and return `PluginValidationResult` with actionable messages.
