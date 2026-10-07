/// policy { } - every key GitHub documents for managed-settings.json, as F# values.
///
///     docs: content/copilot/reference/enterprise-administrators/enterprise-managed-settings.md
///
///     computerUse   ->  features.computerUse
///     model         ->  model                                 (overridable, "unmanaged" in team files)
///     autoTier      ->  autoTier                              (overridable, teams may only tighten when enforced)
///     disableBypass ->  permissions.disableBypassPermissionsMode
///     deny/ask/allow->  permissions.deny | ask | allow        (Shell/Read/Edit/Domain selectors)
///     telemetry     ->  telemetry                             (OTLP; never team-overridable)
///     remoteControl ->  remoteControl
///     allowMcp      ->  allowedMcpServers                     (intersection across sources)
///     denyMcp       ->  deniedMcpServers                      (union across sources; never team-overridable)
///     enablePlugins ->  enabledPlugins                        (additive in team files)
///     marketplaces  ->  extraKnownMarketplaces
///     onlyMarketplaces -> strictKnownMarketplaces             ([] = complete lockdown)
///     sandbox       ->  sandbox                               (whole object wrapped when overridable)
module AgentPlatform.Policy

open System.Text.Json.Nodes
open AgentPlatform.Json

// ---------------------------------------------------------------------------
// The words
// ---------------------------------------------------------------------------

/// How a value is held. Enterprise files use Enforced or Overridable; team files use
/// Enforced (a plain value) or Unmanaged (hand the key back to the user).
type Setting<'T> =
    | Enforced of 'T
    | Overridable of 'T
    | Unmanaged

let enforced v = Enforced v
let overridable v = Overridable v
let unmanaged<'T> : Setting<'T> = Unmanaged

let settingValue =
    function
    | Enforced v
    | Overridable v -> Some v
    | Unmanaged -> None

/// A permission rule. Selector syntax is GitHub's; see the reference for glob roots.
type PermissionRule =
    | Shell of string
    | Read of string
    | Edit of string
    | Domain of string

    member r.Text =
        match r with
        | Shell s -> $"Shell({s})"
        | Read s -> $"Read({s})"
        | Edit s -> $"Edit({s})"
        | Domain s -> $"Domain({s})"

let shell s = Shell s
let read s = Read s
let edit s = Edit s
let domain s = Domain s

type Tier =
    | Efficiency
    | Balance
    | Intelligence

    member t.Text =
        match t with
        | Efficiency -> "efficiency"
        | Balance -> "balance"
        | Intelligence -> "intelligence"

    /// Lower is more restrictive. Teams may move down from an enforced tier, never up.
    member t.Rank =
        match t with
        | Efficiency -> 0
        | Balance -> 1
        | Intelligence -> 2

type Bypass = | Disable

type McpServer =
    | ServerName of string
    | ServerUrl of string
    | ServerCommand of string list

let serverName s = ServerName s
let serverUrl s = ServerUrl s
let serverCommand c = ServerCommand c

/// Where plugin marketplaces live. extraKnownMarketplaces accepts github/git/directory;
/// strictKnownMarketplaces accepts every case.
type Source =
    | GitHubRepo of repo: string * ref: string option * path: string option
    | GitUrl of url: string * ref: string option * path: string option
    | Directory of path: string
    | UrlSource of url: string
    | Npm of package: string
    | FileSource of path: string
    | HostPattern of regex: string
    | PathPattern of regex: string

let github repo = GitHubRepo(repo, None, None)
let githubAt repo ref = GitHubRepo(repo, Some ref, None)
let directory path = Directory path

type KnownMarketplace = { Name: string; Source: Source; AutoUpdate: bool option }

let marketplace name source = { Name = name; Source = source; AutoUpdate = None }
let autoUpdating (m: KnownMarketplace) = { m with AutoUpdate = Some true }

type Protocol =
    | HttpJson
    | HttpProtobuf

type Telemetry = {
    Enabled: bool
    Endpoint: string
    Protocol: Protocol
    CaptureContent: bool
    LockCaptureContent: bool
    ServiceName: string option
    ResourceAttributes: (string * string) list
    Headers: (string * string) list
}

/// OpenTelemetry export to a collector. Metadata only until `capturingContent`.
let otlp endpoint = {
    Enabled = true
    Endpoint = endpoint
    Protocol = HttpJson
    CaptureContent = false
    LockCaptureContent = false
    ServiceName = None
    ResourceAttributes = []
    Headers = []
}

let capturingContent t = { t with CaptureContent = true }
let lockedContent t = { t with LockCaptureContent = true }
let serviceNamed name t = { t with ServiceName = Some name }
let withAttributes attrs t = { t with ResourceAttributes = attrs }
let withHeaders headers t = { t with Headers = headers }

type RemoteControl =
    | RemoteDisabled
    | RequireSso of orgs: string list
    | RemoteEnabled

let requireSso orgs = RequireSso orgs

type Sandbox = {
    Enabled: bool option
    FailIfUnavailable: bool option
    AllowBypass: bool option
    AddCurrentWorkingDirectory: bool option
    SandboxMcpServers: bool option
    SandboxLspServers: bool option
    GitAuth: bool option
    GhAuth: bool option
    AllowDevToolAccess: bool option
    ReadWritePaths: string list option
    ReadOnlyPaths: string list option
    DeniedPaths: string list
    AllowOutbound: bool option
    AllowLocalNetwork: bool option
    AllowedHosts: string list
    BlockedHosts: string list
    Proxy: string option
    KeychainAccess: bool option
}

let private openSandbox: Sandbox = {
    Enabled = None; FailIfUnavailable = None; AllowBypass = None; AddCurrentWorkingDirectory = None
    SandboxMcpServers = None; SandboxLspServers = None; GitAuth = None; GhAuth = None
    AllowDevToolAccess = None; ReadWritePaths = None; ReadOnlyPaths = None; DeniedPaths = []
    AllowOutbound = None; AllowLocalNetwork = None; AllowedHosts = []; BlockedHosts = []
    Proxy = None; KeychainAccess = None
}

/// sandbox { required; failClosed; noBypass; ... }
type SandboxBuilder() =
    member _.Yield(_: unit) = openSandbox
    [<CustomOperation "required">] member _.Required(s: Sandbox) = { s with Enabled = Some true }
    [<CustomOperation "failClosed">] member _.FailClosed(s: Sandbox) = { s with FailIfUnavailable = Some true }
    [<CustomOperation "noBypass">] member _.NoBypass(s: Sandbox) = { s with AllowBypass = Some false }
    [<CustomOperation "noAutoWorkingDirectory">] member _.NoCwd(s: Sandbox) = { s with AddCurrentWorkingDirectory = Some false }
    [<CustomOperation "sandboxMcpServers">] member _.Mcp(s: Sandbox) = { s with SandboxMcpServers = Some true }
    [<CustomOperation "sandboxLspServers">] member _.Lsp(s: Sandbox) = { s with SandboxLspServers = Some true }
    [<CustomOperation "noGitAuth">] member _.NoGit(s: Sandbox) = { s with GitAuth = Some false }
    [<CustomOperation "noGhAuth">] member _.NoGh(s: Sandbox) = { s with GhAuth = Some false }
    [<CustomOperation "noDevToolAccess">] member _.NoDev(s: Sandbox) = { s with AllowDevToolAccess = Some false }
    [<CustomOperation "readWrite">] member _.Rw(s: Sandbox, p) = { s with ReadWritePaths = Some p }
    [<CustomOperation "readOnly">] member _.Ro(s: Sandbox, p) = { s with ReadOnlyPaths = Some p }
    [<CustomOperation "denyPaths">] member _.Deny(s: Sandbox, p) = { s with DeniedPaths = p }
    [<CustomOperation "noOutbound">] member _.NoOut(s: Sandbox) = { s with AllowOutbound = Some false }
    [<CustomOperation "noLocalNetwork">] member _.NoLan(s: Sandbox) = { s with AllowLocalNetwork = Some false }
    [<CustomOperation "allowHosts">] member _.Allow(s: Sandbox, h) = { s with AllowedHosts = h }
    [<CustomOperation "blockHosts">] member _.Block(s: Sandbox, h) = { s with BlockedHosts = h }
    [<CustomOperation "proxy">] member _.Proxy(s: Sandbox, url) = { s with Proxy = Some url }
    [<CustomOperation "noKeychain">] member _.NoKeychain(s: Sandbox) = { s with KeychainAccess = Some false }

let sandbox = SandboxBuilder()

/// Everything managed-settings.json can say.
type Policy = {
    ComputerUse: bool option
    Model: Setting<string> option
    AutoTier: Setting<Tier> option
    DisableBypass: Setting<Bypass> option
    Deny: Setting<PermissionRule list> option
    Ask: Setting<PermissionRule list> option
    Allow: Setting<PermissionRule list> option
    Telemetry: Telemetry option
    RemoteControl: RemoteControl option
    McpAllow: Setting<McpServer list> option
    McpDeny: Setting<McpServer list> option
    EnabledPlugins: (string * bool) list
    Marketplaces: Setting<KnownMarketplace list> option
    StrictMarketplaces: Setting<Source list> option
    Sandbox: Setting<Sandbox> option
}

let empty: Policy = {
    ComputerUse = None; Model = None; AutoTier = None; DisableBypass = None
    Deny = None; Ask = None; Allow = None; Telemetry = None; RemoteControl = None
    McpAllow = None; McpDeny = None; EnabledPlugins = []; Marketplaces = None
    StrictMarketplaces = None; Sandbox = None
}

/// Plain operations ENFORCE a value (`deny [...]`). `...ByDefault` operations set an enterprise
/// default that enterprise teams may override (`{ "overridable": ... }`). `unmanaged...` operations
/// hand a key back to the user and are only meaningful in team files.
type PolicyBuilder() =
    member _.Yield(_: unit) = empty
    [<CustomOperation "computerUse">] member _.ComputerUse(p: Policy, v) = { p with ComputerUse = Some v }
    [<CustomOperation "model">] member _.Model(p: Policy, v: string) = { p with Model = Some(Enforced v) }
    [<CustomOperation "modelByDefault">] member _.ModelDefault(p: Policy, v: string) = { p with Model = Some(Overridable v) }
    [<CustomOperation "unmanagedModel">] member _.ModelFree(p: Policy) = { p with Model = Some Unmanaged }
    [<CustomOperation "autoTier">] member _.AutoTier(p: Policy, v: Tier) = { p with AutoTier = Some(Enforced v) }
    [<CustomOperation "autoTierByDefault">] member _.AutoTierDefault(p: Policy, v: Tier) = { p with AutoTier = Some(Overridable v) }
    [<CustomOperation "unmanagedAutoTier">] member _.AutoTierFree(p: Policy) = { p with AutoTier = Some Unmanaged }
    [<CustomOperation "noBypassMode">] member _.NoBypass(p: Policy) = { p with DisableBypass = Some(Enforced Disable) }
    [<CustomOperation "noBypassModeByDefault">] member _.NoBypassDefault(p: Policy) = { p with DisableBypass = Some(Overridable Disable) }
    [<CustomOperation "allowBypassMode">] member _.BypassFree(p: Policy) = { p with DisableBypass = Some Unmanaged }
    [<CustomOperation "deny">] member _.Deny(p: Policy, v: PermissionRule list) = { p with Deny = Some(Enforced v) }
    [<CustomOperation "denyByDefault">] member _.DenyDefault(p: Policy, v: PermissionRule list) = { p with Deny = Some(Overridable v) }
    [<CustomOperation "ask">] member _.Ask(p: Policy, v: PermissionRule list) = { p with Ask = Some(Enforced v) }
    [<CustomOperation "askByDefault">] member _.AskDefault(p: Policy, v: PermissionRule list) = { p with Ask = Some(Overridable v) }
    [<CustomOperation "allow">] member _.Allow(p: Policy, v: PermissionRule list) = { p with Allow = Some(Enforced v) }
    [<CustomOperation "allowByDefault">] member _.AllowDefault(p: Policy, v: PermissionRule list) = { p with Allow = Some(Overridable v) }
    [<CustomOperation "telemetry">] member _.Telemetry(p: Policy, v) = { p with Telemetry = Some v }
    [<CustomOperation "remoteControl">] member _.Remote(p: Policy, v) = { p with RemoteControl = Some v }
    [<CustomOperation "allowMcp">] member _.AllowMcp(p: Policy, v: McpServer list) = { p with McpAllow = Some(Enforced v) }
    [<CustomOperation "allowMcpByDefault">] member _.AllowMcpDefault(p: Policy, v: McpServer list) = { p with McpAllow = Some(Overridable v) }
    [<CustomOperation "denyMcp">] member _.DenyMcp(p: Policy, v: McpServer list) = { p with McpDeny = Some(Enforced v) }
    [<CustomOperation "denyMcpByDefault">] member _.DenyMcpDefault(p: Policy, v: McpServer list) = { p with McpDeny = Some(Overridable v) }
    [<CustomOperation "enablePlugins">] member _.Enable(p: Policy, names: string list) = { p with EnabledPlugins = p.EnabledPlugins @ (names |> List.map (fun n -> n, true)) }
    [<CustomOperation "blockPlugins">] member _.Block(p: Policy, names: string list) = { p with EnabledPlugins = p.EnabledPlugins @ (names |> List.map (fun n -> n, false)) }
    [<CustomOperation "marketplaces">] member _.Markets(p: Policy, v: KnownMarketplace list) = { p with Marketplaces = Some(Enforced v) }
    [<CustomOperation "marketplacesByDefault">] member _.MarketsDefault(p: Policy, v: KnownMarketplace list) = { p with Marketplaces = Some(Overridable v) }
    [<CustomOperation "onlyMarketplaces">] member _.Strict(p: Policy, v: Source list) = { p with StrictMarketplaces = Some(Enforced v) }
    [<CustomOperation "onlyMarketplacesByDefault">] member _.StrictDefault(p: Policy, v: Source list) = { p with StrictMarketplaces = Some(Overridable v) }
    [<CustomOperation "sandboxed">] member _.Sandbox(p: Policy, v: Sandbox) = { p with Sandbox = Some(Enforced v) }
    [<CustomOperation "sandboxedByDefault">] member _.SandboxDefault(p: Policy, v: Sandbox) = { p with Sandbox = Some(Overridable v) }
/// Describes managed settings. See the module docs for the mapping to JSON keys.
let policy = PolicyBuilder()

// ---------------------------------------------------------------------------
// To JSON: exactly the shape the docs show
// ---------------------------------------------------------------------------

let private setting (render: 'T -> JsonNode) =
    function
    | Enforced v -> render v
    | Overridable v -> objOf [ "overridable", render v ]
    | Unmanaged -> str "unmanaged"

let private rules (rs: PermissionRule list) = rs |> List.map _.Text |> strs

let private mcp =
    function
    | ServerName n -> objOf [ "serverName", str n ]
    | ServerUrl u -> objOf [ "serverUrl", str u ]
    | ServerCommand c -> objOf [ "serverCommand", strs c ]

let sourceJson =
    function
    | GitHubRepo(repo, ref, path) ->
        obj [ "source", Some(str "github"); "repo", Some(str repo); "ref", ref |> Option.map str; "path", path |> Option.map str ]
    | GitUrl(url, ref, path) ->
        obj [ "source", Some(str "git"); "url", Some(str url); "ref", ref |> Option.map str; "path", path |> Option.map str ]
    | Directory p -> objOf [ "source", str "directory"; "path", str p ]
    | UrlSource u -> objOf [ "source", str "url"; "url", str u ]
    | Npm p -> objOf [ "source", str "npm"; "package", str p ]
    | FileSource p -> objOf [ "source", str "file"; "path", str p ]
    | HostPattern r -> objOf [ "source", str "hostPattern"; "hostPattern", str r ]
    | PathPattern r -> objOf [ "source", str "pathPattern"; "pathPattern", str r ]

let private markets (ms: KnownMarketplace list) =
    ms
    |> List.map (fun m -> m.Name, obj [ "source", Some(sourceJson m.Source); "autoUpdate", m.AutoUpdate |> Option.map bool ])
    |> objOf

let private telemetry (t: Telemetry) =
    obj [
        "enabled", Some(bool t.Enabled)
        "endpoint", Some(str t.Endpoint)
        "protocol", Some(str (match t.Protocol with HttpJson -> "http/json" | HttpProtobuf -> "http/protobuf"))
        "captureContent", Some(bool t.CaptureContent)
        "lockCaptureContent", Some(bool t.LockCaptureContent)
        "serviceName", t.ServiceName |> Option.map str
        "resourceAttributes", (if t.ResourceAttributes.IsEmpty then None else Some(t.ResourceAttributes |> List.map (fun (k, v) -> k, str v) |> objOf))
        "headers", (if t.Headers.IsEmpty then None else Some(t.Headers |> List.map (fun (k, v) -> k, str v) |> objOf))
    ]

let private sandboxJson (s: Sandbox) =
    let b = Option.map bool
    let list xs = if List.isEmpty xs then None else Some(strs xs)
    let filesystem =
        obj [ "readwritePaths", s.ReadWritePaths |> Option.map strs; "readonlyPaths", s.ReadOnlyPaths |> Option.map strs; "deniedPaths", list s.DeniedPaths ]
    let network =
        obj [
            "allowOutbound", b s.AllowOutbound; "allowLocalNetwork", b s.AllowLocalNetwork
            "allowedHosts", list s.AllowedHosts; "blockedHosts", list s.BlockedHosts
            "proxy", s.Proxy |> Option.map (fun u -> objOf [ "url", str u ])
        ]
    let seatbelt = obj [ "keychainAccess", b s.KeychainAccess ]
    let nonEmpty (n: JsonNode) = if (n :?> JsonObject).Count = 0 then None else Some n
    let userPolicy = obj [ "filesystem", nonEmpty filesystem; "network", nonEmpty network; "seatbelt", nonEmpty seatbelt ]
    obj [
        "enabled", b s.Enabled; "failIfUnavailable", b s.FailIfUnavailable; "allowBypass", b s.AllowBypass
        "addCurrentWorkingDirectory", b s.AddCurrentWorkingDirectory; "sandboxMcpServers", b s.SandboxMcpServers
        "sandboxLspServers", b s.SandboxLspServers; "gitAuth", b s.GitAuth; "ghAuth", b s.GhAuth
        "allowDevToolAccess", b s.AllowDevToolAccess; "userPolicy", nonEmpty userPolicy
    ]

/// The managed-settings.json (or a team file) for a policy.
let toJson (p: Policy) : JsonNode =
    let permissions =
        obj [
            "disableBypassPermissionsMode", p.DisableBypass |> Option.map (setting (fun Disable -> str "disable"))
            "deny", p.Deny |> Option.map (setting rules)
            "ask", p.Ask |> Option.map (setting rules)
            "allow", p.Allow |> Option.map (setting rules)
        ]
    obj [
        "features", p.ComputerUse |> Option.map (fun v -> objOf [ "computerUse", bool v ])
        "model", p.Model |> Option.map (setting str)
        "autoTier", p.AutoTier |> Option.map (setting (fun t -> str t.Text))
        "permissions", (if (permissions :?> JsonObject).Count = 0 then None else Some permissions)
        "telemetry", p.Telemetry |> Option.map telemetry
        "remoteControl",
        p.RemoteControl
        |> Option.map (function
            | RemoteDisabled -> objOf [ "mode", str "disabled" ]
            | RemoteEnabled -> objOf [ "mode", str "enabled" ]
            | RequireSso orgs -> objOf [ "mode", str "requireSSO"; "githubDotComOrganizations", strs orgs ])
        "allowedMcpServers", p.McpAllow |> Option.map (setting (List.map mcp >> arr))
        "deniedMcpServers", p.McpDeny |> Option.map (setting (List.map mcp >> arr))
        "enabledPlugins", (if p.EnabledPlugins.IsEmpty then None else Some(p.EnabledPlugins |> List.map (fun (n, v) -> n, bool v) |> objOf))
        "extraKnownMarketplaces", p.Marketplaces |> Option.map (setting markets)
        "strictKnownMarketplaces", p.StrictMarketplaces |> Option.map (setting (List.map sourceJson >> arr))
        "sandbox", p.Sandbox |> Option.map (setting sandboxJson)
    ]

// ---------------------------------------------------------------------------
// Team overrides: the rules GitHub's validator applies, checked before you push
// ---------------------------------------------------------------------------

/// Delivery channels other than server-managed have no teams, so `overridable` means nothing
/// there: the enterprise default is simply what applies.
let flatten (p: Policy) : Policy =
    let f =
        function
        | Some(Overridable v) -> Some(Enforced v)
        | other -> other
    { p with
        Model = f p.Model; AutoTier = f p.AutoTier; DisableBypass = f p.DisableBypass
        Deny = f p.Deny; Ask = f p.Ask; Allow = f p.Allow; McpAllow = f p.McpAllow; McpDeny = f p.McpDeny
        Marketplaces = f p.Marketplaces; StrictMarketplaces = f p.StrictMarketplaces; Sandbox = f p.Sandbox }

let private isOverridable =
    function
    | Some(Overridable _) -> true
    | _ -> false

/// Problems with a team file, in the words a reviewer would use.
let validateTeam (enterprise: Policy) (teamName: string) (team: Policy) : string list = [
    let key name (teamValue: Setting<'T> option) (enterpriseValue: Setting<'T> option) unmanagedAllowed = [
        match teamValue with
        | None -> ()
        | Some(Overridable _) -> $"{teamName}: '{name}' uses 'overridable' - only the enterprise file may do that."
        | Some Unmanaged when not unmanagedAllowed -> $"{teamName}: '{name}' cannot be 'unmanaged'; give a replacement value."
        | Some _ when not (isOverridable enterpriseValue) ->
            $"{teamName}: '{name}' is not marked overridable in managed-settings.json, so the team value would be ignored."
        | Some _ -> ()
    ]
    yield! key "model" team.Model enterprise.Model true
    yield! key "permissions.disableBypassPermissionsMode" team.DisableBypass enterprise.DisableBypass true
    yield! key "permissions.deny" team.Deny enterprise.Deny false
    yield! key "permissions.ask" team.Ask enterprise.Ask false
    yield! key "permissions.allow" team.Allow enterprise.Allow false
    yield! key "allowedMcpServers" team.McpAllow enterprise.McpAllow false
    yield! key "deniedMcpServers" team.McpDeny enterprise.McpDeny false
    yield! key "extraKnownMarketplaces" team.Marketplaces enterprise.Marketplaces false
    yield! key "strictKnownMarketplaces" team.StrictMarketplaces enterprise.StrictMarketplaces false
    yield! key "sandbox" team.Sandbox enterprise.Sandbox false
    match team.AutoTier, enterprise.AutoTier with
    | Some(Overridable _), _ -> $"{teamName}: 'autoTier' uses 'overridable' - only the enterprise file may do that."
    | Some(Enforced t), Some(Enforced e) when t.Rank > e.Rank ->
        $"{teamName}: 'autoTier' {t.Text} is less restrictive than the enforced {e.Text}; teams may only tighten it."
    | Some Unmanaged, Some(Enforced _) -> $"{teamName}: 'autoTier' is enforced, so a team cannot make it unmanaged."
    | _ -> ()
    if team.Telemetry.IsSome then $"{teamName}: 'telemetry' is not overridable for teams."
    if team.RemoteControl.IsSome then $"{teamName}: 'remoteControl' is not overridable for teams."
    if team.ComputerUse.IsSome then $"{teamName}: 'features.computerUse' is not overridable for teams."
]

/// "Paths should be absolute" (enterprise-managed-settings.md, sandbox.userPolicy.filesystem). `~` is only a
/// root in permission rules (Read(~/...)), not in sandbox paths: deny home-directory secrets with permissions.deny.
let validateSandboxPaths (where: string) (p: Policy) = [
    match p.Sandbox with
    | Some(Enforced s | Overridable s) ->
        let all = (defaultArg s.ReadWritePaths []) @ (defaultArg s.ReadOnlyPaths []) @ s.DeniedPaths
        for path in all do
            let absolute = path.StartsWith "/" || (path.Length > 2 && path[1] = ':' && (path[2] = '\\' || path[2] = '/')) || path.StartsWith @"\\"
            if not absolute then
                $"{where}: sandbox path '{path}' must be absolute. For home-directory secrets use deny [ read \"~/...\" ] instead."
    | _ -> ()
]

/// Problems with the enterprise file itself.
let validateEnterprise (p: Policy) : string list = [
    let has =
        function
        | Some Unmanaged -> true
        | _ -> false
    if has p.Model || has p.AutoTier || has p.DisableBypass || has p.Deny || has p.Ask || has p.Allow
       || has p.McpAllow || has p.McpDeny || has p.Marketplaces || has p.StrictMarketplaces || has p.Sandbox then
        "managed-settings.json: 'unmanaged' belongs in team files, not the enterprise file."
    match p.Telemetry with
    | Some t when not (t.Endpoint.StartsWith "https://") -> "telemetry.endpoint must be https:// - clients refuse to export in cleartext."
    | _ -> ()
    match p.RemoteControl with
    | Some(RequireSso []) -> "remoteControl: requireSSO needs at least one organization."
    | _ -> ()
    match p.Marketplaces |> Option.bind settingValue with
    | Some ms ->
        for m in ms do
            match m.Source with
            | GitHubRepo _ | GitUrl _ | Directory _ -> ()
            | _ -> $"extraKnownMarketplaces.{m.Name}: only github, git and directory sources are supported."
    | None -> ()
    yield! validateSandboxPaths "managed-settings.json" p
    match p.Sandbox with
    | Some(Enforced s | Overridable s) when s.FailIfUnavailable = Some true && s.Enabled <> Some true ->
        "sandbox.failIfUnavailable does nothing unless sandbox.enabled is true."
    | _ -> ()
    // Docs: "If [a managed source] defines any permission rule ... an unmatched supported operation defaults
    // to requiring approval." Without an allow list, every ordinary read, edit and command needs a person,
    // and headless sessions (SDK services, CI) are refused. Declare `allow []` to opt in on purpose.
    if (p.Deny.IsSome || p.Ask.IsSome) && p.Allow.IsNone then
        "permissions: deny/ask rules without an allow list make every unmatched operation require approval, so headless SDK and CI sessions will be refused. Add allow / allowByDefault (use allow [] if that is intended)."
]

/// What one member of a team actually gets on the server-managed path: team values for
/// overridable keys, enterprise values for the rest, enabledPlugins unioned.
let effective (enterprise: Policy) (team: Policy option) : Policy =
    match team with
    | None -> flatten enterprise
    | Some t ->
        let pick (tv: Setting<'T> option) (ev: Setting<'T> option) =
            match tv, ev with
            | Some v, Some(Overridable _) -> Some v
            | _, e -> e
        let tier =
            match t.AutoTier, enterprise.AutoTier with
            | Some(Enforced tt), Some(Enforced et) when tt.Rank <= et.Rank -> Some(Enforced tt)
            | Some v, Some(Overridable _) -> Some v
            | _, e -> e
        flatten {
            enterprise with
                Model = pick t.Model enterprise.Model
                AutoTier = tier
                DisableBypass = pick t.DisableBypass enterprise.DisableBypass
                Deny = pick t.Deny enterprise.Deny
                Ask = pick t.Ask enterprise.Ask
                Allow = pick t.Allow enterprise.Allow
                McpAllow = pick t.McpAllow enterprise.McpAllow
                McpDeny = pick t.McpDeny enterprise.McpDeny
                Marketplaces = pick t.Marketplaces enterprise.Marketplaces
                StrictMarketplaces = pick t.StrictMarketplaces enterprise.StrictMarketplaces
                Sandbox = pick t.Sandbox enterprise.Sandbox
                EnabledPlugins = enterprise.EnabledPlugins @ t.EnabledPlugins |> List.distinctBy fst
        }

/// The keys a policy sets, as dotted names - used to prove coverage of the documented schema.
let keysOf (p: Policy) =
    let rec walk prefix (n: JsonNode) = [
        match n with
        | :? JsonObject as o ->
            for kv in o do
                let k = if prefix = "" then kv.Key else $"{prefix}.{kv.Key}"
                match kv.Value with
                | :? JsonObject as child when kv.Key <> "headers" && kv.Key <> "resourceAttributes" && kv.Key <> "enabledPlugins" ->
                    if child.ContainsKey "overridable" then k else yield! walk k child
                | _ -> k
        | _ -> ()
    ]
    walk "" (toJson p)

/// Every top-level setting the reference documents, with its client support (CLI, VS Code,
/// Copilot app, cloud agent, JetBrains), straight from the table in the docs.
let documented = [
    "permissions.disableBypassPermissionsMode", "CLI VSCode App JetBrains"
    "permissions.deny", "CLI VSCode App"
    "permissions.ask", "CLI VSCode App"
    "permissions.allow", "CLI VSCode App"
    "features.computerUse", "CLI App"
    "model", "CLI VSCode App CloudAgent"
    "autoTier", "CLI VSCode"
    "enabledPlugins", "CLI VSCode App CloudAgent JetBrains"
    "extraKnownMarketplaces", "CLI VSCode App CloudAgent JetBrains"
    "strictKnownMarketplaces", "CLI VSCode App CloudAgent JetBrains"
    "telemetry", "CLI VSCode JetBrains"
    "remoteControl", "CLI VSCode App"
    "allowedMcpServers", "CLI VSCode App JetBrains"
    "deniedMcpServers", "CLI VSCode App JetBrains"
    "sandbox", "CLI App"
]
