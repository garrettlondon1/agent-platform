/// The Copilot SDK surface. An agent from the platform becomes a governed SDK session, with
/// every control mapped to one SDK property and the SAME hook rules running in-process:
///
///     managed settings (device/server)   ->  SessionConfig.EnableManagedSettings = true
///     permissions deny/ask/allow, bypass ->  SessionConfig.ManagedSettings
///     model                              ->  SessionConfig.Model
///     telemetry                          ->  CopilotClientOptions.Telemetry (+ OTEL_* for the runtime)
///     enabledPlugins                     ->  SessionConfig.PluginDirectories (rendered marketplace)
///     standards                          ->  SessionConfig.SystemMessage
///     custom agents                      ->  SessionConfig.CustomAgents
///     MCP catalog                        ->  SessionConfig.McpServers (only approved servers exist)
///     hook rules + capture               ->  SessionHooks.*  -> Hooks.evaluate  (no process, no pipe)
///     managed `ask`                      ->  OnPermissionRequest honours ManagedApprovalRequired
module AgentPlatform.Sdk

open System
open System.Collections.Generic
open System.IO
open System.Text.Json
open System.Threading.Tasks
open GitHub.Copilot
open Microsoft.Extensions.AI
open AgentPlatform.Policy
open AgentPlatform.HookEvents
open AgentPlatform.Hooks
open AgentPlatform.Mcp
open AgentPlatform.Standards
open AgentPlatform.Agents
open AgentPlatform.Platform

type Host =
    /// Someone can answer a managed `ask`.
    | Interactive
    /// Nobody is there: managed `ask` is refused, as cloud agent does.
    | Headless

type Recorder = string -> HookCall option -> obj -> unit

type Session = {
    Platform: Platform
    Team: string option
    Agent: Agent
    Host: Host
    WorkingDirectory: string
    /// Rendered marketplace root (copilot-plugins/) whose plugins load into the session.
    PluginRoot: string option
    /// PEM of the platform host CA, for OTLP export.
    HostCa: string option
    Token: string option
    /// Where hook records go (the platform service store, a file, or nowhere).
    Record: Recorder
    /// F# functions offered to the agent as tools.
    Functions: AIFunction list
}

let tool name description (implementation: unit -> string) =
    CopilotTool.DefineTool(Func<string>(implementation), factoryOptions = AIFunctionFactoryOptions(Name = name, Description = description))

let private say color (text: string) =
    let c = Console.ForegroundColor
    Console.ForegroundColor <- color
    Console.WriteLine text
    Console.ForegroundColor <- c

let private list (xs: string seq) = ResizeArray xs :> IList<string>

let managedSettings (p: Policy) =
    let rules (s: Setting<PermissionRule list> option) = s |> Option.bind settingValue |> Option.defaultValue [] |> List.map _.Text |> list
    ManagedSettings(
        Permissions =
            ManagedSettingsPermissions(
                Deny = rules p.Deny,
                Ask = rules p.Ask,
                Allow = rules p.Allow,
                DisableBypassPermissionsMode = (match p.DisableBypass |> Option.bind settingValue with Some b -> b.Text | None -> null)
            )
    )

let clientOptions (s: Session) =
    let pol = effectivePolicy s.Platform s.Team
    let env = Dictionary<string, string>()
    for kv in Environment.GetEnvironmentVariables() |> Seq.cast<Collections.DictionaryEntry> do
        env[string kv.Key] <- string kv.Value
    env["OTEL_RESOURCE_ATTRIBUTES"] <-
        [ "agentp.surface", "sdk"; "agentp.agent", s.Agent.Name; "agentp.team", defaultArg s.Team "default"; "agentp.platform", s.Platform.Name; "agentp.version", s.Platform.Version ]
        |> List.map (fun (k, v) -> $"{k}={v}") |> String.concat ","
    s.HostCa |> Option.iter (fun ca -> env["OTEL_EXPORTER_OTLP_CERTIFICATE"] <- Path.GetFullPath ca)
    s.Token |> Option.iter (fun t -> env["OTEL_EXPORTER_OTLP_HEADERS"] <- $"Authorization=Bearer {t}")
    let options = CopilotClientOptions(Environment = env, WorkingDirectory = s.WorkingDirectory)
    match pol.Telemetry with
    | Some t when t.Enabled ->
        options.Telemetry <-
            TelemetryConfig(
                OtlpEndpoint = t.Endpoint,
                OtlpProtocol = (match t.Protocol with HttpJson -> "http/json" | HttpProtobuf -> "http/protobuf"),
                CaptureContent = Nullable t.CaptureContent,
                SourceName = "agentp.sdk"
            )
    | _ -> ()
    options

let private asJson (o: obj) =
    JsonSerializer.SerializeToElement(o, JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase))

/// The platform's hook rules, plus the agent's own `cannotSee` subjects, run in-process.
let private rulesFor (s: Session) =
    let own =
        s.Agent.Forbidden
        |> List.map (fun subject -> beforeTool $"{s.Agent.Name}-cannot-see-{subject}" (argsMatching (Text.RegularExpressions.Regex.Escape subject)) (block $"{s.Agent.Name} may not look at {subject}."))
    s.Platform.Hooks.Rules @ own

let hooks (s: Session) =
    let source = $"sdk:{s.Agent.Name}"
    let rules = rulesFor s
    let capture = s.Platform.Hooks.Capture |> Option.map _.Events |> Option.defaultValue []
    let run (event: HookEvent) (input: obj) =
        match parse (Some event) source (asJson input) with
        | None -> proceed
        | Some call ->
            let call = { call with Surface = Sdk }
            let verdict = evaluate rules call
            if List.contains event capture || not verdict.Matched.IsEmpty then
                s.Record event.Name (Some call) {| decision = verdict.Decision.Permission |> Option.map string |> Option.toObj; reason = verdict.Decision.Reason |> Option.toObj; rules = verdict.Matched |> List.map _.Name; payload = input |}
            match verdict.Decision.Permission with
            | Some Deny -> say ConsoleColor.Red $"  blocked {call.ToolName |> Option.defaultValue event.Name} - {verdict.Decision.Reason |> Option.defaultValue String.Empty}"
            | _ -> ()
            verdict.Decision
    let ctx (d: Decision) = d.AdditionalContext |> Option.toObj
    SessionHooks(
        OnSessionStart = Func<_, _, _>(fun (i: SessionStartHookInput) _ -> let d = run SessionStart i in Task.FromResult(if isNull (ctx d) then null else SessionStartHookOutput(AdditionalContext = ctx d))),
        OnUserPromptSubmitted = Func<_, _, _>(fun (i: UserPromptSubmittedHookInput) _ -> run UserPromptSubmitted i |> ignore; Task.FromResult<UserPromptSubmittedHookOutput> null),
        OnPreToolUse =
            Func<_, _, _>(fun (i: PreToolUseHookInput) _ ->
                let d = run PreToolUse i
                match d.Permission with
                | None when isNull (ctx d) -> Task.FromResult<PreToolUseHookOutput> null
                | perm ->
                    let decision = match perm with Some Allow -> "allow" | Some Deny -> "deny" | Some Ask -> "ask" | None -> null
                    Task.FromResult(PreToolUseHookOutput(PermissionDecision = decision, PermissionDecisionReason = (d.Reason |> Option.toObj), AdditionalContext = ctx d))),
        OnPostToolUse = Func<_, _, _>(fun (i: PostToolUseHookInput) _ -> let d = run PostToolUse i in Task.FromResult(if isNull (ctx d) then null else PostToolUseHookOutput(AdditionalContext = ctx d))),
        OnPostToolUseFailure = Func<_, _, _>(fun (i: PostToolUseFailureHookInput) _ -> run PostToolUseFailure i |> ignore; Task.FromResult<PostToolUseFailureHookOutput> null),
        OnAgentStop = Func<_, _, _>(fun (i: AgentStopHookInput) _ -> run AgentStop i |> ignore; Task.FromResult<AgentStopHookOutput> null),
        OnErrorOccurred = Func<_, _, _>(fun (i: ErrorOccurredHookInput) _ -> run ErrorOccurred i |> ignore; Task.FromResult<ErrorOccurredHookOutput> null),
        OnSessionEnd = Func<_, _, _>(fun (i: SessionEndHookInput) _ -> run SessionEnd i |> ignore; Task.FromResult<SessionEndHookOutput> null)
    )

let permissionHandler (s: Session) =
    Func<PermissionRequest, PermissionInvocation, Task<Rpc.PermissionDecision>>(fun request _ ->
        let describe =
            match request with
            | :? PermissionRequestShell as x -> $"run `{x.FullCommandText}`"
            | :? PermissionRequestWrite as x -> $"write {x.FileName}"
            | :? PermissionRequestUrl as x -> $"fetch {x.Url}"
            | :? PermissionRequestMcp as x -> $"call {x.ServerName}/{x.ToolName}"
            | other -> other.Kind
        if request.ManagedApprovalRequired = Nullable true then
            s.Record "managedApprovalRequired" None {| kind = request.Kind; request = describe; host = string s.Host |}
            match s.Host with
            | Headless ->
                say ConsoleColor.Yellow $"  policy requires a person to approve: {describe} - nobody here, refused."
                Task.FromResult(Rpc.PermissionDecision.UserNotAvailable())
            | Interactive ->
                say ConsoleColor.Yellow $"  policy asks you to approve: {describe} [y/N] "
                match Console.ReadLine() with
                | null -> Task.FromResult(Rpc.PermissionDecision.UserNotAvailable())
                | a when a.Trim().ToLowerInvariant() = "y" -> Task.FromResult(Rpc.PermissionDecision.ApproveOnce())
                | _ -> Task.FromResult(Rpc.PermissionDecision.Reject "Declined under the enterprise ask policy.")
        else Task.FromResult(Rpc.PermissionDecision.ApproveOnce()))

let events (s: Session) =
    Action<SessionEvent>(fun ev ->
        match ev with
        | :? SessionManagedSettingsResolvedEvent as r ->
            let d = r.Data
            s.Record "managedSettingsResolved" None {| source = (if isNull (box d.Source) then null else d.Source.Value); managedKeys = d.ManagedKeys; deviceManaged = d.DeviceManaged; serverManaged = d.ServerManaged; clientManaged = d.ClientManaged; failClosed = d.FailClosed; bypassPermissionsDisabled = d.BypassPermissionsDisabled |}
        | :? SessionManagedSettingsEnforcedEvent as e ->
            let d = e.Data
            s.Record "managedSettingsEnforced" None {| setting = d.Setting; action = (if isNull (box d.Action) then null else d.Action.Value); message = d.Message; failClosed = d.FailClosed |}
        | :? AssistantUsageEvent as u ->
            let d = u.Data
            s.Record "assistantUsage" None {| model = d.Model; inputTokens = d.InputTokens; outputTokens = d.OutputTokens; cacheReadTokens = d.CacheReadTokens; cost = d.Cost |}
        | :? ToolExecutionStartEvent as t -> say ConsoleColor.DarkGray $"  ...{t.Data.ToolName}"
        | _ -> ())

let pluginDirectories (s: Session) =
    match s.PluginRoot with
    | None -> []
    | Some root ->
        s.Platform.Plugins
        // The hook-carrying plugin is replaced by in-process hooks; loading it would double-run rules.
        |> List.filter (fun p -> not p.CarriesHooks)
        |> List.map (fun p -> Path.GetFullPath(Path.Combine(root, "plugins", p.Name)))
        |> List.filter Directory.Exists

let mcpConfig (server: Server) : McpServerConfig =
    match server.Transport, commandLine server with
    | Remote(url, _), _ -> McpHttpServerConfig(Url = url) :> McpServerConfig
    | _, Some(cmd, args) -> McpStdioServerConfig(Command = cmd, Args = ResizeArray args) :> McpServerConfig
    | _ -> McpStdioServerConfig(Command = "true") :> McpServerConfig

let sessionConfig (s: Session) =
    let s = { s with Platform = staged s.Platform }
    let p = s.Platform
    let pol = effectivePolicy p s.Team
    let a = s.Agent
    let standards = standardsNamed p a.Standards @ (p.Standards |> List.filter (fun st -> st.AppliesTo.IsEmpty)) |> List.distinctBy _.Name
    let servers = Dictionary<string, McpServerConfig>()
    for srv in serversNamed p a.Mcp do servers[srv.Key] <- mcpConfig srv
    let persona (x: Agent) =
        CustomAgentConfig(
            Name = x.Name,
            DisplayName = x.Name,
            Description = x.Description,
            Prompt = dedent x.Instructions,
            Model = (x.Model |> Option.toObj),
            Tools = (if x.Tools.IsEmpty then null else list (x.Tools |> List.map _.Text))
        )
    SessionConfig(
        EnableManagedSettings = Nullable true,
        ManagedSettings = managedSettings pol,
        Model = (a.Model |> Option.orElse (pol.Model |> Option.bind settingValue) |> Option.toObj),
        WorkingDirectory = s.WorkingDirectory,
        McpServers = servers,
        CustomAgents = ResizeArray(persona a :: (p.Agents |> List.filter (fun x -> x.Name <> a.Name) |> List.map persona)),
        Agent = a.Name,
        PluginDirectories = ResizeArray(pluginDirectories s),
        SystemMessage = SystemMessageConfig(Content = systemMessage standards),
        Tools = ResizeArray<AIFunctionDeclaration>(s.Functions |> List.map (fun t -> t :> AIFunctionDeclaration)),
        Hooks = hooks s,
        OnPermissionRequest = permissionHandler s,
        OnEvent = events s,
        Streaming = Nullable false
    )

type Outcome = { SessionId: string; Answer: string; Seconds: float }

/// One prompt to completion, the way a pipeline or a service would run it.
let runOnce (s: Session) (prompt: string) =
    task {
        let started = DateTimeOffset.UtcNow
        use client = new CopilotClient(clientOptions s)
        let! session = client.CreateSessionAsync(sessionConfig s)
        let! reply = session.SendAndWaitAsync(prompt, Nullable(TimeSpan.FromMinutes 8.0))
        let answer = match reply with null -> "" | m -> m.Data.Content
        let id = session.SessionId
        let seconds = (DateTimeOffset.UtcNow - started).TotalSeconds
        s.Record "sessionResult" None {| sessionId = id; agent = s.Agent.Name; team = defaultArg s.Team "default"; prompt = prompt; finalMessage = answer; seconds = seconds |}
        do! session.DisposeAsync()
        do! client.StopAsync()
        return { SessionId = id; Answer = answer; Seconds = seconds }
    }

/// A governed conversation in the console, zero-to-agents style.
let chatWith (s: Session) =
    task {
        use client = new CopilotClient(clientOptions s)
        let! session = client.CreateSessionAsync(sessionConfig s)
        say ConsoleColor.White $"{s.Agent.Name} - governed by the {s.Platform.Name} agent platform {s.Platform.Version}. Ctrl+C to leave."
        let mutable talking = true
        while talking do
            Console.Write "You: "
            match Console.ReadLine() with
            | null -> talking <- false
            | "" -> ()
            | q ->
                let! reply = session.SendAndWaitAsync(q, Nullable(TimeSpan.FromMinutes 5.0))
                let text = match reply with null -> "(no answer)" | m -> m.Data.Content
                say ConsoleColor.Cyan $"\n{s.Agent.Name}: {text}\n"
        do! session.DisposeAsync()
    }

/// Records to an NDJSON file in the same shape the service stores.
let fileRecorder (path: string) : Recorder =
    let gate = obj ()
    fun event call payload ->
        let line =
            JsonSerializer.Serialize {|
                received_at = DateTimeOffset.UtcNow
                event = event
                source = (call |> Option.map _.Source |> Option.toObj)
                surface = "sdk"
                session_id = (call |> Option.map _.SessionId |> Option.toObj)
                tool = (call |> Option.bind _.ToolName |> Option.toObj)
                payload = payload
            |}
        lock gate (fun () ->
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath path)) |> ignore
            File.AppendAllText(path, line + "\n"))
