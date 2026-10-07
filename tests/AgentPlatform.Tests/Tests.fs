module AgentPlatform.Tests

open System
open System.Text.Json
open System.Text.Json.Nodes
open Xunit
open AgentPlatform
open AgentPlatform.Policy
open AgentPlatform.HookEvents
open AgentPlatform.Hooks
open AgentPlatform.Mcp
open AgentPlatform.Standards
open AgentPlatform.Plugins
open AgentPlatform.Agents
open AgentPlatform.Platform

// ---------------------------------------------------------------------------
// A small platform used by every test
// ---------------------------------------------------------------------------

let private reviewer =
    agent {
        named "reviewer"
        useWhen "Review a diff."
        instructions "Review the diff."
        uses [ readFiles; searchFiles ]
        follows [ "engineering" ]
        mcpServers [ "jira" ]
        evaluatedBy [ question "cited" "Did it cite lines?" ]
    }

let private jira =
    mcpServer {
        id "com.atlassian/jira"
        key "jira"
        title "Jira"
        describedAs "Read Jira."
        remote "https://mcp.atlassian.com/v1/mcp"
        tools [ "getJiraIssue" ]
        readOnly
    }

let private engineering =
    standard {
        named "engineering"
        rules [ "Every change ships with a test." ]
    }

let private guard =
    hooks {
        rule (beforeTool "no-force-push" (anyOf [ shellCommand "git push --force"; shellCommand "git push -f" ]) (block "No force pushes."))
        rule (beforeTool "no-secrets" (reading "**/.env") (block "No secrets."))
        rule (beforeTool "approve-deploys" (shellCommand "terraform apply") (requireApproval "Needs a person."))
        rule (afterTool "test-reminder" (writingTo "src/**") (addContext "Add a test."))
        captureEverything
    }

let private everyone =
    policy {
        modelByDefault "auto"
        noBypassModeByDefault
        denyByDefault [ read "~/.ssh/**"; shell "git push --force *" ]
        allowByDefault [ read "/**"; edit "/src/**" ]
        telemetry (otlp "" |> capturingContent)
    }

let private governancePlugin =
    plugin {
        named "acme-governance"
        describedAs "Hooks."
        carriesHooks
    }

let private engineeringPlugin =
    plugin {
        named "acme-eng"
        describedAs "Standards."
        standards [ "engineering" ]
        agents [ "reviewer" ]
    }

let private reviewWorkflow =
    workflow {
        named "review"
        describedAs "Review PRs."
        runs reviewer
        on [ PullRequestOpened ]
        task "Review the pull request."
    }

let private catalog =
    mcp {
        approve jira
        block [ blockUrl "https://*.ngrok.io/*" ]
    }

let private sample =
    platform {
        named "Acme"
        organization "acme"
        platformHost "https://agents.acme.example"
        version "1.2.3"
        governedBy everyone
        team "payments" [ "payments-eng" ] (policy { autoTier Efficiency })
        standards [ engineering ]
        mcpCatalog catalog
        hooks guard
        plugins [ governancePlugin; engineeringPlugin ]
        agents [ reviewer ]
        workflows [ reviewWorkflow ]
    }

let private published = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
let private files = Render.all sample published
let private fileAt path = files |> List.find (fun f -> f.Path = path) |> _.Content
let private json path = JsonNode.Parse(fileAt path)

type private Step =
    | K of string
    | I of int

/// Walks a JSON path: K "key" indexes objects, I n indexes arrays.
let private at (path: Step list) (n: JsonNode) =
    path |> List.fold (fun (node: JsonNode) step -> match step with I i -> node.AsArray().[i] | K k -> node.[k]) n

let private call event (payload: string) =
    (parse (Some event) "test" (JsonDocument.Parse(payload).RootElement)).Value

// ---------------------------------------------------------------------------
// Validation
// ---------------------------------------------------------------------------

[<Fact>]
let ``the sample platform is valid`` () = Assert.Empty(validate sample)

[<Fact>]
let ``an agent that uses an unapproved MCP server is rejected`` () =
    let rogue = { reviewer with Name = "rogue"; Mcp = [ "filesystem" ] }
    let problems = validate { sample with Agents = sample.Agents @ [ rogue ] }
    Assert.Contains(problems, fun p -> p.Contains "filesystem" && p.Contains "not in the approved catalog")

[<Fact>]
let ``a team cannot override a key the enterprise did not mark overridable`` () =
    let strict = { everyone with Deny = Some(Enforced [ shell "rm -rf *" ]) }
    let team = policy { deny [ shell "ls" ] }
    Assert.Contains(validateTeam strict "t" team, fun p -> p.Contains "permissions.deny" && p.Contains "not marked overridable")

[<Fact>]
let ``a team cannot loosen an enforced auto tier`` () =
    let ent = policy { autoTier Balance }
    Assert.Contains(validateTeam ent "t" (policy { autoTier Intelligence }), fun p -> p.Contains "only tighten")
    Assert.Empty(validateTeam ent "t" (policy { autoTier Efficiency }))

[<Fact>]
let ``deniedMcpServers can be overridden by a team when the enterprise allows it`` () =
    let ent = policy { denyMcpByDefault [ serverUrl "https://*.ngrok.io/*" ] }
    Assert.Empty(validateTeam ent "t" (policy { denyMcp [ serverUrl "https://*.ngrok.io/*"; serverUrl "https://bad.example/*" ] }))
    Assert.Contains(validateTeam (policy { denyMcp [] }) "t" (policy { denyMcp [] }), fun m -> m.Contains "not marked overridable")

[<Fact>]
let ``sandbox paths must be absolute`` () =
    let p = policy { sandboxed (sandbox { required; denyPaths [ "~/.ssh"; "/etc/ssh" ] }) }
    let problems = validateEnterprise { p with Allow = Some(Enforced []) }
    Assert.Contains(problems, fun m -> m.Contains "'~/.ssh' must be absolute")
    Assert.DoesNotContain(problems, fun m -> m.Contains "/etc/ssh")

[<Fact>]
let ``hooks carry the platform token when one is configured`` () =
    let withToken = Render.all { sample with HookTokenVariable = Some "AGENTP_HOOK_TOKEN" } published
    let h = JsonNode.Parse(withToken |> List.find (fun f -> f.Path = "copilot-plugins/plugins/acme-governance/hooks.json") |> _.Content)
    let args = (at [ K "hooks"; K "preToolUse"; I 0; K "args" ] h).AsArray() |> Seq.map string |> List.ofSeq
    Assert.Contains("%AGENTP_HOOK_TOKEN", args)
    Assert.Contains("Authorization: Bearer {{AGENTP_HOOK_TOKEN}}", args)
    let http = at [ K "hooks"; K "sessionStart"; I 0 ] h
    Assert.Equal("Bearer ${AGENTP_HOOK_TOKEN}", string (at [ K "headers"; K "Authorization" ] http))
    Assert.Equal("AGENTP_HOOK_TOKEN", string (at [ K "allowedEnvVars"; I 0 ] http))
    let repo = withToken |> List.find (fun f -> f.Path.StartsWith "repo-baseline/.github/hooks/") |> _.Content
    Assert.Contains("Authorization: Bearer ${AGENTP_HOOK_TOKEN}", repo)

[<Fact>]
let ``capture stores no content unless asked`` () =
    Assert.False(guard.Capture.Value.IncludeContent)
    Assert.True((hooks { captureEverything; includingContent }).Capture.Value.IncludeContent)

[<Fact>]
let ``deny rules without an allow list are flagged`` () =
    let p = policy { deny [ shell "rm -rf *" ] }
    Assert.Contains(validateEnterprise p, fun m -> m.Contains "without an allow list")
    Assert.Empty(validateEnterprise (policy { deny [ shell "rm -rf *" ]; allow [] }))

[<Fact>]
let ``telemetry is never team-overridable`` () =
    Assert.Contains(validateTeam everyone "t" (policy { telemetry (otlp "https://x") }), fun p -> p.Contains "telemetry")

[<Fact>]
let ``an internal platform host with HTTP capture is flagged`` () =
    let internal' = { sample with Host = "https://agents.acme.internal" }
    Assert.Contains(validate internal', fun p -> p.Contains "captureOverCurl")
    let overCurl = { internal' with Hooks = { internal'.Hooks with CaptureVia = CurlCommands } }
    Assert.DoesNotContain(validate overCurl, fun p -> p.Contains "captureOverCurl")

// ---------------------------------------------------------------------------
// Managed settings
// ---------------------------------------------------------------------------

[<Fact>]
let ``managed settings carry overridable wrappers exactly as documented`` () =
    let ms = json ".github-private/copilot/managed-settings.json"
    Assert.Equal("auto", string (at [ K "model"; K "overridable" ] ms))
    Assert.Equal("disable", string (at [ K "permissions"; K "disableBypassPermissionsMode"; K "overridable" ] ms))
    Assert.Equal("Read(~/.ssh/**)", string (at [ K "permissions"; K "deny"; K "overridable"; I 0 ] ms))

[<Fact>]
let ``every plugin is enabled and its marketplace is the only one allowed`` () =
    let ms = json ".github-private/copilot/managed-settings.json"
    Assert.True((at [ K "enabledPlugins"; K "acme-governance@acme-copilot-plugins" ] ms).GetValue<bool>())
    Assert.True((at [ K "enabledPlugins"; K "acme-eng@acme-copilot-plugins" ] ms).GetValue<bool>())
    Assert.Equal("acme/copilot-plugins", string (at [ K "strictKnownMarketplaces"; K "overridable"; I 0; K "repo" ] ms))

[<Fact>]
let ``approved MCP servers are allowlisted by origin and blocked ones denied`` () =
    let ms = json ".github-private/copilot/managed-settings.json"
    Assert.Equal("https://mcp.atlassian.com/*", string (at [ K "allowedMcpServers"; K "overridable"; I 0; K "serverUrl" ] ms))
    Assert.Equal("https://*.ngrok.io/*", string (at [ K "deniedMcpServers"; I 0; K "serverUrl" ] ms))

[<Fact>]
let ``telemetry defaults to the platform host`` () =
    let ms = json ".github-private/copilot/managed-settings.json"
    Assert.Equal("https://agents.acme.example", string (at [ K "telemetry"; K "endpoint" ] ms))

[<Fact>]
let ``file and MDM delivery flatten overridable values`` () =
    let local = json "workstation/linux/etc/github-copilot/managed-settings.json"
    Assert.Equal("auto", string (at [ K "model" ] local))
    let reg = fileAt "workstation/windows/GitHubCopilot-policy.reg"
    Assert.Contains("\"permissions.disableBypassPermissionsMode\"=\"disable\"", reg)

[<Fact>]
let ``team files map to enterprise teams`` () =
    let mappings = json ".github-private/copilot/team-mappings.json"
    Assert.Equal("payments-eng", string (at [ K "payments.json"; I 0 ] mappings))
    Assert.Equal("efficiency", string (at [ K "autoTier" ] (json ".github-private/copilot/teams/payments.json")))

// ---------------------------------------------------------------------------
// Hooks: one engine, every transport
// ---------------------------------------------------------------------------

[<Fact>]
let ``force push is denied even inside a compound command`` () =
    let v = evaluate guard.Rules (call PreToolUse """{"sessionId":"s","toolName":"bash","toolArgs":{"command":"cd app && git push --force origin main"}}""")
    Assert.Equal(Some Deny, v.Decision.Permission)
    Assert.Contains("no-force-push", v.Decision.Reason.Value)

[<Fact>]
let ``reading a secret file is denied`` () =
    let v = evaluate guard.Rules (call PreToolUse """{"sessionId":"s","toolName":"view","toolArgs":{"path":"C:\\repo\\.env"}}""")
    Assert.Equal(Some Deny, v.Decision.Permission)

[<Fact>]
let ``approval rules ask locally but deny in cloud agent`` () =
    let local = evaluate guard.Rules (call PreToolUse """{"sessionId":"s","cwd":"C:/repo","toolName":"bash","toolArgs":{"command":"terraform apply"}}""")
    Assert.Equal(Some Ask, local.Decision.Permission)
    let cloud = evaluate guard.Rules (call PreToolUse """{"sessionId":"s","cwd":"/workspace","toolName":"bash","toolArgs":{"command":"terraform apply"}}""")
    Assert.Equal(Some Deny, cloud.Decision.Permission)

[<Fact>]
let ``safe calls carry on untouched`` () =
    let c = call PreToolUse """{"sessionId":"s","toolName":"bash","toolArgs":{"command":"git status"}}"""
    let v = evaluate guard.Rules c
    Assert.Equal(None, v.Decision.Permission)
    Assert.Equal("{}", toOutput c v.Decision)

[<Fact>]
let ``post tool rules add context`` () =
    let v = evaluate guard.Rules (call PostToolUse """{"sessionId":"s","toolName":"edit","toolArgs":{"path":"src/a.cs"}}""")
    Assert.Equal(Some "Add a test.", v.Decision.AdditionalContext)

[<Fact>]
let ``VS Code shaped payloads get the documented decision fields`` () =
    let c = call PreToolUse """{"hook_event_name":"PreToolUse","session_id":"s","tool_name":"Bash","tool_input":{"command":"git push -f"}}"""
    Assert.True c.VsCodeShape
    let out = JsonNode.Parse(toOutput c (evaluate guard.Rules c).Decision)
    Assert.Equal("deny", string (at [ K "permissionDecision" ] out))
    Assert.Null(out.["hookSpecificOutput"])

[<Fact>]
let ``PascalCase event names and fields are understood`` () =
    let stop = (parse None "t" (JsonDocument.Parse("""{"hook_event_name":"Stop","session_id":"s","stop_reason":"end_turn"}""").RootElement)).Value
    Assert.Equal(AgentStop, stop.Event)
    Assert.Equal(Some "end_turn", stop.Reason)
    let prompt = (parse None "t" (JsonDocument.Parse("""{"hook_event_name":"UserPromptSubmit","session_id":"s","prompt":"hi"}""").RootElement)).Value
    Assert.Equal(UserPromptSubmitted, prompt.Event)
    let post = (parse None "t" (JsonDocument.Parse("""{"hook_event_name":"PostToolUse","session_id":"s","tool_name":"Bash","tool_result":{"x":1}}""").RootElement)).Value
    Assert.True(post.ToolResult.IsSome)

[<Fact>]
let ``rendered rule files round-trip to the same decisions`` () =
    let loaded = RuleFile.parse (fileAt "endpoints/hook-rules.json")
    Assert.Equal(guard.Rules.Length, loaded.Rules.Length)
    let c = call PreToolUse """{"sessionId":"s","toolName":"bash","toolArgs":{"command":"git push -f"}}"""
    Assert.Equal((evaluate guard.Rules c).Decision, (evaluate loaded.Rules c).Decision)

[<Fact>]
let ``deciding hooks fail closed and capture hooks start no process`` () =
    let h = json "copilot-plugins/plugins/acme-governance/hooks.json"
    let pre = at [ K "hooks"; K "preToolUse"; I 0 ] h
    Assert.Equal("command", string (at [ K "type" ] pre))
    Assert.Equal("curl.exe", string (at [ K "exec" ] pre))
    Assert.Contains("--fail", (at [ K "args" ] pre).AsArray() |> Seq.map string)
    Assert.Equal("http", string (at [ K "hooks"; K "sessionStart"; I 0; K "type" ] h))

[<Fact>]
let ``repository hooks are bash and skip events cloud agent never fires`` () =
    let path = files |> List.find (fun f -> f.Path.StartsWith "repo-baseline/.github/hooks/") |> _.Path
    let h = json path
    let pre = at [ K "hooks"; K "preToolUse"; I 0 ] h
    Assert.Contains("|| exit 2", string (at [ K "bash" ] pre))
    Assert.Null(pre.["exec"])
    Assert.Null((at [ K "hooks" ] h).["notification"])
    Assert.Null((at [ K "hooks" ] h).["permissionRequest"])

// ---------------------------------------------------------------------------
// Plugins, agents, standards, MCP registry, agentic workflows
// ---------------------------------------------------------------------------

[<Fact>]
let ``standards ship as skills inside plugins`` () =
    let skillMd = fileAt "copilot-plugins/plugins/acme-eng/skills/standard-engineering/SKILL.md"
    Assert.StartsWith("---\nname: standard-engineering\n", skillMd)
    Assert.Contains("Every change ships with a test.", skillMd)

[<Fact>]
let ``agent profiles name their MCP servers and inline their standards`` () =
    let prof = fileAt ".github-private/agents/reviewer.md"
    Assert.Contains("mcp-servers:\n  jira:", prof)
    Assert.Contains("\"jira/*\"", prof)
    Assert.Contains("- Every change ships with a test.", prof)

[<Fact>]
let ``the MCP registry follows v0_1 routes`` () =
    let list = json "endpoints/v0.1/servers.json"
    Assert.Equal("com.atlassian/jira", string (at [ K "servers"; I 0; K "server"; K "name" ] list))
    Assert.Contains(files, fun f -> f.Path = "endpoints/v0.1/servers/com.atlassian%2Fjira/versions/latest.json")

[<Fact>]
let ``agentic workflows import the agent, carry evals and avoid reserved tool names`` () =
    let wf = fileAt "agentic-workflows/.github/workflows/review.md"
    Assert.Contains("  - .github/agents/reviewer.md", wf)
    Assert.Contains("evals:\n  - id: cited", wf)
    Assert.Contains("  acme-jira:", wf)
    Assert.DoesNotContain("\n  jira:", wf)
    Assert.Contains("strict: true", wf)

[<Fact>]
let ``renders are deterministic`` () =
    let again = Render.all sample published
    Assert.Equal<string list>(files |> List.map _.Content, again |> List.map _.Content)

[<Fact>]
let ``the lock file hashes every asset`` () =
    let lock = json "platform.lock.json"
    Assert.Equal("1.2.3", string (at [ K "version" ] lock))
    Assert.Equal(files.Length - 1, (at [ K "files" ] lock).AsObject().Count)

// ---------------------------------------------------------------------------
// Drift
// ---------------------------------------------------------------------------

[<Fact>]
let ``drift reports additions and removals per area`` () =
    let events = Drift.known["hooks.events"] |> List.filter ((<>) "notification")
    let documentedNow = Drift.known |> Map.add "hooks.events" ("newEvent" :: events)
    let c = Assert.Single(Drift.diff Drift.known documentedNow)
    Assert.Equal("hooks.events", c.Area)
    Assert.Equal<string list>([ "newEvent" ], c.Added)
    Assert.Equal<string list>([ "notification" ], c.Removed)

[<Fact>]
let ``the hook events the DSL models match the drift snapshot`` () =
    Assert.Equal<string list>(Drift.known["hooks.events"] |> List.sort, allEvents |> List.map _.Name |> List.sort)

[<Fact>]
let ``the documented managed keys are all modelled`` () =
    let fromReference = Drift.known["managed-settings.keys"]
    // The CLI table names the groups (permissions, sandbox) the reference lists by sub-key.
    let cliOnly = Drift.known["managed-settings.cli-keys"] |> List.filter (fun k -> not (fromReference |> List.exists (fun r -> r = k || r.StartsWith(k + "."))))
    Assert.Equal<string list>(fromReference @ cliOnly |> List.distinct |> List.sort, documented |> List.map fst |> List.filter (fun k -> k <> "effortLevel" && k <> "contextTier") |> List.sort)

// ---------------------------------------------------------------------------
// Rollout stages and self-protection
// ---------------------------------------------------------------------------

let private observed = { sample with Rollout = Observe; GitHub = { sample.GitHub with Rulesets = [ Governance.ruleset { named "review"; require [ Governance.copilotReviewsEveryPullRequest ] } ] } }
let private observedFiles = Render.all observed published
let private observedJson path = JsonNode.Parse(observedFiles |> List.find (fun f -> f.Path = path) |> _.Content)

[<Fact>]
let ``observe ships the platform but blocks nothing`` () =
    let ms = observedJson ".github-private/copilot/managed-settings.json"
    for key in [ "permissions"; "sandbox"; "allowedMcpServers"; "strictKnownMarketplaces" ] do
        Assert.Null(ms.[key])
    Assert.NotNull(ms.["enabledPlugins"])
    Assert.NotNull(ms.["deniedMcpServers"])
    Assert.NotNull(ms.["telemetry"])
    let h = observedJson "copilot-plugins/plugins/acme-governance/hooks.json"
    Assert.Equal("http", string (at [ K "hooks"; K "preToolUse"; I 0; K "type" ] h))
    Assert.Equal("Observe", string (observedJson "platform.lock.json").["rollout"])

[<Fact>]
let ``observe records what it would have denied`` () =
    let p = staged observed
    let v = evaluate p.Hooks.Rules (call PreToolUse """{"sessionId":"s","toolName":"bash","toolArgs":"{\"command\":\"git push --force\"}"}""")
    Assert.Equal(None, v.Decision.Permission)
    Assert.Contains(v.WouldHave, fun w -> w.StartsWith "[no-force-push] would deny")
    Assert.True(staged p = p, "staging is idempotent")

[<Fact>]
let ``your rulesets evaluate until Enforce but the platform always protects itself`` () =
    let rs = (staged observed).GitHub.Rulesets
    Assert.Equal("evaluate", (rs |> List.find (fun r -> r.Name = "review")).Enforcement)
    let rendered = rs |> List.find (fun r -> r.Name = "agent-platform-rendered-repositories")
    Assert.Equal("active", rendered.Enforcement)
    Assert.Contains(".github-private", rendered.IncludeRepos)
    let body = Governance.rulesetJson rendered
    Assert.Contains("\"update\"", body.ToJsonString())
    Assert.Equal("OrganizationAdmin", string (at [ K "bypass_actors"; I 0; K "actor_type" ] body))
    let withApp = staged { observed with DeployedBy = PlatformApp 42 }
    let definition = withApp.GitHub.Rulesets |> List.find (fun r -> r.Name = "agent-platform-definition")
    Assert.Empty(definition.Bypass)
    Assert.Equal("Integration", string (at [ K "bypass_actors"; I 0; K "actor_type" ] (Governance.rulesetJson (withApp.GitHub.Rulesets |> List.find (fun r -> r.Name = "agent-platform-rendered-repositories")))))
    Assert.Equal("active", ((staged sample).GitHub.Rulesets |> List.find (fun r -> r.Name = "agent-platform-definition")).Enforcement)

[<Fact>]
let ``guard enforces denies but never requires the sandbox`` () =
    let p = { sample with Rollout = Guard; Policy = { everyone with Sandbox = Some(Enforced(sandbox { required })) } }
    let ms = toJson (resolvedPolicy (staged p))
    Assert.NotNull(ms.["permissions"].["deny"])
    Assert.False(ms.["sandbox"].["failIfUnavailable"].GetValue<bool>())

[<Fact>]
let ``the definition repository must be its own repository`` () =
    Assert.Contains(validate { sample with DefinitionRepo = ".github-private" }, fun m -> m.Contains "also a rendered repository")

[<Fact>]
let ``the vocabulary lists the DSL as compiled`` () =
    let v = Vocabulary.markdown ()
    Assert.Contains("`rollout <Rollout>`", v)
    Assert.Contains("`Observe`", v)
    Assert.Contains("`PlatformApp of appId: int`", v)
    Assert.Contains("shellCommand", v)

[<Fact>]
let ``a pilot ring gets the hook plugins and nobody else does`` () =
    let pilot = { sample with HookPilotTeams = [ "agent-pilots" ]; HostCaPath = Some @"C:\ProgramData\AgentPlatform\ca.pem" }
    let rendered = Render.all pilot published
    let get path = JsonNode.Parse(rendered |> List.find (fun f -> f.Path = path) |> _.Content)
    let everyone = get ".github-private/copilot/managed-settings.json"
    Assert.Null(everyone.["enabledPlugins"].["acme-governance@acme-copilot-plugins"])
    Assert.NotNull(everyone.["enabledPlugins"].["acme-eng@acme-copilot-plugins"])
    Assert.Equal("agent-pilots", string (at [ K $"{hookPilotTeam}.json"; I 0 ] (get ".github-private/copilot/team-mappings.json")))
    Assert.True((get $".github-private/copilot/teams/{hookPilotTeam}.json").["enabledPlugins"].["acme-governance@acme-copilot-plugins"].GetValue<bool>())
    Assert.DoesNotContain(rendered, fun f -> f.Path.StartsWith "repo-baseline/.github/hooks/")
    let args = (at [ K "hooks"; K "preToolUse"; I 0; K "args" ] (get "copilot-plugins/plugins/acme-governance/hooks.json")).AsArray() |> Seq.map string |> List.ofSeq
    Assert.Contains("--cacert", args)

[<Fact>]
let ``rulesets for every repository are not marked protected (GitHub rejects ~ALL with protected)`` () =
    let all = Governance.rulesetJson (Governance.ruleset { named "everywhere"; require [ Governance.NoDeletion ] })
    Assert.False((at [ K "conditions"; K "repository_name"; K "protected" ] all).GetValue<bool>())
    let named = Governance.rulesetJson (Governance.ruleset { named "some"; repositories [ "a" ]; require [ Governance.NoDeletion ] })
    Assert.True((at [ K "conditions"; K "repository_name"; K "protected" ] named).GetValue<bool>())

[<Fact>]
let ``apply_patch edits are matched by the files they touch`` () =
    let patch = "*** Begin Patch\n*** Update File: src/app.txt\n@@\n-app\n+app verified\n*** End Patch\n"
    let raw = { (call PostToolUse """{"sessionId":"s","toolName":"apply_patch"}""") with ToolArgs = Some patch }
    Assert.Contains((evaluate guard.Rules raw).Matched, fun r -> r.Name = "test-reminder")
    let wrapped = { raw with ToolArgs = Some(JsonSerializer.Serialize {| input = patch.Replace("src/app.txt", ".github/workflows/ci.yml") |}) }
    Assert.DoesNotContain((evaluate guard.Rules wrapped).Matched, fun r -> r.Name = "test-reminder")
    let deny = { (call PreToolUse """{"sessionId":"s","toolName":"apply_patch"}""") with ToolArgs = Some "*** Begin Patch\n*** Add File: config/.env\n+X=1\n*** End Patch" }
    Assert.Equal(Some Deny, (evaluate [ beforeTool "no-env-writes" (writingTo "**/.env") (block "no") ] deny).Decision.Permission)

// ---------------------------------------------------------------------------
// Every managed-settings key and team rule (enterprise-managed-settings.md, cli-config-dir-reference.md,
// override-settings-for-teams.md)
// ---------------------------------------------------------------------------

[<Fact>]
let ``every CLI managed-settings key renders with the documented name and value`` () =
    let p =
        policy {
            model "auto"
            effortLevel "high"
            contextTier LongContext
            assistedApprovalOnly
            deny [ powershell "Remove-Item *"; write "//etc/**" ]
            allow [ read "/**" ]
            shellShortcut false
            policyHelper { helperAt "/usr/local/bin/copilot-policy" with Args = [ "--json" ]; TimeoutMs = Some 2000 }
            onlySignInTo [ "acme" ]
            alwaysRefreshServerSettings
            onlyMarketplaces [ urlSourceWithHeaders "https://plugins.acme.example/marketplace.json" [ "Authorization", "Bearer ${TOKEN}" ]; npm "@acme/plugins"; hostPattern "^plugins\\.acme\\.example$" ]
            marketplaces [ Policy.marketplace "frozen" (Policy.github "acme/frozen") |> neverAutoUpdating ]
            sandboxed (sandbox { required; learning RecordAndAllow })
        }
    let j = toJson p
    Assert.Equal("allow-auto-only", string (at [ K "permissions"; K "disableBypassPermissionsMode" ] j))
    Assert.Equal("PowerShell(Remove-Item *)", string (at [ K "permissions"; K "deny"; I 0 ] j))
    Assert.Equal("Edit(//etc/**)", string (at [ K "permissions"; K "deny"; I 1 ] j))
    Assert.Equal("high", string j.["effortLevel"])
    Assert.Equal("long_context", string j.["contextTier"])
    Assert.False(j.["shellShortcut"].GetValue<bool>())
    Assert.Equal("--json", string (at [ K "policyHelper"; K "args"; I 0 ] j))
    Assert.Equal(2000, (at [ K "policyHelper"; K "timeoutMs" ] j).GetValue<int>())
    Assert.Equal("acme", string (at [ K "forceLoginOrgs"; I 0 ] j))
    Assert.True(j.["forceRemoteSettingsRefresh"].GetValue<bool>())
    Assert.Equal("Bearer ${TOKEN}", string (at [ K "strictKnownMarketplaces"; I 0; K "headers"; K "Authorization" ] j))
    Assert.Equal("npm", string (at [ K "strictKnownMarketplaces"; I 1; K "source" ] j))
    Assert.Equal("hostPattern", string (at [ K "strictKnownMarketplaces"; I 2; K "source" ] j))
    Assert.False((at [ K "extraKnownMarketplaces"; K "frozen"; K "autoUpdate" ] j).GetValue<bool>())
    Assert.Equal("allow", string (at [ K "sandbox"; K "learningMode" ] j))
    Assert.Empty(validateEnterprise p)

[<Fact>]
let ``device-only keys never reach the server file, learningMode only reaches native MDM`` () =
    let p = policy { onlySignInTo [ "acme" ]; sandboxed (sandbox { required; learning RecordAndAllow }) }
    let server = toJson (forServer p)
    Assert.Null(server.["forceLoginOrgs"])
    Assert.Null(at [ K "sandbox"; K "learningMode" ] server)
    let file = toJson (forFile p)
    Assert.Equal("acme", string (at [ K "forceLoginOrgs"; I 0 ] file))
    Assert.Null(at [ K "sandbox"; K "learningMode" ] file)
    let mdm = Render.mdmValues p |> Map.ofList
    Assert.Equal("allow", mdm.["sandbox.learningMode"])
    Assert.Equal("""["acme"]""", mdm.["forceLoginOrgs"])

[<Fact>]
let ``managed settings validation follows the documented constraints`` () =
    let problems = validateEnterprise (policy { effortLevel "high"; allowMcp [ serverName "jira*" ]; policyHelper (helperAt "bin/helper") })
    Assert.Contains(problems, fun m -> m.Contains "effortLevel / contextTier apply alongside a managed model")
    Assert.Contains(problems, fun m -> m.Contains "serverName 'jira*' may only use letters")
    Assert.Contains(problems, fun m -> m.Contains "policyHelper.path 'bin/helper'")

[<Fact>]
let ``teams may only set overridable keys, tighter autoTier and additive plugins`` () =
    let ent = policy { modelByDefault "auto"; autoTier Balance; effortLevel "medium"; noBypassMode }
    let problems = validateTeam ent "t" (policy { unmanagedModel; autoTier Intelligence; effortLevel "high"; allowBypassMode })
    Assert.DoesNotContain(problems, fun m -> m.Contains "'model'")
    Assert.Contains(problems, fun m -> m.Contains "'autoTier' intelligence is less restrictive")
    Assert.Contains(problems, fun m -> m.Contains "'effortLevel' is not overridable")
    Assert.Contains(problems, fun m -> m.Contains "permissions.disableBypassPermissionsMode' is not marked overridable")
    Assert.Empty(validateTeam ent "t" (policy { autoTier Efficiency; enablePlugins [ "extra@m" ] }))

let private sec = plugin { named "security-tools"; describedAs "AppSec skills." }
let private withTeams =
    { sample with
        Plugins = sample.Plugins @ [ sec ]
        Policy = { everyone with Marketplaces = Some(Overridable [ Policy.marketplace "community" (Policy.github "acme/community") ]) } }
    |> fun p ->
        let teamA = teamOverride { named "payments"; forEnterpriseTeams [ "payments-eng" ]; overriding (policy { denyByDefault [ shell "git push --force *" ]; allowByDefault [ read "/**" ] }); addPlugins [ sec ] }
        let teamB = teamOverride { named "platform"; forEnterpriseTeams [ "ent:platform-eng" ]; overriding (policy { denyByDefault [ shell "git push --force *"; read "~/.ssh/**" ]; allowByDefault [ read "/**"; edit "/infra/**" ] }) }
        { p with Teams = [ teamA; teamB ] }

[<Fact>]
let ``a team's plugins are enabled only in its own file`` () =
    let rendered = Render.all { withTeams with Teams = withTeams.Teams |> List.map (fun t -> { t with Policy = { t.Policy with Deny = t.Policy.Deny |> Option.map (function Overridable v -> Enforced v | x -> x); Allow = t.Policy.Allow |> Option.map (function Overridable v -> Enforced v | x -> x) } }) } published
    let get path = JsonNode.Parse(rendered |> List.find (fun f -> f.Path = path) |> _.Content)
    Assert.Null((get ".github-private/copilot/managed-settings.json").["enabledPlugins"].["security-tools@acme-copilot-plugins"])
    Assert.True((get ".github-private/copilot/teams/payments.json").["enabledPlugins"].["security-tools@acme-copilot-plugins"].GetValue<bool>())
    Assert.Null((get ".github-private/copilot/teams/platform.json").["enabledPlugins"])

[<Fact>]
let ``team files are checked against the documented team rules`` () =
    let bad =
        { withTeams with
            Teams =
                [ teamOverride { named "x y"; overriding (policy { blockPlugins [ "acme-eng@acme-copilot-plugins" ]; marketplaces [ Policy.marketplace "other" (Policy.github "acme/other") ]; onlyMarketplaces [] }) }
                  teamOverride { named "dup"; forEnterpriseTeams [ "a" ] }
                  teamOverride { named "dup"; forEnterpriseTeams [ "b" ] } ] }
    let problems = validate bad
    Assert.Contains(problems, fun m -> m.Contains "team x y: the name becomes a file name")
    Assert.Contains(problems, fun m -> m.Contains "team x y: map it to at least one enterprise team")
    Assert.Contains(problems, fun m -> m.Contains "cannot turn off a plugin the enterprise enables")
    Assert.Contains(problems, fun m -> m.Contains "drops 'community'")
    Assert.Contains(problems, fun m -> m.Contains "complete lockdown")
    Assert.Contains(problems, fun m -> m.Contains "team dup: defined twice")

[<Fact>]
let ``a member of several teams gets them combined least-restrictively`` () =
    let teamA = policy { model "gpt-a"; deny [ shell "git push --force *" ]; allow [ read "/**" ]; autoTier Efficiency; sandboxed (sandbox { required; noOutbound; denyPaths [ "/a"; "/shared" ] }) }
    let teamB = policy { model "gpt-b"; deny [ shell "git push --force *"; read "~/.ssh/**" ]; allow [ edit "/infra/**" ]; autoTier Intelligence; sandboxed (sandbox { required; denyPaths [ "/shared" ] }) }
    let combined, warnings = combineTeams [ "a", teamA; "b", teamB ]
    Assert.Equal(Some(Enforced [ shell "git push --force *" ]), combined.Deny)
    Assert.Equal(Some(Enforced [ read "/**"; edit "/infra/**" ]), combined.Allow)
    Assert.Equal(Some(Enforced Intelligence), combined.AutoTier)
    let s = match combined.Sandbox with Some(Enforced s) -> s | _ -> failwith "sandbox"
    Assert.Equal(Some true, s.Enabled)
    Assert.Equal(None, s.AllowOutbound)
    Assert.Equal<string list>([ "/shared" ], s.DeniedPaths)
    Assert.Contains(warnings, fun w -> w.Contains "'model' differs between teams a, b")
    let unmanaged, _ = combineTeams [ "a", policy { noBypassMode }; "b", policy { allowBypassMode } ]
    Assert.Equal(Some Unmanaged, unmanaged.DisableBypass)

[<Fact>]
let ``whatif resolves enterprise team slugs with or without ent prefix`` () =
    let p = { withTeams with Policy = { withTeams.Policy with Deny = Some(Overridable [ shell "rm -rf *" ]); Allow = Some(Overridable [ read "/**" ]) } }
    let pol, files, _ = effectiveForMember p [ "ent:payments-eng"; "platform-eng" ]
    Assert.Equal<string list>([ "payments"; "platform" ], files)
    Assert.Equal(Some(Enforced [ shell "git push --force *" ]), pol.Deny)
    let none, noFiles, _ = effectiveForMember p [ "unknown" ]
    Assert.Empty(noFiles)
    Assert.Equal(Some(Enforced [ shell "rm -rf *" ]), none.Deny)

// ---------------------------------------------------------------------------
// Devices (MDM / file), session storage, and the README's complete example
// ---------------------------------------------------------------------------

let private withDevices =
    { sample with
        DevicePolicy = Some(policy { noBypassMode; deny [ read "~/.ssh/**" ]; onlySignInTo [ "acme" ]; sandboxed (sandbox { required; learning RecordAndAllow }) })
        SessionStorage = Some ViewFromCloud }

[<Fact>]
let ``devices get their own policy, with device-only keys, and policy hooks through the registry`` () =
    Assert.Empty(validate withDevices)
    let rendered = Render.all withDevices published
    let text path = rendered |> List.find (fun f -> f.Path = path) |> _.Content
    let deviceFile = JsonNode.Parse(text "workstation/windows/ProgramFiles/GitHubCopilot/managed-settings.json")
    Assert.Equal("acme", string (at [ K "forceLoginOrgs"; I 0 ] deviceFile))
    Assert.Null(at [ K "sandbox"; K "learningMode" ] deviceFile)
    Assert.Null((JsonNode.Parse(text ".github-private/copilot/managed-settings.json")).["forceLoginOrgs"])
    let reg = text "workstation/windows/GitHubCopilot-policy.reg"
    Assert.Contains("\"sandbox.learningMode\"=\"allow\"", reg)
    Assert.Contains(@"[HKEY_LOCAL_MACHINE\SOFTWARE\Policies\GitHub\Copilot\agent-platform]", reg)
    Assert.Contains("\"Policy\"=\"{\\\"version\\\":1", reg)
    let detect = text "workstation/windows/intune/Detect-GitHubCopilotPolicy.ps1"
    let remediate = text "workstation/windows/intune/Remediate-GitHubCopilotPolicy.ps1"
    Assert.Contains("exit 1", detect)
    Assert.Contains("Remove-ItemProperty", remediate)
    Assert.DoesNotContain(rendered, fun f -> f.Path.StartsWith "workstation/windows" && f.Path.Contains "policy.d")
    Assert.Contains("chmod 0644", text "workstation/macos/install.sh")

[<Fact>]
let ``device-only keys and session storage are validated`` () =
    let serverOnly = { sample with Policy = { everyone with ForceLoginOrgs = Some [ "acme" ] } }
    Assert.Contains(validate serverOnly, fun m -> m.Contains "onlySignInTo must reach a device")
    let remote = { sample with SessionStorage = Some ViewFromCloud; Policy = { everyone with RemoteControl = Some(requireSso [ "acme" ]) } }
    Assert.Contains(validate remote, fun m -> m.Contains "needs 'Store local sessions in the Cloud' = View and control")
    Assert.Contains(Deploy.manualSteps withDevices, fun m -> m.StartsWith "Store local sessions in the Cloud = View from cloud")

[<Fact>]
let ``custom graders render as repository scripts`` () =
    let g = Agents.operationalValue "Minutes saved" "Saved" "minutes" "#!/usr/bin/env bash\necho '[{\"id\":\"m\",\"value\":1}]'"
    let p = { sample with Workflows = [ { reviewWorkflow with Graders = [ BuiltInGraders; g ] } ] }
    let rendered = Render.all p published
    let md = rendered |> List.find (fun f -> f.Path = "agentic-workflows/.github/workflows/review.md") |> _.Content
    Assert.Contains("run: .github/graders/review-operational-value.sh", md)
    Assert.Contains(rendered, fun f -> f.Path = "agentic-workflows/.github/graders/review-operational-value.sh")

[<Fact>]
let ``the README's complete example is the compiled Contoso example, verbatim`` () =
    let rec root (d: IO.DirectoryInfo) = if IO.File.Exists(IO.Path.Combine(d.FullName, "AgentPlatform.slnx")) then d.FullName else root d.Parent
    let repo = root (IO.DirectoryInfo AppContext.BaseDirectory)
    let norm (s: string) = s.Replace("\r\n", "\n").TrimEnd()
    let example = norm (IO.File.ReadAllText(IO.Path.Combine(repo, "examples", "contoso", "Platform.fs")))
    let readme = norm (IO.File.ReadAllText(IO.Path.Combine(repo, "README.md")))
    Assert.True(readme.Contains example, "README.md is out of date: run proof/sync-readme.ps1")
