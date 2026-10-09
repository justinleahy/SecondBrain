# vLLM provider

SecondBrain's reference provider is a vLLM server on the RTX 5090 host. vLLM hosts exactly one model per server, so this Compose project runs two servers on the one GPU: a chat/enrichment model with native tool calling and an embedding model. Each is an OpenAI-compatible endpoint, bound to loopback unless `VLLM_BIND` names a private address.

| Service | Model | Endpoint | Served limits |
| --- | --- | --- | --- |
| `chat` | `Qwen/Qwen3-8B` (bf16) | `http://127.0.0.1:8000/v1` | 32768-token context; Hermes tool parser; thinking off by default (per-request `chat_template_kwargs: {"enable_thinking": true}` turns it on, and the Qwen3 reasoning parser keeps it out of `content`); 70% of GPU memory |
| `embed` | `Qwen/Qwen3-Embedding-0.6B` | `http://127.0.0.1:8001/v1` | 1024 dimensions (Matryoshka 32–1024); 8192-token input; 12% of GPU memory |

## Prerequisites

- An NVIDIA driver that supports the image's CUDA runtime, Docker Engine with the NVIDIA Container Toolkit, and Compose v2. `docker run --rm --gpus all nvidia/cuda:12.8.0-base-ubuntu24.04 nvidia-smi -L` must print the GPU.
- About 20 GB for the image and 18 GB for the two models. The servers never contact the Hugging Face Hub: they run with `HF_HUB_OFFLINE=1` against a read-only mount of `~/.cache/huggingface` (`VLLM_HF_CACHE` overrides the path), so download the weights first, as your own user:

```sh
uvx --from huggingface_hub hf download Qwen/Qwen3-Embedding-0.6B
uvx --from huggingface_hub hf download Qwen/Qwen3-8B
```

## Run

```sh
docker compose -f deploy/vllm/compose.yaml up -d
docker compose -f deploy/vllm/compose.yaml logs -f          # until both servers log "Application startup complete"
curl -s http://127.0.0.1:8001/v1/models
curl -s http://127.0.0.1:8000/v1/models
docker compose -f deploy/vllm/compose.yaml down
```

The first chat start takes several minutes (weights, torch.compile and CUDA graph capture); later starts reuse the compile caches. Thinking is off by default because a thinking turn can spend a whole output budget before any answer appears; with it on, the model used all 400 tokens of one probe on reasoning and returned no content. `VLLM_CHAT_GPU_FRACTION` and `VLLM_EMBED_GPU_FRACTION` divide the GPU; the defaults leave about 5 GB for a desktop session on a 32 GB card. `VLLM_CHAT_PORT`, `VLLM_EMBED_PORT` and `VLLM_BIND` set the published endpoints. One `Ignoring corrupted tree cache file … Permission denied` line per start is expected: `hf download` writes `trees/*.json` with mode 0600, and the capability-less container cannot read it, which huggingface_hub tolerates.

Confirm the capabilities you are about to declare. A tool call must come back as structured `tool_calls`, and an embedding must have the declared dimensions:

```sh
curl -s http://127.0.0.1:8000/v1/chat/completions -H 'content-type: application/json' -d '{
  "model": "Qwen/Qwen3-8B",
  "messages": [{"role": "user", "content": "What is the weather in Boston? Use the tool."}],
  "tools": [{"type": "function", "function": {"name": "get_weather", "parameters": {"type": "object", "properties": {"city": {"type": "string"}}, "required": ["city"]}}}]
}'
curl -s http://127.0.0.1:8001/v1/embeddings -H 'content-type: application/json' \
  -d '{"model": "Qwen/Qwen3-Embedding-0.6B", "input": "hello"}' | jq '.data[0].embedding | length'
```

## SecondBrain configuration

Bind the roles to two providers. Under `local_only`, both origins must be trusted services, and a Compose or systemd deployment also needs both `IP:port` pairs in its egress allowlist (`SECONDBRAIN_PROVIDER_ALLOWLIST="10.8.0.5:8000 10.8.0.5:8001"`).

```yaml
providers:
  vllm: {adapter: openai_compatible, base_url: http://127.0.0.1:8000/v1, trusted: true}
  vllm-embed: {adapter: openai_compatible, base_url: http://127.0.0.1:8001/v1, trusted: true}
models:
  chat: {provider: vllm, model: Qwen/Qwen3-8B, capabilities: {tools: true, streaming: true}, limits: {context_tokens: 32768, max_output_tokens: 4096}}
  enrich: {provider: vllm, model: Qwen/Qwen3-8B, capabilities: {tools: true, streaming: true}, limits: {context_tokens: 32768, max_output_tokens: 4096}}
  embed: {provider: vllm-embed, model: Qwen/Qwen3-Embedding-0.6B, limits: {embed_dimensions: 1024, embed_max_input_tokens: 8192, embed_batch_max: 32}}
privacy:
  local_only: true
  trusted_services: [http://127.0.0.1:8000, http://127.0.0.1:8001]
```

## Qualification

The live gate takes the same two endpoints. `SECONDBRAIN_QUAL_VLLM_EMBED_URL` is optional and defaults to the chat URL for a single server that happens to serve both models.

```sh
export SECONDBRAIN_QUAL_VLLM_URL=http://127.0.0.1:8000/v1
export SECONDBRAIN_QUAL_VLLM_EMBED_URL=http://127.0.0.1:8001/v1
export SECONDBRAIN_QUAL_VLLM_CHAT_MODEL=Qwen/Qwen3-8B
export SECONDBRAIN_QUAL_VLLM_EMBED_MODEL=Qwen/Qwen3-Embedding-0.6B
export SECONDBRAIN_QUAL_VLLM_DIMENSIONS=1024
export SECONDBRAIN_QUAL_VLLM_CONTEXT_WINDOW=32768
export SECONDBRAIN_QUAL_VLLM_MAX_OUTPUT_TOKENS=4096
export SECONDBRAIN_QUAL_VLLM_MAX_INPUT_TOKENS=8192
dotnet test tests/SecondBrain.Server.Tests --no-restore --filter 'Category=Qualification'
```

## Changing models

Edit `compose.yaml`. `--tool-call-parser` and `--reasoning-parser` are model-specific, and `models.<role>.capabilities` is a reviewed declaration, so repeat the tool-call and embedding checks above before declaring a new model. A larger chat model needs a smaller `--max-model-len` or a quantized checkpoint to fit beside the embedding server.
