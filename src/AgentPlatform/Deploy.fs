/// Applying the settings that only exist as GitHub APIs, with a plan first.
///
///     plan   reads live state and prints what would change (safe on pull requests)
///     apply  makes it so (run on main, with a GitHub App token)
///
/// Repositories (.github-private, the marketplace, the agentic workflows) are not written here: the
/// deploy workflow commits the rendered directories into them, so every change is a reviewed commit.
/// This module covers what only exists as settings:
///
///     configuration source   PUT  /enterprises/{e}/copilot/custom-agents/source   (selects .github-private + protects agents/)
///     cloud agent access     PUT  /orgs/{org}/copilot/coding-agent/permissions
///     content exclusion      PUT  /orgs/{org}/copilot/content_exclusion
///     rulesets               POST / PUT /orgs/{org}/rulesets[/{id}]   (matched by name, so re-runs update)
///     agent variables        POST / PATCH /orgs/{org}/agents/variables[/{name}]
///
/// Calls go through `gh api`, so GH_TOKEN (a workflow or GitHub App token) authenticates.
module AgentPlatform.Deploy

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open AgentPlatform.Json
open AgentPlatform.Governance
open AgentPlatform.Platform

type Step = { Description: string; Method: string; Path: string; Body: JsonNode option; Reason: string }

type ApiResult = { Ok: bool; Body: string }

let private gh (args: string list) (stdin: string option) =
    let psi = ProcessStartInfo("gh", RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin.IsSome, UseShellExecute = false)
    for a in args do psi.ArgumentList.Add a
    use p = Process.Start psi
    stdin |> Option.iter (fun s -> p.StandardInput.Write s; p.StandardInput.Close())
    let out = p.StandardOutput.ReadToEnd()
    let err = p.StandardError.ReadToEnd()
    p.WaitForExit()
    { Ok = p.ExitCode = 0; Body = (if p.ExitCode = 0 then out else out + err) }

let private version = "X-GitHub-Api-Version: 2026-03-10"

let get (path: string) = gh [ "api"; "-H"; version; path ] None

let send (method': string) (path: string) (body: JsonNode option) =
    match body with
    | Some b -> gh [ "api"; "-X"; method'; "-H"; version; path; "--input"; "-" ] (Some(b.ToJsonString()))
    | None -> gh [ "api"; "-X"; method'; "-H"; version; path ] None

let private parse (r: ApiResult) = if r.Ok then (try Some(JsonNode.Parse r.Body) with _ -> None) else None

/// A setting the token cannot read is never assumed missing: `plan` reports it and `apply` refuses to run.
let accessMethod = "ACCESS"

let private lacksAccess (r: ApiResult) =
    // A 404 means "does not exist" here (the caller's token has the scopes - see below); org rulesets are the
    // exception: GitHub answers 404 when the token lacks admin:org, so rulesets also check for the scope hint.
    not r.Ok
    && not (r.Body.Contains "HTTP 404" && not (r.Body.Contains "needs the"))
    && (r.Body.Contains "needs the" || r.Body.Contains "HTTP 403" || r.Body.Contains "HTTP 401" || r.Body.Contains "Resource not accessible")

let private noAccess (what: string) (path: string) (r: ApiResult) =
    let hint =
        r.Body.Split('\n')
        |> Array.tryFind (fun l -> l.Contains "scope" || l.Contains "HTTP 4")
        |> Option.map _.Trim()
        |> Option.defaultValue (r.Body.Trim())
    { Description = $"Cannot read {what}: {hint}"; Method = accessMethod; Path = path; Body = None; Reason = "token lacks access" }

let private text (n: JsonNode) = if isNull n then "" else n.ToString()

/// True when every value in `want` is present and equal in `live` (objects may carry extra server defaults;
/// arrays must match element by element). The organization-admin bypass actor reads back with a null actor_id.
let rec covers (want: JsonNode) (live: JsonNode) =
    match want, live with
    | null, _ -> true
    | _, null -> false
    | (:? JsonObject as w), (:? JsonObject as l) ->
        w |> Seq.forall (fun kv ->
            let found, v = l.TryGetPropertyValue(kv.Key: string)
            if not found then false
            elif kv.Key = "actor_id" && isNull v then true
            else covers kv.Value v)
    | (:? JsonArray as w), (:? JsonArray as l) -> w.Count = l.Count && Seq.forall2 covers w l
    | w, l -> JsonNode.DeepEquals(w, l)

/// What `apply` would do, given live state.
let plan (p: Platform) : Step list = [
    let p = staged p
    let org = p.Organization
    if p.Enterprise <> "" then
      let sourcePath = $"/enterprises/{p.Enterprise}/copilot/custom-agents/source"
      let sourceRead = get sourcePath
      if lacksAccess sourceRead then noAccess "the enterprise configuration source" sourcePath sourceRead
      else
        let live = parse sourceRead
        let current = live |> Option.map (fun l -> text (l["organization"] |> Option.ofObj |> Option.map (fun o -> o["login"]) |> Option.toObj))
        if current <> Some org then
            match parse (get $"/orgs/{org}") with
            | Some o ->
                { Description = $"Select {org}/.github-private as the enterprise configuration source (and protect agents/ with a ruleset)"
                  Method = "PUT"; Path = $"/enterprises/{p.Enterprise}/copilot/custom-agents/source"
                  Body = Some(objOf [ "organization_id", num (o["id"].GetValue<int>()); "create_ruleset", bool true ]); Reason = "configuration source is not this organization" }
            | None -> ()
    let wantAccess = cloudAgentPermissionsJson p.GitHub
    let accessPath = $"/orgs/{org}/copilot/coding-agent/permissions"
    let accessRead = get accessPath
    if lacksAccess accessRead then noAccess "cloud agent repository access" accessPath accessRead
    else
        match parse accessRead with
        | Some live when text live["enabled_repositories"] = text wantAccess["enabled_repositories"] -> ()
        | _ -> { Description = "Set which repositories Copilot cloud agent may work in"; Method = "PUT"; Path = accessPath; Body = Some wantAccess; Reason = "differs from definition" }
    if not p.GitHub.ContentExclusion.IsEmpty then
        let want = contentExclusionJson p.GitHub
        let exclusionPath = $"/orgs/{org}/copilot/content_exclusion"
        let exclusionRead = get exclusionPath
        if lacksAccess exclusionRead then noAccess "Copilot content exclusion" exclusionPath exclusionRead
        else
            match parse exclusionRead with
            | Some live when JsonNode.DeepEquals(live, want) -> ()
            | _ -> { Description = "Set Copilot content exclusion"; Method = "PUT"; Path = exclusionPath; Body = Some want; Reason = "differs from definition" }
    // The pilot ring's enterprise teams must exist before a team file can name them.
    if p.Enterprise <> "" && not p.HookPilotTeams.IsEmpty then
      // Listed rather than fetched one by one: a missing team answers 404 with a misleading scope hint.
      let path = $"/enterprises/{p.Enterprise}/teams"
      let listed = gh [ "api"; "-H"; version; "--paginate"; path; "--jq"; ".[].slug" ] None
      if not listed.Ok then noAccess "enterprise teams" path listed
      else
        let existing = listed.Body.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries) |> Array.map (fun s -> s.Trim()) |> Set.ofArray
        for slug in p.HookPilotTeams do
            if existing.Contains slug || existing.Contains("ent:" + slug) then ()
            else
                { Description = $"Create enterprise team '{slug}' (the hook pilot ring; add members after)"
                  Method = "POST"; Path = $"/enterprises/{p.Enterprise}/teams"
                  Body = Some(objOf [ "name", str slug; "description", str "Receives the agent platform's hook plugin (pilot ring)."; "organization_selection_type", str "disabled" ])
                  Reason = "missing" }
    let rulesetsRead = get $"/orgs/{org}/rulesets"
    if lacksAccess rulesetsRead then noAccess "organization rulesets" $"/orgs/{org}/rulesets" rulesetsRead
    let liveRulesets =
        match parse rulesetsRead with
        | Some(:? JsonArray as a) -> [ for r in a -> text r["name"], r["id"].GetValue<int>() ]
        | _ -> []
    if not (lacksAccess rulesetsRead) then
     for r in p.GitHub.Rulesets do
        let body = rulesetJson r
        match liveRulesets |> List.tryFind (fun (n, _) -> n = r.Name) with
        | Some(_, id) ->
            // GitHub returns the ruleset with server defaults filled in, so "unchanged" means every value the
            // definition sets is already live (and nothing more is required).
            match parse (get $"/orgs/{org}/rulesets/{id}") with
            | Some live when covers body live -> ()
            | _ -> { Description = $"Update ruleset '{r.Name}'"; Method = "PUT"; Path = $"/orgs/{org}/rulesets/{id}"; Body = Some body; Reason = "differs from definition" }
        | None -> { Description = $"Create ruleset '{r.Name}'"; Method = "POST"; Path = $"/orgs/{org}/rulesets"; Body = Some body; Reason = "missing" }
    let varsRead = if p.GitHub.AgentVariables.IsEmpty then { Ok = true; Body = "{}" } else get $"/orgs/{org}/agents/variables"
    if lacksAccess varsRead then noAccess "organization agent variables" $"/orgs/{org}/agents/variables" varsRead
    let liveVars =
        match parse varsRead with
        | Some live -> match live["variables"] with :? JsonArray as a -> [ for v in a -> text v["name"], text v["value"] ] | _ -> []
        | None -> []
    if not (lacksAccess varsRead) then
     for name, value in p.GitHub.AgentVariables do
        let body = Some(objOf [ "name", str name; "value", str value; "visibility", str "all" ])
        match liveVars |> List.tryFind (fun (n, _) -> n = name) with
        | Some(_, v) when v = value -> ()
        | Some _ -> { Description = $"Update agent variable {name}"; Method = "PATCH"; Path = $"/orgs/{org}/agents/variables/{name}"; Body = body; Reason = "value differs" }
        | None -> { Description = $"Create agent variable {name}"; Method = "POST"; Path = $"/orgs/{org}/agents/variables"; Body = body; Reason = "missing" }
]

/// Settings with no documented API - printed so an admin completes them once.
let manualSteps (p: Platform) = [
    if not p.Standards.IsEmpty then
        $"Organization custom instructions: paste github/organization-custom-instructions.md into https://github.com/organizations/{p.Organization}/settings/copilot/custom_instructions"
    let registry = p.Mcp.RegistryUrl |> Option.defaultValue p.Host
    let mode = match p.Mcp.RegistryMode with Mcp.RegistryOnly -> "Registry only" | Mcp.DiscoveryOnly -> "Allow all"
    if not p.Mcp.Servers.IsEmpty then
        $"AI controls > MCP: MCP Registry URL = {registry}, 'Restrict MCP access to registry servers' = {mode}"
    if not p.GitHub.AgentSecrets.IsEmpty then
        let names = String.Join(", ", p.GitHub.AgentSecrets)
        $"Agent secrets (values never live in the definition): gh secret set <NAME> --org {p.Organization} --app agents   for {names}"
    let hosts = p.GitHub.CloudAgent.FirewallAllow @ (if hostIsInternal p then [] else [ Uri(p.Host).Host ]) |> List.distinct
    if not hosts.IsEmpty then
        $"""Cloud agent firewall (Organization settings > Copilot > Cloud agent > Internet access): allow {String.Join(", ", hosts)}"""
    if p.Enterprise <> "" then
        for slug in p.HookPilotTeams do
            $"Pilot ring members: gh api -X PUT /enterprises/{p.Enterprise}/teams/{slug}/memberships/<username>   (one per pilot user)"
]

let apply (steps: Step list) = [ for s in steps -> s, send s.Method s.Path s.Body ]

// ---------------------------------------------------------------------------
// publish: commit each rendered tree into the repository that serves it (the CI deploy job does the same in bash)
// ---------------------------------------------------------------------------

type Published = { Repo: string; Outcome: string; Ok: bool }

let private exec (exe: string) (args: string list) (cwd: string) =
    let psi = ProcessStartInfo(exe, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = cwd)
    for a in args do psi.ArgumentList.Add a
    use p = Process.Start psi
    let out = p.StandardOutput.ReadToEndAsync()
    let err = p.StandardError.ReadToEndAsync()
    p.WaitForExit()
    { Ok = p.ExitCode = 0; Body = out.Result + err.Result }

/// rendered sub-directory -> repository, in publish order. The marketplace goes first: agentic workflows pin
/// plugins to its commit SHAs when they compile.
let publishTargets (p: Platform) = [
    "copilot-plugins", p.MarketplaceRepo
    ".github-private", ".github-private"
    "agentic-workflows", "agentic-workflows"
    "endpoints", "platform-endpoints"
]

/// Replaces the default branch contents of each rendered repository with the render and pushes one commit
/// (nothing when unchanged). Missing repositories are created (internal) only when organization owners deploy.
/// agentic-workflows is compiled with `gh aw compile --strict` first and is not pushed if that fails.
let publish (p: Platform) (rendered: string) : Published list = [
    let p = staged p
    let org = p.Organization
    let work = Path.Combine(Path.GetTempPath(), $"agentp-publish-{Guid.NewGuid():N}")
    Directory.CreateDirectory work |> ignore
    try
        for src, repo in publishTargets p do
            let full = $"{org}/{repo}"
            let source = Path.Combine(rendered, src)
            if not (Directory.Exists source) then
                { Repo = full; Outcome = $"nothing rendered at {source}"; Ok = false }
            else
                let exists = (exec "gh" [ "repo"; "view"; full; "--json"; "name" ] work).Ok
                let created =
                    if exists then Ok false
                    else
                        match p.DeployedBy with
                        | PlatformApp _ -> Error "missing; create it once (an admin), the platform App only publishes"
                        | OrganizationOwners ->
                            let r = exec "gh" [ "repo"; "create"; full; "--internal"; "--description"; "Managed by the agent platform. Do not edit by hand." ] work
                            if r.Ok then Ok true else Error(r.Body.Trim())
                match created with
                | Error why -> { Repo = full; Outcome = why; Ok = false }
                | Ok wasCreated ->
                    let dir = Path.Combine(work, repo.TrimStart '.')
                    let clone = exec "gh" [ "repo"; "clone"; full; dir; "--"; "-q" ] work
                    if not clone.Ok then { Repo = full; Outcome = clone.Body.Trim(); Ok = false }
                    else
                        let git args = exec "git" args dir
                        let branch =
                            let r = git [ "symbolic-ref"; "--short"; "HEAD" ]
                            if r.Ok && r.Body.Trim() <> "" then r.Body.Trim() else "main"
                        for e in Directory.GetFileSystemEntries dir do
                            if Path.GetFileName e <> ".git" then
                                if Directory.Exists e then Directory.Delete(e, true) else File.Delete e
                        let rec copy (from: string) (into: string) =
                            Directory.CreateDirectory into |> ignore
                            for f in Directory.GetFiles from do File.Copy(f, Path.Combine(into, Path.GetFileName f), true)
                            for d in Directory.GetDirectories from do copy d (Path.Combine(into, Path.GetFileName d))
                        copy source dir
                        let compiled =
                            if src = "agentic-workflows" && Directory.Exists(Path.Combine(dir, ".github", "workflows")) then
                                let r = exec "gh" [ "aw"; "compile"; "--strict"; "--approve" ] dir
                                if r.Ok then Ok() else Error(r.Body.Trim())
                            else Ok()
                        match compiled with
                        | Error why -> { Repo = full; Outcome = $"gh aw compile failed, not pushed:\n{why}"; Ok = false }
                        | Ok() ->
                            git [ "add"; "-A" ] |> ignore
                            let commit = git [ "commit"; "-q"; "-m"; $"Agent platform {p.Version} ({p.Rollout})" ]
                            if not commit.Ok then { Repo = full; Outcome = "unchanged"; Ok = true }
                            else
                                let push = git [ "push"; "-q"; "origin"; $"HEAD:{branch}" ]
                                if not push.Ok then { Repo = full; Outcome = push.Body.Trim(); Ok = false }
                                else
                                    git [ "tag"; "-f"; $"v{p.Version}" ] |> ignore
                                    git [ "push"; "-q"; "-f"; "origin"; $"v{p.Version}" ] |> ignore
                                    let how = if wasCreated then "created and published" else "published"
                                    { Repo = full; Outcome = $"{how} {p.Version} ({p.Rollout}) to {branch}"; Ok = true }
        // Organization-wide agentic workflow defaults and policy variables.
        let defaults = Path.Combine(rendered, "agentic-workflows", "aw-defaults.yml")
        if File.Exists defaults then
            let r = exec "gh" [ "aw"; "env"; "update"; Path.GetFullPath defaults; "--scope"; "org"; "--org"; org; "--yes" ] work
            { Repo = $"{org} (org variables)"; Outcome = (if r.Ok then "agentic workflow defaults set" else r.Body.Trim()); Ok = r.Ok }
        let policy = Path.Combine(rendered, "agentic-workflows", "aw-policy.json")
        if File.Exists policy then
            match JsonNode.Parse(File.ReadAllText policy) with
            | :? JsonObject as o ->
                for kv in o do
                    let r = exec "gh" [ "variable"; "set"; kv.Key; "--org"; org; "--body"; string kv.Value ] work
                    { Repo = $"{org} variable {kv.Key}"; Outcome = (if r.Ok then "set" else r.Body.Trim()); Ok = r.Ok }
            | _ -> ()
    finally
        try
            for f in Directory.GetFiles(work, "*", SearchOption.AllDirectories) do File.SetAttributes(f, FileAttributes.Normal)
            Directory.Delete(work, true)
        with _ -> ()
]
