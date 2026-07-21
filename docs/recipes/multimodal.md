# Multimodal with FreeAgent

The kernel is **text-first by design**. Image generation, speech-to-text, and text-to-speech are
reached the same way LLMs are: by pointing FreeAgent at an OpenAI-compatible server that exposes
them. No embedded inference, no per-platform native binaries.

## The recipe

[LocalAI](https://localai.io/) is the simplest path — one binary, OpenAI-compatible HTTP endpoints
for chat completions, image generation (DALL·E-shape), audio transcription (Whisper-shape), and
TTS, all behind the same `OPENAI_BASE_URL` you'd normally point at OpenAI itself.

```bash
# Start LocalAI somewhere (Docker is the path of least resistance):
docker run -p 8080:8080 -v localai-models:/build/models localai/localai:latest

# Point FreeAgent at it:
export OPENAI_BASE_URL=http://localhost:8080/v1
export OPENAI_API_KEY=localai            # LocalAI ignores the value but the host requires non-empty
export FREEMODEL=gpt-4o-mini             # or whatever LocalAI is serving
freeagent
```

The agent now uses LocalAI for `/chat/completions`. Image / audio endpoints are reachable from
**outside the agent** by hitting `OPENAI_BASE_URL` directly with `curl` — they're not wired into
FreeAgent's tool registry today because the agent is text-only by design.

```bash
# Generate an image (DALL·E shape):
curl -s -X POST "$OPENAI_BASE_URL/images/generations" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $OPENAI_API_KEY" \
  -d '{"prompt":"a robot reading a JSON spec","size":"512x512"}' | jq .

# Transcribe audio (Whisper shape):
curl -s -X POST "$OPENAI_BASE_URL/audio/transcriptions" \
  -H "Authorization: Bearer $OPENAI_API_KEY" \
  -F file=@./clip.wav -F model=whisper-1
```

## Alternatives

- **OpenAI itself** — the OpenAI provider already supports the same endpoints; no FreeAgent change
  needed, just call them outside the agent or wrap them in your own tool.
- **Ollama** — text/code only; no image or audio endpoints today.
- **`whisper.net`** — a mature .NET binding for whisper.cpp. The only in-process exception we'd
  consider if first-class voice-in ever becomes a real ask; today it's documentation only.

## Why not bake it into the agent?

Tool use is the point. A multimodal *agent* needs:

1. **Tools** that exercise the image / audio endpoints (`GenerateImage`, `Transcribe`, `Speak`).
2. **Capabilities** (`NetworkEgressCap` or a dedicated `MultimodalCap`) so the permission engine
   can gate them.
3. **Artifact handling** so big binaries (audio files, generated images) don't blow up token
   budgets — the existing `IArtifactStore` covers this shape already.

These are additive; the kernel doesn't need to change. Open a `multimodal` issue or
`/spawn-agent Coder "add a GenerateImage tool"` when you want them.
