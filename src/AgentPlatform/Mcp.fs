/// mcp { } - the organisation's MCP catalog, declared once, enforced everywhere.
///
/// One approved server becomes, at render time:
///
///     allowedMcpServers / deniedMcpServers     managed-settings.json   CLI, VS Code, app, JetBrains (GA, URL/command match)
///     v0.1 MCP registry (static JSON)          AI controls > MCP       VS Code, Visual Studio, JetBrains, Eclipse, Xcode, CLI
///                                                                     ("Registry only": name/ID match, public preview)
///     cloud agent MCP configuration            repo settings / API     Copilot cloud agent (registry not honoured there)
///     agent profile `mcp-servers`              .agent.md               cloud agent, CLI, IDE custom agents
///     plugin mcp.json                          plugins                 CLI, app, cloud agent
///     gh-aw `mcp-servers:`                     workflow frontmatter    agentic workflows
///     SessionConfig.McpServers                 SDK                     Copilot SDK
///
///     docs: content/copilot/how-tos/administer-copilot/manage-mcp-usage/*.md
///           content/copilot/reference/enterprise-administrators/mcp-private-registry-enforcement.md
///           content/copilot/how-tos/copilot-on-github/customize-copilot/configure-mcp-servers.md
module AgentPlatform.Mcp

open System
open System.Text.Json.Nodes
open AgentPlatform.Json

type Transport =
    /// A remote server. `streamableHttp` unless the server only speaks SSE.
    | Remote of url: string * sse: bool
    /// A local stdio server started from a package.
    | Package of registry: PackageRegistry * identifier: string * version: string * runtime: string * args: string list
    /// A local stdio server started by an exact command.
    | Command of command: string * args: string list

and PackageRegistry =
    | NpmPackage
    | PyPiPackage
    | OciImage
    | NuGetPackage

type Secret = { Name: string; Description: string; Header: bool }

type Server = {
    /// Reverse-DNS registry name, e.g. "com.contoso/jira". Also the server ID "Registry only" matches.
    Id: string
    /// Short key used in client configs, e.g. "jira".
    Key: string
    Title: string
    Description: string
    Version: string
    Transport: Transport
    /// Tools to expose. ["*"] means every tool; prefer naming read-only tools.
    Tools: string list
    /// Every tool is read-only (required for Copilot code review to use it).
    ReadOnly: bool
    Secrets: Secret list
    Repository: string option
}

let private blank = {
    Id = ""; Key = ""; Title = ""; Description = ""; Version = "1.0.0"; Transport = Command("", []); Tools = [ "*" ]
    ReadOnly = false; Secrets = []; Repository = None
}

type ServerBuilder() =
    member _.Yield(_: unit) = blank
    [<CustomOperation "id">] member _.Id(s: Server, id: string) = { s with Id = id; Key = (if s.Key = "" then id.Split('/') |> Array.last else s.Key) }
    [<CustomOperation "key">] member _.Key(s: Server, k) = { s with Key = k }
    [<CustomOperation "title">] member _.Title(s: Server, t) = { s with Title = t }
    [<CustomOperation "describedAs">] member _.Describe(s: Server, d) = { s with Description = d }
    [<CustomOperation "version">] member _.Version(s: Server, v) = { s with Version = v }
    [<CustomOperation "remote">] member _.Remote(s: Server, url) = { s with Transport = Remote(url, false) }
    [<CustomOperation "remoteSse">] member _.RemoteSse(s: Server, url) = { s with Transport = Remote(url, true) }
    [<CustomOperation "npm">] member _.Npm(s: Server, package, version) = { s with Transport = Package(NpmPackage, package, version, "npx", [ "-y" ]) }
    [<CustomOperation "pypi">] member _.PyPi(s: Server, package, version) = { s with Transport = Package(PyPiPackage, package, version, "uvx", []) }
    [<CustomOperation "container">] member _.Oci(s: Server, image, version) = { s with Transport = Package(OciImage, image, version, "docker", [ "run"; "-i"; "--rm" ]) }
    [<CustomOperation "command">] member _.Command(s: Server, command, args) = { s with Transport = Command(command, args) }
    [<CustomOperation "tools">] member _.Tools(s: Server, t) = { s with Tools = t }
    [<CustomOperation "readOnly">] member _.ReadOnly(s: Server) = { s with ReadOnly = true }
    [<CustomOperation "needsSecret">] member _.Secret(s: Server, name, description) = { s with Secrets = s.Secrets @ [ { Name = name; Description = description; Header = false } ] }
    [<CustomOperation "needsHeader">] member _.Header(s: Server, name, description) = { s with Secrets = s.Secrets @ [ { Name = name; Description = description; Header = true } ] }
    [<CustomOperation "repository">] member _.Repo(s: Server, url) = { s with Repository = Some url }

let mcpServer = ServerBuilder()

/// Servers nobody may run, whatever else allows them.
type Blocked =
    | BlockUrl of string
    | BlockCommand of string list
    | BlockName of string

type RegistryMode =
    /// Publish the registry for discovery only ("Allow all").
    | DiscoveryOnly
    /// "Registry only": clients refuse servers missing from the registry (name/ID match).
    | RegistryOnly

type Catalog = {
    Servers: Server list
    Blocked: Blocked list
    /// Where the rendered v0.1 registry is served, e.g. https://mcp-registry.contoso.com
    RegistryUrl: string option
    RegistryMode: RegistryMode
    /// Also allow servers the clients ship with (the GitHub MCP server is always allowed).
    AllowBuiltIns: bool
}

let private noCatalog = { Servers = []; Blocked = []; RegistryUrl = None; RegistryMode = DiscoveryOnly; AllowBuiltIns = true }

type CatalogBuilder() =
    member _.Yield(_: unit) = noCatalog
    [<CustomOperation "approve">] member _.Approve(c: Catalog, s: Server) = { c with Servers = c.Servers @ [ s ] }
    [<CustomOperation "approveAll">] member _.ApproveAll(c: Catalog, s: Server list) = { c with Servers = c.Servers @ s }
    [<CustomOperation "block">] member _.Block(c: Catalog, b: Blocked list) = { c with Blocked = c.Blocked @ b }
    [<CustomOperation "registryAt">] member _.Registry(c: Catalog, url: string) = { c with RegistryUrl = Some(url.TrimEnd '/') }
    [<CustomOperation "registryOnly">] member _.Only(c: Catalog) = { c with RegistryMode = RegistryOnly }

let mcp = CatalogBuilder()

let blockUrl u = BlockUrl u
let blockCommand c = BlockCommand c
let blockName n = BlockName n

// ---------------------------------------------------------------------------
// Renderings
// ---------------------------------------------------------------------------

let commandLine (s: Server) =
    match s.Transport with
    | Package(_, id, version, runtime, args) ->
        let pinned =
            match runtime with
            | "npx" -> $"{id}@{version}"
            | "uvx" -> $"{id}=={version}"
            | "docker" -> $"{id}:{version}"
            | _ -> id
        Some(runtime, args @ [ pinned ])
    | Command(cmd, args) -> Some(cmd, args)
    | Remote _ -> None

/// managed-settings.json matcher for one approved server (URL for remote, exact command for local).
let allowMatcher (s: Server) : JsonNode =
    match s.Transport, commandLine s with
    | Remote(url, _), _ -> objOf [ "serverUrl", str url ]
    | _, Some(cmd, args) -> objOf [ "serverCommand", strs (cmd :: args) ]
    | _ -> objOf [ "serverName", str s.Key ]

let denyMatcher =
    function
    | BlockUrl u -> objOf [ "serverUrl", str u ]
    | BlockCommand c -> objOf [ "serverCommand", strs c ]
    | BlockName n -> objOf [ "serverName", str n ]

/// One server in MCP registry v0.1 server.json form (schema 2025-12-11).
let serverJson (s: Server) : JsonNode =
    let envVars = s.Secrets |> List.filter (fun x -> not x.Header)
    let headers = s.Secrets |> List.filter _.Header
    let secretList (xs: Secret list) =
        xs |> List.map (fun x -> objOf [ "name", str x.Name; "description", str x.Description; "isRequired", bool true; "isSecret", bool true ]) |> arr
    obj [
        "$schema", Some(str "https://static.modelcontextprotocol.io/schemas/2025-12-11/server.schema.json")
        "name", Some(str s.Id)
        "title", Some(str s.Title)
        "description", Some(str s.Description)
        "version", Some(str s.Version)
        "repository", s.Repository |> Option.map (fun u -> objOf [ "url", str u; "source", str "github" ])
        match s.Transport with
        | Remote(url, sse) ->
            "remotes",
            Some(arr [ obj [ "type", Some(str (if sse then "sse" else "streamable-http")); "url", Some(str url); "headers", (if headers.IsEmpty then None else Some(secretList headers)) ] ])
        | Package(registry, id, version, runtime, args) ->
            let registryType, baseUrl =
                match registry with
                | NpmPackage -> "npm", "https://registry.npmjs.org"
                | PyPiPackage -> "pypi", "https://pypi.org"
                | OciImage -> "oci", "https://docker.io"
                | NuGetPackage -> "nuget", "https://api.nuget.org"
            "packages",
            Some(
                arr [
                    obj [
                        "registryType", Some(str registryType)
                        "registryBaseUrl", Some(str baseUrl)
                        "identifier", Some(str id)
                        "version", Some(str version)
                        "runtimeHint", Some(str runtime)
                        "transport", Some(objOf [ "type", str "stdio" ])
                        "runtimeArguments", (if args.IsEmpty then None else Some(args |> List.map (fun a -> objOf [ "type", str "positional"; "value", str a ]) |> arr))
                        "environmentVariables", (if envVars.IsEmpty then None else Some(secretList envVars))
                    ]
                ]
            )
        | Command _ -> ()
    ]

let private meta (published: DateTimeOffset) =
    let at () = str (published.ToString "o")
    objOf [
        "io.modelcontextprotocol.registry/official",
        objOf [ "status", str "active"; "statusChangedAt", at (); "publishedAt", at (); "updatedAt", at (); "isLatest", bool true ]
    ]

/// The static v0.1 registry: every file under /v0.1/servers that GitHub's clients request.
/// Server names contain "/", so routes use the URL-encoded name.
let registryFiles (c: Catalog) (published: DateTimeOffset) =
    let entries = c.Servers |> List.filter (fun s -> match s.Transport with Command _ -> false | _ -> true)
    let entry s = objOf [ "server", serverJson s; "_meta", meta published ]
    [
        "v0.1/servers", render (objOf [ "servers", (entries |> List.map entry |> arr); "metadata", objOf [ "count", num entries.Length ] ])
        for s in entries do
            let enc = Uri.EscapeDataString s.Id
            $"v0.1/servers/{enc}/versions/latest", render (entry s)
            $"v0.1/servers/{enc}/versions/{s.Version}", render (entry s)
    ]

/// Copilot cloud agent MCP configuration (repository settings / cloud-agent configuration API).
/// The agent secret a server's secret is stored in: COPILOT_MCP_<KEY>_<NAME>. Cloud agent only exposes
/// Agents secrets whose names start with COPILOT_MCP_ (configure-mcp-servers.md).
let secretName (s: Server) (x: Secret) =
    "COPILOT_MCP_" + (s.Key + "_" + x.Name).ToUpperInvariant().Replace('-', '_').Replace('.', '_')

/// Secrets are referenced as $COPILOT_MCP_* substitutions, the documented syntax.
let cloudAgentConfig (servers: Server list) : JsonNode =
    let env (s: Server) =
        s.Secrets |> List.filter (fun x -> not x.Header) |> List.map (fun x -> x.Name, str ("$" + secretName s x)) |> objOf
    let headers (s: Server) =
        s.Secrets |> List.filter _.Header |> List.map (fun x -> x.Name, str ("$" + secretName s x)) |> objOf
    objOf [
        "mcpServers",
        servers
        |> List.map (fun s ->
            s.Key,
            match s.Transport, commandLine s with
            | Remote(url, sse), _ ->
                obj [ "type", Some(str (if sse then "sse" else "http")); "url", Some(str url); "headers", (if s.Secrets |> List.exists _.Header then Some(headers s) else None); "tools", Some(strs s.Tools) ]
            | _, Some(cmd, args) ->
                obj [ "type", Some(str "local"); "command", Some(str cmd); "args", Some(strs args); "env", (if s.Secrets |> List.exists (fun x -> not x.Header) then Some(env s) else None); "tools", Some(strs s.Tools) ]
            | _ -> objOf [])
        |> objOf
    ]

/// `mcp-servers:` YAML for an .agent.md profile.
let agentProfileYaml (servers: Server list) =
    [
        if not servers.IsEmpty then "mcp-servers:"
        for s in servers do
            $"  {s.Key}:"
            match s.Transport, commandLine s with
            | Remote(url, sse), _ ->
                let kind = if sse then "sse" else "http"
                $"    type: {kind}"
                $"    url: \"{url}\""
            | _, Some(cmd, args) ->
                "    type: local"
                $"    command: \"{cmd}\""
                let quoted = args |> List.map (fun a -> $"\"{a}\"") |> String.concat ", "
                $"    args: [{quoted}]"
            | _ -> ()
            let toolList = s.Tools |> List.map (fun t -> $"\"{t}\"") |> String.concat ", "
            $"    tools: [{toolList}]"
    ]

/// `.mcp.json` for a (legacy-format) plugin.
let pluginMcpJson (servers: Server list) : JsonNode =
    objOf [
        "mcpServers",
        servers
        |> List.map (fun s ->
            s.Key,
            match s.Transport, commandLine s with
            | Remote(url, _), _ -> objOf [ "type", str "http"; "url", str url; "tools", strs s.Tools ]
            | _, Some(cmd, args) -> objOf [ "type", str "local"; "command", str cmd; "args", strs args; "tools", strs s.Tools ]
            | _ -> objOf [])
        |> objOf
    ]

let validate (c: Catalog) = [
    for s in c.Servers do
        if not (s.Id.Contains '/') then $"mcp {s.Key}: id must be reverse-DNS with a namespace, e.g. com.example/{s.Key}."
        match s.Transport with
        | Remote(url, _) when not (url.StartsWith "https://") -> $"mcp {s.Id}: remote servers must use https."
        | Package(_, _, version, _, _) when version = "" || version = "latest" -> $"mcp {s.Id}: pin a package version; 'latest' defeats exact-command allowlisting."
        | Command(cmd, _) when cmd = "" -> $"mcp {s.Id}: needs a remote URL, a package or a command."
        | _ -> ()
        if s.Tools = [ "*" ] && not s.ReadOnly then
            $"mcp {s.Id}: exposes every tool; name the tools (cloud agent runs them without asking)."
    let dupes = c.Servers |> List.countBy _.Key |> List.filter (fun (_, n) -> n > 1)
    for k, _ in dupes do $"mcp: two servers share the key '{k}'."
    match c.RegistryMode, c.RegistryUrl with
    | RegistryOnly, None -> "mcp: registryOnly needs registryAt <url> - clients must be able to fetch the registry."
    | _ -> ()
]
