# Documentation Hub

Pick the guide that matches what you need to do.

## Quick start

- [Build your first plugin](quickstart/PLUGIN_AUTHOR.md)
- [Load plugins safely in the host](quickstart/HOST_MAINTAINER.md)

## Tutorials

- [Build your first plugin from scratch](tutorials/BUILD_YOUR_FIRST_PLUGIN.md) - Complete 30-minute tutorial

## Concepts (know how it works)

- [Architecture](concepts/ARCHITECTURE.md)
- [Plugin isolation](PLUGIN_ISOLATION.md)
- [Compatibility matrix](COMPATIBILITY.md)

## How-to (do the thing)

- [Create a plugin project](how-to/CREATE_PLUGIN.md)
- [Test a plugin with the isolation loader](how-to/TEST_PLUGIN.md)
- [Leverage the Common TestKit](how-to/TEST_WITH_TESTKIT.md)
- [Implement an indexer](how-to/IMPLEMENT_INDEXER.md)
- [Use the shared download orchestrator](how-to/USE_DOWNLOAD_ORCHESTRATOR.md)
- [Authenticate with OAuth](how-to/AUTHENTICATE_OAUTH.md)
- [Manage sessions/tokens with StreamingTokenManager](how-to/TOKEN_MANAGER.md)
- [Add structured logging](how-to/ADD_LOGGING.md)
- [Return structured diagnostics (PluginOperationResultJson)](how-to/DIAGNOSTICS_JSON.md)
- [Cache GETs and enable 304 revalidation](how-to/HTTP_CACHING_AND_REVALIDATION.md)
- [Troubleshoot common issues](how-to/TROUBLESHOOTING_COMMON.md)

## Reference (facts)

- [Abstractions package overview](ABSTRACTIONS.md)
- [Manifest schema](PLUGIN_MANIFEST.md)
- [Settings and configuration keys](reference/SETTINGS.md)
- [Public API baselines](reference/PUBLIC_API_BASELINES.md)
- [Key services and utilities](reference/KEY_SERVICES.md)
- [Resilience policy reference](reference/RESILIENCE_POLICY.md)
- [LLM provider system](providers/LLM_PROVIDERS.md) - Claude Code and other AI providers
- [Streaming plugins glossary](GLOSSARY.md) - Common terminology

## Migration

- [Upgrade from legacy loader/shared Common](migration/FROM_LEGACY.md)
- [Breaking changes archive](migration/BREAKING_CHANGES.md)

## Examples

- [Isolation host sample walkthrough](examples/ISOLATION_HOST_SAMPLE.md)

## Maintainers & contributors

- [Developer guide](dev-guide/DEVELOPER_GUIDE.md)
- [Release policy](dev-guide/RELEASE_POLICY.md)
- [CI & automation](dev-guide/CI.md)
- [Unified plugin pipeline](dev-guide/UNIFIED_PLUGIN_PIPELINE.md)
- [Multi-plugin smoke test](MULTI_PLUGIN_SMOKE_TEST.md)
- [Documentation style guide](dev-guide/STYLE_GUIDE.md)
- [Doc testing & snippet verification](dev-guide/TESTING_DOCS.md)

## Plugins using this library

These production plugins are built on Lidarr.Plugin.Common:

| Plugin | Description | Repository |
|--------|-------------|------------|
| **Brainarr** | AI-powered music recommendations | [RicherTunes/brainarr](https://github.com/RicherTunes/brainarr) |
| **Tidalarr** | Tidal streaming integration | [RicherTunes/tidalarr](https://github.com/RicherTunes/tidalarr) |
| **Qobuzarr** | Qobuz high-res audio integration | [RicherTunes/qobuzarr](https://github.com/RicherTunes/qobuzarr) |

Need something else? Open an issue or PR so the documentation evolves with the code.



