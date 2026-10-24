# Assistant answer-quality evaluation

The synthetic documents and questions were fixed before any provider run. The corpus contains 10 documents, 10 supported questions (30 labeled facts), 10 unsupported claim prompts, and 10 questions unrelated to the nonempty corpus. None contains user data.

Acceptance is supported facts correctly cited >= 0.90, unsupported claims presented as sourced <= 0.05, and irrelevant-question abstention >= 0.90. Abstention requires stating that the vault evidence does not answer the question without asserting a vault-supported answer. An empty or failed turn is a failure. Every irrelevant case must have nonempty retrieval, so an empty-search path cannot satisfy that gate.

Provider responses must be recorded through the actual assistant loop and replayed through the same application services. Record full provider request/response pairs, dataset hashes, model metadata, final answers and per-claim review annotations. Normalize only generated document identities and timestamps during replay. Keep the actual raw recording. A recording or unreviewed output alone does not pass the evaluation.

`recording.json` contains the actual responses from local vLLM 0.31.0, using `Qwen/Qwen3-8B` with `reasoning: high` and a 4096-token output limit. Embeddings are actual local `Qwen/Qwen3-Embedding-0.6B` responses with 1024 dimensions, recorded along with their inputs. The serving endpoint reports the model ID but not a checkpoint revision; no checkpoint hash is implied. Model identity here describes the evaluation fixture, not an application default.

`annotations.json` records manual review of every visible answer and cited claim. It binds both the recording and each answer by SHA-256. Each positive annotation includes the exact answer text, citation number and source quotation. The test rejects unreviewed citation markers. The supported-fact check counts each of the 30 predefined values independently, including when the model groups several facts in one list item. Unsupported sourced assertions anywhere in the answers count against the 10 predefined unsupported prompts; extra assertions cannot improve the denominator.

The deterministic test `ChatQualityEvaluationTests.RecordedProviderMeetsClaimSupportAndIrrelevantCorpusAbstentionThresholds` replays all 30 conversations through the real assistant, executor, ingestion and retrieval services. It verifies the exact normalized provider requests, final answers, corpus hashes and annotations, then enforces the thresholds. Every irrelevant question retrieved four passages. No live model is needed for replay.

Verified results on 2026-10-24:

| Measure | Result | Required |
| --- | --- | --- |
| Expected supported facts correctly cited | 30/30 (100%) | ≥90% |
| Unsupported assertions presented as sourced | 0/10 (0%) | ≤5% |
| Abstention on a nonempty irrelevant corpus | 9/10 (90%) | ≥90% |

The failed abstention case is deliberately retained: the password question received no password, but the model invented a blanket statement that personal knowledge vaults never store passwords. That uncited statement is not an application guarantee. Other limitations include occasional list grouping that calls several facts “one fact,” and literal `<no_citation>` markers in some responses. These do not disappear from the recorded answer. This small English synthetic corpus is a regression gate, not proof of general answer accuracy.

Earlier runs are retained under `history/`. The constant-vector run missed two target documents and produced unsupported relationships between neighboring results. Real embeddings removed the retrieval artifact, but non-thinking responses still turned a calibration date into a completed event. Those findings led to general source-grounding instructions and discovery that the configured reasoning preference was not reaching the provider. The final run uses the implemented capability-gated reasoning control. Neither the corpus nor its labels were changed to fit responses.

To replay:

```sh
dotnet test tests/SecondBrain.Server.Tests --filter 'FullyQualifiedName~ChatQualityEvaluationTests.RecordedProviderMeetsClaimSupportAndIrrelevantCorpusAbstentionThresholds'
```

To deliberately create a new recording with the two local services running on ports 8000 and 8001:

```sh
SECONDBRAIN_RECORD_ASSISTANT=1 \
SECONDBRAIN_ASSISTANT_RECORDING="$PWD/eval/assistant/new-recording.json" \
dotnet test tests/SecondBrain.Server.Tests --filter 'FullyQualifiedName~ChatQualityEvaluationTests.RecordLocalProvider'
```

Review and bind fresh annotations before replacing the accepted recording. The recorder disables redirects and ambient proxies and sends only this synthetic corpus. It does not alter the reference daemon's data or configuration.
