---
name: "ask-platform"
description: "Answer /platform questions on issues and pull requests with the platform engineer."
on:
  slash_command:
    name: platform
  workflow_dispatch:
permissions:
  contents: read
  issues: read
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
    toolsets: ["issues", "pull_requests"]
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
imports:
  - shared/contoso-governance.md
  - .github/agents/platform-engineer.md
evals:
  - id: validated
    question: "Did the agent run validation (fmt/validate/plan or build) before finishing?"
graders: {}
---

# ask-platform

Answer the question in the triggering comment with the platform-engineer method. Reply with one comment.
