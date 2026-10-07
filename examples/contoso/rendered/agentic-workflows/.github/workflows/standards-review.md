---
name: "standards-review"
description: "Review every pull request against the engineering standards."
on:
  pull_request:
    types: [opened, synchronize, reopened]
  workflow_dispatch:
permissions:
  contents: read
  pull-requests: read
  copilot-requests: write
engine:
  id: copilot
  copilot-sdk: true
  model: claude-opus-4.8
strict: true
timeout-minutes: 20
network:
  allowed:
    - "defaults"
    - "agents.contoso.com"
tools:
  github:
    toolsets: ["pull_requests"]
plugins:
  - contoso/copilot-plugins/plugins/contoso-engineering@main
safe-outputs:
  submit-pull-request-review:
    max: 1
    allowed-events: [COMMENT, REQUEST_CHANGES]
  noop:
imports:
  - shared/contoso-governance.md
  - .github/agents/standards-reviewer.md
evals:
  - id: cited_lines
    question: "Did every FAIL cite a file and line?"
graders: {}
---

# standards-review

Review the pull request with the standards-reviewer method and leave one review.
