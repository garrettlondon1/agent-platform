/// standards { } - coding standards and instructions, written once, delivered to every agent.
///
/// A standard is a named set of rules, optionally scoped to files. It is rendered to:
///
///     .github/copilot-instructions.md            repo-wide        CLI, VS Code, VS, JetBrains, cloud agent, code review
///     .github/instructions/<name>.instructions.md  applyTo-scoped  CLI, VS Code, cloud agent, code review
///     AGENTS.md                                  agent-agnostic   CLI, cloud agent, Codex/Claude engines in gh-aw
///     plugin rules + skills                      enforced via enabledPlugins on every machine
///     organization custom instructions (text)    org settings     Chat on github.com, code review, cloud agent
///     SessionConfig.SystemMessage                SDK
///
///     docs: content/copilot/how-tos/copilot-cli/customize-copilot/add-custom-instructions.md
///           content/copilot/tutorials/customize-code-review.md
module AgentPlatform.Standards

open System

type Standard = {
    Name: string
    Title: string
    /// Glob(s) the standard applies to; empty means everything.
    AppliesTo: string list
    Rules: string list
    /// Extra guidance shown only to the code reviewer.
    ReviewFocus: string list
}

let private blank = { Name = ""; Title = ""; AppliesTo = []; Rules = []; ReviewFocus = [] }

type StandardBuilder() =
    member _.Yield(_: unit) = blank
    [<CustomOperation "named">] member _.Named(s: Standard, n: string) = { s with Name = n; Title = (if s.Title = "" then n else s.Title) }
    [<CustomOperation "title">] member _.Title(s: Standard, t) = { s with Title = t }
    [<CustomOperation "appliesTo">] member _.Applies(s: Standard, globs) = { s with AppliesTo = globs }
    [<CustomOperation "rule">] member _.Rule(s: Standard, r: string) = { s with Rules = s.Rules @ [ r ] }
    [<CustomOperation "rules">] member _.Rules(s: Standard, rs: string list) = { s with Rules = s.Rules @ rs }
    [<CustomOperation "whenReviewing">] member _.Review(s: Standard, rs: string list) = { s with ReviewFocus = s.ReviewFocus @ rs }

let standard = StandardBuilder()

let private bullets (xs: string list) = xs |> List.map (fun x -> $"- {x}") |> String.concat "\n"

/// .github/instructions/<name>.instructions.md
let instructionsFile (s: Standard) =
    let header =
        match s.AppliesTo with
        | [] -> ""
        | globs -> "---\napplyTo: \"" + String.Join(",", globs) + "\"\n---\n\n"
    let review =
        if s.ReviewFocus.IsEmpty then "" else $"\n\n## When reviewing\n\n{bullets s.ReviewFocus}"
    $"{header}# {s.Title}\n\n{bullets s.Rules}{review}\n"

/// .github/copilot-instructions.md - only the standards that apply everywhere.
let repositoryInstructions (orgName: string) (standards: Standard list) =
    let global' = standards |> List.filter (fun s -> s.AppliesTo.IsEmpty)
    let scoped = standards |> List.filter (fun s -> not s.AppliesTo.IsEmpty)
    let sections = global' |> List.map (fun s -> $"## {s.Title}\n\n{bullets s.Rules}") |> String.concat "\n\n"
    let pointer =
        if scoped.IsEmpty then ""
        else "\n\n## File-specific standards\n\n" + (scoped |> List.map (fun s -> $"- {s.Title}: `{String.Join(',', s.AppliesTo)}` (see `.github/instructions/{s.Name}.instructions.md`)") |> String.concat "\n")
    $"# {orgName} engineering standards\n\nThese instructions are managed centrally by the agent platform. Change them in the platform definition, not here.\n\n{sections}{pointer}\n"

/// AGENTS.md - the same standards for any agent that reads the open format.
let agentsMd (orgName: string) (standards: Standard list) =
    let sections =
        standards
        |> List.map (fun s ->
            let scope = if s.AppliesTo.IsEmpty then "" else $"\n\nApplies to: `{String.Join(',', s.AppliesTo)}`"
            $"## {s.Title}{scope}\n\n{bullets s.Rules}")
        |> String.concat "\n\n"
    $"# AGENTS.md\n\n{orgName} standards for every coding agent working in this repository. Managed centrally.\n\n{sections}\n"

/// Organization custom instructions text (paste into, or set through, org Copilot settings).
let organizationInstructions (standards: Standard list) =
    standards
    |> List.map (fun s ->
        let scope = if s.AppliesTo.IsEmpty then "" else $" (for {String.Join(',', s.AppliesTo)})"
        $"{s.Title}{scope}:\n{bullets s.Rules}")
    |> String.concat "\n\n"

/// A system-message section for SDK sessions.
let systemMessage (standards: Standard list) =
    standards
    |> List.map (fun s ->
        let scope = if s.AppliesTo.IsEmpty then "" else $" (files matching {String.Join(',', s.AppliesTo)})"
        $"{s.Title}{scope}:\n{bullets s.Rules}")
    |> String.concat "\n\n"

let validate (standards: Standard list) = [
    for s in standards do
        if s.Name = "" then "standard: every standard needs a name."
        elif s.Name |> Seq.exists (fun c -> not (Char.IsLetterOrDigit c || c = '-')) then $"standard {s.Name}: use lowercase letters, digits and hyphens."
        if s.Rules.IsEmpty then $"standard {s.Name}: has no rules."
    for n, c in standards |> List.countBy _.Name do
        if c > 1 then $"standard {n}: defined twice."
]
