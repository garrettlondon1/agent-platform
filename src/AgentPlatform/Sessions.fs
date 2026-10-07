/// One session model for every surface, and an adapter for each place session data lives:
///
///     hooks.ndjson        the platform service - CLI, VS Code, app, cloud agent hooks, SDK in-process hooks
///     otlp-traces.ndjson  the platform service - CLI, VS Code, JetBrains, SDK, gh-aw spans (gen_ai conventions)
///     gh aw logs --json   + run dirs           - gh-aw runs with evals.jsonl and grader_results.json
///     GET /agents/tasks/{id}                    - Copilot cloud agent tasks and sessions (prompt, model, AI credits)
module AgentPlatform.Sessions

open System
open System.IO
open System.Text.Json
open AgentPlatform.Json

type ToolCall = { Name: string; Args: string; Ok: bool option; Error: string option }

type AgentSession = {
    Id: string
    Surface: string
    Agent: string option
    CapturedBy: string list
    Started: DateTimeOffset option
    Ended: DateTimeOffset option
    Prompt: string option
    FinalMessage: string option
    EndReason: string option
    Tools: ToolCall list
    Attempts: int
    Decisions: (string * string * string list) list
    ManagedKeys: string list
    Model: string option
    InputTokens: int64
    OutputTokens: int64
    Credits: float option
    Turns: int option
    Succeeded: bool option
    ExternalScores: (string * float) list
    ExternalEvals: (string * string) list
    Url: string option
}

let blank id surface = {
    Id = id; Surface = surface; Agent = None; CapturedBy = []; Started = None; Ended = None; Prompt = None
    FinalMessage = None; EndReason = None; Tools = []; Attempts = 0; Decisions = []; ManagedKeys = []; Model = None
    InputTokens = 0L; OutputTokens = 0L; Credits = None; Turns = None; Succeeded = None; ExternalScores = []
    ExternalEvals = []; Url = None
}

let private lines (path: string) =
    if File.Exists path then
        File.ReadLines path
        |> Seq.filter (String.IsNullOrWhiteSpace >> not)
        |> Seq.choose (fun l -> try Some(JsonDocument.Parse(l).RootElement) with _ -> None)
        |> List.ofSeq
    else []

let private date (s: string option) =
    s |> Option.bind (fun s -> match DateTimeOffset.TryParse s with | true, d -> Some d | _ -> None)

let private raw (e: JsonElement option) =
    match e with
    | Some v when v.ValueKind <> JsonValueKind.Null && v.ValueKind <> JsonValueKind.Undefined -> v.GetRawText()
    | _ -> ""

let private layer (source: string) =
    match source with
    | null -> "hook"
    | s when s.StartsWith "plugin:" -> "plugin"
    | s when s.StartsWith "sdk:" -> "sdk"
    | s -> s

/// Sessions from the service's hooks.ndjson (one record per hook call, any transport).
let fromHooks (path: string) =
    lines path
    |> List.choose (fun r ->
        let sid =
            tryString [ "session_id" ] r
            |> Option.filter (String.IsNullOrEmpty >> not)
            |> Option.orElse (tryProp [ "payload" ] r |> Option.bind (fun p -> tryString [ "sessionId" ] p |> Option.orElse (tryString [ "session_id" ] p)))
            |> Option.orElse (tryProp [ "payload"; "payload" ] r |> Option.bind (tryString [ "sessionId" ]))
        sid |> Option.map (fun s -> s, r))
    |> List.groupBy fst
    |> List.map (fun (sid, records) ->
        let rs = records |> List.map snd
        let ev name = rs |> List.filter (fun r -> tryString [ "event" ] r = Some name)
        let payload (r: JsonElement) =
            match tryProp [ "payload"; "payload" ] r with
            | Some inner when inner.ValueKind = JsonValueKind.Object -> inner
            | _ -> tryProp [ "payload" ] r |> Option.defaultValue r
        let sources = rs |> List.choose (tryString [ "source" ]) |> List.distinct
        let surface = rs |> List.tryPick (tryString [ "surface" ]) |> Option.defaultValue "cli"
        let dedupe (xs: JsonElement list) =
            xs |> List.distinctBy (fun r -> let p = payload r in tryString [ "timestamp" ] p, tryString [ "toolName" ] p |> Option.orElse (tryString [ "tool_name" ] p), raw (tryProp [ "toolArgs" ] p))
        let toolName (p: JsonElement) = tryString [ "toolName" ] p |> Option.orElse (tryString [ "tool_name" ] p) |> Option.defaultValue "?"
        let toolArgs (p: JsonElement) = raw (tryProp [ "toolArgs" ] p |> Option.orElse (tryProp [ "tool_input" ] p))
        let completed =
            dedupe (ev "postToolUse")
            |> List.map (fun r ->
                let p = payload r
                let result = raw (tryProp [ "toolResult" ] p |> Option.orElse (tryProp [ "tool_response" ] p))
                { Name = toolName p; Args = toolArgs p; Ok = Some(not (result.Contains "\"resultType\":\"failure\"" || result.Contains "\"resultType\":\"denied\"")); Error = None })
        let failed =
            dedupe (ev "postToolUseFailure")
            |> List.map (fun r -> let p = payload r in { Name = toolName p; Args = toolArgs p; Ok = Some false; Error = tryString [ "error" ] p })
        let decisions =
            rs
            |> List.choose (fun r ->
                match tryString [ "decision" ] r with
                | Some d when d <> "" ->
                    let rules = match tryProp [ "rules" ] r with Some a when a.ValueKind = JsonValueKind.Array -> [ for x in a.EnumerateArray() -> x.GetString() ] | _ -> []
                    Some(d, defaultArg (tryString [ "reason" ] r) "", rules)
                | _ -> None)
        let resolved = ev "managedSettingsResolved" |> List.tryLast
        let usage = ev "assistantUsage"
        let sum key = usage |> List.sumBy (fun r -> tryFloat [ key ] (payload r) |> Option.defaultValue 0.0) |> int64
        let result = ev "sessionResult" |> List.tryLast
        let ends = ev "sessionEnd" @ ev "agentStop"
        let stamps = rs |> List.choose (fun r -> date (tryString [ "received_at" ] r))
        let endReason = ends |> List.tryPick (fun r -> let p = payload r in tryString [ "reason" ] p |> Option.orElse (tryString [ "stopReason" ] p))
        {
            blank sid surface with
                Agent = result |> Option.bind (fun r -> tryString [ "agent" ] (payload r)) |> Option.orElse (sources |> List.tryPick (fun s -> if s.StartsWith "sdk:" then Some(s.Substring 4) else None))
                CapturedBy = sources |> List.map layer |> List.distinct
                Started = (if stamps.IsEmpty then None else Some(List.min stamps))
                Ended = (if stamps.IsEmpty then None else Some(List.max stamps))
                Prompt =
                    ev "userPromptSubmitted" |> List.tryPick (fun r -> tryString [ "prompt" ] (payload r))
                    |> Option.orElse (result |> Option.bind (fun r -> tryString [ "prompt" ] (payload r)))
                FinalMessage = result |> Option.bind (fun r -> tryString [ "finalMessage" ] (payload r)) |> Option.orElse (ends |> List.tryPick (fun r -> tryString [ "finalMessage" ] (payload r)))
                EndReason = endReason
                Tools = completed @ failed
                Attempts = (dedupe (ev "preToolUse")).Length
                Decisions = decisions
                ManagedKeys =
                    resolved
                    |> Option.bind (fun r -> tryProp [ "managedKeys" ] (payload r))
                    |> Option.filter (fun k -> k.ValueKind = JsonValueKind.Array)
                    |> Option.map (fun k -> [ for x in k.EnumerateArray() -> x.GetString() ])
                    |> Option.defaultValue []
                Model = usage |> List.tryPick (fun r -> tryString [ "model" ] (payload r))
                InputTokens = sum "inputTokens"
                OutputTokens = sum "outputTokens"
                Credits = (let c = usage |> List.sumBy (fun r -> tryFloat [ "cost" ] (payload r) |> Option.defaultValue 0.0) in if c > 0.0 then Some c else None)
                Turns = (if usage.IsEmpty then None else Some usage.Length)
                Succeeded =
                    match endReason with
                    | Some "complete" | Some "end_turn" -> Some true
                    | Some "error" | Some "timeout" | Some "abort" -> Some false
                    | _ -> result |> Option.map (fun r -> (tryString [ "finalMessage" ] (payload r) |> Option.defaultValue "") <> "")
        })

/// Sessions from `gh aw logs --json` plus the run directories it downloaded.
let fromAgenticWorkflows (logsJson: string) (logsDir: string) =
    if not (File.Exists logsJson) then []
    else
        let text = File.ReadAllText logsJson
        use doc = JsonDocument.Parse(text.Substring(text.IndexOf '{'))
        let runDirs = if Directory.Exists logsDir then Directory.GetDirectories(logsDir, "run-*", SearchOption.AllDirectories) else [||]
        [ match tryProp [ "runs" ] doc.RootElement with
          | Some runs when runs.ValueKind = JsonValueKind.Array ->
              for r in runs.EnumerateArray() do
                  let id = defaultArg (tryString [ "run_id" ] r) "?"
                  let dir = runDirs |> Array.tryFind (fun d -> Path.GetFileName d = $"run-{id}")
                  let files name = match dir with Some d -> Directory.GetFiles(d, name, SearchOption.AllDirectories) |> List.ofArray | None -> []
                  let evals =
                      files "evals.jsonl" |> List.collect lines
                      |> List.choose (fun e -> match tryString [ "id" ] e, tryString [ "answer" ] e with Some i, Some a -> Some(i, a) | _ -> None)
                      |> List.distinctBy fst
                  let graders =
                      files "grader_results.json"
                      |> List.collect (fun f ->
                          try
                              use g = JsonDocument.Parse(File.ReadAllText f)
                              [ match tryProp [ "results" ] g.RootElement with
                                | Some rs when rs.ValueKind = JsonValueKind.Array ->
                                    for x in rs.EnumerateArray() do
                                        match tryString [ "id" ] x, tryFloat [ "value" ] x with
                                        | Some i, Some v -> i, v
                                        | _ -> ()
                                | _ -> () ]
                          with _ -> [])
                      |> List.distinctBy fst
                  let usage = tryProp [ "token_usage_summary" ] r
                  let conclusion = tryString [ "conclusion" ] r
                  {
                      blank $"gh-aw:{id}" "agentic-workflow" with
                          Agent = tryString [ "workflow_name" ] r
                          CapturedBy = [ "gh-aw" ] @ (if evals.IsEmpty then [] else [ "gh-aw-evals" ]) @ (if graders.IsEmpty then [] else [ "gh-aw-graders" ])
                          Started = date (tryString [ "started_at" ] r) |> Option.orElse (date (tryString [ "created_at" ] r))
                          Ended = date (tryString [ "updated_at" ] r)
                          Model = usage |> Option.bind (tryProp [ "by_model" ]) |> Option.bind (fun m -> if m.ValueKind = JsonValueKind.Object then m.EnumerateObject() |> Seq.tryHead |> Option.map _.Name else None)
                          InputTokens = usage |> Option.bind (tryFloat [ "total_input_tokens" ]) |> Option.map int64 |> Option.defaultValue 0L
                          OutputTokens = usage |> Option.bind (tryFloat [ "total_output_tokens" ]) |> Option.map int64 |> Option.defaultValue 0L
                          Credits = tryFloat [ "aic" ] r
                          Turns = tryFloat [ "turns" ] r |> Option.map int
                          Succeeded = conclusion |> Option.map (fun c -> c = "success")
                          EndReason = conclusion
                          ExternalScores = graders
                          ExternalEvals = evals
                          Url = tryString [ "url" ] r
                  }
          | _ -> () ]

/// Sessions from a Copilot cloud agent task (GET /agents/tasks/{task_id}). usage.amount is nano AI credits.
let fromCloudTask (task: JsonElement) =
    let taskId = defaultArg (tryString [ "id" ] task) "?"
    [ match tryProp [ "sessions" ] task with
      | Some ss when ss.ValueKind = JsonValueKind.Array ->
          for s in ss.EnumerateArray() do
              let state = tryString [ "state" ] s
              let sessionId = defaultArg (tryString [ "id" ] s) taskId
              let stateText = defaultArg state "?"
              let branch = defaultArg (tryString [ "head_ref" ] s) "-"
              {
                  blank $"cloud:{sessionId}" "cloud-agent" with
                      Agent = Some "copilot-cloud-agent"
                      CapturedBy = [ "cloud-api" ]
                      Started = date (tryString [ "created_at" ] s)
                      Ended = date (tryString [ "completed_at" ] s)
                      Prompt = tryString [ "prompt" ] s
                      FinalMessage = tryString [ "name" ] s |> Option.map (fun n -> $"{n} ({stateText}; branch {branch})")
                      Model = tryString [ "model" ] s
                      Credits = tryFloat [ "usage"; "amount" ] s |> Option.map (fun n -> n / 1e9)
                      Succeeded = state |> Option.map (fun x -> x = "completed")
                      EndReason = state
              }
      | _ -> () ]
