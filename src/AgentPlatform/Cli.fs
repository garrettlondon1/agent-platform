/// The command line every organisation's platform project gets for free:
///
///     [<EntryPoint>]
///     let main argv = Cli.run myPlatform argv
///
///     dotnet run -- validate                       check the definition
///     dotnet run -- render  [--out .platform]      write every asset (versioned, deterministic)
///     dotnet run -- diff --against <dir>           what changed since a previous render
///     dotnet run -- coverage                       which control reaches which surface
///     dotnet run -- plan                           what apply would change in GitHub settings (rulesets, cloud agent, ...)
///     dotnet run -- apply                          apply it (needs GH_TOKEN with org admin + enterprise AI controls)
///     dotnet run -- serve   [--port 4319]          the platform service: hooks, OTLP, MCP registry
///     dotnet run -- hook-test <event> < payload    run the rules on one payload, print the decision
///     dotnet run -- sdk <agent> "<prompt>"         run a governed Copilot SDK session
///     dotnet run -- drift   --docs <dir> [--gh-aw <dir>]   compare the DSL with the current GitHub docs
///     dotnet run -- evals   --data <dir> [--aw-logs <dir>] [--judge]   score sessions from every surface
module AgentPlatform.Cli

open System
open System.IO
open System.Text.Json
open AgentPlatform.Json
open AgentPlatform.HookEvents
open AgentPlatform.Hooks
open AgentPlatform.Platform

let private opt (args: string array) (name: string) =
    args |> Array.tryFindIndex ((=) name) |> Option.bind (fun i -> if i + 1 < args.Length then Some args[i + 1] else None)

let private flag (args: string array) (name: string) = Array.contains name args

let private fail (lines: string list) =
    for l in lines do eprintfn "  x %s" l
    1

let validateCmd (p: Platform) =
    match validate p with
    | [] ->
        printfn $"{p.Name} {p.Version} ({p.Rollout}): valid. {p.Agents.Length} agents, {p.Plugins.Length} plugins, {p.Hooks.Rules.Length} hook rules, {p.Mcp.Servers.Length} MCP servers, {p.Standards.Length} standards, {p.Workflows.Length} workflows, {p.Teams.Length} teams."
        0
    | problems ->
        eprintfn $"{p.Name}: {problems.Length} problem(s)"
        fail problems

let renderCmd (p: Platform) (args: string array) =
    match validate p with
    | [] ->
        let out = defaultArg (opt args "--out") ".platform"
        // A fixed publish time keeps renders byte-for-byte reproducible; override for real releases.
        let published =
            match opt args "--published" with
            | Some s -> DateTimeOffset.Parse s
            | None -> DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
        if flag args "--clean" && Directory.Exists out then Directory.Delete(out, true)
        let files = Render.all p published
        let n = Render.writeTo out files
        printfn $"rendered {n} files to {Path.GetFullPath out}"
        for group in files |> List.groupBy (fun f -> f.Path.Split('/')[0]) do
            printfn $"  {fst group,-22} {(snd group).Length,4} files"
        0
    | problems -> fail problems

/// Compares a fresh render with a previous one (a committed directory) and lists every changed asset.
let diffCmd (p: Platform) (args: string array) =
    match opt args "--against" with
    | None -> fail [ "diff needs --against <previously rendered directory>" ]
    | Some previous ->
        let files = Render.all p (DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
        let fresh = files |> List.map (fun f -> f.Path, f.Content.Replace("\r\n", "\n")) |> Map.ofList
        let old =
            if Directory.Exists previous then
                Directory.GetFiles(previous, "*", SearchOption.AllDirectories)
                |> Array.map (fun f -> Path.GetRelativePath(previous, f).Replace('\\', '/'), File.ReadAllText(f).Replace("\r\n", "\n"))
                |> Map.ofArray
            else Map.empty
        let added = fresh |> Map.filter (fun k _ -> not (old.ContainsKey k)) |> Map.keys |> List.ofSeq
        let removed = old |> Map.filter (fun k _ -> not (fresh.ContainsKey k)) |> Map.keys |> List.ofSeq
        let changed = fresh |> Map.filter (fun k v -> old.ContainsKey k && old[k] <> v) |> Map.keys |> List.ofSeq
        for f in added do printfn $"  + {f}"
        for f in removed do printfn $"  - {f}"
        for f in changed do printfn $"  ~ {f}"
        printfn $"{added.Length} added, {removed.Length} removed, {changed.Length} changed"
        if added.IsEmpty && removed.IsEmpty && changed.IsEmpty then 0 else 3

/// Settings that only exist as GitHub APIs: show the plan, optionally apply it.
let planCmd (p: Platform) (apply: bool) =
    let steps = Deploy.plan p
    if steps.IsEmpty then printfn "GitHub settings already match the definition."
    for s in steps do printfn $"  {s.Method,-6} {s.Path}\n         {s.Description} ({s.Reason})"
    for m in Deploy.manualSteps p do printfn $"  manual  {m}"
    let unreadable = steps |> List.filter (fun s -> s.Method = Deploy.accessMethod) |> List.length
    if unreadable > 0 then
        printfn $"\nPartial plan: {unreadable} setting(s) could not be read with this token, so they are not compared. The deploy job (platform App token) sees all of them."
    let blind = steps |> List.filter (fun s -> s.Method = Deploy.accessMethod)
    if apply && not blind.IsEmpty then
        eprintfn $"  refusing to apply: {blind.Length} setting(s) could not be read, so the plan is incomplete. Refresh the token's scopes (see above) and re-run."
        1
    elif apply && not steps.IsEmpty then
        let results = Deploy.apply steps
        for s, r in results do
            let mark = if r.Ok then "ok" else "FAILED"
            printfn $"  {mark,-6} {s.Method} {s.Path}"
            if not r.Ok then eprintfn $"         {r.Body.Trim()}"
        if results |> List.forall (fun (_, r) -> r.Ok) then 0 else 1
    else 0

/// Which control reaches which surface, as a table.
let coverageCmd (p: Platform) =
    let surfaces = [ "cli"; "vscode"; "visual-studio"; "jetbrains"; "copilot-app"; "cloud-agent"; "sdk"; "agentic-workflow" ]
    let header = String.Join(" | ", "control" :: surfaces)
    printfn $"| {header} |"
    let dashes = String.Join(" | ", List.replicate (surfaces.Length + 1) "---")
    printfn $"| {dashes} |"
    for row in (Render.coverage p).AsArray() do
        let cells = surfaces |> List.map (fun s -> let v = string row["surfaces"].[s] in if v = "" then "-" else v)
        let line = String.Join(" | ", string row["control"] :: cells)
        printfn $"| {line} |"
    0
let serveCmd (p: Platform) (args: string array) =
    let port = opt args "--port" |> Option.map int |> Option.defaultValue 4319
    let data = defaultArg (opt args "--data") ".platform-data"
    let endpoints = opt args "--endpoints" |> Option.orElse (if Directory.Exists ".platform/endpoints" then Some ".platform/endpoints" else None)
    let hosts = (opt args "--host" |> Option.toList) @ [ Uri(p.Host).Host ]
    Service.run {
        Port = port; DataDir = data; Hosts = hosts; Endpoints = endpoints; Rules = p.Hooks
        Token =
            opt args "--token"
            |> Option.orElse (p.HookTokenVariable |> Option.bind (Environment.GetEnvironmentVariable >> Option.ofObj))
            |> Option.orElse (Environment.GetEnvironmentVariable "AGENTP_TOKEN" |> Option.ofObj)
    }
    0

let hookTestCmd (p: Platform) (args: string array) =
    let ev = if args.Length > 1 then args[1] else "preToolUse"
    let payload = Console.In.ReadToEnd()
    match parse (tryParseEvent ev) "hook-test" (JsonDocument.Parse(payload).RootElement) with
    | None -> fail [ $"unknown event {ev}" ]
    | Some call ->
        let v = evaluate p.Hooks.Rules call
        printfn "%s" (toOutput call v.Decision)
        let names = v.Matched |> List.map _.Name |> String.concat ", "
        eprintfn $"matched: {names}"
        for w in v.WouldHave do eprintfn $"observe: {w}"
        0

/// The managed settings one person actually gets, given the enterprise teams they belong to.
let whatIfCmd (p: Platform) (args: string array) =
    let teams =
        opt args "--teams" |> Option.map (fun s -> s.Split([| ',' |], StringSplitOptions.RemoveEmptyEntries) |> Array.map _.Trim() |> List.ofArray) |> Option.defaultValue []
    let pol, files, warnings = effectiveForMember p teams
    let who = if teams.IsEmpty then "a member of no mapped enterprise team" else "a member of " + String.Join(", ", teams)
    let via = if files.IsEmpty then "managed-settings.json only" else "managed-settings.json + teams/" + String.Join(", teams/", files |> List.map (fun f -> f + ".json"))
    eprintfn $"{who}: {via}"
    for w in warnings do eprintfn $"  ! {w}"
    printfn "%s" (render (Policy.toJson (Policy.forServer pol)))
    0

let driftCmd (args: string array) =
    match opt args "--docs" with
    | None -> fail [ "drift needs --docs <path to a github/docs clone>" ]
    | Some docs ->
        let documented = Drift.extract docs (opt args "--gh-aw")
        let changes = Drift.diff Drift.known documented
        let out = defaultArg (opt args "--out") "drift"
        Directory.CreateDirectory out |> ignore
        File.WriteAllText(Path.Combine(out, "documented.json"), render (Drift.snapshotJson documented))
        File.WriteAllText(Path.Combine(out, "changes.json"), render (Drift.changesJson changes))
        File.WriteAllText(Path.Combine(out, "report.md"), Drift.report changes)
        printf "%s" (Drift.report changes)
        // Exit 2 = drift found, so a workflow step can branch on it without parsing output.
        if changes.IsEmpty then 0 else 2

let sdkCmd (p: Platform) (args: string array) =
    match args |> Array.skip 1 |> List.ofArray with
    | agentName :: prompt :: _ ->
        match agentNamed p agentName with
        | None -> fail [ $"no agent named {agentName}" ]
        | Some a ->
            let session: Sdk.Session = {
                Platform = p; Team = opt args "--team"; Agent = a
                Host = (if flag args "--interactive" then Sdk.Interactive else Sdk.Headless)
                WorkingDirectory = defaultArg (opt args "--cwd") Environment.CurrentDirectory
                PluginRoot = opt args "--plugins" |> Option.orElse (if Directory.Exists ".platform/copilot-plugins" then Some ".platform/copilot-plugins" else None)
                HostCa = opt args "--ca"
                Token = Environment.GetEnvironmentVariable "AGENTP_TOKEN" |> Option.ofObj
                Record = Sdk.fileRecorder (defaultArg (opt args "--record") ".platform-data/hooks.ndjson")
                Functions = []
            }
            let o = (Sdk.runOnce session prompt).GetAwaiter().GetResult()
            printfn $"\n{o.Answer}\n\n(session {o.SessionId}, {o.Seconds:F1}s)"
            0
    | _ -> fail [ "usage: sdk <agent> \"<prompt>\"" ]

let evalsCmd (p: Platform) (args: string array) =
    let data = defaultArg (opt args "--data") ".platform-data"
    let sessions =
        Sessions.fromHooks (Path.Combine(data, "hooks.ndjson"))
        @ (match opt args "--aw-logs" with
           | Some dir -> Sessions.fromAgenticWorkflows (Path.Combine(dir, defaultArg (opt args "--aw-json") "runs.json")) dir
           | None -> [])
        @ (match opt args "--cloud-tasks" with
           | Some file when File.Exists file ->
               use doc = JsonDocument.Parse(File.ReadAllText file)
               if doc.RootElement.ValueKind = JsonValueKind.Array then [ for t in doc.RootElement.EnumerateArray() do yield! Sessions.fromCloudTask t ]
               else Sessions.fromCloudTask doc.RootElement
           | _ -> [])
    let judgeWith =
        if flag args "--judge" then
            Some(fun (s: Sessions.AgentSession) ->
                let questions = Evals.questionsFor p.Agents s
                let session: Sdk.Session = {
                    Platform = p; Team = None; Agent = Evals.judge; Host = Sdk.Headless
                    WorkingDirectory = Path.GetTempPath(); PluginRoot = None; HostCa = opt args "--ca"
                    Token = None; Record = (fun _ _ _ -> ()); Functions = []
                }
                let o = (Sdk.runOnce session (Evals.judgePrompt questions s)).GetAwaiter().GetResult()
                Evals.parseAnswers questions o.Answer)
        else None
    let evaluated =
        sessions
        |> List.map (fun s ->
            let judged =
                match s.ExternalEvals, judgeWith with
                | _ :: _, _ -> Evals.externalEvals s
                | [], Some j -> j s
                | [], None -> []
            ({ Session = s; Graders = Evals.graders s; Evals = judged }: Evals.Evaluated))
    let out = defaultArg (opt args "--out") (Path.Combine(data, "evals.json"))
    Evals.writeReport out evaluated
    printfn $"scored {evaluated.Length} sessions -> {Path.GetFullPath out}"
    for s in evaluated |> List.groupBy _.Session.Surface do
        printfn $"  {fst s,-18} {(snd s).Length,4} sessions"
    0

let run (p: Platform) (argv: string array) =
    // Everything below sees the definition as it deploys at its rollout stage.
    let p = staged p
    match argv |> Array.tryHead with
    | Some "validate" -> validateCmd p
    | Some "render" -> renderCmd p argv
    | Some "diff" -> diffCmd p argv
    | Some "plan" -> planCmd p false
    | Some "apply" -> planCmd p true
    | Some "publish" ->
        let rendered = defaultArg (opt argv "--from") ".platform"
        let results = Deploy.publish p rendered
        for r in results do printfn $"""  {(if r.Ok then "ok" else "FAILED"),-6} {r.Repo,-40} {r.Outcome}"""
        if results |> List.forall _.Ok then 0 else 1
    | Some "coverage" -> coverageCmd p
    | Some "serve" -> serveCmd p argv
    | Some "hook-test" -> hookTestCmd p argv
    | Some "drift" -> driftCmd argv
    | Some "sdk" -> sdkCmd p argv
    | Some "evals" -> evalsCmd p argv
    | Some "whatif" -> whatIfCmd p argv
    | Some "vocabulary" -> printf "%s" (Vocabulary.markdown ()); 0
    | _ ->
        printfn "usage: validate | render [--out dir] | diff --against <dir> | coverage | plan | apply | publish [--from .platform] | serve [--port n] | hook-test <event> | sdk <agent> <prompt> | drift --docs <dir> | evals --data <dir> | vocabulary | whatif [--teams a,b]"
        if argv.Length = 0 then 0 else 1
