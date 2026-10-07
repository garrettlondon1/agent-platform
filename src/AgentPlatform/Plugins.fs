/// skill { } and plugin { } - how everything reaches every machine.
///
/// A plugin bundles skills, custom agents, standards, MCP servers and hooks. Managed settings
/// name it in `enabledPlugins` and only allow its marketplace in `strictKnownMarketplaces`, so it
/// is installed for every user and cannot be removed locally. gh-aw installs the same plugin
/// with `plugins: [owner/repo/plugins/<name>@<ref>]`.
///
///     docs: content/copilot/reference/copilot-cli-reference/cli-plugin-reference.md
///           content/copilot/concepts/agents/about-plugins.md
module AgentPlatform.Plugins

open AgentPlatform.Json
open AgentPlatform.Standards

type Skill = {
    Name: string
    Description: string
    Body: string
    /// Extra files shipped next to SKILL.md: (relative path, content).
    Files: (string * string) list
}

let private blankSkill = { Name = ""; Description = ""; Body = ""; Files = [] }

type SkillBuilder() =
    member _.Yield(_: unit) = blankSkill
    [<CustomOperation "named">] member _.Named(s: Skill, n) = { s with Name = n }
    /// When the agent should load this skill. This is what the model reads to decide.
    [<CustomOperation "useWhen">] member _.When(s: Skill, d) = { s with Description = d }
    [<CustomOperation "steps">] member _.Steps(s: Skill, b) = { s with Body = b }
    [<CustomOperation "withFile">] member _.File(s: Skill, path, content) = { s with Files = s.Files @ [ path, content ] }

let skill = SkillBuilder()

let skillMarkdown (s: Skill) =
    let body = s.Body.Split('\n') |> Array.map _.Trim() |> String.concat "\n" |> _.Trim()
    $"---\nname: {s.Name}\ndescription: {s.Description}\n---\n\n{body}\n"

type Plugin = {
    Name: string
    Version: string
    Description: string
    Category: string
    Skills: Skill list
    /// Agent names (from the platform's agents) shipped in this plugin.
    Agents: string list
    /// Standard names shipped as skills in this plugin.
    Standards: string list
    /// MCP server keys (from the platform's catalog) this plugin activates.
    Mcp: string list
    /// Carries the platform's central hooks (rules + capture). Install it everywhere.
    CarriesHooks: bool
}

let private blankPlugin = {
    Name = ""; Version = "1.0.0"; Description = ""; Category = "engineering"
    Skills = []; Agents = []; Standards = []; Mcp = []; CarriesHooks = false
}

type PluginBuilder() =
    member _.Yield(_: unit) = blankPlugin
    [<CustomOperation "named">] member _.Named(p: Plugin, n) = { p with Name = n }
    [<CustomOperation "version">] member _.Version(p: Plugin, v) = { p with Version = v }
    [<CustomOperation "describedAs">] member _.Describe(p: Plugin, d) = { p with Description = d }
    [<CustomOperation "category">] member _.Category(p: Plugin, c) = { p with Category = c }
    [<CustomOperation "skills">] member _.Skills(p: Plugin, s) = { p with Skills = p.Skills @ s }
    [<CustomOperation "agents">] member _.Agents(p: Plugin, a) = { p with Agents = p.Agents @ a }
    [<CustomOperation "standards">] member _.Standards(p: Plugin, s) = { p with Standards = p.Standards @ s }
    [<CustomOperation "mcpServers">] member _.Mcp(p: Plugin, m) = { p with Mcp = p.Mcp @ m }
    [<CustomOperation "carriesHooks">] member _.Hooks(p: Plugin) = { p with CarriesHooks = true }

let plugin = PluginBuilder()

type Marketplace = {
    Name: string
    /// OWNER/REPO the marketplace is published from.
    Repo: string
    Ref: string
    Owner: string
    OwnerEmail: string
    Description: string
    Plugins: Plugin list
}

/// `name@marketplace`, the key enabledPlugins uses.
let pluginId (m: Marketplace) (p: Plugin) = $"{p.Name}@{m.Name}"

let pluginJson (p: Plugin) (hasMcp: bool) =
    obj [
        "name", Some(str p.Name)
        "version", Some(str p.Version)
        "description", Some(str p.Description)
        "category", Some(str p.Category)
        "author", Some(objOf [ "name", str "Agent platform" ])
        "agents", (if p.Agents.IsEmpty then None else Some(str "agents/"))
        "skills", (if p.Skills.IsEmpty && p.Standards.IsEmpty then None else Some(str "skills/"))
        "hooks", (if p.CarriesHooks then Some(str "hooks.json") else None)
        "mcpServers", (if hasMcp then Some(str ".mcp.json") else None)
    ]

let marketplaceJson (m: Marketplace) =
    objOf [
        "name", str m.Name
        "owner", objOf [ "name", str m.Owner; "email", str m.OwnerEmail ]
        "metadata", objOf [ "description", str m.Description ]
        "plugins",
        m.Plugins
        |> List.map (fun p ->
            objOf [
                "name", str p.Name
                "source", str $"./plugins/{p.Name}"
                "description", str p.Description
                "version", str p.Version
                "category", str p.Category
            ])
        |> arr
    ]

/// A standard shipped inside a plugin becomes a skill the agent loads whenever it is relevant,
/// so the standard also reaches clients that do not read repository instruction files.
let standardAsSkill (s: Standard) =
    let scope = if s.AppliesTo.IsEmpty then "any code" else "files matching " + System.String.Join(", ", s.AppliesTo)
    let rules = s.Rules |> List.map (fun r -> $"- {r}") |> String.concat "\n"
    {
        Name = $"standard-{s.Name}"
        Description = $"{s.Title}. Use whenever writing, changing or reviewing {scope}."
        Body = $"Follow these {s.Title} rules for {scope}:\n\n{rules}"
        Files = []
    }

let validate (m: Marketplace) = [
    if m.Repo = "" || not (m.Repo.Contains '/') then "marketplace: set repo to OWNER/REPO."
    for p in m.Plugins do
        if p.Name = "" then "plugin: every plugin needs a name."
        elif p.Name.Length > 64 || p.Name |> Seq.exists (fun c -> not (System.Char.IsLower c || System.Char.IsDigit c || c = '-')) then
            $"plugin {p.Name}: kebab-case, at most 64 characters."
        if p.Description.Length > 1024 then $"plugin {p.Name}: description longer than 1024 characters."
        for s in p.Skills do
            if s.Description = "" then $"plugin {p.Name}: skill {s.Name} needs useWhen - it is how the agent decides to load it."
    for n, c in m.Plugins |> List.countBy _.Name do
        if c > 1 then $"plugin {n}: defined twice."
]
