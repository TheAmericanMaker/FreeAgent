# Contributing to FreeAgent

Thank you for helping improve FreeAgent.

## Before you start

- Search existing issues and pull requests before proposing overlapping work.
- Use a focused issue for significant behavior, protocol, security, or architecture changes.
- Report vulnerabilities privately using [SECURITY.md](SECURITY.md), not a public issue.
- Keep changes narrow; avoid unrelated formatting or refactoring.

## Development setup

FreeAgent requires the .NET 10 SDK selected by `global.json`. The TUI additionally requires Bun.

```bash
dotnet restore FreeAgent.slnx
dotnet build FreeAgent.slnx -c Release --no-restore
dotnet test FreeAgent.slnx -c Release --no-build --no-restore

cd clients/tui
bun install --frozen-lockfile
bun test
bun run typecheck
```

Run the publication boundary check when changing packaging, dependencies, workflows, governance, or release metadata:

```bash
python3 scripts/check-release-readiness.py
```

## Pull requests

- Add or update tests for behavior changes and bug fixes.
- Preserve the flat `FreeAgent.Kernel` namespace convention.
- Keep builds warning-free; warnings are treated as errors.
- Update user-facing documentation and `CHANGELOG.md` when behavior changes.
- Explain the problem, approach, security implications, and verification commands in the PR description.
- Do not commit credentials, local agent state, build output, transcripts, or provider configuration.

## License

By submitting a contribution, you agree that it may be distributed under the repository's [Apache-2.0 license](LICENSE). You represent that you have the right to submit the contribution under those terms.

All contributors must follow the [Code of Conduct](CODE_OF_CONDUCT.md).
