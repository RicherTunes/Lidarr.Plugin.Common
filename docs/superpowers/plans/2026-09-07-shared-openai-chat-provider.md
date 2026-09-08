# Shared OpenAI Chat Provider Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract Brainarr's OpenAI Chat Completions plumbing into a NET 8 Common base while preserving each provider's wire contract through explicit hooks.

**Architecture:** The Common base owns ordered body construction, credential/circuit orchestration, response parsing, health checks, and SSE decoding. A transport-neutral request/response seam lets Brainarr adapt its host `IHttpClient` and existing streaming executor without changing host timeout, SSRF, or error behavior; Common provides the generic `HttpClient` implementation for future consumers.

**Tech Stack:** .NET 8, `System.Net.Http`, `System.Text.Json`, xUnit, existing Common LLM abstractions, error mapper, and OpenAI SSE decoder.

**Spec:** `C:/Users/Alexandre/.codex/attachments/fb8c1179-6887-42e0-b409-c242d6295262/pasted-text-1.txt`

## Global Constraints

- NET 8 only; do not add packages.
- Preserve serialized order: `model`, `messages`, `temperature` when enabled, `max_tokens`, `stream`, then `response_format` when enabled.
- Preserve Bearer auth, hook header precedence, timeout/cancellation behavior, error-map precedence, and malformed-response raw-body fallback.
- Keep Brainarr-only hints, model mapping, capabilities, registry, and host transport out of Common.
- Commit contract tests before production code and retain TRX/log receipts under `artifacts/shared-openai-chat`.

---

### Task 1: Pin the Common public contract

**Files:**
- Create: `tests/Providers/OpenAi/OpenAiChatProviderBaseSurfaceContractTests.cs`
- Create: `docs/superpowers/plans/2026-09-07-shared-openai-chat-provider.md`

**Interfaces:**
- Produces: runtime-verified requirements for `OpenAiChatProviderBase`, `IOpenAiChatTransport`, and `IOpenAiChatAuthCircuit` in `Lidarr.Plugin.Common.Providers.OpenAi`.

- [ ] **Step 1: Write the failing reflection contract test**

Assert the required type names and hook methods from the compiled Common assembly so the absent feature fails as an assertion rather than a compile error.

- [ ] **Step 2: Run the test and record the expected red**

Run: `dotnet test tests/Lidarr.Plugin.Common.Tests.csproj -c Release --filter FullyQualifiedName~OpenAiChatProviderBaseSurfaceContractTests --logger "trx;LogFileName=red-surface-contract.trx" --results-directory artifacts/shared-openai-chat`

Expected: FAIL because `Lidarr.Plugin.Common.Providers.OpenAi.OpenAiChatProviderBase` is absent.

- [ ] **Step 3: Commit the tests**

Run: `git add docs/superpowers/plans/2026-09-07-shared-openai-chat-provider.md tests/Providers/OpenAi/OpenAiChatProviderBaseSurfaceContractTests.cs && git commit -m "test: pin shared OpenAI chat provider surface"`

### Task 2: Implement the transport-neutral base

**Files:**
- Create: `src/Providers/OpenAi/OpenAiChatProviderBase.cs`
- Create: `src/Providers/OpenAi/IOpenAiChatTransport.cs`
- Create: `src/Providers/OpenAi/IOpenAiChatAuthCircuit.cs`
- Create: `src/Providers/OpenAi/SystemHttpOpenAiChatTransport.cs`

**Interfaces:**
- Consumes: `ILlmProvider`, `LlmRequest`, `LlmResponse`, `LlmErrorMapper`, `OpenAiStreamDecoder`.
- Produces: an abstract `OpenAiChatProviderBase` with quirk hooks and an ordered wire body.

- [ ] **Step 1: Implement only the types required by the red surface contract**

- [ ] **Step 2: Run the surface contract and verify green**

- [ ] **Step 3: Replace reflection tests with typed contract tests before extending behavior**

### Task 3: Port behavioral contracts

**Files:**
- Modify: `tests/Providers/OpenAi/OpenAiChatProviderBaseSurfaceContractTests.cs`
- Create: `tests/Providers/OpenAi/OpenAiChatProviderBaseContractTests.cs`

**Interfaces:**
- Consumes: `OpenAiChatProviderBase` and a scripted transport.
- Produces: typed tests for request body, headers, mapping, auth circuit, health, parse, and streaming.

- [ ] **Step 1: Add one failing typed behavior test at a time**

- [ ] **Step 2: Run each test to confirm a behavioral red**

- [ ] **Step 3: Make the minimal implementation change and run green**

- [ ] **Step 4: Repeat timing-sensitive circuit/cancellation tests ten times and preserve the receipts**

### Task 4: Validate and hand off for Brainarr adoption

**Files:**
- Modify: `CHANGELOG.md`

**Interfaces:**
- Produces: a documented Common contract and a committed candidate for the Brainarr factory-adoption worktree.

- [ ] **Step 1: Run default and CLI suites, build, analyzer formatting, and documentation/hygiene gates**

- [ ] **Step 2: Record failures that are pre-existing or environment-specific without suppressing them**

- [ ] **Step 3: Commit production and documentation changes separately from the test-only commit**
