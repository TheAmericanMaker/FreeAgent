# Security Policy

## Supported versions

Until the first stable release, security fixes are provided for the latest `0.1.x` release and the current `main` branch. Older development snapshots may not receive backports.

## Reporting a vulnerability

Please do not open a public issue for a suspected vulnerability.

1. Use GitHub's **Security → Report a vulnerability** flow when private vulnerability reporting is available for this repository.
2. If that flow is unavailable, email **james.sesler@pm.me** with the subject `FreeAgent security report`.

Include the affected version or commit, operating system, configuration, reproduction steps, expected impact, and any suggested mitigation. Do not include live credentials, private customer data, or destructive proof-of-concept payloads.

You should receive an acknowledgement within seven days. Disclosure timing will be coordinated after the report is reproduced and a remediation plan exists.

## Security-sensitive areas

Reports are especially useful for:

- capability and permission bypasses, including path or symlink escapes;
- unsafe tool execution, process handling, hooks, or project trust decisions;
- MCP or LSP subprocess boundaries;
- protocol-server binding, authentication, session isolation, or SSE handling;
- provider configuration, credential handling, SSRF, or unsafe endpoint probing;
- installer, update, package, or release-workflow supply-chain risks;
- transcript, memory, artifact, or configuration disclosure.

FreeAgent deliberately executes tools authorized by the user. A report should demonstrate behavior outside the documented permission, trust, or configuration boundary rather than merely showing that an explicitly approved tool can have side effects.
