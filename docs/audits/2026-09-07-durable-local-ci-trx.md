# Durable local CI TRX receipts

## Contract

`scripts/local-ci.ps1` must retain the TRX files used to calculate its test summary. It creates a unique default run directory under `artifacts/local-ci/`, or uses the directory named by `LIDARR_LOCAL_CI_RESULTS_DIR`. Every test project publishes its TRX before the temporary VSTest directory is removed. Project, UTC timestamp, and GUID form the receipt name, preventing repeat or concurrent runs from overwriting evidence.

The runner parses counters from the published file rather than the temporary source. Missing TRX, an invalid/unwritable destination, failed copy, or malformed retained TRX fails the deterministic-test stage. Failed test executions still publish their TRX before the stage reports failure. The runner never deletes the durable destination.

Gitea CI runs the real success/failure/skip receipt contract explicitly. `scripts/tests/Test-LocalCiTrxWorkflowWiring.ps1` guards that invocation against accidental removal or duplication.

This changes evidence retention only. It does not change dependencies, deterministic filters, skip behavior, warning budgets, package gates, or test result classification.

## TDD evidence

Test-only commit `1cbb77f3956089f083eba25e743b6d0ee31f2dcf` introduced `scripts/tests/Test-LocalCiTrxRetention.ps1`. The initial run failed because the receipt module did not exist; `artifacts/shared-openai-chat/trx-retention-red.log` records that expected red. The test drives real successful and intentionally failing .NET test projects, then verifies retained files, literal counters, collision-free repeated publication, and fail-closed handling of an invalid destination.

The first implementation run exposed a test-fixture error: the installed MSTest template no longer contained the assumed `Assert.Fail()` placeholder, so the intended failing input stayed green. That failed attempt is preserved in `artifacts/shared-openai-chat/trx-retention-green.log`. The fixture now writes an explicit failing test; the corrected contract run is preserved in `artifacts/shared-openai-chat/trx-retention-green-final.log`.

A parent review then identified that real xUnit TRX may report skipped cases as `UnitTestResult outcome="NotExecuted"` while leaving the aggregate `notExecuted` counter at zero. A real one-skip xUnit fixture failed the initial parser as recorded in `artifacts/shared-openai-chat/trx-skipped-accounting-red.log`. The parser now uses the greater of the aggregate counter and explicit not-executed result count, matching the retained evidence without double counting.

Independent adversarial review, remote CI, consumer adoption, consumer full pipelines, and post-merge receipts remain separate gates and are not claimed here.
