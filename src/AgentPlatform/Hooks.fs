/// hooks { } - every hook in the organisation, defined once, as rules.
///
/// Admins write rules; the platform decides where they run. One rule engine serves:
///
///     in-process   Copilot SDK sessions        SessionHooks.*            ~0 ms
///     curl         CLI / VS Code / cloud agent command hooks -> service  fail-closed, nothing installed
///     http         CLI / Copilot app / cloud agent  HTTP hook -> daemon  no process at all
///
/// and the platform renders the hook files that reach each surface:
///
///     policy.d/*.json          machine-wide, cannot be disabled (CLI)          enforcement + audit
///     plugin hooks.json        installed everywhere by enabledPlugins           enforcement + capture
///     .github/hooks/*.json     every repo; the only hooks cloud agent loads     enforcement + capture
///
/// Enforcement rides on command hooks because they FAIL CLOSED: if the daemon cannot be reached
/// the client exits 2 and preToolUse denies. HTTP hooks fail open, so they carry capture only -
/// a collector outage never stops a developer. (hooks-reference: "Command vs HTTP fail behavior")
module AgentPlatform.Hooks

open System
open System.Text.RegularExpressions
open AgentPlatform.HookEvents

// ---------------------------------------------------------------------------
// The words
// ---------------------------------------------------------------------------

/// What a rule looks at.
type Condition =
    /// Runtime tool name: bash, powershell, edit, create, view, grep, glob, web_fetch, task, ...
    | ToolIs of string list
    /// Regex over the tool's arguments (raw JSON text).
    | ArgsMatch of string
    /// A shell command that starts with this text (bash or powershell tools).
    | CommandStartsWith of string
    /// A file path argument matching this glob (edit/create/view tools).
    | PathMatches of string
    /// The prompt contains this text (userPromptSubmitted).
    | PromptContains of string
    | OnSurface of Surface list
    | All of Condition list
    | AnyOf of Condition list
    | Not of Condition
    | Always

/// What a rule does when its condition holds.
type Action =
    | Block of reason: string
    | RequireApproval of reason: string
    | Approve
    | AddContext of string
    | KeepGoing of prompt: string
    | Record
    /// Observe rollout: evaluate the inner action and record what it WOULD have done, but never act on it.
    | DryRun of Action

/// What happens to a deciding rule when the decision cannot be reached in time.
type Failure =
    | FailClosed
    | FailOpen

type Rule = {
    Name: string
    Events: HookEvent list
    When: Condition
    Then: Action
    OnFailure: Failure
    Description: string
}

let private shellTools = [ "bash"; "powershell" ]
let private pathTools = [ "edit"; "create"; "view"; "str_replace_editor"; "apply_patch" ]

let tool names = ToolIs names
let shellCommand prefix = All [ ToolIs shellTools; CommandStartsWith prefix ]
let writingTo glob = All [ ToolIs [ "edit"; "create"; "str_replace_editor"; "apply_patch" ]; PathMatches glob ]
let reading glob = All [ ToolIs [ "view"; "grep"; "glob" ]; PathMatches glob ]
let fetching hostRegex = All [ ToolIs [ "web_fetch" ]; ArgsMatch hostRegex ]
let argsMatching regex = ArgsMatch regex
let promptContains text = PromptContains text
let onSurface s = OnSurface s
let anyOf cs = AnyOf cs
let allOf cs = All cs
let not' c = Not c

let block reason = Block reason
let requireApproval reason = RequireApproval reason
let addContext text = AddContext text
let keepGoing prompt = KeepGoing prompt

/// A rule that decides before a tool runs.
let beforeTool name condition action =
    { Name = name; Events = [ PreToolUse ]; When = condition; Then = action; OnFailure = FailClosed; Description = "" }

/// A rule that adds guidance after a tool succeeds.
let afterTool name condition action =
    { Name = name; Events = [ PostToolUse ]; When = condition; Then = action; OnFailure = FailOpen; Description = "" }

/// A rule on any events.
let on events name condition action =
    { Name = name; Events = events; When = condition; Then = action; OnFailure = FailOpen; Description = "" }

let failOpen r = { r with OnFailure = FailOpen }
let describedAs text r = { r with Description = text }

/// How observation-only events reach the platform service.
type CaptureTransport =
    /// The runtime POSTs the payload itself: no process starts. The Copilot CLI refuses HTTP hook
    /// URLs that resolve to loopback, private or link-local addresses, so the platform host must
    /// resolve to a public address (observed in CLI 1.0.92; not stated in the hooks reference).
    | HttpHooks
    /// A fail-open curl command hook per event. Works with internal-network hosts.
    | CurlCommands

/// Where captured events go and which events are captured.
type CaptureSettings = { Events: HookEvent list; IncludeContent: bool }

type HookSet = {
    Rules: Rule list
    Capture: CaptureSettings option
    /// Seconds a command hook may take before the runtime gives up (timeouts always fail open).
    TimeoutSec: int
    CaptureVia: CaptureTransport
}

let private none = { Rules = []; Capture = None; TimeoutSec = 5; CaptureVia = HttpHooks }

type HooksBuilder() =
    member _.Yield(_: unit) = none
    [<CustomOperation "rule">] member _.Rule(h: HookSet, r: Rule) = { h with Rules = h.Rules @ [ r ] }
    [<CustomOperation "rules">] member _.Rules(h: HookSet, rs: Rule list) = { h with Rules = h.Rules @ rs }
    /// Record every lifecycle event of every session, everywhere - metadata only (event, tool, decision).
    /// Prompts, tool arguments and results stay out unless you add `includingContent`.
    [<CustomOperation "captureEverything">]
    member _.Capture(h: HookSet) =
        { h with Capture = Some { Events = [ SessionStart; UserPromptSubmitted; PreToolUse; PostToolUse; PostToolUseFailure; SubagentStart; SubagentStop; AgentStop; ErrorOccurred; PreCompact; SessionEnd ]; IncludeContent = false } }
    [<CustomOperation "captureMetadataOnly">]
    member _.CaptureMeta(h: HookSet) =
        { h with Capture = Some { Events = [ SessionStart; PostToolUse; PostToolUseFailure; AgentStop; ErrorOccurred; SessionEnd ]; IncludeContent = false } }
    /// Also store prompts, tool arguments and tool results. These can contain code, secrets and personal
    /// data: get privacy/legal sign-off and set a retention period first.
    [<CustomOperation "includingContent">]
    member _.WithContent(h: HookSet) =
        { h with Capture = h.Capture |> Option.map (fun c -> { c with IncludeContent = true }) }
    [<CustomOperation "timeoutSeconds">] member _.Timeout(h: HookSet, s) = { h with TimeoutSec = s }
    /// The platform host is on an internal network: capture with curl command hooks instead of HTTP hooks.
    [<CustomOperation "captureOverCurl">] member _.OverCurl(h: HookSet) = { h with CaptureVia = CurlCommands }

let hooks = HooksBuilder()

// ---------------------------------------------------------------------------
// The engine: one evaluation, used by every transport
// ---------------------------------------------------------------------------

let private globToRegex (glob: string) =
    let escaped = Regex.Escape(glob.Replace('\\', '/'))
    let pattern =
        escaped.Replace(@"\*\*/", "(.*/)?").Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*").Replace(@"\?", ".")
    Regex("(^|/)" + pattern + "$", RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant)

let private globCache = Collections.Concurrent.ConcurrentDictionary<string, Regex>()
let private regexCache = Collections.Concurrent.ConcurrentDictionary<string, Regex>()

let private globMatch (glob: string) (path: string) =
    globCache.GetOrAdd(glob, globToRegex).IsMatch(path.Replace('\\', '/'))

let private regexMatch (pattern: string) (text: string) =
    regexCache.GetOrAdd(pattern, fun p -> Regex(p, RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant)).IsMatch text

/// Files named in an apply_patch body: `*** Add File: p`, `*** Update File: p`, `*** Delete File: p`, `*** Move to: p`.
let private patchPaths (text: string) =
    [ for m in Regex.Matches(text, @"^\*\*\* (?:Add File|Update File|Delete File|Move to): *(.+?)\s*$", RegexOptions.Multiline) -> m.Groups[1].Value ]

/// Strings a tool call refers to as file paths.
let private pathsIn (call: HookCall) =
    match call.ToolArgs with
    | None -> []
    | Some args ->
        let fromJson =
            try
                use doc = Text.Json.JsonDocument.Parse args
                [ for key in [ "path"; "file_path"; "filePath"; "file"; "pattern"; "target" ] do
                    match doc.RootElement.ValueKind, Json.tryString [ key ] doc.RootElement with
                    | Text.Json.JsonValueKind.Object, Some p -> p
                    | _ -> ()
                  // apply_patch may wrap its patch in a JSON field.
                  for key in [ "input"; "patch" ] do
                    match doc.RootElement.ValueKind, Json.tryString [ key ] doc.RootElement with
                    | Text.Json.JsonValueKind.Object, Some p -> yield! patchPaths p
                    | _ -> () ]
                |> Some
            with _ -> None
        match fromJson with
        | Some paths -> paths
        // apply_patch sends the raw patch: match the files it touches, not the patch text.
        | None -> match patchPaths args with [] -> [ args ] | paths -> paths

let private commandIn (call: HookCall) =
    match call.ToolArgs with
    | None -> ""
    | Some args ->
        try
            use doc = Text.Json.JsonDocument.Parse args
            Json.tryString [ "command" ] doc.RootElement |> Option.defaultValue args
        with _ -> args

let rec holds (call: HookCall) (c: Condition) =
    match c with
    | Always -> true
    | ToolIs names -> call.ToolName |> Option.exists (fun t -> names |> List.exists (fun n -> String.Equals(n, t, StringComparison.OrdinalIgnoreCase)))
    | ArgsMatch pattern -> call.ToolArgs |> Option.exists (regexMatch pattern)
    | CommandStartsWith prefix ->
        let cmd = (commandIn call).TrimStart()
        // Check every segment of a compound command, so `cd x && git push -f` is still caught.
        cmd.Split([| "&&"; "||"; ";"; "|"; "\n" |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.exists (fun seg -> seg.Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
    | PathMatches glob -> pathsIn call |> List.exists (globMatch glob)
    | PromptContains text -> call.Prompt |> Option.exists (fun p -> p.Contains(text, StringComparison.OrdinalIgnoreCase))
    | OnSurface surfaces -> List.contains call.Surface surfaces
    | All cs -> cs |> List.forall (holds call)
    | AnyOf cs -> cs |> List.exists (holds call)
    | Not inner -> not (holds call inner)

/// `WouldHave` lists what dry-run (observe) rules would have done: "[rule] would deny: reason".
type Verdict = { Decision: Decision; Matched: Rule list; WouldHave: string list }

/// Evaluates every rule for an event. Deny beats ask beats allow, exactly as the runtime
/// combines several preToolUse hooks; additional context from every matching rule is kept.
let evaluate (rules: Rule list) (call: HookCall) : Verdict =
    let matched = rules |> List.filter (fun r -> List.contains call.Event r.Events && holds call r.When)
    let pick f = matched |> List.tryPick (fun r -> f r.Then |> Option.map (fun x -> r, x))
    let deny = pick (function Block why -> Some why | _ -> None)
    let ask = pick (function RequireApproval why -> Some why | _ -> None)
    let allow = pick (function Approve -> Some() | _ -> None)
    let keep = pick (function KeepGoing p -> Some p | _ -> None)
    let context =
        matched |> List.choose (fun r -> match r.Then with AddContext t -> Some t | _ -> None)
        |> function [] -> None | xs -> Some(String.Join("\n\n", xs))
    let decision =
        match deny, ask, allow with
        | Some(r, why), _, _ -> { proceed with Permission = Some Deny; Reason = Some $"[{r.Name}] {why}" }
        | None, Some(r, why), _ ->
            // Nobody can answer "ask" in a cloud agent job; the runtime would deny anyway - say why.
            if call.Surface = CloudAgent then { proceed with Permission = Some Deny; Reason = Some $"[{r.Name}] {why} (needs approval; no user in cloud agent)" }
            else { proceed with Permission = Some Ask; Reason = Some $"[{r.Name}] {why}" }
        | None, None, Some(r, ()) -> { proceed with Permission = Some Allow; Reason = Some $"[{r.Name}] approved by policy" }
        | None, None, None -> proceed
    let decision =
        { decision with
            AdditionalContext = context
            Block = keep |> Option.map snd }
    let wouldHave =
        matched
        |> List.choose (fun r ->
            match r.Then with
            | DryRun(Block why) -> Some $"[{r.Name}] would deny: {why}"
            | DryRun(RequireApproval why) -> Some $"[{r.Name}] would ask: {why}"
            | DryRun(KeepGoing _) -> Some $"[{r.Name}] would keep the agent going"
            | DryRun a -> Some $"[{r.Name}] would {a}"
            | _ -> None)
    { Decision = decision; Matched = matched; WouldHave = wouldHave }

/// Events the rules need to see, so the rendered hook files only fire where something listens.
let eventsFor (h: HookSet) =
    (h.Rules |> List.collect _.Events) @ (h.Capture |> Option.map _.Events |> Option.defaultValue [])
    |> List.distinct

/// Events that carry deciding rules (rendered as fail-closed command hooks).
let decidingEvents (h: HookSet) =
    h.Rules
    |> List.filter (fun r -> match r.Then with Record | AddContext _ | DryRun _ -> false | _ -> true)
    |> List.collect _.Events
    |> List.distinct

/// A matcher regex the runtime applies before spawning anything, so the client only starts
/// for tools some rule actually cares about.
let matcherFor (h: HookSet) (event: HookEvent) =
    let rules = h.Rules |> List.filter (fun r -> List.contains event r.Events)
    let rec tools c =
        match c with
        | ToolIs names -> Some names
        | All cs -> cs |> List.tryPick tools
        | _ -> None
    let names = rules |> List.map (fun r -> tools r.When)
    if names.IsEmpty || names |> List.exists Option.isNone then None
    else
        names |> List.choose id |> List.concat |> List.distinct
        |> List.map Regex.Escape
        |> String.concat "|"
        |> Some
