---
name: "security-triage"
description: "Triage new code scanning alerts every night and open a draft fix when exploitable."
on:
  schedule: "daily around 02:00 on weekdays"
  workflow_dispatch:
permissions:
  contents: read
  issues: read
  pull-requests: read
  security-events: read
  copilot-requests: write
engine:
  id: copilot
  copilot-sdk: true
  model: claude-opus-4.8
strict: true
timeout-minutes: 20
max-ai-credits: 2000
network:
  allowed:
    - "defaults"
    - "agents.contoso.com"
tools:
  github:
    toolsets: ["code_security", "issues", "pull_requests"]
  bash: ["git diff:*", "git status:*", "dotnet test:*", "npm test:*"]
  edit:
mcp-servers:
  contoso-jira:
    type: http
    url: "https://mcp.atlassian.com/v1/mcp"
    headers:
      Authorization: "${{ secrets.MCP_JIRA_AUTHORIZATION }}"
    allowed: ["getJiraIssue", "searchJiraIssuesUsingJql", "getVisibleJiraProjects"]
plugins:
  - contoso/copilot-plugins/plugins/contoso-governance@main
  - contoso/copilot-plugins/plugins/contoso-engineering@main
safe-outputs:
  create-pull-request:
    title-prefix: "[security-triage] "
    draft: true
  create-issue:
    title-prefix: "[security-triage] "
    labels: ["security", "agent"]
    max: 5
  noop:
imports:
  - shared/contoso-governance.md
  - .github/agents/security-triage.md
evals:
  - id: verdict_justified
    question: "Did the agent justify its exploitability verdict with a concrete call chain?"
  - id: fix_tested
    question: "If a fix was made, was a test added and run?"
graders: {}
---

# security-triage

Find code scanning alerts opened in the last day on the default branch.
Triage each with the security-triage agent's method. Open one draft pull request per exploitable alert,
or an issue when a person must decide. Use noop when there is nothing new.
