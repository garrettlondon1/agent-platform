/// agent { } and workflow { } - one description of an agent, run on every surface.
///
///     agent profile (.agent.md)  ->  .github-private/agents (enterprise: CLI, VS Code, VS, JetBrains, cloud agent)
///                                    plugins/<p>/agents      (installed everywhere by enabledPlugins)
///                                    .github/agents          (gh-aw `imports:`)
///     SDK CustomAgentConfig      ->  Copilot SDK sessions
///     workflow { runs agent }    ->  .github/workflows/<name>.md (gh-aw), compiled with `gh aw compile --strict`
///
///     docs: content/copilot/reference/custom-agents-configuration.md
///           gh-aw docs/src/content/docs/reference/frontmatter.md
module AgentPlatform.Agents

open System

/// Tool aliases every Copilot surface understands (custom-agents-configuration "Tool aliases").
type ToolRef =
    | ReadFiles
    | SearchFiles
    | EditFiles
    | RunShell
    | WebAccess
    | Todos
    | Delegate
    | GitHub of tool: string
    | McpTools of server: string * tool: string

    member t.Text =
        match t with
        | ReadFiles -> "read"
        | SearchFiles -> "search"
        | EditFiles -> "edit"
        | RunShell -> "execute"
        | WebAccess -> "web"
        | Todos -> "todo"
        | Delegate -> "agent"
        | GitHub t -> $"github/{t}"
        | McpTools(s, t) -> $"{s}/{t}"

let readFiles = ReadFiles
let searchFiles = SearchFiles
let editFiles = EditFiles
let runShell = RunShell
let webAccess = WebAccess
let delegateTo = Delegate
let github = GitHub "*"
let githubTool name = GitHub name
let mcpTool server tool = McpTools(server, tool)
let allOf server = McpTools(server, "*")

/// A YES/NO question asked about every finished session (gh-aw `evals:` semantics).
type EvalQuestion = { Id: string; Question: string }

let question id text = { Id = id; Question = text }

type Agent = {
    Name: string
    Description: string
    Instructions: string
    Model: string option
    Tools: ToolRef list
    /// MCP server keys from the platform catalog, scoped to this agent.
    Mcp: string list
    /// Standard names whose rules are appended to the agent's instructions.
    Standards: string list
    Evals: EvalQuestion list
    Owner: string option
    OnlyWhenAsked: bool
    NotUserSelectable: bool
    /// Subjects the agent must never look at; enforced by an in-process hook in SDK sessions
    /// and stated in the profile everywhere else.
    Forbidden: string list
}

let private blank = {
    Name = ""; Description = ""; Instructions = ""; Model = None; Tools = []; Mcp = []; Standards = []
    Evals = []; Owner = None; OnlyWhenAsked = false; NotUserSelectable = false; Forbidden = []
}

type AgentBuilder() =
    member _.Yield(_: unit) = blank
    [<CustomOperation "named">] member _.Named(a: Agent, n) = { a with Name = n }
    /// When Copilot should use this agent. Required: it is how agents are chosen.
    [<CustomOperation "useWhen">] member _.Describe(a: Agent, d) = { a with Description = d }
    [<CustomOperation "instructions">] member _.Instructions(a: Agent, i) = { a with Instructions = i }
    [<CustomOperation "model">] member _.Model(a: Agent, m) = { a with Model = Some m }
    [<CustomOperation "uses">] member _.Uses(a: Agent, t) = { a with Tools = t }
    [<CustomOperation "mcpServers">] member _.Mcp(a: Agent, m) = { a with Mcp = m }
    [<CustomOperation "follows">] member _.Follows(a: Agent, s) = { a with Standards = s }
    [<CustomOperation "evaluatedBy">] member _.Evals(a: Agent, e) = { a with Evals = e }
    [<CustomOperation "owner">] member _.Owner(a: Agent, o) = { a with Owner = Some o }
    [<CustomOperation "onlyWhenAsked">] member _.OnlyWhenAsked(a: Agent) = { a with OnlyWhenAsked = true }
    [<CustomOperation "notUserSelectable">] member _.Hidden(a: Agent) = { a with NotUserSelectable = true }
    [<CustomOperation "cannotSee">] member _.CannotSee(a: Agent, subjects) = { a with Forbidden = subjects }

let agent = AgentBuilder()

let dedent (text: string) =
    text.Split('\n') |> Array.map _.Trim() |> String.concat "\n" |> _.Trim()

// ---------------------------------------------------------------------------
// workflow { } - the same agent, on an event, in GitHub Actions (gh-aw)
// ---------------------------------------------------------------------------

type Trigger =
    | Schedule of fuzzy: string
    | PullRequestOpened
    | IssueOpened
    | WorkflowFailed of workflows: string list
    | Manually
    | SlashCommand of name: string

type SafeOutput =
    | CreateIssue of titlePrefix: string * labels: string list * max: int
    | AddComment of max: int
    | AddLabels of allowed: string list
    | CreatePullRequest of titlePrefix: string * draft: bool
    | ReviewPullRequest of comment: bool
    | AssignToCopilot
    | Noop

type Grader =
    | BuiltInGraders
    /// A frozen Bash evaluator scoring the run's operational value (gh-aw `operational-value`).
    | OperationalValue of name: string * description: string * unit: string * script: string

type Experiment = { Name: string; Variants: string list; Metric: string; MinSamples: int }

type Workflow = {
    Name: string
    Description: string
    Agent: string
    On: Trigger list
    Reads: string list
    Writes: SafeOutput list
    Network: string list
    Toolsets: string list
    Bash: string list
    CanEdit: bool
    /// Plugin names from the platform marketplace to install in the run.
    Plugins: string list
    Graders: Grader list
    Experiments: Experiment list
    Task: string
    TimeoutMinutes: int
    MaxAiCredits: int option
    CopilotSdk: bool
}

let private noWorkflow = {
    Name = ""; Description = ""; Agent = ""; On = [ Manually ]; Reads = [ "contents" ]; Writes = [ Noop ]
    Network = [ "defaults" ]; Toolsets = [ "default" ]; Bash = []; CanEdit = false; Plugins = []
    Graders = [ BuiltInGraders ]; Experiments = []; Task = ""; TimeoutMinutes = 20; MaxAiCredits = None; CopilotSdk = true
}

type WorkflowBuilder() =
    member _.Yield(_: unit) = noWorkflow
    [<CustomOperation "named">] member _.Named(w: Workflow, n) = { w with Name = n }
    [<CustomOperation "describedAs">] member _.Describe(w: Workflow, d) = { w with Description = d }
    [<CustomOperation "runs">] member _.Runs(w: Workflow, a: Agent) = { w with Agent = a.Name }
    [<CustomOperation "on">] member _.On(w: Workflow, t) = { w with On = t }
    [<CustomOperation "reads">] member _.Reads(w: Workflow, r) = { w with Reads = r }
    [<CustomOperation "mayOnly">] member _.Writes(w: Workflow, s) = { w with Writes = s }
    [<CustomOperation "network">] member _.Network(w: Workflow, n) = { w with Network = n }
    [<CustomOperation "githubToolsets">] member _.Toolsets(w: Workflow, t) = { w with Toolsets = t }
    [<CustomOperation "bash">] member _.Bash(w: Workflow, b) = { w with Bash = b }
    [<CustomOperation "canEdit">] member _.CanEdit(w: Workflow) = { w with CanEdit = true }
    [<CustomOperation "plugins">] member _.Plugins(w: Workflow, p) = { w with Plugins = p }
    [<CustomOperation "graders">] member _.Graders(w: Workflow, g) = { w with Graders = g }
    [<CustomOperation "experiment">] member _.Experiment(w: Workflow, e) = { w with Experiments = w.Experiments @ [ e ] }
    [<CustomOperation "task">] member _.Task(w: Workflow, t) = { w with Task = t }
    [<CustomOperation "timeoutMinutes">] member _.Timeout(w: Workflow, t) = { w with TimeoutMinutes = t }
    [<CustomOperation "maxAiCredits">] member _.Credits(w: Workflow, c) = { w with MaxAiCredits = Some c }
    [<CustomOperation "useCliEngine">] member _.Cli(w: Workflow) = { w with CopilotSdk = false }

let workflow = WorkflowBuilder()

let schedule fuzzy = Schedule fuzzy
let workflowFailed names = WorkflowFailed names
let slashCommand name = SlashCommand name
let createIssue prefix labels = CreateIssue(prefix, labels, 5)
let addComment = AddComment 3
let addLabels allowed = AddLabels allowed
let draftPullRequest prefix = CreatePullRequest(prefix, true)
let commentReview = ReviewPullRequest true
let operationalValue name description unit script = OperationalValue(name, description, unit, script)
