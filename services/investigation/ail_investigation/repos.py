"""Repository access for code intelligence: a local fixture repo and the GitHub REST API (read-only)."""

from __future__ import annotations

import json
import os
import re
from dataclasses import dataclass, field
from pathlib import Path
from typing import Protocol

import httpx
from ail_simulator import repo_root

_REPO_RE = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")


@dataclass
class FileChange:
    path: str
    status: str = "modified"
    additions: int = 0
    deletions: int = 0
    patch: str = ""


@dataclass
class CommitInfo:
    sha: str
    message: str
    author: str
    date: str
    files: list[FileChange] = field(default_factory=list)


class RepoProvider(Protocol):
    name: str

    def compare(self, base: str, head: str) -> list[CommitInfo]: ...


class FixtureRepo:
    """Serves commit ranges from a JSON file, for offline demos and deterministic tests."""

    def __init__(self, path: Path) -> None:
        self._data = json.loads(path.read_text())
        self.name = self._data["name"]

    def compare(self, base: str, head: str) -> list[CommitInfo]:
        raw = self._data["compares"].get(f"{base}...{head}")
        if raw is None:
            return []
        return [
            CommitInfo(c["sha"], c["message"], c["author"], c["date"], [FileChange(**f) for f in c["files"]])
            for c in raw["commits"]
        ]


class GitHubRepo:
    """Read-only GitHub access. A token with `contents:read` / `metadata:read` is sufficient."""

    API = "https://api.github.com"

    def __init__(self, repository: str, token: str | None = None, client: httpx.Client | None = None,
                 max_commits: int = 30) -> None:
        if not _REPO_RE.match(repository):
            raise ValueError("repository must look like 'owner/name'")
        self.name = repository
        self.max_commits = max_commits
        token = token or os.environ.get("GITHUB_TOKEN")
        headers = {"Accept": "application/vnd.github+json", "X-GitHub-Api-Version": "2022-11-28"}
        if token:
            headers["Authorization"] = f"Bearer {token}"
        self._http = client or httpx.Client(base_url=self.API, headers=headers, timeout=30)

    def compare(self, base: str, head: str) -> list[CommitInfo]:
        resp = self._http.get(f"/repos/{self.name}/compare/{base}...{head}")
        resp.raise_for_status()
        out = []
        for c in resp.json().get("commits", [])[: self.max_commits]:
            detail = self._http.get(f"/repos/{self.name}/commits/{c['sha']}")
            detail.raise_for_status()
            d = detail.json()
            out.append(CommitInfo(
                sha=c["sha"],
                message=c["commit"]["message"].splitlines()[0],
                author=(c.get("author") or {}).get("login", "unknown"),
                date=c["commit"]["author"]["date"],
                files=[FileChange(f["filename"], f.get("status", "modified"), f.get("additions", 0),
                                  f.get("deletions", 0), f.get("patch", "")) for f in d.get("files", [])],
            ))
        return out

    def create_issue(self, title: str, body: str, labels: list[str] | None = None) -> str:
        """Needs `issues:write`. Only called when the user explicitly asks for it."""
        resp = self._http.post(f"/repos/{self.name}/issues",
                               json={"title": title, "body": body, "labels": labels or []})
        resp.raise_for_status()
        return resp.json()["html_url"]


def get_provider(repository: str | None, mode: str = "auto") -> RepoProvider | None:
    """mode: 'auto' (fixture for fixture/* names, GitHub otherwise), 'fixture', 'github' or 'off'."""
    if not repository or mode == "off":
        return None
    if mode == "fixture" or (mode == "auto" and repository.startswith("fixture/")):
        name = repository.split("/", 1)[1]
        path = repo_root() / "docs" / "scenarios" / "repos" / f"{name}.json"
        return FixtureRepo(path) if path.exists() else None
    if mode in ("github", "auto"):
        return GitHubRepo(repository)
    return None
