/// Keeping the DSL current with GitHub.
///
/// `known` is everything the typed DSL models today. `extract` reads the documentation and the
/// gh-aw clone and pulls out the same surface as GitHub currently documents it. `diff` says what
/// moved. The docs-drift workflow runs this daily; the self-update agentic workflow hands the
/// diff to an agent that edits the F# and opens a pull request.
///
/// Sources (paths relative to the docs repository root):
///     managed-settings keys + client matrix    content/copilot/reference/enterprise-administrators/enterprise-managed-settings.md
///     overridable keys                          content/copilot/how-tos/administer-copilot/manage-for-enterprise/use-managed-settings/override-settings-for-teams.md
///     sandbox / telemetry sub-keys              (same reference, bullet lists)
///     hook events                               content/copilot/reference/hooks-reference.md ("Hook events" table)
///     plugin manifest + marketplace fields      content/copilot/reference/copilot-cli-reference/cli-plugin-reference.md
///     agent profile frontmatter                 content/copilot/reference/custom-agents-configuration.md
///     policy surfaces                           content/copilot/reference/supported-surfaces-for-policies.md
///     MCP registry surfaces                     content/copilot/reference/enterprise-administrators/mcp-private-registry-enforcement.md
///     ruleset rule types                        src/rest/data/<latest ghec>/repos.json
///     Copilot governance REST endpoints         src/rest/data/<latest ghec>/*.json
///     gh-aw frontmatter fields                  gh-aw docs/src/content/docs/reference/frontmatter.md
module AgentPlatform.Drift

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open AgentPlatform.Json

type Snapshot = Map<string, string list>

/// What the DSL models. Keep in step with Policy, HookEvents, Plugins, Agents, Governance.
/// The self-update workflow edits this list together with the types it describes.
let known: Snapshot =
    Map.ofList [
        "managed-settings.keys",
        [ "autoTier"; "allowedMcpServers"; "deniedMcpServers"; "enabledPlugins"; "extraKnownMarketplaces"; "features.computerUse"; "model"
          "permissions.allow"; "permissions.ask"; "permissions.deny"; "permissions.disableBypassPermissionsMode"; "remoteControl"; "sandbox"
          "strictKnownMarketplaces"; "telemetry" ]
        "managed-settings.overridable",
        [ "allowedMcpServers"; "autoTier"; "deniedMcpServers"; "extraKnownMarketplaces"; "model"; "permissions.allow"; "permissions.ask"
          "permissions.deny"; "permissions.disableBypassPermissionsMode"; "sandbox"; "strictKnownMarketplaces" ]
        "managed-settings.sandbox",
        [ "addCurrentWorkingDirectory"; "allowBypass"; "allowDevToolAccess"; "enabled"; "failIfUnavailable"; "ghAuth"; "gitAuth"
          "sandboxLspServers"; "sandboxMcpServers"; "userPolicy" ]
        "managed-settings.telemetry",
        [ "captureContent"; "enabled"; "endpoint"; "headers"; "lockCaptureContent"; "protocol"; "resourceAttributes"; "serviceName" ]
        "managed-settings.clients", [ "Copilot CLI"; "VS Code"; "GitHub Copilot app"; "Copilot cloud agent"; "JetBrains IDEs" ]
        "hooks.events",
        [ "agentStop"; "errorOccurred"; "notification"; "permissionRequest"; "postToolUse"; "postToolUseFailure"; "preCompact"; "preToolUse"
          "sessionEnd"; "sessionStart"; "subagentStart"; "subagentStop"; "userPromptSubmitted"; "userPromptTransformed" ]
        "plugins.manifest",
        [ "agents"; "author"; "category"; "commands"; "description"; "extensions"; "homepage"; "hooks"; "keywords"; "license"; "lspServers"
          "mcpServers"; "name"; "repository"; "skills"; "tags"; "version" ]
        "plugins.marketplace-entry",
        [ "agents"; "author"; "category"; "commands"; "description"; "homepage"; "hooks"; "keywords"; "license"; "lspServers"; "mcpServers"
          "name"; "repository"; "skills"; "source"; "strict"; "tags"; "version" ]
        "agents.frontmatter",
        [ "description"; "disable-model-invocation"; "infer"; "mcp-servers"; "metadata"; "model"; "name"; "target"; "tools"; "user-invocable" ]
        "rulesets.rule-types",
        [ "branch_name_pattern"; "code_coverage"; "code_quality"; "code_scanning"; "commit_author_email_pattern"; "commit_message_pattern"
          "committer_email_pattern"; "copilot_code_review"; "creation"; "deletion"; "file_extension_restriction"; "file_path_restriction"
          "license_compliance_scanning"; "max_file_path_length"; "max_file_size"; "merge_queue"; "non_fast_forward"; "pull_request"
          "required_deployments"; "required_linear_history"; "required_signatures"; "required_status_checks"; "tag_name_pattern"; "update"
          "workflows" ]
        "mcp-registry.surfaces", [ "Copilot CLI"; "Copilot cloud agent"; "Eclipse"; "JetBrains"; "VS Code"; "Visual Studio"; "Xcode" ]
        "policy.names",
        [ "Configure custom models"; "Configure models"; "Content exclusion"; "Copilot Memory"; "Copilot can search the web"; "Editor preview features"
          "MCP servers in Copilot"; "Restrict MCP access to registry servers"; "Suggestions matching public code" ]
        "gh-aw.builtin-tools",
        [ "agentic-workflows"; "bash"; "cache-memory"; "cli-proxy"; "comment-memory"; "drive-memory"; "edit"; "github"; "jira"; "ledger"
          "linear"; "playwright"; "repo-memory"; "safety-prompt"; "startup-timeout"; "timeout"; "web-fetch"; "web-search"; "work-queue" ]
    ]

// ---------------------------------------------------------------------------
// Extraction
// ---------------------------------------------------------------------------

/// Resolves {% data variables.x.y %} the way the docs build does, from data/variables/*.yml.
let private variables (docsRoot: string) =
    let dir = Path.Combine(docsRoot, "data", "variables")
    if not (Directory.Exists dir) then Map.empty
    else
        [ for f in Directory.GetFiles(dir, "*.yml") do
            let scope = Path.GetFileNameWithoutExtension f
            for line in File.ReadAllLines f do
                let m = Regex.Match(line, @"^([A-Za-z0-9_]+):\s*'?(.*?)'?\s*$")
                if m.Success && not (line.StartsWith " ") then $"{scope}.{m.Groups[1].Value}", m.Groups[2].Value ]
        |> Map.ofList

let private resolve (vars: Map<string, string>) (text: string) =
    Regex.Replace(text, @"\{%\s*data variables\.([A-Za-z0-9_.]+)\s*%\}", fun m -> defaultArg (vars.TryFind m.Groups[1].Value) m.Groups[1].Value)

let private read (root: string) (relative: string) =
    let path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))
    if File.Exists path then Some(File.ReadAllText path) else None

let private section (text: string) (heading: string) =
    let start = text.IndexOf(heading, StringComparison.Ordinal)
    if start < 0 then ""
    else
        let rest = text.Substring(start + heading.Length)
        let next = Regex.Match(rest, @"\n#{1,3} ")
        if next.Success then rest.Substring(0, next.Index) else rest

let private firstColumnCodes (table: string) =
    [ for m in Regex.Matches(table, @"(?m)^\|\s*`([^`]+)`") -> m.Groups[1].Value ] |> List.distinct

let private bulletCodes (text: string) =
    [ for m in Regex.Matches(text, @"(?m)^\*\s+`([A-Za-z]+)`") -> m.Groups[1].Value ] |> List.distinct

let private headerCells (table: string) =
    table.Split('\n')
    |> Array.tryFind (fun l -> l.TrimStart().StartsWith "|")
    |> Option.map (fun l -> l.Split('|') |> Array.map _.Trim() |> Array.filter (fun c -> c <> "") |> List.ofArray)
    |> Option.defaultValue []

/// Reads the latest REST description directory (ghec-YYYY-MM-DD) and returns every operation.
let private restOperations (docsRoot: string) =
    let dir = Path.Combine(docsRoot, "src", "rest", "data")
    if not (Directory.Exists dir) then []
    else
        match Directory.GetDirectories(dir, "ghec-*") |> Array.sort |> Array.tryLast with
        | None -> []
        | Some latest ->
            [ for f in Directory.GetFiles(latest, "*.json") do
                use doc = JsonDocument.Parse(File.ReadAllText f)
                let rec ops (e: JsonElement) = seq {
                    match e.ValueKind with
                    | JsonValueKind.Array -> for x in e.EnumerateArray() do yield! ops x
                    | JsonValueKind.Object ->
                        match tryString [ "requestPath" ] e, tryString [ "verb" ] e with
                        | Some p, Some v -> yield (v.ToUpperInvariant(), p, e.Clone())
                        | _ -> for prop in e.EnumerateObject() do yield! ops prop.Value
                    | _ -> ()
                }
                yield! ops doc.RootElement ]

let private ruleTypes (ops: (string * string * JsonElement) list) =
    ops
    |> List.tryFind (fun (v, p, _) -> v = "POST" && p = "/repos/{owner}/{repo}/rulesets")
    |> Option.map (fun (_, _, op) ->
        let found = Collections.Generic.HashSet<string>()
        let rec walk (e: JsonElement) =
            match e.ValueKind with
            | JsonValueKind.Object ->
                match tryString [ "name" ] e, tryProp [ "enum" ] e with
                | Some "type", Some en when en.ValueKind = JsonValueKind.Array && en.GetArrayLength() = 1 -> found.Add(en[0].GetString()) |> ignore
                | _ -> ()
                for p in e.EnumerateObject() do walk p.Value
            | JsonValueKind.Array -> for x in e.EnumerateArray() do walk x
            | _ -> ()
        match tryProp [ "bodyParameters" ] op with
        | Some b ->
            for param in b.EnumerateArray() do
                if tryString [ "name" ] param = Some "rules" then walk param
        | None -> ()
        found |> Seq.filter (fun t -> not (t = "User" || t = "Team" || t = "IntegrationInstallation" || t = "RepositoryRole")) |> Seq.sort |> List.ofSeq)
    |> Option.defaultValue []

/// The documented surface, as of the clones on disk.
let extract (docsRoot: string) (ghAwRoot: string option) : Snapshot =
    let vars = variables docsRoot
    let doc rel = read docsRoot rel |> Option.map (resolve vars) |> Option.defaultValue ""
    let ref' = doc "content/copilot/reference/enterprise-administrators/enterprise-managed-settings.md"
    let keysTable = section ref' "## Supported keys"
    let overrides = doc "content/copilot/how-tos/administer-copilot/manage-for-enterprise/use-managed-settings/override-settings-for-teams.md"
    let overridable =
        let s = section overrides "## Supported keys"
        let line = s.Split('\n') |> Array.tryFind (fun l -> l.Contains "overridable") |> Option.defaultValue ""
        [ for m in Regex.Matches(line, @"`([A-Za-z.]+)`") -> m.Groups[1].Value ] |> List.filter (fun k -> k <> "overridable") |> List.distinct
    let hooksRef = doc "content/copilot/reference/hooks-reference.md"
    let pluginRef = doc "content/copilot/reference/copilot-cli-reference/cli-plugin-reference.md"
    let agentsRef = doc "content/copilot/reference/custom-agents-configuration.md"
    let surfaces = doc "content/copilot/reference/supported-surfaces-for-policies.md"
    let registry = doc "content/copilot/reference/enterprise-administrators/mcp-private-registry-enforcement.md"
    let ops = restOperations docsRoot
    let copilotEndpoints =
        ops
        |> List.filter (fun (_, p, _) -> Regex.IsMatch(p, @"/copilot/|/agents/|custom-agents|content_exclusion"))
        |> List.map (fun (v, p, _) -> $"{v} {p}")
        |> List.distinct |> List.sort
    let tableRows (t: string) = [ for m in Regex.Matches(t, @"(?m)^\|\s*([^|`][^|]*?)\s*\|") -> m.Groups[1].Value.Trim() ] |> List.filter (fun s -> s <> "---" && s <> "Policy name" && s <> "Surface" && not (s.StartsWith "-"))
    Map.ofList [
        "managed-settings.keys", firstColumnCodes keysTable |> List.sort
        "managed-settings.overridable", overridable |> List.sort
        "managed-settings.sandbox", section ref' "## `sandbox`" |> bulletCodes |> List.sort
        "managed-settings.telemetry", section ref' "## telemetry" |> bulletCodes |> List.sort
        "managed-settings.clients", headerCells keysTable |> List.skip 2
        "hooks.events", section hooksRef "## Hook events" |> firstColumnCodes |> List.sort
        "plugins.manifest",
        (firstColumnCodes (section pluginRef "#### Required field") @ firstColumnCodes (section pluginRef "#### Optional metadata fields") @ firstColumnCodes (section pluginRef "#### Component path fields"))
        |> List.distinct |> List.sort
        "plugins.marketplace-entry", section pluginRef "Plugin entry fields" |> firstColumnCodes |> List.sort
        "agents.frontmatter", section agentsRef "## YAML frontmatter properties" |> firstColumnCodes |> List.sort
        "rulesets.rule-types", ruleTypes ops
        "mcp-registry.surfaces", section registry "## Supported surfaces" |> tableRows |> List.sort
        "policy.names", surfaces |> tableRows |> List.sort
        "rest.copilot-governance", copilotEndpoints
        match ghAwRoot |> Option.bind (fun r -> read r "docs/src/content/docs/reference/frontmatter.md") with
        | Some fm -> "gh-aw.frontmatter", [ for m in Regex.Matches(fm, @"(?m)^### .*\(`([a-z0-9.-]+):?`") -> m.Groups[1].Value ] |> List.distinct |> List.sort
        | None -> ()
        match ghAwRoot |> Option.bind (fun r -> read r "pkg/workflow/mcp_config_validation.go") with
        | Some go ->
            let block = Regex.Match(go, @"builtInToolNames\s*=\s*map\[string\]bool\{(?<body>[^}]*)\}")
            if block.Success then
                "gh-aw.builtin-tools", [ for m in Regex.Matches(block.Groups["body"].Value, "\"([a-z0-9-]+)\"") -> m.Groups[1].Value ] |> List.distinct |> List.sort
        | None -> ()
    ]

// ---------------------------------------------------------------------------
// Diff
// ---------------------------------------------------------------------------

type Change = { Area: string; Added: string list; Removed: string list }

let diff (modeled: Snapshot) (documented: Snapshot) : Change list =
    documented
    |> Map.toList
    |> List.choose (fun (area, docs) ->
        match modeled.TryFind area with
        | None -> None
        | Some ours ->
            let added = docs |> List.filter (fun x -> not (List.contains x ours))
            let removed = ours |> List.filter (fun x -> not (List.contains x docs))
            if added.IsEmpty && removed.IsEmpty then None else Some { Area = area; Added = added; Removed = removed })

let snapshotJson (s: Snapshot) =
    s |> Map.toList |> List.map (fun (k, v) -> k, strs v) |> objOf

let loadSnapshot (path: string) : Snapshot =
    use doc = JsonDocument.Parse(File.ReadAllText path)
    [ for p in doc.RootElement.EnumerateObject() -> p.Name, [ for x in p.Value.EnumerateArray() -> x.GetString() ] ] |> Map.ofList

let report (changes: Change list) =
    if changes.IsEmpty then "No drift: the DSL models everything the documentation describes.\n"
    else
        let lines = [
            "# Agent platform schema drift"
            ""
            "The GitHub documentation describes surface the DSL does not model yet (or no longer describes surface it does)."
            ""
            for c in changes do
                $"## {c.Area}"
                ""
                for a in c.Added do $"- added in docs: `{a}`"
                for r in c.Removed do $"- gone from docs: `{r}`"
                ""
        ]
        String.concat "\n" lines

let changesJson (changes: Change list) =
    changes |> List.map (fun c -> objOf [ "area", str c.Area; "added", strs c.Added; "removed", strs c.Removed ]) |> arr
