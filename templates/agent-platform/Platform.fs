/// Acme Corp's GitHub Copilot agent platform.
///
/// Everything Copilot does for Acme Corp - in VS Code, Visual Studio, JetBrains and the CLI, through the
/// Copilot SDK, as cloud agent, and as agentic workflows - is governed from this file. Change it in a pull
/// request: CI validates and renders it, shows the plan, and on merge deploys it.
module Platform

open AgentPlatform.Policy
open AgentPlatform.Hooks
open AgentPlatform.Mcp
open AgentPlatform.Standards
open AgentPlatform.Plugins
open AgentPlatform.Agents
open AgentPlatform.Governance
open AgentPlatform.Platform

// ---------------------------------------------------------------------------
// Coding standards: instructions, AGENTS.md, plugin skills and SDK system messages all come from here
// ---------------------------------------------------------------------------

let engineering =
    standard {
        named "engineering"
        title "Acme Corp engineering standards"
        rules [
            "Every behaviour change ships with a test that fails without it."
            "Never commit secrets, .env files or generated credentials."
            "Prefer small pull requests with one concern each."
        ]
        whenReviewing [ "Flag any change without a test." ]
    }

// ---------------------------------------------------------------------------
// MCP: the approved catalog. Anything not listed is blocked on every governed client.
// ---------------------------------------------------------------------------

let catalog =
    mcp {
        // approve (mcpServer { id "com.example/tickets"; key "tickets"; title "Tickets"; describedAs "..."; remote "https://mcp.example.com/mcp"; tools [ "getTicket" ]; readOnly })
        block [ blockUrl "https://*.ngrok.io/*"; blockUrl "https://*.ngrok-free.app/*" ]
    }

// ---------------------------------------------------------------------------
// Hooks: written once, enforced on every surface
// ---------------------------------------------------------------------------

let guardrails =
    hooks {
        rule (beforeTool "no-force-push" (anyOf [ shellCommand "git push --force"; shellCommand "git push -f" ]) (block "Force-pushing rewrites shared history. Push a new commit instead."))
        rule (beforeTool "no-secrets" (anyOf [ reading "**/.env"; reading "**/*.pem"; reading "**/.ssh/**" ]) (block "Secrets and credential files are off limits to agents."))
        rule (beforeTool "no-pipe-to-shell" (argsMatching @"curl[^|]*\|\s*(ba|z)?sh") (block "Piping downloads into a shell is not allowed."))
        rule (beforeTool "protect-workflows" (writingTo ".github/workflows/**") (requireApproval "Changing CI workflows needs a person to approve."))
        rule (afterTool "test-reminder" (writingTo "src/**") (addContext "Add or update a test for this change before finishing."))
        captureEverything
    }

// ---------------------------------------------------------------------------
// Agents
// ---------------------------------------------------------------------------

let reviewer =
    agent {
        named "standards-reviewer"
        // Pinned for agentic workflows to a model the enterprise model policy enables ("auto" can resolve elsewhere).
        model "claude-opus-4.8"
        useWhen "Review a diff against Acme Corp engineering standards."
        instructions
            """Review the current diff against the standards that apply to each file.
            Return PASS or FAIL per standard with file and line for every FAIL. Do not edit files."""
        uses [ readFiles; searchFiles ]
        follows [ "engineering" ]
        evaluatedBy [ question "cited_lines" "Did every FAIL cite a file and line?" ]
    }

// ---------------------------------------------------------------------------
// Plugins: how standards, skills, agents and hooks reach every machine
// ---------------------------------------------------------------------------

let governance =
    plugin {
        named "platform-governance"
        describedAs "Central hooks: guardrails on every tool call and capture of every session."
        category "governance"
        carriesHooks
    }

let engineeringPlugin =
    plugin {
        named "engineering-standards"
        describedAs "Engineering standards as skills, and the standards reviewer."
        standards [ "engineering" ]
        agents [ "standards-reviewer" ]
    }

// ---------------------------------------------------------------------------
// Agentic workflows: the same agents, on events
// ---------------------------------------------------------------------------

let reviewPullRequests =
    workflow {
        named "standards-review"
        describedAs "Review every pull request against the engineering standards."
        runs reviewer
        on [ PullRequestOpened ]
        reads [ "contents"; "pull-requests" ]
        githubToolsets [ "pull_requests" ]
        mayOnly [ commentReview; Noop ]
        task "Review the pull request with the standards-reviewer method and leave one review comment."
    }

// ---------------------------------------------------------------------------
// Managed settings
// ---------------------------------------------------------------------------

let everyone =
    policy {
        computerUse false
        modelByDefault "auto"
        noBypassModeByDefault
        denyByDefault [ read "~/.ssh/**"; read "~/.aws/**"; read "**/.env"; shell "git push --force *" ]
        askByDefault [ shell "git push *"; edit "/.github/workflows/**" ]
        allowByDefault [
            read "/**"; edit "/src/**"; edit "/test/**"; edit "/tests/**"; edit "/docs/**"
            shell "git status *"; shell "git diff *"; shell "git log *"; shell "git add *"; shell "git commit *"
            domain "github.com"; domain "*.githubusercontent.com"
        ]
        // Metadata only, locked: prompts and code never leave the machine through telemetry.
        // capturingContent exports prompts and responses - get privacy sign-off first.
        telemetry (otlp "" |> lockedContent)
    }

/// A team file: members of these enterprise teams may replace what `everyone` marks ...ByDefault.
/// Run `dotnet run -- whatif --teams ai-pioneers` to see exactly what a member gets.
let aiPioneers =
    teamOverride {
        named "ai-pioneers"
        forEnterpriseTeams [ "ai-pioneers" ]
        overriding (policy { unmanagedModel })
    }

// ---------------------------------------------------------------------------
// GitHub settings
// ---------------------------------------------------------------------------

let onGitHub =
    githubSettings {
        enforce (ruleset { named "copilot-reviews-pull-requests"; require [ copilotReviewsEveryPullRequest ] })
        enforce (
            ruleset {
                named "protect-agent-configuration"
                onPush
                require [ protectPaths [ ".github/hooks/**"; ".github/agents/**"; ".github/copilot-instructions.md"; "AGENTS.md" ] ]
                bypassedBy [ OrgAdmins ]
            }
        )
        cloudAgentIn AllRepositories
    }

// ---------------------------------------------------------------------------
// The platform
// ---------------------------------------------------------------------------

let definition =
    platform {
        named "Acme Corp"
        organization "acme-org"
        enterprise "acme-enterprise"
        platformHost "https://agents.acme.example"
        // Every hook sends this bearer token; set AGENTP_HOOK_TOKEN on developer machines (MDM) and as a cloud agent secret.
        hookTokenFrom "AGENTP_HOOK_TOKEN"
        version "1.0.0"

        // Start at Observe: everything ships, nothing blocks, hooks record what they WOULD have denied.
        // Promote to Guard, then Enforce, by changing this one line in a reviewed pull request.
        rollout Observe
        // Production: deployedBy (PlatformApp <app id>) - then only the app can change the rendered repos.
        deployedBy OrganizationOwners
        definitionRepo "agent-platform"

        governedBy everyone
        teams [ aiPioneers ]
        // sessionsInCloud ViewFromCloud                                     // "Store local sessions in the Cloud"
        // onDevices (policy { noBypassMode; onlySignInTo [ "acme-org" ] })  // Intune / Jamf: non-negotiables on devices
        standards [ engineering ]
        mcpCatalog catalog
        hooks guardrails
        plugins [ governance; engineeringPlugin ]
        agents [ reviewer ]
        workflows [ reviewPullRequests ]
        github onGitHub

        agenticDefaults [ "default_max_ai_credits", "20K"; "default_timeout_minutes", "30" ]
    }
