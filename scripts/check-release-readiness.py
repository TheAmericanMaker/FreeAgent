#!/usr/bin/env python3
"""Fail-closed publication metadata and workflow boundary checks."""

from __future__ import annotations

import re
import sys
import xml.etree.ElementTree as ET
from datetime import date
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
REQUIRED_FILES = (
    "LICENSE",
    "NOTICE",
    "SECURITY.md",
    "CONTRIBUTING.md",
    "CODE_OF_CONDUCT.md",
)
EXPECTED_LICENSE = "AGPL-3.0-only"
PATCHED_OPENAPI_VERSION = "2.7.5"
SEMVER_TAG = re.compile(
    r"^v(?P<version>(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*))$"
)
DATED_CHANGELOG = re.compile(
    r"^## \[(?P<version>[^]]+)] - (?P<date>\d{4}-\d{2}-\d{2})$", re.MULTILINE
)


def project_properties(path: Path) -> dict[str, str]:
    root = ET.parse(path).getroot()
    return {
        child.tag: (child.text or "").strip()
        for group in root.findall("PropertyGroup")
        for child in group
    }


def package_references(path: Path) -> dict[str, str]:
    root = ET.parse(path).getroot()
    return {
        item.attrib["Include"]: item.attrib.get("Version", "")
        for group in root.findall("ItemGroup")
        for item in group.findall("PackageReference")
    }


def require_tokens(path: Path, tokens: tuple[str, ...], failures: list[str]) -> None:
    text = path.read_text(encoding="utf-8")
    for token in tokens:
        if token not in text:
            failures.append(f"{path.relative_to(ROOT)} must contain: {token}")


def require_pinned_actions(path: Path, failures: list[str]) -> None:
    text = path.read_text(encoding="utf-8")
    for line_number, line in enumerate(text.splitlines(), 1):
        match = re.search(r"\buses:\s+([^\s#]+)", line)
        if match is None:
            continue
        action = match.group(1)
        if action.startswith("./"):
            continue
        ref = action.rsplit("@", 1)[-1]
        if re.fullmatch(r"[0-9a-f]{40}", ref) is None:
            failures.append(
                f"{path.relative_to(ROOT)}:{line_number} must pin action to a full commit SHA"
            )


def main() -> int:
    failures: list[str] = []

    for relative in REQUIRED_FILES:
        if not (ROOT / relative).is_file():
            failures.append(f"missing required publication file: {relative}")

    required_publication_content = {
        "LICENSE": ("GNU AFFERO GENERAL PUBLIC LICENSE", "END OF TERMS AND CONDITIONS"),
        "NOTICE": ("StartupHakk/OpenMonoAgent.ai", "AGPL-3.0"),
        "SECURITY.md": ("james.sesler@pm.me", "Report a vulnerability"),
        "CONTRIBUTING.md": ("AGPL-3.0-only", "SECURITY.md"),
        "CODE_OF_CONDUCT.md": ("Contributor Covenant", "james.sesler@pm.me"),
    }
    for relative, tokens in required_publication_content.items():
        path = ROOT / relative
        if path.is_file():
            require_tokens(path, tokens, failures)

    host = ROOT / "src/FreeAgent.Host/FreeAgent.Host.csproj"
    host_properties = project_properties(host)
    if host_properties.get("PackageLicenseExpression") != EXPECTED_LICENSE:
        failures.append(
            "src/FreeAgent.Host/FreeAgent.Host.csproj must set "
            f"PackageLicenseExpression={EXPECTED_LICENSE}"
        )
    require_tokens(host, (r"..\..\LICENSE", r"..\..\NOTICE"), failures)

    server = ROOT / "src/FreeAgent.Server/FreeAgent.Server.csproj"
    openapi_version = package_references(server).get("Microsoft.OpenApi")
    if openapi_version != PATCHED_OPENAPI_VERSION:
        failures.append(
            "src/FreeAgent.Server/FreeAgent.Server.csproj must pin "
            f"Microsoft.OpenApi {PATCHED_OPENAPI_VERSION} (found {openapi_version or 'no direct pin'})"
        )

    require_tokens(
        ROOT / ".github/workflows/ci.yml",
        (
            "python3 scripts/check-release-readiness.py",
            "dotnet restore FreeAgent.slnx",
            "dotnet build FreeAgent.slnx -c Release --no-restore",
            "dotnet test FreeAgent.slnx -c Release --no-build --no-restore",
            "bun install --frozen-lockfile",
            "bun test",
            "bun run typecheck",
        ),
        failures,
    )
    require_pinned_actions(ROOT / ".github/workflows/ci.yml", failures)
    require_tokens(
        ROOT / ".github/workflows/release.yml",
        (
            'python3 scripts/check-release-readiness.py "$GITHUB_REF_NAME"',
            "bun install --frozen-lockfile",
            "bun test",
            "bun run typecheck",
            "sha256sum",
            "dotnet tool install",
        ),
        failures,
    )
    require_pinned_actions(ROOT / ".github/workflows/release.yml", failures)

    tag = sys.argv[1] if len(sys.argv) > 1 else None
    if len(sys.argv) > 2:
        failures.append("usage: check-release-readiness.py [vMAJOR.MINOR.PATCH]")
    if tag is not None:
        match = SEMVER_TAG.fullmatch(tag)
        if match is None:
            failures.append(f"release tag must be strict vMAJOR.MINOR.PATCH: {tag}")
        else:
            version = match.group("version")
            if host_properties.get("Version") != version:
                failures.append(
                    f"tag {tag} does not match package version {host_properties.get('Version')}"
                )
            changelog = (ROOT / "CHANGELOG.md").read_text(encoding="utf-8")
            dated_versions: set[str] = set()
            for heading in DATED_CHANGELOG.finditer(changelog):
                try:
                    date.fromisoformat(heading.group("date"))
                except ValueError:
                    failures.append(
                        f"CHANGELOG.md contains an invalid release date: {heading.group('date')}"
                    )
                else:
                    dated_versions.add(heading.group("version"))
            if version not in dated_versions:
                failures.append(
                    f"CHANGELOG.md must contain a dated '## [{version}] - YYYY-MM-DD' section before tagging"
                )

    if failures:
        print("release-readiness boundary: FAIL", file=sys.stderr)
        for failure in failures:
            print(f"- {failure}", file=sys.stderr)
        return 1

    print("release-readiness boundary: PASS")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
