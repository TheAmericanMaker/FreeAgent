# CodeCartographer audit snapshot

This directory contains curated audit outputs from the OpenMonoAgent.ai CodeCartographer deep-audit.

The canonical implementation contract for FreeAgent is:

- `docs/codecarto/reimplementation-spec.md`

FreeAgent is a ground-up product rework, not a source-level port. These documents preserve behavioral contracts, architecture observations, protocol/state notes, defect findings, and reimplementation guidance so the new implementation can keep the intended behavior while intentionally leaving legacy hazards behind.

The original `.codecarto/` workflow state and backup directories are intentionally not tracked in this repository. They are local analysis workspace state, not product source.
