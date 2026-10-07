# AgentPlatform

Govern every GitHub Copilot agent your organisation runs, from one strongly typed F# file.

One `platform { }` definition renders every asset GitHub, the Copilot clients and GitHub Agentic Workflows read:
enterprise managed settings, team overrides, plugins, skills, hooks, MCP allowlists and the MCP registry, custom agents,
coding standards, rulesets and agentic workflows. The output is plain files in a versioned, deterministic tree you commit
to git.

> **Status.** A community project, not an official GitHub product. It is built only on documented GitHub features, several
> of which (enterprise managed settings, the MCP registry, GitHub Agentic Workflows) are in public preview and change
> often. The `drift` command and the daily `docs-drift` workflow track those changes so the typed model can keep up.

| Surface | How it is governed |
| --- | --- |
| VS Code, Visual Studio, JetBrains, Copilot app, Copilot CLI | `managed-settings.json` (+ team files), plugins installed by `enabledPlugins`, `.github-private` agents, repository instructions and hooks, MCP registry |
| Copilot SDK | `Sdk.sessionConfig`: the same policy, plugins, standards, agents and hook rules, applied in-process |
| Copilot cloud agent | `.github-private` agents, repository hooks (`bash`), cloud agent MCP configuration, rulesets |
| GitHub Agentic Workflows | rendered `.github/workflows/*.md` that import the same agents, plugins, MCP servers and evals |

## Quick start

Prerequisites: the [.NET 10 SDK](https://dotnet.microsoft.com/download), the [GitHub CLI](https://cli.github.com) and, for
agentic workflows, its [gh-aw](https://github.com/github/gh-aw) extension. The Copilot SDK dependency downloads the matching
Copilot CLI runtime at build time.

```fsharp
open AgentPlatform.Policy
open AgentPlatform.Hooks
open AgentPlatform.Mcp
open AgentPlatform.Standards
open AgentPlatform.Plugins
open AgentPlatform.Agents
open AgentPlatform.Platform

let guardrails =
    hooks {
        rule (beforeTool "no-force-push" (shellCommand "git push --force") (block "Push a new commit instead."))
        rule (beforeTool "no-secrets" (reading "**/.env") (block "Secrets are off limits to agents."))
        rule (beforeTool "prod" (shellCommand "terraform apply") (requireApproval "Production changes need a person."))
        captureEverything
    }

let reviewer =
    agent {
        named "reviewer"
        useWhen "Review a diff against our standards."
        instructions "Return PASS or FAIL per standard, with file and line."
        uses [ readFiles; searchFiles ]
        follows [ "engineering" ]
        evaluatedBy [ question "cited" "Did every FAIL cite a file and line?" ]
    }

let acme =
    platform {
        organization "acme"
        platformHost "https://agents.acme.com"
        governedBy (policy { modelByDefault "auto"; noBypassModeByDefault; denyByDefault [ read "~/.ssh/**" ]; allowByDefault [ read "/**"; edit "/src/**" ] })
        standards [ standard { named "engineering"; rules [ "Every behaviour change ships with a test." ] } ]
        hooks guardrails
        plugins [ plugin { named "acme-governance"; describedAs "Central hooks."; carriesHooks } ]
        agents [ reviewer ]
    }

[<EntryPoint>]
let main argv = AgentPlatform.Cli.run acme argv
```

```text
dotnet run -- validate        every problem with the definition, before anything is written
dotnet run -- render          write every asset to .platform/ (deterministic, with platform.lock.json)
dotnet run -- diff --against <dir>   what changed since a previous render
dotnet run -- coverage        which control reaches which surface
dotnet run -- whatif --teams a,b     the managed settings a member of those enterprise teams gets
dotnet run -- vocabulary      every builder, operation and choice this version of the DSL accepts
dotnet run -- plan            what GitHub settings would change (read-only; unreadable settings show as ACCESS)
dotnet run -- apply           apply that plan (rulesets, cloud agent access, content exclusion, variables, ...)
dotnet run -- publish         commit each rendered tree into the repository that serves it (gh CLI, org owner)
dotnet run -- serve           the platform service: hook decisions, OTLP collector, MCP registry
dotnet run -- hook-test preToolUse < payload.json
dotnet run -- sdk <agent> "<prompt>"
dotnet run -- drift --docs <github/docs clone> --gh-aw <github/gh-aw clone>
dotnet run -- evals --data <service data dir> [--aw-logs <gh aw logs dir>] [--judge]
```

See `examples/contoso/Platform.fs` for a complete organisation (it is also reproduced in full below).

## A complete organization

Every control GitHub documents for Copilot, in one definition: standards, an MCP catalog with every transport, hooks
with composed conditions, skills, custom agents (including hidden and ask-only ones), plugins, five agentic workflows
(schedule, failed CI, pull requests, issues, slash command; custom graders and an experiment), every managed-settings key
for the enterprise default, a separate device policy for Intune / Jamf, four enterprise team overrides, rulesets with
every rule and bypass type, content exclusion, cloud agent settings, session storage, rollout stage and the deployer.

This is `examples/contoso/Platform.fs`, verbatim: CI compiles it, `validate` passes, it renders 95 files, and its five
agentic workflows compile with `gh aw compile --strict`. The rendered output is committed in
[`examples/contoso/rendered`](examples/contoso/rendered) — for example the enterprise
[`managed-settings.json`](examples/contoso/rendered/.github-private/copilot/managed-settings.json) and its
[team overrides](examples/contoso/rendered/.github-private/copilot/teams).

<!-- full-example:start -->
```fsharp
/// Contoso's agent platform - every GitHub Copilot control the company uses, in one typed definition.
///
/// It governs every agent Contoso runs: VS Code, Visual Studio, JetBrains and the CLI, the Copilot app, the Copilot SDK,
/// Copilot cloud agent and GitHub Agentic Workflows. Change it in a pull request; CI validates and renders it, shows
/// what changes, and deploys it on merge.
module Contoso.Platform

open AgentPlatform
open AgentPlatform.Policy
open AgentPlatform.HookEvents
open AgentPlatform.Hooks
open AgentPlatform.Mcp
open AgentPlatform.Standards
open AgentPlatform.Plugins
open AgentPlatform.Agents
open AgentPlatform.Governance
open AgentPlatform.Platform

// ---------------------------------------------------------------------------
// Coding standards
// One source for copilot-instructions.md, path-scoped *.instructions.md, AGENTS.md, plugin skills, the SDK system
// message and the organization custom instructions.
// ---------------------------------------------------------------------------

let everywhere =
    standard {
        named "engineering"
        title "Contoso engineering standards"
        rules [
            "Every behaviour change ships with a test that fails without it."
            "Branch names and commit subjects start with the Jira key, e.g. PAY-142."
            "Never commit secrets, .env files or generated credentials."
        ]
        rule "Prefer small pull requests; one concern per pull request."
        whenReviewing [ "Flag any change without a test."; "Flag new dependencies that are not pinned." ]
    }

let dotnet =
    standard {
        named "dotnet"
        title ".NET services"
        appliesTo [ "**/*.cs"; "**/*.fs"; "**/*.csproj"; "**/*.fsproj" ]
        rules [
            "Target net10.0 and enable nullable reference types."
            "Use ILogger with structured templates; never string-interpolate log messages."
            "Never log request bodies, card numbers, emails or tokens."
        ]
    }

let infrastructure =
    standard {
        named "infrastructure"
        title "Infrastructure as code"
        appliesTo [ "**/*.tf"; "**/*.bicep"; "deploy/**" ]
        rules [
            "Every resource carries owner and cost-center tags."
            "No public network access unless the pull request explains why."
            "Run terraform fmt and validate before proposing a change."
        ]
    }

// ---------------------------------------------------------------------------
// MCP: the approved catalog
// Rendered as allowedMcpServers / deniedMcpServers, a v0.1 MCP registry, cloud agent MCP configuration, plugin
// .mcp.json files and gh-aw mcp-servers. Anything not listed is blocked on every governed client.
// ---------------------------------------------------------------------------

let jira =
    mcpServer {
        id "com.atlassian/jira"
        key "jira"
        title "Jira"
        describedAs "Read Jira issues, epics and sprints."
        remote "https://mcp.atlassian.com/v1/mcp"
        tools [ "getJiraIssue"; "searchJiraIssuesUsingJql"; "getVisibleJiraProjects" ]
        needsHeader "Authorization" "Bearer token for a Jira service account with read-only access."
        readOnly
    }

let sentry =
    mcpServer {
        id "io.sentry/sentry"
        key "sentry"
        title "Sentry"
        describedAs "Read production errors and their stack traces."
        remoteSse "https://mcp.sentry.dev/sse"
        tools [ "get_issue_details"; "search_issues" ]
        needsHeader "Authorization" "Bearer token for the Sentry read-only integration."
        readOnly
    }

let azure =
    mcpServer {
        id "com.microsoft/azure"
        key "azure"
        title "Azure"
        describedAs "Inspect Azure resources (read-only tools)."
        npm "@azure/mcp" "0.9.3"
        tools [ "group_list"; "subscription_list"; "monitor_query"; "resource_list" ]
        readOnly
    }

let playwright =
    mcpServer {
        id "com.microsoft/playwright"
        key "playwright"
        title "Playwright"
        describedAs "Drive a browser against local test servers."
        npm "@playwright/mcp" "0.0.41"
        tools [ "browser_navigate"; "browser_snapshot"; "browser_click"; "browser_type"; "browser_take_screenshot" ]
    }

let markitdown =
    mcpServer {
        id "com.microsoft/markitdown"
        key "markitdown"
        title "MarkItDown"
        describedAs "Convert PDFs and Office documents to Markdown."
        pypi "markitdown-mcp" "0.0.1a4"
        tools [ "convert_to_markdown" ]
        readOnly
    }

let terraform =
    mcpServer {
        id "io.hashicorp/terraform"
        key "terraform"
        title "Terraform registry"
        describedAs "Look up Terraform providers and modules."
        container "hashicorp/terraform-mcp-server" "0.2.3"
        tools [ "searchModules"; "moduleDetails"; "resolveProviderDocID"; "getProviderDocs" ]
        readOnly
    }

let contosoDocs =
    mcpServer {
        id "com.contoso/docs"
        key "contoso-docs"
        title "Contoso engineering docs"
        describedAs "Search Contoso's internal engineering handbook and runbooks."
        version "1.4.0"
        command "dotnet" [ "tool"; "run"; "contoso-docs-mcp" ]
        tools [ "search"; "get_page" ]
        needsSecret "DOCS_TOKEN" "Read-only token for the docs service."
        repository "https://github.com/contoso/docs-mcp"
        readOnly
    }

let catalog =
    mcp {
        approveAll [ jira; sentry; azure; playwright; markitdown; terraform ]
        approve contosoDocs
        block [
            blockCommand [ "npx"; "-y"; "@modelcontextprotocol/server-filesystem"; "/" ]
            blockUrl "https://*.ngrok.io/*"
            blockUrl "https://*.ngrok-free.app/*"
            blockName "untrusted-server"
        ]
        registryAt "https://agents.contoso.com"
        registryOnly
    }

// ---------------------------------------------------------------------------
// Hooks: written once, enforced on every surface
// Deciding rules render as fail-closed command hooks (plugin, Windows registry / policy.d on devices, repository
// .github/hooks for cloud agent) and run in-process in SDK sessions. Capture streams every event to the platform service.
// ---------------------------------------------------------------------------

let guardrails =
    hooks {
        rule (beforeTool "no-force-push" (anyOf [ shellCommand "git push --force"; shellCommand "git push -f" ]) (block "Force-pushing rewrites shared history. Push a new commit instead."))
        rule (beforeTool "no-secrets" (anyOf [ reading "**/.env"; reading "**/*.pem"; reading "**/.ssh/**"; argsMatching @"(\.aws|\.azure)[\\/]" ]) (block "Secrets and credential files are off limits to agents."))
        rule (beforeTool "no-pipe-to-shell" (argsMatching @"curl[^|]*\|\s*(ba|z)?sh") (block "Piping downloads into a shell is not allowed."))
        rule (beforeTool "no-exfil-hosts" (fetching @"(pastebin\.com|transfer\.sh|ngrok)") (block "That host is a known exfiltration channel."))
        rule (beforeTool "protect-workflows" (writingTo ".github/workflows/**") (requireApproval "Changing CI workflows needs a person to approve."))
        rule (beforeTool "prod-deploys" (anyOf [ shellCommand "terraform apply"; shellCommand "kubectl apply"; shellCommand "kubectl delete" ]) (requireApproval "Production changes need a person to approve."))
        // Conditions compose: only the internal npm registry, and cloud agent never touches clusters.
        rule (beforeTool "internal-npm-only" (Hooks.allOf [ shellCommand "npm install"; not' (argsMatching "--registry=https://npm\\.contoso\\.com") ]) (requireApproval "Install from https://npm.contoso.com or ask a person."))
        rule (beforeTool "cloud-agent-no-kubectl" (Hooks.allOf [ onSurface [ CloudAgent ]; shellCommand "kubectl" ]) (block "Cloud agent never talks to clusters; open a pull request instead."))
        rule (afterTool "test-reminder" (writingTo "src/**") (addContext "Contoso standard: add or update a test for this change before finishing."))
        rule (
            on [ UserPromptSubmitted ] "cardholder-data" (promptContains "card number") (addContext "Cardholder data never goes into code, logs, tests or prompts; use the tokenization service.")
            |> describedAs "Reminds the agent of PCI rules when a prompt mentions card data."
            |> failOpen
        )
        captureEverything
        // includingContent   // also store prompts and tool output - only with privacy/legal sign-off
        timeoutSeconds 5
    }

// ---------------------------------------------------------------------------
// Skills and custom agents
// Agents render to .github-private/agents (every repository, every client), plugin agents, SDK custom agents and
// gh-aw imports, each with its own evals.
// ---------------------------------------------------------------------------

let releaseNotes =
    skill {
        named "release-notes"
        useWhen "Writing release notes, a changelog entry or a summary of merged pull requests."
        steps
            """List merged pull requests since the last tag with `git log --merges`.
            Group them by Jira epic. Write one line per change in the past tense, user-facing first.
            End with a 'Breaking changes' section, even if it says 'None'."""
        withFile "template.md" "## Highlights\n\n## Fixes\n\n## Breaking changes\n"
    }

let incidentRunbook =
    skill {
        named "incident-runbook"
        useWhen "Responding to a production incident or writing a post-incident review."
        steps "Open the incident channel, page the owning team, and follow the runbook in the Contoso docs MCP server."
    }

let securityTriage =
    agent {
        named "security-triage"
        useWhen "Triage a code scanning, secret scanning or Dependabot alert and propose the smallest safe fix."
        instructions
            """You triage security alerts for Contoso.
            Decide whether the finding is reachable from untrusted input and say EXPLOITABLE, NOT_EXPLOITABLE or NEEDS_HUMAN, with the call chain as evidence.
            If exploitable, make the smallest fix, add a test that fails before it, and run the tests.
            Never weaken a query, a suppression or a ruleset to make an alert go away."""
        model "claude-opus-4.8"
        uses [ readFiles; searchFiles; editFiles; runShell; github ]
        mcpServers [ "jira" ]
        follows [ "engineering" ]
        evaluatedBy [
            question "verdict_justified" "Did the agent justify its exploitability verdict with a concrete call chain?"
            question "fix_tested" "If a fix was made, was a test added and run?"
        ]
        owner "appsec@contoso.com"
        cannotSee [ "customer-pii" ]
    }

let platformEngineer =
    agent {
        named "platform-engineer"
        useWhen "Change infrastructure, CI pipelines or Azure resources for a Contoso service."
        instructions
            """You make infrastructure and pipeline changes for Contoso services.
            Read the current state first (Azure tools are read-only), propose the change as code, and validate it locally.
            Never apply changes to production yourself; open a pull request."""
        uses [ readFiles; searchFiles; editFiles; runShell; github; Agents.allOf "terraform"; mcpTool "contoso-docs" "search" ]
        mcpServers [ "azure"; "terraform"; "contoso-docs" ]
        follows [ "engineering"; "infrastructure" ]
        evaluatedBy [ question "validated" "Did the agent run validation (fmt/validate/plan or build) before finishing?" ]
        owner "platform@contoso.com"
    }

let reviewer =
    agent {
        named "standards-reviewer"
        useWhen "Review a diff against Contoso engineering standards."
        instructions
            """Review the current diff against the Contoso standards that apply to each file.
            Return PASS or FAIL per standard with file and line for every FAIL. Do not edit files."""
        model "claude-opus-4.8"
        uses [ readFiles; searchFiles ]
        follows [ "engineering"; "dotnet"; "infrastructure" ]
        evaluatedBy [ question "cited_lines" "Did every FAIL cite a file and line?" ]
        owner "devex@contoso.com"
    }

/// Used only by the issue-triage workflow, so it never appears in anyone's agent picker.
let triager =
    agent {
        named "issue-triager"
        useWhen "Label a new issue, ask for missing details, and hand well-specified work to Copilot cloud agent."
        instructions
            """Read the new issue. Add one area label and one type label. If reproduction steps or acceptance criteria are
            missing, comment once asking for them. If the issue is a small, well-specified change, assign it to Copilot."""
        model "claude-opus-4.8"
        uses [ readFiles; githubTool "issue_read"; webAccess ]
        follows [ "engineering" ]
        evaluatedBy [ question "labels_fit" "Do the labels match the issue's area and type?" ]
        owner "devex@contoso.com"
        notUserSelectable
    }

/// Never picked automatically: a person must ask for it by name.
let incidentCommander =
    agent {
        named "incident-commander"
        useWhen "Coordinate a production incident: gather signals, keep a timeline, draft updates."
        instructions "Gather errors from Sentry, keep a timestamped timeline, and draft status updates. Change nothing in production."
        uses [ readFiles; searchFiles; delegateTo; Todos; Agents.allOf "sentry"; mcpTool "contoso-docs" "get_page" ]
        mcpServers [ "sentry"; "contoso-docs" ]
        follows [ "engineering" ]
        evaluatedBy [ question "timeline" "Did the agent keep a timestamped timeline of the incident?" ]
        owner "sre@contoso.com"
        onlyWhenAsked
    }

// ---------------------------------------------------------------------------
// Plugins: how standards, skills, agents, MCP servers and hooks reach every machine
// The marketplace repository is installed everywhere through enabledPlugins and is the only one strictKnownMarketplaces
// allows (plus any you add).
// ---------------------------------------------------------------------------

let governancePlugin =
    plugin {
        named "contoso-governance"
        version "3.0.0"
        describedAs "Contoso's central hooks: guardrails on every tool call and capture of every session."
        category "governance"
        carriesHooks
    }

let engineeringPlugin =
    plugin {
        named "contoso-engineering"
        version "2.4.1"
        describedAs "Contoso standards as skills, the standards reviewer, and the release notes skill."
        category "engineering"
        skills [ releaseNotes ]
        standards [ "engineering"; "dotnet"; "infrastructure" ]
        agents [ "standards-reviewer" ]
    }

let securityPlugin =
    plugin {
        named "contoso-security"
        describedAs "Security triage agent with read-only Jira."
        category "security"
        agents [ "security-triage" ]
        mcpServers [ "jira" ]
    }

let platformPlugin =
    plugin {
        named "contoso-platform-ops"
        describedAs "Platform engineering and incident agents with read-only Azure, Terraform, Sentry and docs tools."
        category "operations"
        skills [ incidentRunbook ]
        agents [ "platform-engineer"; "incident-commander" ]
        mcpServers [ "azure"; "terraform"; "sentry"; "contoso-docs" ]
    }

let documentsPlugin =
    plugin {
        named "contoso-documents"
        describedAs "Read PDFs and Office documents (only for teams that need it)."
        category "productivity"
        mcpServers [ "markitdown" ]
    }

// ---------------------------------------------------------------------------
// Agentic workflows: the same agents, on events (gh-aw, compiled with `gh aw compile --strict`)
// ---------------------------------------------------------------------------

let nightlyTriage =
    workflow {
        named "security-triage"
        describedAs "Triage new code scanning alerts every night and open a draft fix when exploitable."
        runs securityTriage
        on [ schedule "daily around 02:00 on weekdays" ]
        reads [ "contents"; "issues"; "pull-requests"; "security-events" ]
        githubToolsets [ "code_security"; "issues"; "pull_requests" ]
        canEdit
        bash [ "git diff:*"; "git status:*"; "dotnet test:*"; "npm test:*" ]
        plugins [ "contoso-governance"; "contoso-engineering" ]
        mayOnly [ draftPullRequest "[security-triage] "; createIssue "[security-triage] " [ "security"; "agent" ]; Noop ]
        graders [ BuiltInGraders ]
        task
            """Find code scanning alerts opened in the last day on the default branch.
            Triage each with the security-triage agent's method. Open one draft pull request per exploitable alert,
            or an issue when a person must decide. Use noop when there is nothing new."""
        maxAiCredits 2000
    }

let ciDoctor =
    workflow {
        named "ci-doctor"
        describedAs "Explain failed CI runs on main and propose a fix."
        runs platformEngineer
        on [ workflowFailed [ "CI" ] ]
        reads [ "contents"; "actions"; "pull-requests" ]
        githubToolsets [ "actions"; "pull_requests" ]
        mayOnly [ addComment; draftPullRequest "[ci-doctor] "; Noop ]
        experiment { Name = "prompt_style"; Variants = [ "concise"; "detailed" ]; Metric = "grader:tool-success-rate"; MinSamples = 20 }
        task
            """A CI run on main failed. Read the failing job's logs, find the root cause, and either comment on the
            triggering pull request with the cause and fix, or open a draft pull request when the fix is mechanical."""
    }

let standardsReview =
    workflow {
        named "standards-review"
        describedAs "Review every pull request against the engineering standards."
        runs reviewer
        on [ PullRequestOpened ]
        reads [ "contents"; "pull-requests" ]
        githubToolsets [ "pull_requests" ]
        plugins [ "contoso-engineering" ]
        mayOnly [ commentReview; Noop ]
        task "Review the pull request with the standards-reviewer method and leave one review."
    }

let issueTriage =
    workflow {
        named "issue-triage"
        describedAs "Label new issues, ask for missing details, and hand small well-specified work to Copilot."
        runs triager
        on [ IssueOpened; Manually ]
        reads [ "contents"; "issues" ]
        githubToolsets [ "issues" ]
        network [ "docs.contoso.com" ]
        mayOnly [ addLabels [ "bug"; "feature"; "docs"; "area:payments"; "area:platform"; "area:web" ]; addComment; AssignToCopilot; Noop ]
        graders [
            BuiltInGraders
            operationalValue "Triage minutes saved" "Maintainer minutes avoided by labelling and routing this issue" "minutes" """
#!/usr/bin/env bash
set -euo pipefail
request=$(cat)
labels=$(printf '%s' "$request" | jq '[.outputs[] | select(.type=="add_labels")] | length')
printf '[{"id":"triage-minutes-saved","value":%s}]\n' "$(( labels * 3 ))"
"""
        ]
        task "Triage the issue with the issue-triager method."
        timeoutMinutes 10
        useCliEngine
    }

let askPlatform =
    workflow {
        named "ask-platform"
        describedAs "Answer /platform questions on issues and pull requests with the platform engineer."
        runs platformEngineer
        on [ slashCommand "platform" ]
        reads [ "contents"; "issues"; "pull-requests" ]
        githubToolsets [ "issues"; "pull_requests" ]
        mayOnly [ addComment ]
        task "Answer the question in the triggering comment with the platform-engineer method. Reply with one comment."
    }

// ---------------------------------------------------------------------------
// Managed settings: the enterprise default (server-managed, .github-private/copilot/managed-settings.json)
// `...ByDefault` = enterprise default that enterprise teams may override; plain = enforced for everyone.
// ---------------------------------------------------------------------------

let standardSandbox =
    sandbox {
        required
        noBypass
        sandboxMcpServers
        sandboxLspServers
        noLocalNetwork
        blockHosts [ "pastebin.com"; "transfer.sh" ]
    }

let everyone =
    policy {
        computerUse false
        modelByDefault "auto"
        autoTierByDefault Balance
        effortLevel "medium"
        contextTier DefaultContext
        noBypassModeByDefault
        shellShortcut false
        alwaysRefreshServerSettings
        denyByDefault [ read "~/.ssh/**"; read "~/.aws/**"; read "**/.env"; edit "//etc/**"; shell "git push --force *"; powershell "Remove-Item -Recurse -Force *"; domain "pastebin.com" ]
        askByDefault [ shell "git push *"; shell "terraform apply *"; write "/.github/workflows/**" ]
        allowByDefault [
            read "/**"; edit "/src/**"; edit "/test/**"; edit "/tests/**"; edit "/docs/**"
            shell "git status *"; shell "git diff *"; shell "git log *"; shell "git add *"; shell "git commit *"
            shell "dotnet build *"; shell "dotnet test *"; shell "npm test *"
            domain "github.com"; domain "*.githubusercontent.com"; domain "npm.contoso.com"
        ]
        // Metadata-only OpenTelemetry from every CLI / VS Code / JetBrains session, live - no agents on the machine.
        telemetry (
            otlp ""
            |> lockedContent
            |> serviceNamed "github-copilot"
            |> withAttributes [ "deployment.environment", "production"; "contoso.business_unit", "engineering" ]
            |> withHeaders [ "x-contoso-tenant", "contoso" ]
        )
        remoteControl (requireSso [ "contoso" ])
        // The MCP catalog adds its servers to these lists automatically.
        allowMcpByDefault [ serverUrl "https://api.githubcopilot.com/*" ]
        denyMcpByDefault [ serverName "untrusted-server" ]
        // The platform marketplace is added automatically; these are extra, and auto-update is pinned per marketplace.
        marketplacesByDefault [ Policy.marketplace "awesome-copilot" (Policy.github "github/awesome-copilot") |> neverAutoUpdating ]
        onlyMarketplacesByDefault [ Policy.github "github/awesome-copilot"; hostPattern "^plugins\\.contoso\\.com$" ]
        blockPlugins [ "unvetted-tools@awesome-copilot" ]
        sandboxedByDefault standardSandbox
    }

// ---------------------------------------------------------------------------
// Devices: MDM (Intune / Jamf) and file-based managed settings
// GitHub recommends MDM for non-negotiable security policy and server-managed settings for what changes. Device values
// win key by key; permissions and sandbox combine most-restrictively with the server. Rendered as Windows registry
// values + an Intune Remediations pair, a macOS .mobileconfig, and root-owned managed-settings.json files - with the
// policy hooks delivered through the same channel (registry on Windows, policy.d elsewhere).
// ---------------------------------------------------------------------------

let devices =
    policy {
        noBypassMode
        deny [ read "~/.ssh/**"; read "~/.aws/**"; read "~/.azure/**"; edit "//etc/**" ]
        sandboxed (
            sandbox {
                required
                failClosed
                noBypass
                noAutoWorkingDirectory
                sandboxMcpServers
                sandboxLspServers
                noGitAuth
                noGhAuth
                noDevToolAccess
                readWrite [ "/home"; "/Users"; @"C:\Users"; @"C:\src" ]
                readOnly [ "/opt/tools"; @"C:\Tools" ]
                denyPaths [ "/etc/ssh"; @"C:\Windows\System32\config" ]
                noLocalNetwork
                allowHosts [ "github.com"; "*.githubusercontent.com"; "*.contoso.com" ]
                blockHosts [ "pastebin.com" ]
                proxy "http://egress.contoso.com:3128"
                noKeychain
                // Windows native MDM only: RecordAndAllow learns what a cohort needs before locking down.
                learning RecordAndDeny
            }
        )
        // Only Contoso accounts can sign in on Contoso devices (device channel only; fails closed).
        onlySignInTo [ "contoso" ]
        policyHelper { helperAt @"C:\Program Files\Contoso\copilot-policy.exe" with Args = [ "--json" ]; TimeoutMs = Some 2000; RefreshIntervalMs = Some 3600000 }
        remoteControl (requireSso [ "contoso" ])
    }

// ---------------------------------------------------------------------------
// Enterprise teams: copilot/team-mappings.json + copilot/teams/<name>.json
// A team file may replace what `everyone` marks ...ByDefault, tighten autoTier, and add plugins. `whatif --teams ...`
// shows what a member of several teams actually gets.
// ---------------------------------------------------------------------------

let paymentsSandbox =
    sandbox {
        required
        failClosed
        noBypass
        sandboxMcpServers
        noDevToolAccess
        noLocalNetwork
        noOutbound
        allowHosts [ "github.com"; "*.githubusercontent.com" ]
    }

let payments =
    teamOverride {
        named "payments"
        forEnterpriseTeams [ "payments-eng"; "payments-contractors" ]
        overriding (
            policy {
                autoTier Efficiency
                deny [ read "~/.ssh/**"; read "**/.env"; read "**/cardholder/**"; shell "curl *"; shell "git push --force *" ]
                ask [ shell "git push *" ]
                allow [ read "/src/**"; read "/test/**"; shell "git status *"; shell "git diff *"; shell "dotnet test *" ]
                denyMcp [ serverName "untrusted-server"; serverUrl "https://*.ngrok.io/*"; serverUrl "https://mcp.sentry.dev/*" ]
                sandboxed paymentsSandbox
            }
        )
    }

let platformTeam =
    teamOverride {
        named "platform"
        forEnterpriseTeams [ "platform-eng"; "sre" ]
        overriding (
            policy {
                // LLM-assisted approval instead of prompts for every command; never full allow-all.
                assistedApprovalOnly
                allowMcp [ serverUrl "https://api.githubcopilot.com/*"; serverUrl "https://mcp.sentry.dev/*"; serverCommand [ "docker"; "run"; "-i"; "--rm"; "hashicorp/terraform-mcp-server:0.2.3" ] ]
            }
        )
    }

let dataTeam =
    teamOverride {
        named "data"
        forEnterpriseTeams [ "data-eng" ]
        overriding (
            policy {
                // A team's marketplace map replaces the default: keep the defaults it still needs.
                marketplaces [
                    Policy.marketplace "contoso-copilot-plugins" (githubAt "contoso/copilot-plugins" "main") |> autoUpdating
                    Policy.marketplace "awesome-copilot" (Policy.github "github/awesome-copilot") |> neverAutoUpdating
                    Policy.marketplace "data-tools" (gitUrl "https://git.contoso.com/data/copilot-plugins.git")
                ]
                onlyMarketplaces [
                    Policy.github "contoso/copilot-plugins"
                    Policy.github "github/awesome-copilot"
                    gitUrl "https://git.contoso.com/data/copilot-plugins.git"
                    urlSourceWithHeaders "https://plugins.contoso.com/marketplace.json" [ "Authorization", "Bearer ${PLUGINS_TOKEN}" ]
                ]
            }
        )
        addPlugins [ documentsPlugin ]
    }

let aiPioneers =
    teamOverride {
        named "ai-pioneers"
        forEnterpriseTeams [ "ai-pioneers" ]
        // Early adopters choose their own model, tier and bypass mode.
        overriding (policy { unmanagedModel; unmanagedAutoTier; allowBypassMode })
    }

// ---------------------------------------------------------------------------
// GitHub settings: rulesets, content exclusion, cloud agent
// ---------------------------------------------------------------------------

let reviewedByCopilot =
    ruleset {
        named "agents-are-reviewed"
        branches [ "~DEFAULT_BRANCH"; "release/*" ]
        require [ CopilotReview(true, true); pullRequestWith 1; statusChecks [ "build"; "test" ]; NoForcePush; NoDeletion ]
        bypassedBy [ Team 4242 ]
    }

let securityGates =
    ruleset {
        named "security-gates"
        repositories [ "payments-*"; "checkout-*" ]
        except [ "payments-sandbox" ]
        require [ codeScanningBlocks "CodeQL"; RequireWorkflows [ 123456789, ".github/workflows/security-triage.lock.yml", "refs/heads/main" ] ]
        bypassedBy [ RepositoryRole 5; App 987654 ]
    }

let agentConfigIsProtected =
    ruleset {
        named "protect-agent-configuration"
        onPush
        require [ protectPaths [ ".github/hooks/**"; ".github/copilot/**"; ".github/agents/**"; "AGENTS.md" ] ]
        bypassedBy [ OrgAdmins ]
    }

let releaseBranches =
    ruleset {
        named "release-branches-trial"
        evaluateOnly
        branches [ "release/*" ]
        require [ RestrictUpdates ]
        bypassedBy [ OrgAdminsByPullRequest ]
    }

let onGitHub =
    githubSettings {
        enforce reviewedByCopilot
        enforce securityGates
        enforce agentConfigIsProtected
        enforce releaseBranches
        neverShowCopilot "*" [ "**/secrets/**"; "**/*.pfx"; "**/cardholder/**" ]
        neverShowCopilot "payments-api" [ "/src/Tokenization/**" ]
        cloudAgentIn (SelectedRepositories [ "web"; "payments-api"; "platform-infra" ])
        cloudAgentMayReach [ "mcp.atlassian.com"; "mcp.sentry.dev"; "npm.contoso.com" ]
        agentVariable "CONTOSO_ENV" "dev"
        agentSecret "COPILOT_MCP_JIRA_AUTHORIZATION"
        agentSecret "COPILOT_MCP_SENTRY_AUTHORIZATION"
        agentSecret "COPILOT_MCP_CONTOSO_DOCS_DOCS_TOKEN"
    }

// ---------------------------------------------------------------------------
// The platform
// ---------------------------------------------------------------------------

let orgSkills =
    [ skill { named "onboarding"; useWhen "A new engineer asks how Contoso builds, tests and ships."; steps "Start with the engineering handbook in the docs MCP server, then the repository README." } ]

let contoso =
    platform {
        named "Contoso"
        organization "contoso"
        enterprise "contoso"
        platformHost "https://agents.contoso.com"
        hookTokenFrom "AGENTP_HOOK_TOKEN"
        version "2026.10.1"

        // Observe -> Guard -> Enforce: one line, promoted in a reviewed pull request.
        rollout Enforce
        // Only the platform GitHub App can change .github-private and the other rendered repositories.
        deployedBy (PlatformApp 123456)
        definitionRepo "agent-platform"
        administeredBy "ai-platform-admins"
        marketplaceRepo "copilot-plugins"
        pluginsPinnedTo "main"
        // enforceHooksOnlyFor [ "agent-platform-pilot" ]   // pilot the blocking hooks with one enterprise team first
        // trustingCa "/etc/ssl/contoso/agents-ca.pem"      // only when the platform host uses a private CA

        governedBy everyone
        onDevices devices
        teams [ payments; platformTeam; dataTeam; aiPioneers ]
        // CLI and Copilot app sessions sync to each user's own GitHub account; remote control limited to SSO'd clients.
        sessionsInCloud ViewAndControl

        standards [ everywhere; dotnet; infrastructure ]
        mcpCatalog catalog
        hooks guardrails
        plugins [ governancePlugin; engineeringPlugin; securityPlugin; platformPlugin; documentsPlugin ]
        organizationSkills orgSkills
        agents [ securityTriage; platformEngineer; reviewer; triager; incidentCommander ]
        workflows [ nightlyTriage; ciDoctor; standardsReview; issueTriage; askPlatform ]
        github onGitHub

        agenticDefaults [ "default_max_ai_credits", "5M"; "default_max_daily_ai_credits", "50M"; "default_model_copilot", "auto"; "default_timeout_minutes", "30" ]
        agenticPolicy [ "GH_AW_POLICY_ALLOW_CREATE_PULL_REQUEST", "true" ]
    }
```
<!-- full-example:end -->

## Devices (Intune, Jamf) and session data

`onDevices (policy { ... })` is what MDM and file-based delivery enforce; without it, devices get the same policy as the
server. GitHub recommends MDM for non-negotiable security settings and server-managed settings for things that change.
Device values win key by key; `permissions` and `sandbox` combine most-restrictively with the server. Device-only keys
(`onlySignInTo`, sandbox `learning`) are rejected in `governedBy`, because the server channel cannot carry them.

| Rendered under `workstation/` | Deploy with |
| --- | --- |
| `windows/intune/Detect-GitHubCopilotPolicy.ps1` + `Remediate-GitHubCopilotPolicy.ps1` | **Intune Remediations** (run as SYSTEM, on a schedule). Detection exits 1 on any drift; remediation writes every `HKLM\SOFTWARE\Policies\GitHubCopilot` value, removes values it set before that the definition no longer sets, and writes the policy hooks to `HKLM\SOFTWARE\Policies\GitHub\Copilot\agent-platform` (`Policy` REG_SZ). No files, no Win32 app, no scheduled task. |
| `windows/GitHubCopilot-policy.reg`, `windows/Install-AgentPlatformPolicy.ps1` | Group Policy Preferences, ConfigMgr, or by hand |
| `macos/com.github.copilot.mobileconfig` + `macos/install.sh` | Intune or Jamf configuration profile (forced `com.github.copilot` preferences) + a root shell script for the policy hooks |
| `linux/install.sh` | configuration management; writes root-owned, non-writable `managed-settings.json` and `policy.d` files |

The Remediations pair records which values it wrote (`HKLM\SOFTWARE\AgentPlatform\GitHubCopilot`) and only ever removes
those, so it coexists with other MDM policies that write to `HKLM\SOFTWARE\Policies\GitHubCopilot`. If another policy
sets the same key (for example `remoteControl.*`), decide which one owns it - two writers of one key will keep
overwriting each other.

`sessionsInCloud LocalOnly | ViewFromCloud | ViewAndControl` sets the "Store local sessions in the Cloud" policy
(unconfigured means local only). With syncing, CLI and Copilot app sessions sync to each user's own GitHub account for
history queries, `/chronicle` and resume; administrators control availability but cannot read synced sessions.
`validate` rejects a `remoteControl` setting that needs "View and control" when the policy does not allow it.

**From scheduled scripts to built-in controls.** A typical home-grown Intune solution reads local session files on a
schedule, runs PowerShell hooks seeded into each profile, and ships a daily summary to Application Insights. GitHub now
provides each piece natively:

| Home-grown | Built in, and how this framework sets it |
| --- | --- |
| Daily task parsing `session-store.db`, `events.jsonl` and VS Code chat files, then uploading a summary | `telemetry`: CLI, VS Code and JetBrains export OpenTelemetry GenAI spans live (`invoke_agent`, `chat`, `execute_tool`: model, input/output/cached tokens, tool calls, turn count, AI units) to any OTLP collector - the platform service, or an OpenTelemetry Collector exporting to Azure Monitor |
| Hashing user IDs with a tenant salt | every span carries `enduser.pseudo.id`; `lockedContent` keeps prompts and responses out and stops users turning content on |
| Hook JSON copied into `~/.copilot/hooks` at logon (users can delete it) | policy hooks from the registry or `policy.d`: machine-wide, loaded before every other hook, cannot be disabled |
| One PowerShell process per hook call (~0.5 s each) | managed `permissions` rules evaluated inside the client, plus one rule engine reached by `curl.exe` (~70 ms) for what rules cannot express |
| Pattern lists for dangerous commands and protected paths | `deny` / `ask` with `Shell(...)`, `PowerShell(...)`, `Read(...)`, `Edit(...)`; edit rules also cover files written by shell redirection |
| Win32 app installer plus machine environment variables | native MDM values under `HKLM\SOFTWARE\Policies\GitHubCopilot`, re-applied by Intune Remediations |
| Transcripts copied to a file share | session sync ("Store local sessions in the Cloud"), the enterprise "Agent sessions" view, and agentic audit-log events (`actor:Copilot`, `agent_session_id`) with audit-log streaming |
| Off-machine analysis of the uploaded data | `evals`: graders and an LLM judge over platform-service records, `gh aw logs` and cloud agent tasks |

## Adopting it in your company

Until the packages are on nuget.org, build them from this repository and install the template:

```text
git clone https://github.com/garrettlondon1/agent-platform && cd agent-platform
dotnet pack src/AgentPlatform -c Release -o out/packages
dotnet pack templates/AgentPlatform.Templates.csproj -c Release -o out/packages
dotnet new install out/packages/AgentPlatform.Templates.0.2.0.nupkg
dotnet new agent-platform -n Contoso.Platform --org contoso --enterprise contoso --host https://agents.contoso.com --company "Contoso"
```

In the new repository, copy `out/packages/AgentPlatform.0.2.0.nupkg` into a `packages/` folder and add a `nuget.config`
that lists it as a source (`<add key="agent-platform-local" value="packages" />`), so CI restores the same package.

The template gives you a repository with `Platform.fs` (a working starter definition), a CI/CD workflow, the
`platform-change` agentic workflow and Dependabot. Then, once:

1. **Pilot from your machine.** As an organization owner (`gh auth refresh -s admin:org,admin:enterprise,copilot`):
   `dotnet run -- publish` creates and fills `.github-private`, `copilot-plugins`, `agentic-workflows` and
   `platform-endpoints`; `dotnet run -- apply` applies rulesets and settings. Start at `rollout Observe`.
2. **Production deploys.** Create a GitHub App on the organization and install it. Store its ID as the `PLATFORM_APP_ID`
   variable and its key as the `PLATFORM_APP_KEY` secret in a `production` environment with required reviewers, then set
   `deployedBy (PlatformApp <id>)`. CI deploys every merge and re-applies everything every six hours. The App needs
   *Contents*, *Workflows* and *Pull requests* (write) on repositories, and organization *Administration*, *Copilot agent
   settings*, *Copilot content exclusion*, *Agent variables* and *Actions variables* (write).
3. **Configuration source.** An enterprise owner selects the organization under Enterprise settings > AI controls >
   Agents (or runs `apply` with an enterprise-scoped token).
4. **Platform service.** Host `dotnet run -- serve --endpoints <platform-endpoints checkout>` at `platformHost`, behind
   your load balancer and certificate. It answers hook decisions, collects OTLP and serves the MCP registry.
5. **Manual steps.** `plan` prints the few settings that have no API: the MCP registry URL, the cloud agent firewall
   allowlist and organization custom instructions.
6. **Optional device enforcement.** Push `workstation/` through Intune or Jamf. Device-level settings take precedence
   over server-managed ones and also cover machines whose Copilot licence comes from another organization.

Server-managed settings reach only people whose Copilot usage is billed to your enterprise ("Usage billed to" in their
Copilot settings). Everyone else is unaffected until they switch, or until device-level settings reach their machine.

After that, every change is a pull request to `Platform.fs`. CI comments the assets that change and the settings plan;
merging deploys it.

## Managed settings, every key

Plain operations **enforce** a value. `...ByDefault` operations set an enterprise default wrapped in `overridable`, which
enterprise teams may replace. `unmanaged...` operations (team files only) hand a key back to users.

| Managed key | `policy { }` |
| --- | --- |
| `model` | `model` / `modelByDefault` / `unmanagedModel` |
| `autoTier` | `autoTier` / `autoTierByDefault` / `unmanagedAutoTier` (teams may only tighten an enforced tier) |
| `effortLevel`, `contextTier` | `effortLevel "high"`, `contextTier LongContext` (applied with `model`) |
| `permissions.disableBypassPermissionsMode` | `noBypassMode` / `noBypassModeByDefault` / `assistedApprovalOnly` (`"allow-auto-only"`) / `allowBypassMode` |
| `permissions.deny` / `ask` / `allow` | `deny` / `ask` / `allow` (+ `ByDefault`) with `shell`, `powershell`, `read`, `edit` / `write`, `domain` |
| `features.computerUse` | `computerUse false` |
| `enabledPlugins` | every platform plugin automatically; `enablePlugins` / `blockPlugins`; a team's `addPlugins` |
| `extraKnownMarketplaces` | the platform marketplace automatically; `marketplaces` / `marketplacesByDefault` with `autoUpdating` / `neverAutoUpdating` |
| `strictKnownMarketplaces` | the platform marketplace automatically; `onlyMarketplaces` with `github`, `gitUrl`, `urlSource(WithHeaders)`, `npm`, `directory`, `hostPattern`, `pathPattern` (`[]` = lockdown) |
| `allowedMcpServers` / `deniedMcpServers` | from the MCP catalog automatically; `allowMcp` / `denyMcp` (+ `ByDefault`) with `serverUrl`, `serverCommand`, `serverName` |
| `telemetry` | `telemetry (otlp "" \|> lockedContent \|> serviceNamed ... \|> withAttributes ... \|> withHeaders ...)` |
| `remoteControl` | `remoteControl RemoteDisabled` / `RemoteEnabled` / `requireSso [ orgs ]` |
| `sandbox` | `sandboxed` / `sandboxedByDefault (sandbox { required; failClosed; noBypass; ... })`, including `learning RecordAndAllow` (Windows native MDM only) |
| `shellShortcut` | `shellShortcut false` |
| `policyHelper` | `policyHelper (helperAt "/usr/local/bin/copilot-policy")` |
| `forceLoginOrgs` | `onlySignInTo [ "acme" ]` (device files and MDM only; never written to `.github-private`) |
| `forceRemoteSettingsRefresh` | `alwaysRefreshServerSettings` |

**Teams.** `validate` enforces the documented team rules: a team file may only set keys the enterprise marks
`overridable`, a tighter `autoTier` and additive `enabledPlugins`; it cannot switch an enterprise plugin off; a replaced
marketplace map must keep the defaults it still needs; `strictKnownMarketplaces []` is a lockdown, not "unmanaged".

**Several teams.** "If a user belongs to multiple teams, their team files are combined using the least restrictive value
for each key." `whatif --teams payments-eng,ai-pioneers` prints the managed settings that person actually gets, and
warns where "least restrictive" is ambiguous (two different default models).

## Rolling out safely, and keeping it tamper-proof

**One line sets how hard it bites.** `rollout Observe | Guard | Enforce` - the same definition at every stage:

| Stage | Managed settings | Hooks | Your rulesets |
| --- | --- | --- | --- |
| `Observe` | plugins, skills, agents, model, telemetry, MCP denylist. No permission rules, approvals, allowlists, strict marketplaces or sandbox | capture only; blocking rules record `would deny: ...` and never block | `evaluate` |
| `Guard` | everything, but the sandbox is never *required* | blocking | `evaluate` |
| `Enforce` | exactly as written | blocking | `active` |

**Pilot ring.** `enforceHooksOnlyFor [ "enterprise-team" ]` gives the hook-carrying plugins (fail-closed decisions) to
that enterprise team only, through a generated team file; everyone else gets every other plugin and setting. Repository
hooks are withheld while piloting, because a repository file reaches everyone who works in it.

**Nobody edits governance by hand - at every stage.** `staged` always adds two active rulesets:

- `agent-platform-rendered-repositories`: only the deployer (`deployedBy (PlatformApp id)`, or organization owners for a
  gh CLI pilot) can update the default branch of `.github-private`, the marketplace, `agentic-workflows` and
  `platform-endpoints`. Anyone can still open a pull request to suggest something; nothing lands except a deploy.
- `agent-platform-definition`: the definition repository changes only by pull request with an approving review and a green
  `render` check. With a platform App, not even owners can push; with `OrganizationOwners`, owners may only bypass by
  merging a pull request.

The deploy also runs every six hours, so a setting changed in the UI is put back.

**Changing it by issue.** Open an issue describing the change; someone with write access labels it `platform-change`.
The `platform-change` agentic workflow reads `agentp vocabulary` (generated from the exact AgentPlatform version the
repository builds against), edits `Platform.fs` - and only `Platform.fs` (`allowed-files`) - validates, renders, and opens a
draft pull request with the rendered-asset diff. A human reviews and merges it like any other change. Compile it once
with `gh aw compile` and commit the lock file.

**`plan` never guesses.** A setting the token cannot read is reported as `ACCESS` with the scope it needs, and `apply`
refuses to run on an incomplete plan.

## Publishing the packages

```text
dotnet pack src/AgentPlatform -c Release -o out/packages
dotnet pack templates/AgentPlatform.Templates.csproj -c Release -o out/packages
dotnet nuget push "out/packages/*.nupkg" --source <nuget.org or your internal feed> --api-key <key>
```

Bump `<Version>` in both projects for every release; NuGet caches a version forever.
## The vocabulary

| Builder | Says | Renders |
| --- | --- | --- |
| `policy { }` | every documented managed-settings key (the enterprise reference and the Copilot CLI table); `deny` enforces, `denyByDefault` lets enterprise teams override | `managed-settings.json`, file-based and MDM payloads |
| `teamOverride { }` | one `copilot/teams/<name>.json`: the enterprise teams it maps to, the overridden values, plugins only that team gets | `team-mappings.json`, `teams/*.json` |
| `onDevices (policy { })` | what MDM and file-based delivery enforce on devices | `workstation/`: Intune Remediations, `.reg`, `.mobileconfig`, root-owned files, registry policy hooks |
| `sessionsInCloud` | "Store local sessions in the Cloud" | a `plan` checklist line, `coverage.json` |
| `standard { }` | coding standards, optionally `appliesTo` globs | `copilot-instructions.md`, `*.instructions.md`, `AGENTS.md`, plugin skills, SDK system message, org instructions text |
| `mcpServer { }` / `mcp { }` | the approved MCP catalog and the blocklist | `allowedMcpServers` / `deniedMcpServers`, a v0.1 MCP registry, cloud agent MCP config, agent `mcp-servers`, plugin `.mcp.json`, gh-aw `mcp-servers:` |
| `hooks { }` | rules (`beforeTool`, `afterTool`, `on`) and capture | plugin `hooks.json`, machine `policy.d`, repository `.github/hooks`, `hook-rules.json` for the service |
| `skill { }` / `plugin { }` | skills and the bundles that carry everything to every machine | the marketplace repository |
| `agent { }` | a custom agent, its tools, MCP servers, standards and evals | `.github-private/agents/*.md`, plugin agents, SDK `CustomAgents` |
| `workflow { }` | the same agent on an event | gh-aw workflow markdown that compiles with `gh aw compile --strict` |
| `ruleset { }` / `githubSettings { }` | rulesets, content exclusion, cloud agent access and firewall | REST request bodies under `github/` |

## Output

```text
.github-private/      the enterprise "Configuration source": managed settings, team overrides, agents
copilot-plugins/      the marketplace every plugin installs from
repo-baseline/        instructions, AGENTS.md and .github/hooks for every repository
agentic-workflows/    gh-aw workflows, the shared governance import, gh aw env defaults
endpoints/            the MCP registry (v0.1) and hook-rules.json, served by the platform service
workstation/          file-based and MDM managed settings, machine-wide policy hooks
github/               REST bodies: rulesets, content exclusion, cloud agent configuration
coverage.json         control x surface matrix
platform.lock.json    version and SHA-256 of every file above
```

## Hooks, centralised

Admins write rules once. The platform decides how each rule reaches each surface:

| Event kind | Rendered as | Cost | On failure |
| --- | --- | --- | --- |
| deciding (`preToolUse`, `permissionRequest`) | `exec curl.exe` (CLI, app), `bash` curl (repository / cloud agent) to `/hooks?decide=1` | one small process, no shell, nothing to install | **closed**: an unreachable service denies the tool call |
| observing (session, prompt, tool results, stop, errors) | HTTP hook | no process at all | open |
| any, in Copilot SDK sessions | `SessionHooks`, same rule engine in-process | none | n/a |

Copilot refuses HTTP hooks whose URL resolves to a loopback, private or link-local address. For a platform host on an
internal network, add `captureOverCurl` to `hooks { }`; `validate` flags the problem if you forget.

## Staying current

`Drift.known` is the surface the DSL models. `drift` extracts the same surface from the current GitHub documentation
(managed settings keys and clients, overridable keys, sandbox and telemetry sub-keys, hook events, plugin and marketplace
fields, agent frontmatter, ruleset rule types, MCP registry surfaces, policy names, Copilot REST endpoints) and from gh-aw
(frontmatter fields, reserved tool names), and exits `2` on drift.

`.github/workflows/docs-drift.yml` runs it daily and opens (or updates) an issue with the report when something drifted.
If the repository variable `AGENTP_SELF_UPDATE` is `true` and a `COPILOT_GITHUB_TOKEN` secret (a fine-grained token with
Copilot Requests) is set, it also starts `agentp-self-update`, an agentic workflow that reads the documentation passage
behind each change, models it as typed F#, updates `Drift.known`, runs the tests and opens a draft pull request.

## What cannot be governed centrally (as documented)

- Cloud agent ignores `permissions.*`, `allowedMcpServers`, `deniedMcpServers`, `sandbox`, `telemetry` and the MCP
  registry. It is governed by repository hooks, its MCP configuration, its firewall and rulesets instead.
- Hooks do not run in Visual Studio, JetBrains, Eclipse or Xcode; managed settings and the MCP registry do.
- "Registry only" MCP enforcement matches server names and can be bypassed locally; the managed-settings allowlist is the
  enforced control (both are rendered).
- Organization custom instructions have no documented REST API; the text is rendered for an admin to paste.
- Command hook timeouts always fail open, even for `preToolUse` (hooks reference).

## License

[MIT](LICENSE). Contributions welcome: open an issue or a pull request; CI must pass.
