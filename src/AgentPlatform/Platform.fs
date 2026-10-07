/// platform { } - an organisation's whole agent platform, in one typed value.
///
///     platform {
///         organization "contoso"                // the org that owns .github-private + the marketplace
///         enterprise "contoso-ent"
///         platformHost "https://agents.contoso.com"   // collector + hook daemon + MCP registry
///         governedBy    (policy { ... })         // managed-settings.json (+ teams)
///         team "payments" ["payments-eng"] (policy { ... })
///         standards     [ ... ]                  // instructions, AGENTS.md, standard skills
///         mcpCatalog    (mcp { ... })            // allowlist, registry, cloud agent MCP
///         hooks         (hooks { ... })          // centralised rules + capture
///         plugins       [ ... ]                  // the marketplace
///         agents        [ ... ]                  // custom agents everywhere
///         workflows     [ ... ]                  // gh-aw
///         github        (githubSettings { ... }) // rulesets, exclusions, cloud agent
///     }
module AgentPlatform.Platform

open System
open AgentPlatform.Policy
open AgentPlatform.Hooks
open AgentPlatform.Mcp
open AgentPlatform.Standards
open AgentPlatform.Plugins
open AgentPlatform.Agents
open AgentPlatform.Governance

/// How hard the definition bites. Promote one stage at a time; every stage deploys the same definition.
type Rollout =
    /// Ship everything, block nothing. Plugins, skills, agents, instructions, MCP denylist and telemetry go out;
    /// permission rules, approvals, allowlists, strict marketplaces and the sandbox are left out of managed
    /// settings; blocking hook rules only record what they WOULD have done; your rulesets run in evaluate mode.
    | Observe
    /// Hard denies and blocking hooks are live. The sandbox is used where available but never required, and
    /// your rulesets still run in evaluate mode.
    | Guard
    /// Everything exactly as written.
    | Enforce

/// Who deploys the rendered repositories, and is therefore the ONLY actor allowed to change them.
type Deployer =
    /// The platform's GitHub App (production). Nobody else can push to a rendered repository, and every
    /// change to the definition needs an approving review from someone other than its author.
    | PlatformApp of appId: int
    /// Organization owners deploying with the gh CLI (pilots and demos). Owners may merge their own
    /// definition pull requests, but still only through a pull request that passed validation.
    | OrganizationOwners

type Team = { Name: string; EnterpriseTeams: string list; Policy: Policy }

type Platform = {
    Name: string
    Organization: string
    Enterprise: string
    /// HTTPS base of the platform service: /hooks, /v1/{traces,metrics,logs}, /v0.1/servers.
    Host: string
    /// Name of the marketplace repo in the organization (default "copilot-plugins").
    MarketplaceRepo: string
    /// Ref agentic workflows pin plugins to. gh-aw resolves it to a commit SHA when compiling,
    /// so it must exist in the marketplace repository (a branch, tag or SHA).
    MarketplaceRef: string
    Policy: Policy
    Teams: Team list
    Standards: Standard list
    Mcp: Catalog
    Hooks: HookSet
    Plugins: Plugin list
    Agents: Agent list
    Workflows: Workflow list
    GitHub: GitHubSettings
    AgenticDefaults: (string * string) list
    AgenticPolicy: (string * string) list
    /// Environment variable holding the bearer token hooks send to the platform service. Set it on every
    /// machine (MDM / login script) and as a cloud agent environment secret; the service rejects calls without it.
    HookTokenVariable: string option
    /// Enterprise team slugs that receive the hook-carrying plugins (a pilot ring). Empty = everyone.
    HookPilotTeams: string list
    /// Path, on every machine, of the PEM that signed the platform host's certificate (private CA).
    HostCaPath: string option
    /// Skills kept at .github-private/skills/<name>/SKILL.md (organization-hosted skills; the hosting path for the
    /// CLI's "(org/enterprise) remote" skills is not documented, so prefer plugin skills for anything new).
    OrganizationSkills: Skill list
    /// Organization team that owns .github-private in CODEOWNERS (omitted when unset).
    AdminTeam: string option
    /// Rollout stage (default Enforce). Start new platforms at Observe.
    Rollout: Rollout
    /// Who may change the rendered repositories (default OrganizationOwners).
    DeployedBy: Deployer
    /// Repository that holds this definition (default "agent-platform"). It is protected too: changes land only
    /// through a reviewed pull request whose `render` check passed.
    DefinitionRepo: string
    /// Version stamped on every rendered asset; bump to roll out.
    Version: string
}

let private nothing = {
    Name = ""; Organization = ""; Enterprise = ""; Host = ""; MarketplaceRepo = "copilot-plugins"; MarketplaceRef = "main"
    Policy = empty; Teams = []; Standards = []; Mcp = { Servers = []; Blocked = []; RegistryUrl = None; RegistryMode = DiscoveryOnly; AllowBuiltIns = true }
    Hooks = { Rules = []; Capture = None; TimeoutSec = 5; CaptureVia = HttpHooks }; Plugins = []; Agents = []; Workflows = []
    GitHub = { Rulesets = []; ContentExclusion = []; CloudAgent = defaultCloudAgent; AgentVariables = []; AgentSecrets = []; OrgInstructions = true }
    AgenticDefaults = []; AgenticPolicy = []; HookTokenVariable = None; HookPilotTeams = []; HostCaPath = None; OrganizationSkills = []; AdminTeam = None; Rollout = Enforce; DeployedBy = OrganizationOwners
    DefinitionRepo = "agent-platform"; Version = "1.0.0"
}

type PlatformBuilder() =
    member _.Yield(_: unit) = nothing
    [<CustomOperation "named">] member _.Named(p: Platform, n) = { p with Name = n }
    [<CustomOperation "organization">] member _.Org(p: Platform, o: string) = { p with Organization = o; Name = (if p.Name = "" then o else p.Name) }
    [<CustomOperation "enterprise">] member _.Ent(p: Platform, e) = { p with Enterprise = e }
    [<CustomOperation "platformHost">] member _.Host(p: Platform, h: string) = { p with Host = h.TrimEnd '/' }
    [<CustomOperation "marketplaceRepo">] member _.Market(p: Platform, r) = { p with MarketplaceRepo = r }
    /// Pin agentic workflows to a marketplace tag or SHA instead of main.
    [<CustomOperation "pluginsPinnedTo">] member _.Pin(p: Platform, r) = { p with MarketplaceRef = r }
    [<CustomOperation "version">] member _.Version(p: Platform, v) = { p with Version = v }
    /// Hooks authenticate to the platform service with the bearer token in this environment variable.
    [<CustomOperation "hookTokenFrom">] member _.HookToken(p: Platform, variable: string) = { p with HookTokenVariable = Some variable }
    /// Observe, Guard or Enforce. See Rollout.
    [<CustomOperation "rollout">] member _.Rollout(p: Platform, r: Rollout) = { p with Rollout = r }
    /// PlatformApp appId in production; OrganizationOwners for a pilot run from the gh CLI.
    [<CustomOperation "deployedBy">] member _.DeployedBy(p: Platform, d: Deployer) = { p with DeployedBy = d }
    /// Pilot ring: only these enterprise teams get the plugins that carry hooks (fail-closed decisions), through a
    /// generated team file. Everyone else still gets every other plugin, skill, agent and setting.
    [<CustomOperation "enforceHooksOnlyFor">] member _.HookPilot(p: Platform, enterpriseTeams: string list) = { p with HookPilotTeams = enterpriseTeams }
    /// The platform host uses a private CA: hooks pass this PEM path to curl (nothing is added to any trust store).
    [<CustomOperation "trustingCa">] member _.TrustingCa(p: Platform, pemPath: string) = { p with HostCaPath = Some pemPath }
    /// Skills that live in .github-private/skills (kept where an organization already hosts them).
    [<CustomOperation "organizationSkills">] member _.OrgSkills(p: Platform, s: Skill list) = { p with OrganizationSkills = p.OrganizationSkills @ s }
    /// The organization team (slug) that owns the governance repositories in CODEOWNERS.
    [<CustomOperation "administeredBy">] member _.Admins(p: Platform, team: string) = { p with AdminTeam = Some team }
    [<CustomOperation "definitionRepo">] member _.DefinitionRepo(p: Platform, r: string) = { p with DefinitionRepo = r }
    [<CustomOperation "governedBy">] member _.Policy(p: Platform, pol) = { p with Policy = pol }
    [<CustomOperation "team">] member _.Team(p: Platform, name, enterpriseTeams, pol) = { p with Teams = p.Teams @ [ { Name = name; EnterpriseTeams = enterpriseTeams; Policy = pol } ] }
    [<CustomOperation "standards">] member _.Standards(p: Platform, s) = { p with Standards = p.Standards @ s }
    [<CustomOperation "mcpCatalog">] member _.Mcp(p: Platform, c) = { p with Mcp = c }
    [<CustomOperation "hooks">] member _.Hooks(p: Platform, h) = { p with Hooks = h }
    [<CustomOperation "plugins">] member _.Plugins(p: Platform, ps) = { p with Plugins = p.Plugins @ ps }
    [<CustomOperation "agents">] member _.Agents(p: Platform, a) = { p with Agents = p.Agents @ a }
    [<CustomOperation "workflows">] member _.Workflows(p: Platform, w) = { p with Workflows = p.Workflows @ w }
    [<CustomOperation "github">] member _.GitHub(p: Platform, g) = { p with GitHub = g }
    [<CustomOperation "agenticDefaults">] member _.Defaults(p: Platform, d) = { p with AgenticDefaults = d }
    [<CustomOperation "agenticPolicy">] member _.AwPolicy(p: Platform, d) = { p with AgenticPolicy = d }

let platform = PlatformBuilder()

let marketplaceName (p: Platform) = $"{p.Organization}-copilot-plugins"
let marketplaceRepo (p: Platform) = $"{p.Organization}/{p.MarketplaceRepo}"
let hooksUrl (p: Platform) = $"{p.Host}/hooks"

let marketplace (p: Platform) : Marketplace = {
    Name = marketplaceName p
    Repo = marketplaceRepo p
    Ref = p.MarketplaceRef
    Owner = p.Name
    OwnerEmail = ""
    Description = $"Approved Copilot plugins for {p.Name}. Installed by enterprise managed settings."
    Plugins = p.Plugins
}

/// True when the platform host is loopback, private or an internal name: Copilot refuses HTTP hooks to it,
/// and GitHub-hosted runners (cloud agent, agentic workflows) cannot reach it at all.
let hostIsInternal (p: Platform) =
    let host = try Uri(p.Host).Host with _ -> ""
    host = "localhost" || host.EndsWith ".localhost" || host.EndsWith ".local" || host.EndsWith ".internal" || host.EndsWith ".corp" || host.EndsWith ".lan"
    || (match Net.IPAddress.TryParse host with
        | true, ip ->
            let b = ip.GetAddressBytes()
            Net.IPAddress.IsLoopback ip || b[0] = 10uy || (b[0] = 172uy && b[1] >= 16uy && b[1] <= 31uy) || (b[0] = 192uy && b[1] = 168uy) || (b[0] = 169uy && b[1] = 254uy)
        | _ -> false)

/// Repositories the deploy publishes rendered trees into. Only the deployer may change them.
let renderedRepositories (p: Platform) = [ ".github-private"; p.MarketplaceRepo; "agentic-workflows"; "platform-endpoints" ]

let private stagePolicy (stage: Rollout) (pol: Policy) =
    match stage with
    | Enforce -> pol
    | Guard ->
        let optional (s: Sandbox) = { s with FailIfUnavailable = Some false }
        { pol with Sandbox = pol.Sandbox |> Option.map (function Enforced s -> Enforced(optional s) | Overridable s -> Overridable(optional s) | x -> x) }
    | Observe ->
        { pol with Deny = None; Ask = None; Allow = None; DisableBypass = None; Sandbox = None; McpAllow = None; StrictMarketplaces = None }

let private observeAction =
    function
    | Block _ | RequireApproval _ | KeepGoing _ | Approve as a -> DryRun a
    | a -> a

/// The governance the platform PROTECTS ITSELF with, at every stage:
///   rendered repositories   only the deployer can update the default branch, nobody can delete or force-push it
///   definition repository   changes land only through a reviewed pull request whose `render` check passed
let protectionRulesets (p: Platform) : Ruleset list =
    let deployer = match p.DeployedBy with PlatformApp id -> App id | OrganizationOwners -> OrgAdmins
    [
        ruleset {
            named "agent-platform-rendered-repositories"
            repositories (renderedRepositories p)
            require [ RestrictUpdates; NoDeletion; NoForcePush ]
            bypassedBy [ deployer ] }
        ruleset {
            named "agent-platform-definition"
            repositories [ p.DefinitionRepo ]
            require [ RequirePullRequest(1, false); RequireStatusChecks [ "render" ]; NoDeletion; NoForcePush ]
            bypassedBy (match p.DeployedBy with PlatformApp _ -> [] | OrganizationOwners -> [ OrgAdminsByPullRequest ]) }
    ]

/// The generated team file that carries the hook plugins to the pilot ring.
let hookPilotTeam = "agent-platform-hook-pilot"

/// The definition as it deploys at its rollout stage. Idempotent; every renderer and `plan` uses it.
let staged (p: Platform) : Platform =
    let rulesets =
        let own = p.GitHub.Rulesets |> List.map (fun r -> if p.Rollout = Enforce then r else { r with Enforcement = "evaluate" })
        let protection = protectionRulesets p
        (own |> List.filter (fun r -> protection |> List.forall (fun x -> x.Name <> r.Name))) @ protection
    { p with
        Policy = stagePolicy p.Rollout p.Policy
        Teams =
            let pilot =
                match p.HookPilotTeams with
                | [] -> []
                | teams ->
                    let m = marketplace p
                    let hookPlugins = p.Plugins |> List.filter _.CarriesHooks |> List.map (fun pl -> pluginId m pl, true)
                    [ { Name = hookPilotTeam; EnterpriseTeams = teams; Policy = { empty with EnabledPlugins = hookPlugins } } ]
            (p.Teams |> List.filter (fun t -> t.Name <> hookPilotTeam)) @ pilot
            |> List.map (fun t -> { t with Policy = stagePolicy p.Rollout t.Policy })
        Hooks =
            if p.Rollout = Observe then { p.Hooks with Rules = p.Hooks.Rules |> List.map (fun r -> { r with Then = observeAction r.Then }) }
            else p.Hooks
        GitHub = { p.GitHub with Rulesets = rulesets } }


/// The policy the platform actually deploys: the author's policy, plus everything the rest of
/// the definition implies - so nobody has to keep them in sync by hand.
///   - every plugin is enabled through enabledPlugins
///   - the marketplace is the known marketplace and (unless stated otherwise) the only one
///   - every approved MCP server is allowlisted; blocked ones are denied
///   - telemetry goes to the platform host
let resolvedPolicy (p: Platform) : Policy =
    let m = marketplace p
    let pol = p.Policy
    // With a pilot ring the hook plugins are enabled only in the pilot team file, not for everyone.
    let everyone = p.Plugins |> List.filter (fun pl -> p.HookPilotTeams.IsEmpty || not pl.CarriesHooks)
    let enabled = pol.EnabledPlugins @ (everyone |> List.map (fun pl -> pluginId m pl, true)) |> List.distinctBy fst
    let known = { Name = m.Name; Source = GitHubRepo(m.Repo, Some m.Ref, None); AutoUpdate = Some true }
    let marketplaces =
        match pol.Marketplaces with
        | Some(Overridable ms) -> Some(Overridable(known :: ms |> List.distinctBy _.Name))
        | Some(Enforced ms) -> Some(Enforced(known :: ms |> List.distinctBy _.Name))
        | Some Unmanaged | None -> Some(Overridable [ known ])
    let strict =
        match pol.StrictMarketplaces with
        | Some(Overridable ss) -> Some(Overridable(GitHubRepo(m.Repo, None, None) :: ss))
        | Some(Enforced ss) -> Some(Enforced(GitHubRepo(m.Repo, None, None) :: ss))
        | Some Unmanaged | None -> Some(Overridable [ GitHubRepo(m.Repo, None, None) ])
    let mcpAllow =
        let approved =
            p.Mcp.Servers
            |> List.map (fun s ->
                match s.Transport, commandLine s with
                | Remote(url, _), _ ->
                    // Allow the server's origin and every path under it, so /v1/sse and /v1/mcp both match.
                    let u = Uri url
                    ServerUrl $"{u.Scheme}://{u.Authority}/*"
                | _, Some(cmd, args) -> ServerCommand(cmd :: args)
                | _ -> ServerName s.Key)
        match pol.McpAllow, approved with
        | Some(Overridable xs), _ -> Some(Overridable(approved @ xs |> List.distinct))
        | Some(Enforced xs), _ -> Some(Enforced(approved @ xs |> List.distinct))
        | _, [] -> pol.McpAllow
        | _, xs -> Some(Overridable xs)
    let mcpDeny =
        let blocked = p.Mcp.Blocked |> List.map (function BlockUrl u -> ServerUrl u | BlockCommand c -> ServerCommand c | BlockName n -> ServerName n)
        match pol.McpDeny, blocked with
        | Some(Overridable xs), _ -> Some(Overridable(xs @ blocked |> List.distinct))
        | Some(Enforced xs), _ -> Some(Enforced(xs @ blocked |> List.distinct))
        | _, [] -> pol.McpDeny
        | _, xs -> Some(Enforced xs)
    let telemetry =
        pol.Telemetry |> Option.map (fun t -> if t.Endpoint = "" then { t with Endpoint = p.Host } else t)
    let resolved = { pol with EnabledPlugins = enabled; Marketplaces = marketplaces; StrictMarketplaces = strict; McpAllow = mcpAllow; McpDeny = mcpDeny; Telemetry = telemetry }
    if p.Rollout = Observe then { resolved with McpAllow = None; StrictMarketplaces = None } else resolved

let effectivePolicy (p: Platform) (team: string option) =
    let teamPolicy = team |> Option.bind (fun t -> p.Teams |> List.tryFind (fun x -> x.Name = t)) |> Option.map _.Policy
    effective (resolvedPolicy p) teamPolicy

let standardsNamed (p: Platform) names = p.Standards |> List.filter (fun s -> List.contains s.Name names)
let serversNamed (p: Platform) keys = p.Mcp.Servers |> List.filter (fun s -> List.contains s.Key keys)
let agentNamed (p: Platform) name = p.Agents |> List.tryFind (fun a -> a.Name = name)

/// Everything wrong with the definition, before a single file is written.
let validate (p: Platform) : string list = [
    if p.Organization = "" then "platform: set organization."
    if not (p.Host.StartsWith "https://") then "platform: platformHost must be an https:// URL (clients refuse cleartext OTLP and hooks)."
    match p.DeployedBy with
    | PlatformApp id when id <= 0 -> "platform: deployedBy (PlatformApp id) needs the numeric App ID from the app's settings page."
    | _ -> ()
    if not p.HookPilotTeams.IsEmpty && not (p.Plugins |> List.exists _.CarriesHooks) then
        "platform: enforceHooksOnlyFor needs a plugin with carriesHooks - that is what the pilot ring receives."
    if p.DefinitionRepo = "" then "platform: definitionRepo must name the repository that holds this definition."
    if renderedRepositories p |> List.contains p.DefinitionRepo then
        $"platform: definitionRepo '{p.DefinitionRepo}' is also a rendered repository; keep the definition in its own repository."
    let pol = resolvedPolicy p
    yield! validateEnterprise pol
    for t in p.Teams do
        yield! validateTeam pol t.Name t.Policy
        yield! validateSandboxPaths $"teams/{t.Name}.json" t.Policy
        if t.EnterpriseTeams.IsEmpty then $"team {t.Name}: map it to at least one enterprise team slug."
    yield! Standards.validate p.Standards
    yield! Mcp.validate p.Mcp
    yield! Plugins.validate (marketplace p)
    let agentNames = p.Agents |> List.map _.Name |> Set.ofList
    let standardNames = p.Standards |> List.map _.Name |> Set.ofList
    let serverKeys = p.Mcp.Servers |> List.map _.Key |> Set.ofList
    let pluginNames = p.Plugins |> List.map _.Name |> Set.ofList
    for a in p.Agents do
        if a.Name = "" then "agent: every agent needs a name."
        if a.Description = "" then $"agent {a.Name}: needs useWhen - it is how Copilot picks the agent."
        for m in a.Mcp do
            if not (serverKeys.Contains m) then $"agent {a.Name}: uses MCP server '{m}', which is not in the approved catalog."
        for s in a.Standards do
            if not (standardNames.Contains s) then $"agent {a.Name}: follows unknown standard '{s}'."
        if a.Evals.IsEmpty then $"agent {a.Name}: has no evals, so nothing scores its sessions."
    for pl in p.Plugins do
        for a in pl.Agents do
            if not (agentNames.Contains a) then $"plugin {pl.Name}: ships unknown agent '{a}'."
        for s in pl.Standards do
            if not (standardNames.Contains s) then $"plugin {pl.Name}: ships unknown standard '{s}'."
        for m in pl.Mcp do
            if not (serverKeys.Contains m) then $"plugin {pl.Name}: activates MCP server '{m}', which is not in the approved catalog."
    if not (p.Hooks.Rules.IsEmpty && p.Hooks.Capture.IsNone) && not (p.Plugins |> List.exists _.CarriesHooks) then
        "hooks: no plugin carriesHooks, so the central hooks only reach policy.d machines and repositories with the baseline."
    if p.Hooks.Capture.IsSome && p.Hooks.CaptureVia = HttpHooks then
        let host = try Uri(p.Host).Host with _ -> ""
        if hostIsInternal p then
            $"hooks: platformHost {host} looks internal, and Copilot refuses HTTP hooks to loopback/private/link-local addresses. Use captureOverCurl."
    for w in p.Workflows do
        if not (agentNames.Contains w.Agent) then $"workflow {w.Name}: runs '{w.Agent}', which is not one of the platform's agents."
        for pl in w.Plugins do
            if not (pluginNames.Contains pl) then $"workflow {w.Name}: installs unknown plugin '{pl}'."
        if w.Task.Trim() = "" then $"workflow {w.Name}: has no task."
    for r in p.Hooks.Rules do
        if r.Name = "" then "hooks: every rule needs a name."
    for n, c in p.Hooks.Rules |> List.countBy _.Name do
        if c > 1 then $"hooks: rule '{n}' is defined twice."
]
