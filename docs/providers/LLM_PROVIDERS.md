# LLM Provider System

The Common library provides a standardized interface for integrating Large Language Model providers into Lidarr plugins.

## Provider Interface

All LLM providers implement `ILlmProvider`:

```csharp
public interface ILlmProvider
{
    string ProviderId { get; }
    string DisplayName { get; }
    LlmProviderCapabilities Capabilities { get; }

    Task<ProviderHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<LlmStreamChunk>? StreamAsync(LlmRequest request, CancellationToken cancellationToken = default);
}
```

## Available Providers

### Cloud Providers (API Key Auth)

| Provider | Provider ID | Default Model | Max Context | Notes |
|----------|-------------|---------------|-------------|-------|
| OpenAI | `openai` | gpt-4o | 128K | |
| Anthropic | `anthropic` | claude-3-5-sonnet-20241022 | 200K | |
| Gemini | `gemini` | gemini-2.0-flash-exp | 1M | |
| Z.AI GLM | `zai` | glm-4.7-flash | 128K-200K | Free tier available |
| Perplexity | `perplexity` | sonnet-small | - | Web-aware fallback |
| Groq | `groq` | llama-3.3-70b | - | Low-latency batches |
| DeepSeek | `deepseek` | deepseek-chat | - | Budget-friendly option |
| OpenRouter | `openrouter` | anthropic/claude-3-sonnet | - | Gateway to many models |

### Local Providers

| Provider | Provider ID | Default Model | Setup |
|----------|-------------|---------------|-------|
| Ollama | `ollama` | qwen2.5:latest | `ollama pull <model>` |
| LM Studio | `lmstudio` | (user selected) | Run LM Studio API server |

### Subscription Providers (CLI Auth)

| Provider | Provider ID | Models | Credentials |
|----------|-------------|--------|-------------|
| Claude Code | `claude-code` | sonnet, opus, haiku | `~/.claude/.credentials.json` |
| OpenAI Codex | `openai-codex` | gpt-4o | `~/.codex/auth.json` |

---

## Claude Code CLI Provider

| Property | Value |
|----------|-------|
| Provider ID | `claude-code` |
| Display Name | Claude Code CLI |
| Authentication | OAuth (user runs `claude login`) |
| Models | `sonnet`, `opus`, `haiku` |
| Max Context | 200,000 tokens |
| Max Output | 8,192 tokens |

**Features:**
- Capability probing via `--help` parsing
- Conditional flags based on CLI version
- Safe defaults when capabilities unknown
- Structured logging with correlation IDs
- Rate limit and auth error mapping

**Requirements:**
- Claude Code CLI installed (`claude --version`)
- User authenticated (`claude login`)

**Usage:**
```csharp
var provider = new ClaudeCodeProvider(cliRunner, detector, settings, logger);

// Health check
var health = await provider.CheckHealthAsync();

// Completion
var response = await provider.CompleteAsync(new LlmRequest
{
    Prompt = "Recommend 5 jazz albums",
    SystemPrompt = "You are a music expert",
    Model = "sonnet"
});
```

## Capability Detection

The `ClaudeCodeCapabilities` class probes the CLI's `--help` output to detect supported flags:

| Capability | Flag | Description |
|------------|------|-------------|
| `SupportsAllowedTools` | `--allowedTools` | Restrict tool access |
| `SupportsMaxTurns` | `--max-turns` | Limit conversation turns |
| `SupportsOutputFormat` | `--output-format` | JSON output mode |
| `SupportsAppendSystemPrompt` | `--append-system-prompt` | System prompt injection |
| `SupportsModel` | `--model` | Model selection |
| `SupportsNoSessionPersistence` | `--no-session-persistence` | Stateless requests |

**Safe Defaults:** When capability detection fails, conservative defaults are used that only assume basic stable flags.

## Error Handling

Providers map errors to standardized exception types:

| Error Type | Exception Class | Retryable |
|------------|-----------------|-----------|
| Authentication | `AuthenticationException` | No |
| Rate Limit | `RateLimitException` | Yes |
| Timeout | `ProviderException` (Timeout) | Yes |
| Provider Unavailable | `ProviderException` (Unavailable) | Yes |
| Invalid Request | `ProviderException` (InvalidRequest) | No |

## Structured Logging

All providers use consistent structured logging via `LlmLoggerExtensions`:

```
[Plugin] Provider Operation started | CorrelationId=abc123 Model=sonnet
[Plugin] Provider Operation completed | CorrelationId=abc123 ElapsedMs=1234
[Plugin] Provider rate limited | CorrelationId=abc123 RetryAfterMs=60000
```

### Event IDs

| Event | ID | Description |
|-------|-----|-------------|
| AUTH_SUCCESS | 2000 | Authentication succeeded |
| AUTH_FAIL | 2001 | Authentication failed |
| REQUEST_START | 2010 | Request started |
| REQUEST_COMPLETE | 2011 | Request completed |
| REQUEST_ERROR | 2012 | Request failed |
| RATE_LIMITED | 2020 | Rate limit hit |
| HEALTH_CHECK_PASS | 2030 | Health check passed |
| HEALTH_CHECK_FAIL | 2031 | Health check failed |

## Adding New Providers

1. Implement `ILlmProvider` interface
2. Use `LlmLoggerExtensions` for consistent logging
3. Map errors to standard exception types
4. Add capability detection if provider has version-specific features
5. Write contract tests using `ProviderContractTestBase<T>`

See `ClaudeCodeProvider` for a reference implementation.
