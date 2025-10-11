# Return structured diagnostics (PluginOperationResultJson)

Use PluginOperationResultJson to emit consistent, machine- and human-friendly results from CLI commands or background jobs.

When to use
- Command-line tools and admin endpoints that need a uniform success/error shape
- Test or CI consumers that parse JSON

Shape
- success: true|false
- error: code, message (present on failures)
- data: arbitrary payload on success

Example (conceptual)
- Success: {"success":true,"data":{"items":42}}
- Failure: {"success":false,"error":{"code":"AuthFailed","message":"invalid credentials"}}

Guidance
- Prefer the JSON helper for public output; logs can include additional context.
- Keep error codes stable; messages can be localized.
- Do not include secrets in data or error text.

See also
- docs/Telemetry.md for emitting metrics alongside diagnostics
- docs/dev-guide/CI.md for surfacing results in PR checks

