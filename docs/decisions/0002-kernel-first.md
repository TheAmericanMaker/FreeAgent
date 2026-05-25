# 0002: Kernel-first implementation strategy

Status: Accepted

FreeAgent will be built kernel-first. The first milestone is a deterministic agent runtime with fake providers, fake tools, permissions, doom-loop detection, stream normalization, and atomic session persistence.

Kernel-first is the implementation strategy, not the final product scope. User interfaces, adapters, MCP, hooks, subagents, and packaging can be added later once the kernel contract is stable and tested.
