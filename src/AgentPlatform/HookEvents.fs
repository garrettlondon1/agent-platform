/// The hook event payloads, typed once and shared by every transport.
///
///     docs: content/copilot/reference/hooks-reference.md
///
/// The same rule runs whether the event arrived:
///     in-process   from a Copilot SDK session            (no transport at all)
///     over HTTP    from the CLI, the Copilot app, cloud agent  (no process spawn)
///     over curl    from a command hook (exec curl.exe / bash curl) when the call must fail closed
///
/// Payloads come in two casings: camelCase (CLI, cloud agent, SDK) and the VS Code-compatible
/// snake_case form selected by a PascalCase event name. Both are read here.
module AgentPlatform.HookEvents

open System
open System.Text.Json
open AgentPlatform.Json

type HookEvent =
    | SessionStart
    | SessionEnd
    | UserPromptSubmitted
    | UserPromptTransformed
    | PreToolUse
    | PostToolUse
    | PostToolUseFailure
    | PermissionRequest
    | SubagentStart
    | SubagentStop
    | AgentStop
    | ErrorOccurred
    | PreCompact
    | Notification

    member e.Name =
        match e with
        | SessionStart -> "sessionStart"
        | SessionEnd -> "sessionEnd"
        | UserPromptSubmitted -> "userPromptSubmitted"
        | UserPromptTransformed -> "userPromptTransformed"
        | PreToolUse -> "preToolUse"
        | PostToolUse -> "postToolUse"
        | PostToolUseFailure -> "postToolUseFailure"
        | PermissionRequest -> "permissionRequest"
        | SubagentStart -> "subagentStart"
        | SubagentStop -> "subagentStop"
        | AgentStop -> "agentStop"
        | ErrorOccurred -> "errorOccurred"
        | PreCompact -> "preCompact"
        | Notification -> "notification"

    /// Whether the event's output can change what the agent does (vs. observation only).
    member e.CanDecide =
        match e with
        | PreToolUse | PermissionRequest | AgentStop | SubagentStop | PostToolUse | UserPromptTransformed -> true
        | _ -> false

    /// Fires under Copilot cloud agent (hooks-reference "Hook events" table).
    member e.FiresInCloudAgent =
        match e with
        | Notification | PermissionRequest -> false
        | _ -> true

let allEvents = [
    SessionStart; SessionEnd; UserPromptSubmitted; UserPromptTransformed; PreToolUse; PostToolUse
    PostToolUseFailure; PermissionRequest; SubagentStart; SubagentStop; AgentStop; ErrorOccurred; PreCompact; Notification
]

/// The VS Code-compatible (PascalCase) names from hooks-reference.md where they differ from ours.
let private pascalAliases = Map.ofList [ "UserPromptSubmit", UserPromptSubmitted; "Stop", AgentStop ]

let tryParseEvent (name: string) =
    let n = name.Trim()
    match pascalAliases.TryFind n with
    | Some e -> Some e
    | None -> allEvents |> List.tryFind (fun e -> String.Equals(e.Name, n, StringComparison.OrdinalIgnoreCase))

/// Where the session is running, as far as a hook can tell.
type Surface =
    | Cli
    | VsCode
    | VisualStudio
    | JetBrains
    | CopilotApp
    | CloudAgent
    | Sdk
    | AgenticWorkflow
    | UnknownSurface

    member s.Text =
        match s with
        | Cli -> "cli"
        | VsCode -> "vscode"
        | VisualStudio -> "visual-studio"
        | JetBrains -> "jetbrains"
        | CopilotApp -> "copilot-app"
        | CloudAgent -> "cloud-agent"
        | Sdk -> "sdk"
        | AgenticWorkflow -> "agentic-workflow"
        | UnknownSurface -> "unknown"

/// One hook invocation, normalised. `Raw` keeps the original payload for anything not lifted out.
type HookCall = {
    Event: HookEvent
    SessionId: string
    Timestamp: DateTimeOffset
    Cwd: string
    Surface: Surface
    /// Which governance layer delivered it: policy-hook, plugin:<name>, repo-hook, sdk:<agent>, ...
    Source: string
    ToolName: string option
    ToolArgs: string option
    ToolResult: string option
    Prompt: string option
    Reason: string option
    Error: string option
    AgentName: string option
    VsCodeShape: bool
    Raw: JsonElement
}

let private firstString (paths: string list list) (e: JsonElement) = paths |> List.tryPick (fun p -> tryString p e)

let private rawText (paths: string list list) (e: JsonElement) =
    paths
    |> List.tryPick (fun p -> tryProp p e)
    |> Option.bind (fun v ->
        match v.ValueKind with
        | JsonValueKind.Null | JsonValueKind.Undefined -> None
        | JsonValueKind.String -> Some(v.GetString())
        | _ -> Some(v.GetRawText()))

let private surfaceOf (source: string) (cwd: string) (vscodeShape: bool) (e: JsonElement) =
    match source with
    | s when s.StartsWith "sdk:" -> Sdk
    | s when s.StartsWith "gh-aw" -> AgenticWorkflow
    | _ ->
        match firstString [ [ "surface" ] ] e with
        | Some "vscode" -> VsCode
        | Some "visual-studio" -> VisualStudio
        | Some "jetbrains" -> JetBrains
        | Some "copilot-app" -> CopilotApp
        | _ ->
            if vscodeShape then VsCode
            elif cwd = "/workspace" || cwd.StartsWith "/workspace/" then CloudAgent
            elif not (String.IsNullOrEmpty(Environment.GetEnvironmentVariable "GITHUB_AW")) then AgenticWorkflow
            else Cli

/// Reads a hook payload in either casing. `eventHint` is used when the payload does not name
/// its event (camelCase payloads never do; the event is the config key that fired).
let parse (eventHint: HookEvent option) (source: string) (payload: JsonElement) : HookCall option =
    let vscodeShape = (tryString [ "hook_event_name" ] payload).IsSome
    let event =
        tryString [ "hook_event_name" ] payload
        |> Option.bind tryParseEvent
        |> Option.orElse eventHint
    match event with
    | None -> None
    | Some ev ->
        let cwd = firstString [ [ "cwd" ]; [ "workingDirectory" ] ] payload |> Option.defaultValue ""
        let stamp =
            match tryProp [ "timestamp" ] payload with
            | Some t when t.ValueKind = JsonValueKind.Number -> DateTimeOffset.FromUnixTimeMilliseconds(t.GetInt64())
            | Some t when t.ValueKind = JsonValueKind.String ->
                match DateTimeOffset.TryParse(t.GetString()) with
                | true, d -> d
                | _ -> DateTimeOffset.UtcNow
            | _ -> DateTimeOffset.UtcNow
        Some {
            Event = ev
            SessionId = firstString [ [ "sessionId" ]; [ "session_id" ] ] payload |> Option.defaultValue ""
            Timestamp = stamp
            Cwd = cwd
            Surface = surfaceOf source cwd vscodeShape payload
            Source = source
            ToolName = firstString [ [ "toolName" ]; [ "tool_name" ] ] payload
            ToolArgs = rawText [ [ "toolArgs" ]; [ "tool_input" ]; [ "toolInput" ] ] payload
            ToolResult = rawText [ [ "toolResult" ]; [ "tool_result" ]; [ "tool_response" ] ] payload
            Prompt = firstString [ [ "prompt" ]; [ "initialPrompt" ]; [ "initial_prompt" ] ] payload
            Reason = firstString [ [ "reason" ]; [ "stopReason" ]; [ "stop_reason" ]; [ "trigger" ] ] payload
            Error = rawText [ [ "error" ] ] payload
            AgentName = firstString [ [ "agentName" ]; [ "agent_name" ]; [ "agentDisplayName" ]; [ "agent_display_name" ] ] payload
            VsCodeShape = vscodeShape
            Raw = payload.Clone()
        }

/// What a hook says back. `None` everywhere means "carry on as normal".
type Decision = {
    Permission: PermissionDecision option
    Reason: string option
    AdditionalContext: string option
    /// agentStop / subagentStop: keep going instead of stopping.
    Block: string option
}

and PermissionDecision =
    | Allow
    | Deny
    | Ask

let proceed = { Permission = None; Reason = None; AdditionalContext = None; Block = None }

/// The JSON a command or HTTP hook prints/returns, in the shape the event and casing expect.
let toOutput (call: HookCall) (d: Decision) =
    let perm =
        d.Permission
        |> Option.map (function
            | Allow -> "allow"
            | Deny -> "deny"
            | Ask -> "ask")
    let pairs = [
        match call.Event with
        | PreToolUse ->
            // hooks-reference "preToolUse decision control": the same fields for camelCase and the
            // VS Code-compatible (PascalCase) configuration.
            "permissionDecision", perm |> Option.map str
            "permissionDecisionReason", d.Reason |> Option.map str
        | PermissionRequest ->
            match d.Permission with
            | Some Allow -> "behavior", Some(str "allow")
            | Some Deny -> "behavior", Some(str "deny")
            | _ -> ()
            "message", d.Reason |> Option.map str
        | AgentStop | SubagentStop ->
            match d.Block with
            | Some why ->
                "decision", Some(str "block")
                "reason", Some(str why)
            | None -> ()
        | _ -> ()
        "additionalContext", d.AdditionalContext |> Option.map str
    ]
    render (obj pairs) |> _.Trim()
