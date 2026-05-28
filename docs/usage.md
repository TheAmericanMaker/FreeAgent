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

| Variable          | Required | Default                     | Notes                                               |
| ----------------- | :------: | --------------------------- | --------------------------------------------------- |
| `OPENAI_API_KEY`  |   yes    | —                           | Without it the host exits with code 1 immediately.  |
| `OPENAI_BASE_URL` |    no    | `https://api.openai.com/v1` | `/chat/completions` is appended; trailing slash ok. |
| `FREEMODEL`       |    no    | `gpt-4o-mini`               | Sent as the `model` in the request body.            |

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
- It may call tools — `ReadFile`, `WriteFile`, `ProcessExec`, `Glob`, `Grep`, or the
  plan-mode toggles; allowed calls run and their results feed back into the same turn
  automatically.
- **Ctrl+C** cancels the in-progress turn and returns you to the prompt — it does
  *not* kill the process while a turn is running.
- `exit`, `quit`, or end-of-input ends the session.

### Commands

Input starting with `/` is a host command (not sent to the model):

- `/plan` — toggle plan mode; `/plan on` / `/plan off` set it explicitly. In plan mode
  only read-only tools run, so the agent can explore and propose changes without making
  any. The model can also toggle this itself via the `EnterPlanMode` / `ExitPlanMode` tools.

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

| Code | Meaning                                             |
| ---- | --------------------------------------------------- |
| 0    | Normal exit (`exit`/`quit`/EOF); session saved.     |
| 1    | `OPENAI_API_KEY` was not set.                       |

## Troubleshooting

- **`Error: OPENAI_API_KEY is not set.`** — export the key (any non-empty value for
  servers that don't check it).
- **`[Error: OpenAI API returned 401 …]`** — bad/empty key for a server that does
  check it, or wrong `OPENAI_BASE_URL`.
- **`[Error: OpenAI API returned 404 …]`** — base URL is missing the `/v1` segment,
  or the model name is wrong for the endpoint.
- **A tool keeps coming back `PermissionDenied`** — that capability isn't auto-allowed
  by default (e.g. any write, or a non-safe binary). That's the safety model working;
  add an allow rule in code to grant it.
- **`[Doom loop detected — turn ended early]`** — the model repeated the identical
  tool call three times; the runtime broke the loop. Rephrase or give it more
  context.
