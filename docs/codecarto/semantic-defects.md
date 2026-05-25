# Semantic Defects Report — OpenMonoAgent.ai

## Scan Context

- **Source:** `../` (repository root)
- **Architecture reference:** `findings/architecture/architecture-map.md`
- **Contracts reference:** `findings/contracts/behavioral-contracts.md`
- **Protocols reference:** `findings/protocols/protocols-and-state.md`
- **Mechanical defects reference:** `findings/defect-scan-mechanical/mechanical-defects.md`
- **Pipeline:** `workflow/pipeline-full-with-deep-audit.yaml`
- **Date:** 2026-05-24
- **Scope:** Semantic passes only (3 concurrency, 4 security, 5 contract violations). Mechanical passes (1 logic, 2 error handling, 6 configuration) were covered earlier in `defect-scan-mechanical`.

---

## Pass 3: Concurrency and Resource Management

| # | Location | Defect | Severity | Evidence Level | Action |
|---|----------|--------|----------|----------------|--------|
| 1 | `src/OpenMono.Cli/Mcp/McpClient.cs:28-46`, `src/OpenMono.Cli/Lsp/LspClient.cs:28-40` | MCP and LSP subprocesses redirect stderr but no code drains it. A server that writes enough diagnostics to stderr can block, causing JSON-RPC requests to hang even though stdout framing is otherwise correct. This violates the subprocess protocol expectations in protocols B3/B4. | High | Observed fact | Start bounded stderr drain tasks for every subprocess, surface recent stderr in failures, and cancel/kill the process tree if stderr flooding wedges the protocol. |
| 2 | `src/OpenMono.Cli/Hooks/HookRunner.cs:98-116` | Hook timeout cancels `WaitForExitAsync` but does not kill the hook process. Because `Process.Dispose()` does not terminate the child, a timed-out hook may continue running after the tool pipeline proceeds. This violates the documented 30-second hook timeout behavior in contracts §High-Value Behaviors / Hook timeout. | High | Observed fact | On hook timeout, kill the hook process tree, await bounded shutdown, and include truncated stdout/stderr in the warning. |
| 3 | `src/OpenMono.Cli/Tools/BashTool.cs:178-230` | Background Bash returns PID/log path but does not retain a `Process` handle, process group, or registry entry. There is no structured lifecycle management, no readiness state, no cleanup on session shutdown, and no reliable process-tree kill path. This closes `mech-CF2`. | Medium | Observed fact | Introduce a background-process manager with process groups, status polling, log tailing, and shutdown cleanup; persist enough metadata to recover or warn on orphaned jobs. |
| 4 | `src/OpenMono.Cli/Session/ConversationLoop.cs:228-239`, `:574-590`, `:627-728` | Read-only concurrency-safe tools can begin executing while the provider stream is still active. The same execution path can run hooks, write UI events, write journal events, update token/tool metadata, and possibly prompt for permissions. The current design relies on individual components being thread-safe; only some are explicitly synchronized. | Medium | Strong inference | Define the concurrency contract for in-flight tool execution. Restrict pre-stream execution to tools whose whole pipeline is non-interactive and thread-safe, or serialize UI/hooks/permission prompts through a single executor. |
| 5 | `src/OpenMono.Cli/Mcp/McpClient.cs:151-164`, `src/OpenMono.Cli/Lsp/LspClient.cs:243-249` | Dispose paths swallow all process-tree kill/wait failures. If external protocol servers survive disposal, later sessions can inherit orphaned child processes or locked resources with no diagnostic trail. | Low | Observed fact | Log debug diagnostics for failed kill/wait/disposing paths and include server name/command metadata. |

---

## Pass 4: Security and Trust Boundaries

| # | Location | Defect | Severity | Evidence Level | Action |
|---|----------|--------|----------|----------------|--------|
| 1 | `src/OpenMono.Cli/Hooks/HookRunner.cs:71-83` | Hook variables (`{{tool_input}}`, `{{tool_output}}`, `{{tool_name}}`) are inserted directly into a shell command string and executed via `/bin/bash -c`. Tool input/output is partly LLM-controlled and may contain shell metacharacters, so a benign hook template can become command injection. This closes `mech-CF3`. | High | Observed fact | Treat hook variables as data, not shell source. Pass values via environment variables or stdin, or provide an argv-style hook API. If shell templates remain, document them as unsafe and require explicit opt-in. |
| 2 | `src/OpenMono.Cli/Tools/FileReadTool.cs:28-37`, `:43-47`, `:134-180` | `FileRead` with `from_cursor` returns no required capabilities, so the permission engine cannot authorize the actual files read from the cursor. The cursor store becomes the trust boundary; if any cursor-producing tool admits paths outside the workspace, `FileRead` bypasses file-read permission checks. | Medium | Strong inference | Re-evaluate capabilities after resolving cursor contents, one `FileReadCap` per file, or restrict cursor stores to capability-provenance-preserving entries. Add tests with outside-workspace cursor entries. |
| 3 | `src/OpenMono.Cli/Mcp/McpClient.cs:26-59`, `src/OpenMono.Cli/Mcp/McpToolAdapter.cs:37-67` | Configured MCP server commands execute as local subprocesses and registered MCP tools can return arbitrary text/payloads to the model. The contracts identify MCP as a subprocess trust boundary, but there is no explicit server identity verification or sandboxing beyond user configuration and per-tool prompt. | Medium | Strong inference | Document MCP server config as trusted code execution, show warnings when registering external servers, and consider per-server trust levels/sandboxing for untrusted MCP tools. |

---

## Pass 5: API Contract Violations

| # | Location | Defect | Severity | Evidence Level | Action | Spec Reference |
|---|----------|--------|----------|----------------|--------|----------------|
| 1 | `src/OpenMono.Cli/Hooks/HookRunner.cs:100-116` | Contract says each hook has a 30-second hard timeout and failure is non-fatal. Implementation reports timeout as a warning but does not terminate the process, so side effects can continue after the pipeline has moved on. | High | Observed fact | Kill hook process tree on timeout before continuing the tool pipeline. Add an acceptance test with a hook that sleeps/writes after timeout. | Contracts §Tool Execution Pipeline steps 7/9 and §High-Value Behaviors / Hook timeout. |
| 2 | `src/OpenMono.Cli/Lsp/LspClient.cs:206-240` | Protocols document B4 as a JSON-RPC subprocess boundary. Implementation reads `Content-Length` frames but ignores the expected response id and returns the first `result`. This can violate request/response pairing in the LSP state machine when notifications or out-of-order responses occur. | High | Observed fact | Match responses by id and ignore notifications/other ids. Add fixtures for diagnostics notifications interleaved with hover/definition responses. | Protocols B4 and state-machine compatibility hazards. |
| 3 | `src/OpenMono.Cli/Permissions/PermissionEngine.cs:96-155`, `src/OpenMono.Cli/Config/AppConfig.cs:79-84` | `ToolPermissionRules.Ask` is loaded and merged but not evaluated by either legacy or capability permission checks. Users can configure `ask` rules expecting an interactive prompt, but the setting has no effect. This closes `mech-CF1`. | Medium | Observed fact | Either implement `Ask` rule precedence deliberately, remove it from the supported config schema, or mark it deprecated with validation warnings. | Contracts §Permission and Authorization Model / Glob pattern semantics notes `Ask` list is present but unused. |
| 4 | `src/OpenMono.Cli/Session/ConversationLoop.cs:203-221`, `:223-239` | Streaming state treats `ThinkingDelta` as exclusive: when a chunk has `ThinkingDelta`, the loop `continue`s before checking `TextDelta`, `ToolCallDelta`, `Usage`, or `IsComplete`. The provider-neutral stream contract allows chunks to contain any combination of fields. A mixed thinking+tool/text/usage chunk would drop non-thinking fields. | Medium | Observed fact | Process all non-null fields in a chunk without early `continue`, or make provider adapters guarantee one-field chunks and encode that as the stream contract. | Protocols B4: `StreamChunk` optional fields may appear in combination; contracts require provider-neutral stream model. |
| 5 | `src/OpenMono.Cli/Session/SessionManager.cs:20-49`, `:123-153` | Contracts say session JSONL and `index.json` are persisted state updated as the user interacts. Implementation rewrites both directly rather than atomically, so the persistence contract is not crash-safe even though sessions are the primary recovery surface. This duplicates the mechanical finding but is contract-significant for reimplementation. | Medium | Observed fact | Use atomic temp-write + rename for session JSONL, checkpoint sidecars, and index. Treat crash safety as part of the reimplementation persistence contract. | Contracts §Host shell CLI persisted state; Protocols B5 persistent schema notes. |

---

## Summary

### Findings by Severity

| Severity | Count |
|----------|-------|
| Critical | 0 |
| High | 5 |
| Medium | 7 |
| Low | 1 |
| **Total** | 13 |

### Findings by Pass

| Pass | Critical | High | Medium | Low | Total |
|------|----------|------|--------|-----|-------|
| 3. Concurrency and resources | 0 | 2 | 2 | 1 | 5 |
| 4. Security and trust | 0 | 1 | 2 | 0 | 3 |
| 5. API contract violations | 0 | 2 | 3 | 0 | 5 |

### Top Findings

1. Pass 4 — `HookRunner.ExecuteHookAsync`: hook variable shell injection via direct `{{tool_input}}` / `{{tool_output}}` substitution; High; move variables to env/stdin or argv API.
2. Pass 3/5 — `HookRunner.ExecuteHookAsync`: hook timeout warns but does not kill child process; High; kill process tree before continuing.
3. Pass 3 — `McpClient` / `LspClient`: stderr is redirected but never drained; High; drain bounded stderr concurrently.
4. Pass 5 — `LspClient.ReadResponseAsync`: response id ignored; High; implement id-aware dispatch.
5. Pass 5 — `StreamChunk` handling: early `continue` on thinking chunks can drop mixed text/tool/usage/completion fields; Medium; process all fields or narrow adapter contract.

### Carry-Forward Closure

| ID | Source Phase | Closed Because |
|----|--------------|---------------|
| mech-CF1 | `defect-scan-mechanical` | Closed by Pass 5 finding #3: `Ask` rules are loaded but unused, so reimplementation should either implement or remove/deprecate them deliberately. |
| mech-CF2 | `defect-scan-mechanical` | Closed by Pass 3 finding #3: background process lifecycle lacks a manager/cleanup contract. |
| mech-CF3 | `defect-scan-mechanical` | Closed by Pass 4 finding #1: hook variable interpolation creates shell-injection risk at the hook trust boundary. |

---

## Validation

| # | Criterion | Result | Evidence |
|---|-----------|--------|----------|
| 1 | All three semantic passes (3, 4, 5) produced findings or documented "no defects found." | PASS | Pass 3 has 5 findings, Pass 4 has 3 findings, Pass 5 has 5 findings. |
| 2 | Each finding has location, severity, evidence level, and recommended action. | PASS | Every detailed row includes concrete source location, severity, evidence level, and action. |
| 3 | Pass 5 findings cite the contract or protocol reference they violate. | PASS | Pass 5 rows include spec references to contracts and/or protocols. |
| 4 | Findings are organized by pass and sorted by severity; summary tables match the detailed findings. | PASS | Findings are grouped by Pass 3/4/5 and ordered High before Medium before Low; summary totals match 13 findings. |
| 5 | Findings are marked with evidence levels. | PASS | All findings are tagged `Observed fact` or `Strong inference`. |

**Validated by:** Hermes session on 2026-05-24
**Overall:** PASS
