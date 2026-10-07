---
name: "ci-doctor"
description: "Explain failed CI runs on main and propose a fix."
on:
  workflow_run:
    workflows: ["CI"]
    types: [completed]
    branches: [main]
  workflow_dispatch:
permissions:
  contents: read
  actions: read
  pull-requests: read
  copilot-requests: write
engine:
  id: copilot
  copilot-sdk: true
strict: true
timeout-minutes: 20
network:
  allowed:
    - "defaults"
    - "agents.contoso.com"
tools:
  github:
    toolsets: ["actions", "pull_requests"]
mcp-servers:
  azure:
    type: stdio
    command: "npx"
    args: ["-y", "@azure/mcp@0.9.3"]
    allowed: ["group_list", "subscription_list", "monitor_query", "resource_list"]
  terraform:
    type: stdio
    command: "docker"
    args: ["run", "-i", "--rm", "hashicorp/terraform-mcp-server:0.2.3"]
    allowed: ["searchModules", "moduleDetails", "resolveProviderDocID", "getProviderDocs"]
  contoso-docs:
    type: stdio
    command: "dotnet"
    args: ["tool", "run", "contoso-docs-mcp"]
    allowed: ["search", "get_page"]
safe-outputs:
  add-comment:
    max: 3
  create-pull-request:
    title-prefix: "[ci-doctor] "
    draft: true
  noop:
imports:
  - shared/contoso-governance.md
  - .github/agents/platform-engineer.md
evals:
  - id: validated
    question: "Did the agent run validation (fmt/validate/plan or build) before finishing?"
graders: {}
experiments:
  prompt_style:
    variants: ["concise", "detailed"]
    metric: "grader:tool-success-rate"
    min_samples: 20
---

# ci-doctor

A CI run on main failed. Read the failing job's logs, find the root cause, and either comment on the
triggering pull request with the cause and fix, or open a draft pull request when the fix is mechanical.

{{#if experiments.prompt_style == 'concise' }}
Work in a **concise** way for this run.
{{/if}}

{{#if experiments.prompt_style == 'detailed' }}
Work in a **detailed** way for this run.
{{/if}}
