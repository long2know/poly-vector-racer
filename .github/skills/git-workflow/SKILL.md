---
name: "git-workflow"
description: "Project Git workflow: branch from main and open PRs against main"
domain: "version-control"
confidence: "high"
source: "team-decision"
---

## Context

**`main` is this repository's only integration and release branch.** Start
feature branches from `main` and open PRs against `main`.

## Branch Naming Convention

Issue branches MUST use: `squad/{issue-number}-{kebab-case-slug}`

Examples:
- `squad/195-fix-version-stamp-bug`
- `squad/42-add-profile-api`

Hotfixes branch from `main` as `hotfix/{slug}` and also target `main` in a PR.

## Workflow for Issue Work

1. **Branch from `main`:**
   ```bash
   git checkout main
   git pull --ff-only origin main
   git checkout -b squad/{issue-number}-{slug}
   ```

2. **Mark issue in-progress:**
   ```bash
   gh issue edit {number} --add-label "status:in-progress"
   ```

3. **Do the work.** Make changes, write tests, and commit with an issue reference.
   To update your feature branch with the latest `main` when needed:
   ```bash
   git fetch origin main
   git rebase origin/main
   ```
   If the branch was already pushed, update it after rebasing with `git push --force-with-lease`.

4. **Push and create a draft PR targeting `main`:**
   ```bash
   git push -u origin squad/{issue-number}-{slug}
   gh pr create --base main --title "{description}" --body "Closes #{issue-number}" --draft
   ```

5. **Mark ready:**
   ```bash
   gh pr ready
   ```

6. **After merge to `main`:**
   ```bash
   git checkout main
   git pull --ff-only origin main
   git branch -d squad/{issue-number}-{slug}
   git push origin --delete squad/{issue-number}-{slug}
   ```

## Parallel Multi-Issue Work (Worktrees)

When the coordinator routes multiple issues simultaneously (e.g., "fix bugs X, Y, and Z"), use `git worktree` to give each agent an isolated working directory. No filesystem collisions, no branch-switching overhead.

### When to Use Worktrees vs Sequential

| Scenario | Strategy |
|----------|----------|
| Single issue | Standard workflow above — no worktree needed |
| 2+ simultaneous issues in same repo | Worktrees — one per issue |
| Work spanning multiple repos | Separate clones as siblings (see Multi-Repo below) |

### Setup

From the main clone:

```bash
# Fetch the latest main
git fetch origin main

# Create a worktree per issue — siblings to the main clone
git worktree add ../squad-195 -b squad/195-fix-stamp-bug origin/main
git worktree add ../squad-193 -b squad/193-refactor-loader origin/main
```

**Naming convention:** `../{repo-name}-{issue-number}` (e.g., `../squad-195`, `../squad-pr-42`).

Each worktree:
- Has its own working directory and index
- Is on its own `squad/{issue-number}-{slug}` branch from `main`
- Shares the same `.git` object store (disk-efficient)

### Per-Worktree Agent Workflow

Each agent operates inside its worktree exactly like the single-issue workflow:

```bash
cd ../squad-195

# Stage only the files changed for this issue, then inspect the commit contents
git add -- path/to/intentionally-changed-file
git diff --cached --stat
git diff --cached
git commit -m "fix: stamp bug (#195)"
git push -u origin squad/195-fix-stamp-bug

# Create PR targeting main
gh pr create --base main --title "fix: stamp bug" --body "Closes #195" --draft
```

All PRs target `main` independently. Agents never interfere with each other's filesystem.

### .squad/ State in Worktrees

The `.squad/` directory exists in each worktree as a copy. This is safe because:
- `.gitattributes` declares `merge=union` on append-only files (history.md, decisions.md, logs)
- Each agent appends to its own section; union merge reconciles on PR merge to `main`
- **Rule:** Never rewrite or reorder `.squad/` files in a worktree — append only

### Cleanup After Merge

After a worktree's PR is merged to `main`:

```bash
# From the main clone
git worktree remove ../squad-195
git worktree prune          # clean stale metadata
git branch -d squad/195-fix-stamp-bug
git push origin --delete squad/195-fix-stamp-bug
```

If a worktree was deleted manually (rm -rf), `git worktree prune` recovers the state.

---

## Multi-Repo Downstream Scenarios

When work spans multiple repositories (e.g., squad-cli changes need squad-sdk changes, or a user's app depends on squad):

### Setup

Clone downstream repos as siblings to the main repo:

```
~/work/
  squad-pr/          # main repo
  squad-sdk/         # downstream dependency
  user-app/          # consumer project
```

Each repo gets its own issue branch following its own naming convention. If the downstream repo also uses Squad conventions, use `squad/{issue-number}-{slug}`.

### Coordinated PRs

- Create PRs in each repo independently
- Link them in PR descriptions:
  ```
  Closes #42

  **Depends on:** squad-sdk PR #17 (squad-sdk changes required for this feature)
  ```
- Merge order: dependencies first (e.g., squad-sdk), then dependents (e.g., squad-cli)

### Local Linking for Testing

Before pushing, verify cross-repo changes work together:

```bash
# Node.js / npm
cd ../squad-sdk && npm link
cd ../squad-pr && npm link squad-sdk

# Go
# Use replace directive in go.mod:
# replace github.com/org/squad-sdk => ../squad-sdk

# Python
cd ../squad-sdk && pip install -e .
```

**Important:** Remove local links before committing. `npm link` and `go replace` are dev-only — CI must use published packages or PR-specific refs.

### Worktrees + Multi-Repo

These compose naturally. You can have:
- Multiple worktrees in the main repo (parallel issues)
- Separate clones for downstream repos
- Each combination operates independently

---

## Anti-Patterns

- ❌ Basing a feature branch or a PR on anything other than `main`
- ❌ Non-conforming issue branch names (must be squad/{number}-{slug})
- ❌ Committing directly to `main` (use PRs)
- ❌ Switching branches in the main clone while worktrees are active (use worktrees instead)
- ❌ Using worktrees for cross-repo work (use separate clones)
- ❌ Leaving stale worktrees after PR merge (clean up immediately)

## Container Publishing

The Container workflow checks PRs targeting `main` without publishing. In
`long2know/poly-vector-racer`, it can publish to GHCR after checks pass on a push
to `main` or a manual dispatch from `main`.
