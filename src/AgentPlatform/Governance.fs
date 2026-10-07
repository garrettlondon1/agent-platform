/// github { } - the GitHub-side controls that live in settings and APIs, not in files.
///
///     ruleset            POST /orgs/{org}/rulesets            copilot_code_review, workflows (required agentic
///                                                             checks), file_path_restriction (protect .github/),
///                                                             code_scanning, pull_request ...
///     contentExclusion   PUT  /orgs/{org}/copilot/content_exclusion                 (public preview)
///     cloudAgent         PUT  /enterprises/{e}/copilot/policies/coding_agent
///                        PUT  /orgs/{org}/copilot/coding-agent/permissions          (public preview)
///                        GET  /repos/{o}/{r}/copilot/cloud-agent/configuration      firewall, tools, MCP
///     agentSecret/Var    PUT  /orgs/{org}/agents/secrets|variables                  COPILOT_MCP_* for cloud agent MCP
///     governanceSource   PUT  /enterprises/{e}/copilot/custom-agents/source         selects .github-private
///     orgInstructions    org Copilot settings (no REST API documented: emitted as a manual step)
module AgentPlatform.Governance

open AgentPlatform.Json

type Bypass =
    | OrgAdmins
    /// Organization owners may bypass only by merging a pull request (never by pushing).
    | OrgAdminsByPullRequest
    | RepositoryRole of id: int
    | Team of id: int
    | App of id: int

type RuleKind =
    /// Copilot reviews every pull request (optionally on every push and drafts).
    | CopilotReview of onPush: bool * drafts: bool
    /// Required workflows (e.g. the gh-aw security review) from a central repository.
    | RequireWorkflows of (int * string * string) list
    /// Nobody (and no agent) may push changes to these paths without a bypass.
    | RestrictPaths of string list
    | RequirePullRequest of approvals: int * codeOwners: bool
    | RequireStatusChecks of string list
    | CodeScanning of tool: string * security: string * alerts: string
    | NoForcePush
    | NoDeletion
    /// Only bypass actors may push to matching refs ("Restrict updates").
    | RestrictUpdates

type Ruleset = {
    Name: string
    Target: string
    Enforcement: string
    IncludeRefs: string list
    IncludeRepos: string list
    ExcludeRepos: string list
    Rules: RuleKind list
    Bypass: Bypass list
}

let private noRuleset = {
    Name = ""; Target = "branch"; Enforcement = "active"; IncludeRefs = [ "~DEFAULT_BRANCH" ]
    IncludeRepos = [ "~ALL" ]; ExcludeRepos = []; Rules = []; Bypass = []
}

type RulesetBuilder() =
    member _.Yield(_: unit) = noRuleset
    [<CustomOperation "named">] member _.Named(r: Ruleset, n) = { r with Name = n }
    [<CustomOperation "onPush">] member _.Push(r: Ruleset) = { r with Target = "push"; IncludeRefs = [] }
    [<CustomOperation "evaluateOnly">] member _.Evaluate(r: Ruleset) = { r with Enforcement = "evaluate" }
    [<CustomOperation "branches">] member _.Branches(r: Ruleset, b) = { r with IncludeRefs = b }
    [<CustomOperation "repositories">] member _.Repos(r: Ruleset, names) = { r with IncludeRepos = names }
    [<CustomOperation "except">] member _.Except(r: Ruleset, names) = { r with ExcludeRepos = names }
    [<CustomOperation "require">] member _.Require(r: Ruleset, rules) = { r with Rules = r.Rules @ rules }
    [<CustomOperation "bypassedBy">] member _.Bypass(r: Ruleset, b) = { r with Bypass = b }

let ruleset = RulesetBuilder()

let copilotReviewsEveryPullRequest = CopilotReview(true, false)
let protectPaths paths = RestrictPaths paths
let pullRequestWith approvals = RequirePullRequest(approvals, true)
let statusChecks names = RequireStatusChecks names
let codeScanningBlocks tool = CodeScanning(tool, "high_or_higher", "errors")

let private ruleJson =
    function
    | CopilotReview(push, drafts) ->
        objOf [ "type", str "copilot_code_review"; "parameters", objOf [ "review_on_push", bool push; "review_draft_pull_requests", bool drafts ] ]
    | RequireWorkflows wfs ->
        objOf [
            "type", str "workflows"
            "parameters", objOf [ "workflows", (wfs |> List.map (fun (repoId, path, ref) -> objOf [ "repository_id", num repoId; "path", str path; "ref", str ref ]) |> arr) ]
        ]
    | RestrictPaths paths -> objOf [ "type", str "file_path_restriction"; "parameters", objOf [ "restricted_file_paths", strs paths ] ]
    | RequirePullRequest(n, owners) ->
        objOf [
            "type", str "pull_request"
            "parameters",
            objOf [
                "required_approving_review_count", num n
                "require_code_owner_review", bool owners
                "dismiss_stale_reviews_on_push", bool true
                "require_last_push_approval", bool true
                "required_review_thread_resolution", bool true
            ]
        ]
    | RequireStatusChecks checks ->
        objOf [
            "type", str "required_status_checks"
            "parameters", objOf [ "strict_required_status_checks_policy", bool true; "required_status_checks", (checks |> List.map (fun c -> objOf [ "context", str c ]) |> arr) ]
        ]
    | CodeScanning(tool, security, alerts) ->
        objOf [
            "type", str "code_scanning"
            "parameters", objOf [ "code_scanning_tools", arr [ objOf [ "tool", str tool; "security_alerts_threshold", str security; "alerts_threshold", str alerts ] ] ]
        ]
    | NoForcePush -> objOf [ "type", str "non_fast_forward" ]
    | NoDeletion -> objOf [ "type", str "deletion" ]
    | RestrictUpdates -> objOf [ "type", str "update" ]

/// Body for POST/PUT /orgs/{org}/rulesets.
let rulesetJson (r: Ruleset) =
    let refs =
        if r.Target = "push" then None
        else Some("ref_name", objOf [ "include", strs r.IncludeRefs; "exclude", strs [] ])
    objOf [
        "name", str r.Name
        "target", str r.Target
        "enforcement", str r.Enforcement
        "conditions",
        obj [
            refs |> Option.map fst |> Option.defaultValue "ref_name", refs |> Option.map snd
            // `protected` stops renaming a repository out of the ruleset; GitHub rejects it together with ~ALL.
            "repository_name",
            Some(
                objOf [
                    "include", strs r.IncludeRepos
                    "exclude", strs r.ExcludeRepos
                    "protected", bool (not (List.contains "~ALL" r.IncludeRepos))
                ])
        ]
        "rules", (r.Rules |> List.map ruleJson |> arr)
        "bypass_actors",
        r.Bypass
        |> List.map (function
            | OrgAdmins -> objOf [ "actor_type", str "OrganizationAdmin"; "actor_id", num 1; "bypass_mode", str "always" ]
            | OrgAdminsByPullRequest -> objOf [ "actor_type", str "OrganizationAdmin"; "actor_id", num 1; "bypass_mode", str "pull_request" ]
            | RepositoryRole id -> objOf [ "actor_type", str "RepositoryRole"; "actor_id", num id; "bypass_mode", str "pull_request" ]
            | Team id -> objOf [ "actor_type", str "Team"; "actor_id", num id; "bypass_mode", str "pull_request" ]
            | App id -> objOf [ "actor_type", str "Integration"; "actor_id", num id; "bypass_mode", str "always" ])
        |> arr
    ]

type CloudAgentAccess =
    | AllRepositories
    | SelectedRepositories of string list
    | NoRepositories

type CloudAgent = {
    Access: CloudAgentAccess
    FirewallAllow: string list
    RecommendedAllowlist: bool
    RequireWorkflowApproval: bool
    CodeQL: bool
    SecretScanning: bool
    DependencyChecks: bool
    CopilotCodeReview: bool
    Automations: bool
    AutomationsNeedWriteAccess: bool
}

let defaultCloudAgent = {
    Access = AllRepositories; FirewallAllow = []; RecommendedAllowlist = true; RequireWorkflowApproval = true
    CodeQL = true; SecretScanning = true; DependencyChecks = true; CopilotCodeReview = true
    Automations = true; AutomationsNeedWriteAccess = true
}

type GitHubSettings = {
    Rulesets: Ruleset list
    /// repository name (or "*") -> excluded paths
    ContentExclusion: (string * string list) list
    CloudAgent: CloudAgent
    AgentVariables: (string * string) list
    /// Names of agent secrets the deployment must set (values never live in the definition).
    AgentSecrets: string list
    OrgInstructions: bool
}

let private noSettings = { Rulesets = []; ContentExclusion = []; CloudAgent = defaultCloudAgent; AgentVariables = []; AgentSecrets = []; OrgInstructions = true }

type GitHubBuilder() =
    member _.Yield(_: unit) = noSettings
    [<CustomOperation "enforce">] member _.Ruleset(g: GitHubSettings, r) = { g with Rulesets = g.Rulesets @ [ r ] }
    [<CustomOperation "neverShowCopilot">] member _.Exclude(g: GitHubSettings, repo, paths) = { g with ContentExclusion = g.ContentExclusion @ [ repo, paths ] }
    [<CustomOperation "cloudAgentIn">] member _.Access(g: GitHubSettings, a) = { g with CloudAgent = { g.CloudAgent with Access = a } }
    [<CustomOperation "cloudAgentMayReach">] member _.Firewall(g: GitHubSettings, hosts) = { g with CloudAgent = { g.CloudAgent with FirewallAllow = g.CloudAgent.FirewallAllow @ hosts } }
    [<CustomOperation "agentVariable">] member _.Var(g: GitHubSettings, name, value) = { g with AgentVariables = g.AgentVariables @ [ name, value ] }
    [<CustomOperation "agentSecret">] member _.Secret(g: GitHubSettings, name) = { g with AgentSecrets = g.AgentSecrets @ [ name ] }

let githubSettings = GitHubBuilder()

/// Body for PUT /orgs/{org}/copilot/content_exclusion: { "repo": ["path", ...] }.
let contentExclusionJson (g: GitHubSettings) =
    g.ContentExclusion |> List.map (fun (repo, paths) -> repo, strs paths) |> objOf

let cloudAgentPermissionsJson (g: GitHubSettings) =
    match g.CloudAgent.Access with
    | AllRepositories -> objOf [ "enabled_repositories", str "all" ]
    | SelectedRepositories _ -> objOf [ "enabled_repositories", str "selected" ]
    | NoRepositories -> objOf [ "enabled_repositories", str "none" ]

/// The per-repository cloud agent configuration this definition expects (compared against
/// GET /repos/{o}/{r}/copilot/cloud-agent/configuration by `agentp plan`).
let cloudAgentConfigJson (g: GitHubSettings) (mcp: System.Text.Json.Nodes.JsonNode option) =
    let c = g.CloudAgent
    obj [
        "mcp_configuration", mcp
        "enabled_tools", Some(objOf [ "codeql", bool c.CodeQL; "copilot_code_review", bool c.CopilotCodeReview; "secret_scanning", bool c.SecretScanning; "dependency_vulnerability_checks", bool c.DependencyChecks ])
        "require_actions_workflow_approval", Some(bool c.RequireWorkflowApproval)
        "is_firewall_enabled", Some(bool true)
        "is_firewall_recommended_allowlist_enabled", Some(bool c.RecommendedAllowlist)
        "custom_allowlist", Some(strs c.FirewallAllow)
        "is_automations_enabled", Some(bool c.Automations)
        "require_write_access_for_automation_triggers", Some(bool c.AutomationsNeedWriteAccess)
    ]
