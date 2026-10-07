/// Contoso's agent platform: every Copilot agent the company runs - in VS Code, Visual Studio,
/// JetBrains and the CLI, through the Copilot SDK, as cloud agent, and as agentic workflows -
/// governed from this one file. Change it, open a pull request, and CI renders, validates and
/// deploys the result.
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
// Coding standards: one source for instructions, AGENTS.md, plugin skills and the SDK
// ---------------------------------------------------------------------------

let everywhere =
    standard {
        named "engineering"
        title "Contoso engineering standards"
        rules [
            "Every behaviour change ships with a test that fails without it."
            "Branch names and commit subjects start with the Jira key, e.g. PAY-142."
            "Never commit secrets, .env files or generated credentials."
            "Prefer small pull requests; one concern per pull request."
        ]
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
// MCP: the approved catalog. Everything else is blocked on every client.
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

let catalog =
    mcp {
        approveAll [ jira; azure; playwright ]
        block [
            blockCommand [ "npx"; "-y"; "@modelcontextprotocol/server-filesystem"; "/" ]
            blockUrl "https://*.ngrok.io/*"
            blockUrl "https://*.ngrok-free.app/*"
        ]
        registryAt "https://agents.contoso.com"
        registryOnly
    }

// ---------------------------------------------------------------------------
// Hooks: written once, enforced on every surface by one rule engine
// ---------------------------------------------------------------------------

let guardrails =
    hooks {
        rule (beforeTool "no-force-push" (anyOf [ shellCommand "git push --force"; shellCommand "git push -f" ]) (block "Force-pushing rewrites shared history. Push a new commit instead."))
        rule (beforeTool "no-secrets" (anyOf [ reading "**/.env"; reading "**/*.pem"; reading "**/.ssh/**"; argsMatching @"(\.aws|\.azure)[\\/]" ]) (block "Secrets and credential files are off limits to agents."))
        rule (beforeTool "no-pipe-to-shell" (argsMatching @"curl[^|]*\|\s*(ba|z)?sh") (block "Piping downloads into a shell is not allowed."))
        rule (beforeTool "protect-workflows" (writingTo ".github/workflows/**") (requireApproval "Changing CI workflows needs a person to approve."))
        rule (beforeTool "prod-deploys" (anyOf [ shellCommand "terraform apply"; shellCommand "kubectl apply"; shellCommand "kubectl delete" ]) (requireApproval "Production changes need a person to approve."))
        rule (beforeTool "no-exfil-hosts" (fetching @"(pastebin\.com|transfer\.sh|ngrok)") (block "That host is a known exfiltration channel."))
        rule (afterTool "test-reminder" (writingTo "src/**") (addContext "Contoso standard: add or update a test for this change before finishing."))
        captureEverything
    }

// ---------------------------------------------------------------------------
// Skills and agents
// ---------------------------------------------------------------------------

let releaseNotes =
    skill {
        named "release-notes"
        useWhen "Writing release notes, a changelog entry or a summary of merged pull requests."
        steps
            """List merged pull requests since the last tag with `git log --merges`.
            Group them by Jira epic. Write one line per change in the past tense, user-facing first.
            End with a 'Breaking changes' section, even if it says 'None'."""
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
        model "claude-sonnet-4.6"
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
        uses [ readFiles; searchFiles; editFiles; runShell; github ]
        mcpServers [ "azure" ]
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
        uses [ readFiles; searchFiles ]
        follows [ "engineering"; "dotnet"; "infrastructure" ]
        evaluatedBy [ question "cited_lines" "Did every FAIL cite a file and line?" ]
        owner "devex@contoso.com"
    }

// ---------------------------------------------------------------------------
// Plugins: how all of the above reaches every machine
// ---------------------------------------------------------------------------

let governancePlugin =
    plugin {
        named "contoso-governance"
        describedAs "Contoso's central hooks: guardrails on every tool call and capture of every session."
        category "governance"
        carriesHooks
    }

let engineeringPlugin =
    plugin {
        named "contoso-engineering"
        describedAs "Contoso standards as skills, the standards reviewer, and the release notes skill."
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
        describedAs "Platform engineering agent with read-only Azure tools."
        agents [ "platform-engineer" ]
        mcpServers [ "azure" ]
    }

// ---------------------------------------------------------------------------
// Agentic workflows: the same agents, on events
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

// ---------------------------------------------------------------------------
// Managed settings: everyone, and the teams that differ
// ---------------------------------------------------------------------------

let standardSandbox =
    sandbox {
        required
        noBypass
        sandboxMcpServers
        noLocalNetwork
    }

let paymentsSandbox =
    sandbox {
        required
        failClosed
        noBypass
        sandboxMcpServers
        noDevToolAccess
        noLocalNetwork
        allowHosts [ "github.com"; "*.githubusercontent.com" ]
    }

let everyone =
    policy {
        computerUse false
        modelByDefault "auto"
        autoTierByDefault Balance
        effortLevel "medium"
        noBypassModeByDefault
        shellShortcut false
        alwaysRefreshServerSettings
        denyByDefault [ read "~/.ssh/**"; read "~/.aws/**"; read "**/.env"; edit "//etc/**"; shell "git push --force *"; powershell "Remove-Item -Recurse -Force *"; domain "pastebin.com" ]
        askByDefault [ shell "git push *"; shell "terraform apply *"; edit "/.github/workflows/**" ]
        allowByDefault [
            read "/**"; edit "/src/**"; edit "/test/**"; edit "/tests/**"; edit "/docs/**"
            shell "git status *"; shell "git diff *"; shell "git log *"; shell "git add *"; shell "git commit *"
            shell "dotnet build *"; shell "dotnet test *"; shell "npm test *"
            domain "github.com"; domain "*.githubusercontent.com"
        ]
        telemetry (otlp "" |> capturingContent |> lockedContent |> serviceNamed "copilot" |> withAttributes [ "deployment.environment", "production" ])
        remoteControl (requireSso [ "contoso" ])
        sandboxedByDefault standardSandbox
    }

/// Each team file may only change what `everyone` marks ...ByDefault, tighten autoTier, and add plugins.
let payments =
    teamOverride {
        named "payments"
        forEnterpriseTeams [ "payments-eng"; "payments-contractors" ]
        overriding (
            policy {
                autoTier Efficiency
                deny [ read "~/.ssh/**"; read "**/.env"; read "**/cardholder/**"; shell "curl *"; shell "git push --force *" ]
                allow [ read "/src/**"; read "/test/**"; shell "git status *"; shell "git diff *"; shell "dotnet test *" ]
                sandboxed paymentsSandbox
            }
        )
    }

let platformTeam =
    teamOverride {
        named "platform"
        forEnterpriseTeams [ "platform-eng" ]
        // Platform engineers keep LLM-assisted approval (no full allow-all).
        overriding (policy { assistedApprovalOnly })
    }

let aiPioneers =
    teamOverride {
        named "ai-pioneers"
        forEnterpriseTeams [ "ai-pioneers" ]
        // Early adopters choose their own model and tier.
        overriding (policy { unmanagedModel; unmanagedAutoTier })
    }

// ---------------------------------------------------------------------------
// GitHub settings: rulesets, content exclusion, cloud agent
// ---------------------------------------------------------------------------

let reviewedByCopilot =
    ruleset {
        named "agents-are-reviewed"
        require [ copilotReviewsEveryPullRequest; pullRequestWith 1; NoForcePush ]
    }

let agentConfigIsProtected =
    ruleset {
        named "protect-agent-configuration"
        onPush
        require [ protectPaths [ ".github/hooks/**"; ".github/copilot/**"; ".github/agents/**"; "AGENTS.md" ] ]
        bypassedBy [ OrgAdmins ]
    }

let onGitHub =
    githubSettings {
        enforce reviewedByCopilot
        enforce agentConfigIsProtected
        neverShowCopilot "*" [ "**/secrets/**"; "**/*.pfx"; "**/cardholder/**" ]
        cloudAgentIn AllRepositories
        cloudAgentMayReach [ "mcp.atlassian.com" ]
        agentSecret "COPILOT_MCP_JIRA_TOKEN"
    }

// ---------------------------------------------------------------------------
// The platform
// ---------------------------------------------------------------------------

let contoso =
    platform {
        named "Contoso"
        organization "contoso"
        enterprise "contoso"
        platformHost "https://agents.contoso.com"
        hookTokenFrom "AGENTP_HOOK_TOKEN"
        version "2026.10.1"

        governedBy everyone
        teams [ payments; platformTeam; aiPioneers ]

        standards [ everywhere; dotnet; infrastructure ]
        mcpCatalog catalog
        hooks guardrails
        plugins [ governancePlugin; engineeringPlugin; securityPlugin; platformPlugin ]
        agents [ securityTriage; platformEngineer; reviewer ]
        workflows [ nightlyTriage; ciDoctor ]
        github onGitHub

        agenticDefaults [ "default_max_ai_credits", "5M"; "default_max_daily_ai_credits", "50M"; "default_model_copilot", "auto"; "default_timeout_minutes", "30" ]
        agenticPolicy [ "GH_AW_POLICY_ALLOW_CREATE_PULL_REQUEST", "true" ]
    }