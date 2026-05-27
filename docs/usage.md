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
# A local model server
export OPENAI_BASE_URL=http://localhost:8000/v1
export OPENAI_API_KEY=not-needed-but-required   # any non-empty value
export FREEMODEL=your-local-model

# A hosted gateway
export OPENAI_BASE_URL=https://your-gateway.example/v1
export OPENAI_API_KEY=sk-...
export FREEMODEL=some/model
```

## At the prompt

```
> <your request>
```

- The model's text streams as it arrives.
- It may call `ReadFile`, `WriteFile`, or `ProcessExec`; allowed calls run and their
  results feed back into the same turn automatically.
- **Ctrl+C** cancels the in-progress turn and returns you to the prompt — it does
  *not* kill the process while a turn is running.
- `exit`, `quit`, or end-of-input ends the session.

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

> The current host registers tools and the permission engine with defaults and does
> not yet expose flags to add allow rules at startup. Granting writes or extra
> binaries is done in code via `PermissionEngine.AllowTool` /
> `AllowCapabilityRule<T>(pattern)` when composing the runtime — a natural next host
> feature. See [`architecture.md`](architecture.md#permission-engine-permissionengine).

## Sessions and transcripts

Each run starts a **fresh** session with a new id and writes `session.jsonl` to the
working directory, saving after every completed turn and again on exit. The file is
JSONL: line 1 is the header, each later line is one message.

```bash
# Inspect the last session
cat session.jsonl

# Header only
head -n1 session.jsonl
```

Writes are atomic (write-temp → fsync → rename → fsync-dir), so an interrupted run
never leaves a corrupt transcript. The kernel's `JsonlSessionStore` can *load* a
session back into `SessionState`, though the host does not yet offer a resume flag.

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
