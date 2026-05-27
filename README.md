# FreeAgent

Linux-native, modular agent kernel reimplementation work extracted from the OpenMonoAgent.ai CodeCartographer deep-audit path.

The implementation contract lives at:

- `docs/codecarto/reimplementation-spec.md`

Current scope:

- Kernel-first architecture
- Fake-provider/fake-tool acceptance harness
- Stream chunk normalization contract
- Tool-call execution pipeline seam
- Permission engine seam
- JSONL persistence contract with atomic-write seam

Deliberate non-goals for the first milestone include full-screen TUI, MCP, LSP, hooks, background bash, subagents, playbooks, memory, Docker wrapper, Roslyn tools, and full provider matrix.

## Build/test

```bash
dotnet test src/FreeAgent.Kernel.Tests/FreeAgent.Kernel.Tests.csproj -v minimal
```
