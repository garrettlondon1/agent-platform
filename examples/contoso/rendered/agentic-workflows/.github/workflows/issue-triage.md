---
name: "issue-triage"
description: "Label new issues, ask for missing details, and hand small well-specified work to Copilot."
on:
  issues:
    types: [opened]
  workflow_dispatch:
permissions:
  contents: read
  issues: read
  copilot-requests: write
engine:
  id: copilot
  model: claude-opus-4.8
strict: true
timeout-minutes: 10
network:
  allowed:
    - "docs.contoso.com"
    - "agents.contoso.com"
tools:
  github:
    toolsets: ["issues"]
safe-outputs:
  add-labels:
    allowed: ["bug", "feature", "docs", "area:payments", "area:platform", "area:web"]
  add-comment:
    max: 3
  assign-to-agent:
  noop:
imports:
  - shared/contoso-governance.md
  - .github/agents/issue-triager.md
evals:
  - id: labels_fit
    question: "Do the labels match the issue's area and type?"
graders:
  operational-value:
    name: "Triage minutes saved"
    description: "Maintainer minutes avoided by labelling and routing this issue"
    unit: minutes
    direction: higher_is_better
    run: .github/graders/issue-triage-operational-value.sh
---

# issue-triage

Triage the issue with the issue-triager method.
