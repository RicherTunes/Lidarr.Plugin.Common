# ADR-002: Subscription Authentication Research

**Status:** Research Complete
**Date:** 2026-01-30
**Decision Makers:** Project maintainers

## Context

Users have asked about "subscription login" for LLM providers - using existing subscription accounts (OpenAI Plus, Claude Pro, Gemini Advanced) instead of separate API keys.

This research spike evaluates the feasibility and compliance of each approach.

## Provider Analysis

### Claude Code CLI (Anthropic)

**Status:** Supported via Claude Code CLI

| Aspect | Finding |
|--------|---------|
| Official Support | Yes - Claude Code CLI uses OAuth from `~/.claude/.credentials.json` |
| ToS Compliance | Yes - Official tool with sanctioned authentication |
| Implementation | Subprocess-based via ICliRunner, or HTTP with OAuth tokens |
| Streaming | CLI subprocess streams naturally via stdout |

**Recommendation:** Use Claude Code CLI or HTTP with OAuth tokens from credentials file.

### OpenAI (GPT-4, ChatGPT Plus)

**Status:** API Key Only (Officially)

| Aspect | Finding |
|--------|---------|
| Official Support | No - No official OAuth flow for consumer subscriptions |
| ChatGPT Plus | Web-only, no API access without separate API billing |
| Codex CLI | Uses separate `~/.codex/auth.json` but this is for API, not Plus |
| ToS Risk | Scraping ChatGPT web sessions would violate ToS |

**Recommendation:** API key remains the only compliant path. Do not attempt to bridge ChatGPT Plus subscriptions.

### Google Gemini

**Status:** API Key Only (Officially)

| Aspect | Finding |
|--------|---------|
| Official Support | No - Gemini API uses API keys, not Google account OAuth |
| Gemini Advanced | Consumer subscription is web-only, no API bridge |
| Google AI Studio | Provides API keys for developers (free tier available) |
| Vertex AI | Enterprise path with GCP service accounts |

**Recommendation:** API key via Google AI Studio. Gemini Advanced subscriptions cannot be used for API access.

### Z.AI GLM

**Status:** API Key Only

| Aspect | Finding |
|--------|---------|
| Official Support | API key authentication only |
| Free Tier | glm-4.7-flash available at no cost |
| Subscription | No consumer subscription program exists |

**Recommendation:** API key is the only path.

## Decision

| Provider | Auth Method | Rationale |
|----------|-------------|-----------|
| **Claude** | OAuth via credentials file | Official Claude Code CLI writes tokens |
| **OpenAI** | API key | No official consumer OAuth; ToS prohibits workarounds |
| **Gemini** | API key | No consumer OAuth; free tier available |
| **Z.AI GLM** | API key | Only supported method |
| **OpenRouter** | API key | Aggregator, own billing system |
| **DeepSeek** | API key | Only supported method |
| **Groq** | API key | Only supported method |

## Consequences

### Positive

- ToS compliance across all providers
- Clear, documented authentication paths
- No risk of account bans or service termination
- Simpler implementation (no web scraping or reverse engineering)

### Negative

- Users with consumer subscriptions cannot leverage them for API access
- Separate billing for API usage vs web chat

### Accepted Trade-offs

- Claude Code subscription users get seamless auth via credentials file
- Other providers require explicit API key setup
- This matches what each provider officially supports

## Implementation Notes

### Claude Code Credentials

```json
// ~/.claude/.credentials.json
{
  "claudeAiOauth": {
    "accessToken": "...",
    "expiresAt": 1234567890000,
    "refreshToken": "...",
    "subscriptionType": "max"
  }
}
```

Brainarr's `SubscriptionCredentialLoader` already handles this format.

### API Key Providers

All other providers use standard API key authentication:
- OpenAI: `Authorization: Bearer sk-...`
- Gemini: `?key=...` query parameter
- Z.AI: `Authorization: Bearer ...`

## Future Considerations

If OpenAI or Google release official OAuth flows for consumer subscriptions:
1. Update this ADR
2. Implement the official flow
3. Add migration path for existing API key users

Until then, API key authentication remains the compliant path.

## Decision Criteria for Future Auth Modes

Before implementing any subscription-based auth:

| Criterion | Requirement |
|-----------|-------------|
| **Official Documentation** | Provider must document the auth flow publicly |
| **ToS Explicit Permit** | Terms of Service must not prohibit programmatic access |
| **Stable API Surface** | Auth endpoints must be versioned or declared stable |
| **No Web Scraping** | Implementation must not rely on browser session cookies |
| **Revocation Path** | Users must be able to revoke access without provider support |
| **Secret Handling** | No long-lived refresh tokens stored unencrypted; must use OS keychain or CLI-managed credentials |
| **Supportability** | Must provide deterministic health check and clear "how to revoke" documentation |

**Go/No-Go Gate**: All 7 criteria must pass before any code is written.

## References

- [OpenAI API Authentication](https://platform.openai.com/docs/api-reference/authentication)
- [Gemini API Quickstart](https://ai.google.dev/docs/gemini_api_overview)
- [Claude Code CLI](https://docs.anthropic.com/claude/reference/getting-started-with-the-api)
- [Z.AI GLM API](https://open.bigmodel.cn/dev/api)
