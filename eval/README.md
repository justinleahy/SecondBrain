# Retrieval seed evaluation

The 32 authored documents in `documents.jsonl` and 16 labeled questions in `questions.jsonl` were fixed before requesting embeddings or inspecting rankings. They contain synthetic, non-private material, with several related documents per topic. Labels were not adjusted to fit results.

`embeddings.json` records 48 actual embeddings from the existing local vLLM `Qwen/Qwen3-Embedding-0.6B` service, using model snapshot `97b0c614be4d77ee51c0cef4e5f07c00f9eb65b32`. Each vector is 1024 float32 values encoded as little-endian base64. The recording includes the provider/model/revision, dimensions, normalization and input-template descriptor, descriptor fingerprint, hashes of both source files, and the exact input hash for every vector. Document input is `title + "\n" + content`; query input is the raw question, matching the current pipeline. No query instruction prefix was added.

The deterministic Storage test `RetrievalEvaluationTests.SeedRecallAt8Meets085` captures the documents through the real repository, saves vectors through the real embedding repository, activates the space, and runs production keyword, semantic and hybrid retrieval. It verifies source/input hashes and requires hybrid recall@8 ≥0.85. It uses no live provider during testing.

```sh
dotnet test tests/SecondBrain.Storage.Tests --filter 'FullyQualifiedName~RetrievalEvaluationTests' \
  --logger 'console;verbosity=detailed'
```

Recorded results on 2026-10-09:

| Mode | Recall@5 | Recall@8 | MRR@8 |
| --- | ---: | ---: | ---: |
| Keyword | 1.0000 | 1.0000 | 0.9500 |
| Semantic | 1.0000 | 1.0000 | 1.0000 |
| Hybrid | 1.0000 | 1.0000 | 1.0000 |

MRR is truncated at the eight returned results. This small seed is a regression gate, not evidence for broad real-world quality, multilingual retrieval or reference-corpus throughput. The separate adversarial filter test uses more than 50 distractors; publication and mutation tests cover changing projections.

To deliberately replace the recording, run the recorder against an explicitly selected loopback service, supplying its actual model revision:

```sh
python3 eval/record_embeddings.py --url http://127.0.0.1:8001/v1 \
  --model Qwen/Qwen3-Embedding-0.6B \
  --revision 97b0c614be4d77ee51c0cef4e5f07c00f9eb65b32
```

The script sends only checked-in seed inputs, disables proxies and redirects, and does not rank results or rewrite labels. The model above is fixture provenance, not an application default. Model binding remains operator configuration. Review changed labels independently of retrieval changes.
