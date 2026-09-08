# OpenAI Chat Base Original Contract Map

## Scope

This maps every named test in Brainarr's
`Brainarr.Tests/Providers/Llm/OpenAiChatProviderBaseContractTests.cs` at
`gitea/main` commit `8264de06` to the Common extraction. The source fixture
contains fifteen named tests. The Common fixture deliberately has broader
contracts for cancellation, streaming, redaction, and empty-response handling;
those additions are not counted as replacements for an original case here.

| # | Brainarr original test | Common contract | Evidence |
| --- | --- | --- | --- |
| 1 | `Temperature_OmittedWhenPolicySaysNo` | `CompleteAsync_OmitsTemperatureAndResponseFormatWhenHooksDisableThem` | Existing Common fixture |
| 2 | `Temperature_SentByDefault_UsingProviderDefault` | `Temperature_SentByDefault_UsingProviderDefault` | `OpenAiOriginalContractParityTests` |
| 3 | `Temperature_RequestValueWinsOverDefault` | `Temperature_RequestValueWinsOverDefault` | `OpenAiOriginalContractParityTests` |
| 4 | `JsonMode_OmittedWhenProviderDoesNotSupportResponseFormat` | `CompleteAsync_OmitsTemperatureAndResponseFormatWhenHooksDisableThem` | Existing Common fixture |
| 5 | `JsonMode_EmitsResponseFormat_WhenSupported` | `JsonMode_EmitsResponseFormat_WhenSupported` | `OpenAiOriginalContractParityTests` |
| 6 | `RequestBody_PinsWireShape_FieldOrderAndDefaults` | `CompleteAsync_SendsThePinnedDefaultWireBody` | Existing Common fixture |
| 7 | `SystemPrompt_EmittedAsFirstMessage` | `SystemPrompt_EmittedAsFirstMessage` | `OpenAiOriginalContractParityTests` |
| 8 | `CompleteAsync_UsesBearerAuth_AndExtraHeaders` | `CompleteAsync_UsesBearerAuth_AndExtraHeaders` | `OpenAiOriginalContractParityTests` |
| 9 | `MapHttpError_OverrideWins` | `CompleteAsync_UsesTheProviderErrorMapperBeforeTheDefaultMapper` | Existing Common fixture |
| 10 | `AuthFailure_RecordsToCircuit_AndPreflightRejects` | `AuthFailure_RecordsToCircuit_AndPreflightRejects` | `OpenAiOriginalContractParityTests` |
| 11 | `HealthProbe_DefaultBody_UsesReplyWithOkAndFiveTokens` | `HealthProbe_DefaultBody_UsesReplyWithOkAndFiveTokens` | `OpenAiOriginalContractParityTests` |
| 12 | `ParseCompletion_Default_MapsContentFinishReasonAndUsage` | `CompleteAsync_ParsesFinishReasonUsageAndMalformedSalvage` | Existing Common fixture |
| 13 | `ParseCompletion_MalformedJson_FallsBackToRawContent` | `CompleteAsync_ParsesFinishReasonUsageAndMalformedSalvage` | Existing Common fixture |
| 14 | `UpdateModel_MapsThroughModelIdMapper` | `UpdateModel_MapsThroughNormalizeModel` | `OpenAiOriginalContractParityTests`; the Brainarr adapter supplies `NormalizeModel` |
| 15 | `Constructor_EmptyApiKey_Throws` | `Constructor_EmptyApiKey_Throws` | `OpenAiOriginalContractParityTests` |

The original test fixture does not assert transport-thrown exception identity.
That extraction-specific transport seam is covered separately by
`CompleteAsync_SafeMappedExceptionRetainsOriginalIdentityAndInner` in the Common
fixture, so it is not represented as an original-test equivalent.

## Receipts

`artifacts/shared-openai-chat/green-original-contract-parity-only.trx` records
the nine added parity cases passing (9 passed, 0 failed, 0 skipped). The final
combined receipt, `green-openai-original-parity-and-contracts.trx`, records
the nine parity cases plus the existing provider and surface contracts (56
passed, 0 failed, 0 skipped) after source fix `85ed570`. The earlier broader
run is retained separately as `red-existing-openai-contract-regressions.trx`:
it found the two source regressions fixed by `85ed570`, while all nine parity
cases already passed in that run.

The reflection surface receipt is recorded separately because it must execute
against untouched Common `gitea/main` rather than this extracted candidate.
