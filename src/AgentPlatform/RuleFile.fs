/// Loads a hook rule set from the JSON the renderer emits (hook-rules.json), so the daemon,
/// the cloud and any machine without the F# definition run exactly the same rules.
module AgentPlatform.RuleFile

open System.Text.Json
open AgentPlatform.Json
open AgentPlatform.HookEvents
open AgentPlatform.Hooks

let private surface =
    function
    | "cli" -> Cli
    | "vscode" -> VsCode
    | "visual-studio" -> VisualStudio
    | "jetbrains" -> JetBrains
    | "copilot-app" -> CopilotApp
    | "cloud-agent" -> CloudAgent
    | "sdk" -> Sdk
    | "agentic-workflow" -> AgenticWorkflow
    | _ -> UnknownSurface

let private strings (e: JsonElement) = [ for x in e.EnumerateArray() -> x.GetString() ]

let rec private condition (e: JsonElement) : Condition =
    let p = e.EnumerateObject() |> Seq.head
    match p.Name with
    | "toolIs" -> ToolIs(strings p.Value)
    | "argsMatch" -> ArgsMatch(p.Value.GetString())
    | "commandStartsWith" -> CommandStartsWith(p.Value.GetString())
    | "pathMatches" -> PathMatches(p.Value.GetString())
    | "promptContains" -> PromptContains(p.Value.GetString())
    | "onSurface" -> OnSurface(strings p.Value |> List.map surface)
    | "all" -> All [ for x in p.Value.EnumerateArray() -> condition x ]
    | "anyOf" -> AnyOf [ for x in p.Value.EnumerateArray() -> condition x ]
    | "not" -> Not(condition p.Value)
    | _ -> Always

let rec private action (e: JsonElement) : Action =
    let p = e.EnumerateObject() |> Seq.head
    match p.Name with
    | "block" -> Block(p.Value.GetString())
    | "requireApproval" -> RequireApproval(p.Value.GetString())
    | "approve" -> Approve
    | "addContext" -> AddContext(p.Value.GetString())
    | "keepGoing" -> KeepGoing(p.Value.GetString())
    | "dryRun" -> DryRun(action p.Value)
    | _ -> Record

let parse (text: string) : HookSet =
    use doc = JsonDocument.Parse text
    let root = doc.RootElement
    let rules =
        match tryProp [ "rules" ] root with
        | Some rs ->
            [ for r in rs.EnumerateArray() ->
                {
                    Name = defaultArg (tryString [ "name" ] r) "rule"
                    Description = defaultArg (tryString [ "description" ] r) ""
                    Events = (tryProp [ "events" ] r |> Option.map strings |> Option.defaultValue []) |> List.choose tryParseEvent
                    When = tryProp [ "when" ] r |> Option.map condition |> Option.defaultValue Always
                    Then = tryProp [ "then" ] r |> Option.map action |> Option.defaultValue Record
                    OnFailure = (if tryString [ "onFailure" ] r = Some "open" then FailOpen else FailClosed)
                } ]
        | None -> []
    let capture =
        tryProp [ "capture"; "events" ] root
        |> Option.map strings
        |> Option.map (List.choose tryParseEvent)
        |> Option.filter (List.isEmpty >> not)
        |> Option.map (fun events -> { Events = events; IncludeContent = tryString [ "capture"; "includeContent" ] root <> Some "false" })
    { Rules = rules; Capture = capture; TimeoutSec = 5; CaptureVia = HttpHooks }
