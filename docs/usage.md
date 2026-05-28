# Using the FreeAgent CLI

`FreeAgent.Host` is the interactive command-line front end for the kernel. This page
covers running it, configuring the provider, what the permission model means in
practice, and a few recipes. For the architecture behind it, see
[`architecture.md`](architecture.md).

## Running

```bash
export OPENAI_API_KEY=sk-...
dotnet run --project src/FreeAgent.Host
```

Or run the built binary directly after `dotnet build FreeAgent.slnx`:

```bash
./src/FreeAgent.Host/bin/Debug/net10.0/FreeAgent.Host
```

To pass flags through `dotnet run`, put them after `--`:

```bash
dotnet run --project src/FreeAgent.Host -- --verbose
```

## The working directory is the sandbox

The host uses **the directory you launch it from** as the session working
directory. That single choice drives the safety model:

- `ReadFile` is auto-allowed for any path inside it.
- `WriteFile` and `ProcessExec` resolve relative paths against it.
- The session transcript (`session.jsonl`) is written there.

So `cd` into the project you want the agent to work on, then launch. Launching from
`$HOME` hands the agent your whole home directory as its read sandbox — launch from
something narrower.

## Configuration

The active provider is picked by **`FREEPROVIDER`** (default `openai`); supported values are
`openai`, `anthropic`, `azure`, `ollama`, `bedrock`, `vertex`. The host's bootstrap check
requires an API key for `openai` / `anthropic` / `azure`; `ollama` is unauthenticated and
`bedrock` / `vertex` use the ambient cloud credential chain.

OpenAI / OpenAI-compat (the default):

| Variable          | Required | Default                     | Notes                                                |
| ----------------- | :------: | --------------------------- | ---------------------------------------------------- |
| `OPENAI_API_KEY`  |   yes    | —                           | Without it the host exits with code 1.              |
| `OPENAI_BASE_URL` |    no    | `https://api.openai.com/v1` | `/chat/completions` is appended; trailing slash ok. |
| `FREEMODEL`       |    no    | `gpt-4o-mini`               | Sent as the `model` in the request body.            |

For Anthropic / Azure / Ollama / Bedrock / Vertex env vars, see the [README configuration
section](../README.md#configuration); the recipes below cover the typical setup for each.

### Pointing at other OpenAI-compatible servers

Any server that speaks the OpenAI streaming `/chat/completions` shape works. Set the
base URL to its `/v1` root and the model to whatever it serves:

```bash
# A local model server (llama-server, vLLM, etc.)
export OPENAI_BASE_URL=http://localhost:8000/v1
export OPENAI_API_KEY=not-needed-but-required   # any non-empty value
export FREEMODEL=your-local-model

# A hosted gateway
export OPENAI_BASE_URL=https://your-gateway.example/v1
export OPENAI_API_KEY=sk-...
export FREEMODEL=some/model
```

#### Groq

Groq's API is OpenAI-compatible — no special provider needed:

```bash
export OPENAI_BASE_URL=https://api.groq.com/openai/v1
export OPENAI_API_KEY=gsk_...
export FREEMODEL=llama-3.3-70b-versatile   # or any model Groq currently serves
freeagent
```

#### Ollama (OpenAI-compat path)

```bash
export OPENAI_BASE_URL=http://localhost:11434/v1
export OPENAI_API_KEY=ollama                # Ollama ignores the key but the host requires non-empty
export FREEMODEL=qwen2.5-coder
freeagent
```

Or use the native Ollama provider, which speaks Ollama's `/api/chat` (newline-delimited JSON)
directly and lets you tune `num_ctx` / `temperature` per request:

```bash
export FREEPROVIDER=ollama
export OLLAMA_HOST=http://localhost:11434    # default
export FREEMODEL=qwen2.5-coder
export FREE_NUM_CTX=8192                     # optional
export FREE_TEMPERATURE=0.2                  # optional
freeagent
```

#### Anthropic (native)

```bash
export FREEPROVIDER=anthropic
export ANTHROPIC_API_KEY=sk-ant-...
export FREEMODEL=claude-3-7-sonnet-latest
export FREE_THINKING_BUDGET=4096             # optional: enable extended thinking
freeagent
```

#### Azure OpenAI

```bash
export FREEPROVIDER=azure
export AZURE_OPENAI_API_KEY=...
export AZURE_OPENAI_ENDPOINT=https://my-resource.openai.azure.com
export AZURE_OPENAI_DEPLOYMENT=my-gpt-4o-mini-deployment
export AZURE_OPENAI_API_VERSION=2024-08-01-preview   # optional
freeagent
```

#### AWS Bedrock (Anthropic on Bedrock)

Bedrock uses the default AWS credential chain — env vars, shared
profile, IMDS, SSO — so there's no FreeAgent-specific secret. Make
sure your account has the model enabled in the chosen region.

```bash
export FREEPROVIDER=bedrock
export AWS_REGION=us-east-1                       # default if unset
export AWS_ACCESS_KEY_ID=...                      # or use a shared profile
export AWS_SECRET_ACCESS_KEY=...
export BEDROCK_MODEL=anthropic.claude-3-7-sonnet-20250219-v1:0   # default
freeagent
```

`AWSSDK.BedrockRuntime` handles SigV4 signing, region routing, retries,
and event-stream framing; the adapter just translates the request
body and dispatches the chunks.

#### Google Vertex AI (Anthropic on Vertex)

Vertex uses Google Application Default Credentials. The simplest path
is `gcloud auth application-default login`; alternatively set
`GOOGLE_APPLICATION_CREDENTIALS` to a service-account JSON.

```bash
export FREEPROVIDER=vertex
export VERTEX_PROJECT=my-gcp-project              # required
export VERTEX_LOCATION=us-central1                # default if unset
export VERTEX_MODEL=claude-3-7-sonnet@20250219    # default
gcloud auth application-default login             # one-time, if not using a service account
freeagent
```

## At the prompt

```
> <your request>
```

- The model's text streams as it arrives.
- It may call tools — `ReadFile`, `WriteFile`, `EditFile`, `MultiEditFile`, `ApplyPatch`,
  `ProcessExec`, `Glob`, `Grep`, `CSharpAnalysis`, `ReadMemory`, `WriteMemory`,
  `ReadArtifact`, `SpawnAgent`, the plan-mode toggles, plus any configured
  `mcp__{server}__{tool}` and `lsp__{server}__{action}`. Allowed calls run and their results
  feed back into the same turn automatically.
- **Ctrl+C** cancels the in-progress turn and returns you to the prompt — it does
  *not* kill the process while a turn is running.
- `exit`, `quit`, or end-of-input ends the session.

### Commands

Input starting with `/` is a host command (not sent to the model). Type `/commands` for the
full list with fuzzy filter, or `/help` for the inline cheat sheet.

| Command                            | What it does                                                                              |
| ---------------------------------- | ----------------------------------------------------------------------------------------- |
| `/help`                            | Inline help text listing every command.                                                   |
| `/status`                          | Session id, model, working directory, message count, iterations, plan mode, tags.         |
| `/model`                           | Active model + how to change it.                                                          |
| `/plan [on\|off]`                   | Toggle plan mode (only read-only tools run). Model can also call `EnterPlanMode` / `ExitPlanMode`. |
| `/undo`                            | Roll back the most recent agent-driven file change (uses `SessionState.History`).         |
| `/revert [N]`                      | Drop the last `N` user turns from the transcript (files unchanged — pair with `/undo`).   |
| `/tag <name>` / `/untag <name>`    | Manage session tags (visible in `/status` and `/doctor`).                                  |
| `/run <playbook> [args]`           | Render a Markdown playbook with `{{argN}}` substitution and dispatch it as a turn.        |
| `/doctor`                          | One-shot diagnostic: provider, model, base URL, tool inventory, sub-agent roles.          |
| `/serve start <name-or-path> [...]`| Spawn `llama-server` (or any OpenAI-compat binary). A bare name is resolved against the local model catalog. |
| `/serve stop` / `/serve status`    | Kill the recorded server / report its status.                                             |
| `/serve download <url-or-hf:owner/repo/path.gguf> [--name <local-name>]` | Stream a GGUF into the local catalog. `HF_TOKEN` is forwarded for gated repos. |
| `/serve models`                    | List downloaded GGUFs.                                                                    |
| `/fork`                            | Snapshot the current transcript to `session-fork-<id>.jsonl` for branching.               |
| `/commands [query]`                | Fuzzy command palette (same registry the future TUI binds against).                       |

### Verbose mode

`--verbose` / `-v` additionally prints:

- The model's reasoning/thinking deltas, dimmed.
- A `[Tokens: <input> → <output>]` line per turn from the provider's usage report.

Without it, reasoning is suppressed and only the answer text is shown.

## What the agent can and cannot do

The permission engine decides every tool call. In the default host configuration
(no extra allow rules added):

**Runs without asking**

- Reading any file inside the working directory.
- Safe read-only shell commands: `pwd, ls, cat, head, tail, grep, rg, find`, and
  `git status` / `git diff` / `git log`.

**Denied by default (would need an allow rule)**

- Writing files (every `WriteFile` call) — `WriteFile` is never auto-allowed.
- Reading files outside the working directory.
- Running any other binary via `ProcessExec`.

**Always blocked (cannot be allowed)**

- Binaries: `sudo, su, doas, pkexec, chmod, chown, chattr, setfacl, icacls,
  takeown, attrib`.
- Writing under `/etc, /usr, /bin, /sbin, /System, /Library`.

A denied call comes back to the model as a `PermissionDenied` result (often with a
retry hint), so the model can adjust rather than crash.

### Granting more with a config file

Drop a `.freeagent/config.json` in the working directory (or point `FREEAGENT_CONFIG`
at a file) to add allow/deny rules without code. A capability rule with no `pattern`
(or `"*"`) covers the whole capability type; otherwise the `pattern` is a glob matched
against the capability's target (path, binary, …). Hardcoded blocks above still win.

```jsonc
{
  "allow": [
    { "capability": "FileWriteCap", "pattern": "**" },   // write anywhere in the workspace
    { "capability": "ProcessExecCap", "pattern": "npm" }  // run npm
  ],
  "deny": [ { "capability": "ProcessExecCap", "pattern": "rm" } ],
  "allowTools": [],
  "denyTools": []
}
```

Valid capability names: `FileReadCap`, `FileWriteCap`, `ProcessExecCap`,
`NetworkEgressCap`, `VcsMutationCap`, `MemoryCap`, `AgentSpawnCap`. A missing config is
fine; a malformed one prints a warning and is ignored.

## Sessions and transcripts

Each run starts a **fresh** session with a new id and writes `session.jsonl` to the
working directory, saving after every completed turn and again on exit. The file is
JSONL: line 1 is the header, each later line is one message.

Pass **`--resume`** to continue the session in `session.jsonl` (its id and full message
history are restored), or **`--resume <id>`** to resume only if the stored id matches.
If there's nothing to resume (missing file, id mismatch, or a malformed transcript) the
host prints a note and starts fresh.

```bash
# Inspect the last session
cat session.jsonl

# Header only
head -n1 session.jsonl
```

Writes are atomic (write-temp → fsync → rename → fsync-dir), so an interrupted run
never leaves a corrupt transcript. The host rehydrates this file on `--resume` via the
kernel's `JsonlSessionStore`.

## Exit codes

| Code | Meaning                                                                                            |
| ---- | -------------------------------------------------------------------------------------------------- |
| 0    | Normal exit (`exit`/`quit`/EOF); session saved.                                                    |
| 1    | The active provider's API key was not found in env or config (skipped for `ollama`/`bedrock`/`vertex`). |

## Troubleshooting

- **`Error: no API key found for provider 'openai'.`** — export the matching key
  (`OPENAI_API_KEY` / `ANTHROPIC_API_KEY` / `AZURE_OPENAI_API_KEY`), or add it to the user config
  at `~/.config/freeagent/config.json`. For `bedrock` / `vertex`, make sure the cloud credential
  chain is set up (`aws configure`, `gcloud auth application-default login`).
- **`[Error: OpenAI API returned 401 …]`** — bad/empty key for a server that does
  check it, or wrong `OPENAI_BASE_URL`.
- **`[Error: OpenAI API returned 404 …]`** — base URL is missing the `/v1` segment,
  or the model name is wrong for the endpoint.
- **`[Error: Bedrock API returned …]` / `[Error: Vertex API returned …]`** — the cloud
  credentials authenticated but the model isn't enabled in the region/project, or the model id
  is wrong for that endpoint.
- **A tool keeps coming back `PermissionDenied`** — that capability isn't auto-allowed
  by default (any write, a non-safe binary). That's the safety model working; add an
  allow rule in `.freeagent/config.json` (see "Granting more with a config file" above).
- **`[Doom loop detected …]`** — the model repeated the identical tool call three
  times; the runtime broke the loop, then re-prompted up to three times. Rephrase or
  give the agent more context.

## The protocol server (`FreeAgent.Server`)

If you want to drive FreeAgent from something other than the CLI — a TUI, an editor extension,
a web frontend — start the HTTP + SSE server instead:

```bash
dotnet run --project src/FreeAgent.Server                              # listens on http://localhost:5000
FREEAGENT_SERVER_API_KEY=secret dotnet run --project src/FreeAgent.Server   # require Authorization: Bearer
```

Endpoints (full schema at `GET /openapi/v1.json`):

| Method   | Path                          | Notes                                                                  |
| -------- | ----------------------------- | ---------------------------------------------------------------------- |
| `POST`   | `/sessions`                   | Body `{ workingDirectory?: string }` → `{ sessionId, workingDirectory }`. |
| `GET`    | `/sessions`                   | Returns the list of live session ids.                                  |
| `GET`    | `/sessions/{id}`              | State summary: message count, plan mode, tags, iterations.             |
| `POST`   | `/sessions/{id}/turns`        | Body `{ userInput }` → SSE: `event: text\|thinking\|tool_call\|tool_result\|usage`, then `event: done` with the assembled reply. |
| `DELETE` | `/sessions/{id}`              | Remove the session from the in-memory registry.                        |

The same `ProviderConfig` (env vars + `~/.config/freeagent/config.json`) selects the provider for
both the CLI and the server.
