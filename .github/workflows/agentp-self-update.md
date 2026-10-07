---
name: "agentp-self-update"
description: "When GitHub documents new agent governance surface, update the F# DSL to model it and open a pull request."
on:
  workflow_dispatch:
    inputs:
      drift_run_id:
        description: "Run ID of the docs-drift workflow whose report to act on"
        required: true
permissions:
  contents: read
  actions: read
  issues: read
  pull-requests: read
engine:
  # Inference uses the COPILOT_GITHUB_TOKEN secret (fine-grained token with Copilot Requests), so it works in a
  # user-owned public repository. In an organization with Copilot billing, add `copilot-requests: write` instead.
  id: copilot
  copilot-sdk: true
  model: claude-opus-4.8
strict: true
concurrency:
  job-discriminator: ${{ github.run_id }}
timeout-minutes: 45
max-ai-credits: 20000
network:
  allowed:
    - defaults
    - dotnet
    - github
tools:
  github:
    toolsets: [repos, actions, pull_requests]
  edit:
  bash:
    - "dotnet build:*"
    - "dotnet test:*"
    - "dotnet run --project examples/contoso/Contoso.Platform.fsproj -c Release -- drift:*"
    - "dotnet run --project examples/contoso/Contoso.Platform.fsproj -c Release -- render:*"
    - "gh run download:*"
    - "git clone --depth 1 https://github.com/github/docs.git:*"
    - "git clone --depth 1 https://github.com/github/gh-aw.git:*"
    - "git -C /tmp/gh-aw/agent/docs sparse-checkout:*"
    - "git diff:*"
    - "git status:*"
    - "cat:*"
    - "grep:*"
    - "ls:*"
    - "jq:*"
safe-outputs:
  create-pull-request:
    title-prefix: "[agentp-self-update] "
    labels: [agent-platform, schema-drift]
    draft: true
  create-issue:
    title-prefix: "[agentp-self-update] "
    labels: [agent-platform, schema-drift]
    max: 1
  noop:
evals:
  - id: drift_closed
    question: "After the change, does `drift` report no remaining drift for the areas the pull request claims to handle?"
  - id: builds_and_tests
    question: "Did `dotnet build` and `dotnet test` succeed after the change?"
  - id: typed_not_stringly
    question: "Were new settings modelled as F# types or builder operations (not raw strings or JSON passthrough)?"
graders: {}
---

# Keep the agent platform DSL current

You maintain **AgentPlatform**, a strongly typed F# DSL that turns one `platform { }` definition into every asset GitHub
Copilot reads: enterprise managed settings, team overrides, plugins, skills, hooks, MCP allowlists and registry, custom
agents, instructions, rulesets and agentic workflows. GitHub has documented new (or removed) surface that the DSL does not
model yet. Your job is to close that gap with a small, reviewable pull request.

## 1. Read the drift report

If this run was dispatched with `drift_run_id`, download its artifact:
`gh run download ${{ github.event.inputs.drift_run_id }} -n drift -D drift`.
Otherwise clone the sources and compute it yourself:

```
git clone --depth 1 --filter=blob:none --sparse https://github.com/github/docs.git /tmp/gh-aw/agent/docs
git -C /tmp/gh-aw/agent/docs sparse-checkout set content/copilot src/rest/data data/variables data/reusables/copilot
git clone --depth 1 https://github.com/github/gh-aw.git /tmp/gh-aw/agent/gh-aw
dotnet run --project examples/contoso/Contoso.Platform.fsproj -c Release -- drift --docs /tmp/gh-aw/agent/docs --gh-aw /tmp/gh-aw/agent/gh-aw --out drift
```

`drift/changes.json` lists, per area, what the documentation `added` and what it `removed`.
If there is no drift, call `noop`.

## 2. Read the source of truth for each change

For every added item, read the exact documentation passage before writing code. The area tells you where:

| Area | Documentation | DSL module |
| --- | --- | --- |
| `managed-settings.*` | `content/copilot/reference/enterprise-administrators/enterprise-managed-settings.md` | `src/AgentPlatform/Policy.fs` |
| `hooks.events` | `content/copilot/reference/hooks-reference.md` | `src/AgentPlatform/HookEvents.fs`, `Hooks.fs` |
| `plugins.*` | `content/copilot/reference/copilot-cli-reference/cli-plugin-reference.md` | `src/AgentPlatform/Plugins.fs` |
| `agents.frontmatter` | `content/copilot/reference/custom-agents-configuration.md` | `src/AgentPlatform/Agents.fs`, `Render.fs` (profile) |
| `rulesets.rule-types` | `src/rest/data/<latest ghec>/repos.json` | `src/AgentPlatform/Governance.fs` |
| `mcp-registry.surfaces`, `policy.names` | `content/copilot/reference/...` | `Mcp.fs`, `Render.fs` (coverage) |
| `gh-aw.*` | `gh-aw/docs/src/content/docs/reference/frontmatter.md`, `gh-aw/pkg/workflow/mcp_config_validation.go` | `Agents.fs`, `Render.fs` (workflowMarkdown) |

## 3. Change the F#

- Model each new setting as a **typed** value: a record field, a DU case, or a builder operation with an English name
  (`noBypassMode`, `denyByDefault`, `sandboxed`). Never add a raw string or JSON passthrough.
- Render it exactly as the documentation shows, including `{ "overridable": ... }` where the docs say teams may override.
- Add it to the validation in `Policy.validateEnterprise` / `validateTeam` when the docs state a rule (for example
  "not overridable for teams").
- Update `Drift.known` so the snapshot matches what the DSL now models.
- Removed items: mark the operation `[<Obsolete("...")>]` with a pointer to the doc change rather than deleting it.

## 4. Prove it

```
dotnet build
dotnet test
dotnet run --project examples/contoso/Contoso.Platform.fsproj -c Release -- drift --docs /tmp/gh-aw/agent/docs --gh-aw /tmp/gh-aw/agent/gh-aw --out drift
dotnet run --project examples/contoso/Contoso.Platform.fsproj -c Release -- render --out examples/contoso/rendered --clean
```

Drift must report nothing left for the areas you handled. Commit the re-rendered `examples/contoso/rendered` so reviewers see the
asset diff.

## 5. Open the pull request

Title it after the change, for example "Model managed setting `permissions.network`". In the body, list each documented
change with a link to the documentation file and line, the F# you added, and the rendered asset diff. If an item cannot be
modelled safely (the docs are ambiguous), leave it out and open an issue explaining why instead.
