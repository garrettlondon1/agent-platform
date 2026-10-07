---
name: "platform-change"
description: "Turn an issue into a validated pull request against the agent platform definition (Platform.fs)."
on:
  # Anyone may open an issue asking for a change; only someone with write access can apply the label that
  # starts this agent (gh-aw's default on.roles), and the label is removed so it can be re-applied.
  label_command:
    name: platform-change
    events: [issues]
permissions:
  contents: read
  issues: read
  pull-requests: read
  copilot-requests: write
engine:
  id: copilot
  # Pinned to a model the enterprise model policy enables AND the runner's Copilot CLI can call (/chat/completions).
  # "auto" resolved to a /responses-only model; claude-sonnet-5 is not enabled by this enterprise's model policy.
  model: claude-opus-4.8
strict: true
concurrency:
  job-discriminator: ${{ github.run_id }}
timeout-minutes: 30
max-ai-credits: 10000
runtimes:
  dotnet:
    version: "10.0.x"
network:
  allowed:
    - defaults
    - dotnet
    - github
tools:
  github:
    toolsets: [issues, repos]
  edit:
  bash:
    - "dotnet restore:*"
    - "dotnet build:*"
    - "dotnet run -c Release -- vocabulary:*"
    - "dotnet run -c Release -- validate:*"
    - "dotnet run -c Release --no-build -- render:*"
    - "dotnet run -c Release --no-build -- diff:*"
    - "dotnet run -c Release --no-build -- coverage:*"
    - "git stash:*"
    - "git diff:*"
    - "git status:*"
    - "cat:*"
    - "grep:*"
    - "ls:*"
safe-outputs:
  create-pull-request:
    title-prefix: "[platform-change] "
    labels: [agent-platform]
    draft: true
    # The agent may change the definition and nothing else: not Program.fs, not the project file, not workflows.
    allowed-files: [Platform.fs]
    protected-files: blocked
    # PRs opened with the workflow token do not start CI; this token pushes one empty commit so the required
    # `render` check runs. Optional: without it, a maintainer re-runs the check from the PR.
    github-token-for-extra-empty-commit: ${{ secrets.PLATFORM_CI_TOKEN }}
  add-comment:
    max: 2
  noop:
evals:
  - id: only_the_definition
    question: "Did the pull request change only Platform.fs, using operations that appear in the `vocabulary` output?"
  - id: validated
    question: "Did `validate` pass and was the `diff` of rendered assets included in the pull request body?"
  - id: no_silent_weakening
    question: "If the change weakens governance (rollout stage down, deny rules removed, protection or deployer changed), does the pull request say so prominently?"
graders: {}
---

# Platform change request

You maintain this organization's **agent platform definition**: one strongly typed F# file, `Platform.fs`, written in
the AgentPlatform DSL. It renders every governance asset GitHub Copilot reads (enterprise managed settings, plugins,
skills, hooks, MCP allow/deny lists, custom agents, instructions, rulesets, agentic workflows). Nobody edits those
assets by hand: they change only when this definition changes, through a reviewed pull request.

Issue #${{ github.event.issue.number }} asks for a change. Read it with the GitHub tools.

## 1. Learn the vocabulary - do not guess

Run `dotnet run -c Release -- vocabulary` and read it. It is generated from the exact AgentPlatform version this
repository builds against: every builder (`policy { }`, `hooks { }`, `mcp { }`, `plugin { }`, `agent { }`,
`workflow { }`, `ruleset { }`, `platform { }` ...), every operation each accepts, every choice and helper. Use only what
it lists. Then read `Platform.fs` to see how this organization already uses it, and follow its style.

## 2. Decide

- If the request is unclear, contradicts itself, or needs something the vocabulary cannot express, do not edit.
  Comment on the issue with what you need (or what is missing from AgentPlatform) and stop.
- If it asks to bypass governance for one person, to disable the platform's self-protection, to change `deployedBy`,
  or to move `rollout` to a weaker stage, you may still propose it, but the pull request title must start with
  `WEAKENS GOVERNANCE:` and the body must explain exactly what stops being enforced.
- Never put secrets, tokens or credentials in the definition. Secrets are named with `agentSecret` and set separately.

## 3. Change Platform.fs

Make the smallest change that does what the issue asks, in the existing style. Prefer existing standards, plugins,
rules and servers over new ones. Every new rule, server, plugin or agent gets a `describedAs`/description a developer
would understand when they hit it.

## 4. Prove it

1. `dotnet build` and `dotnet run -c Release -- validate` must pass. Fix every problem it reports.
2. `git stash`, then `dotnet build` and `dotnet run -c Release --no-build -- render --out /tmp/gh-aw/agent/before --clean`,
   then `git stash pop` and `dotnet build` again.
3. `dotnet run -c Release --no-build -- diff --against /tmp/gh-aw/agent/before` lists every rendered asset that changes.

## 5. Open the pull request

Create a pull request (it is a draft; a human reviews and merges it). The body must contain:

- `Fixes #${{ github.event.issue.number }}` and one paragraph on what changes for developers and agents, by surface
  (VS Code / Visual Studio / CLI, SDK, cloud agent, agentic workflows) where it matters.
- The rollout stage (read `rollout` in Platform.fs) and what that means for this change: at `Observe` new blocking rules
  only record what they would have done; at `Guard` and `Enforce` they block.
- The `diff` output in a code block.

Then add a short comment on the issue linking to the pull request.
