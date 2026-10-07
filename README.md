# AgentPlatform

Govern every GitHub Copilot agent your organisation runs, from one strongly typed F# file.

One `platform { }` definition renders every asset GitHub, the Copilot clients and GitHub Agentic Workflows read:
enterprise managed settings, team overrides, plugins, skills, hooks, MCP allowlists and the MCP registry, custom agents,
coding standards, rulesets and agentic workflows. The output is plain files in a versioned, deterministic tree you commit
to git.

> **Status.** A community project, not an official GitHub product. It is built only on documented GitHub features, several
> of which (enterprise managed settings, the MCP registry, GitHub Agentic Workflows) are in public preview and change
> often. The `drift` command and the daily `docs-drift` workflow track those changes so the typed model can keep up.

| Surface | How it is governed |
| --- | --- |
| VS Code, Visual Studio, JetBrains, Copilot app, Copilot CLI | `managed-settings.json` (+ team files), plugins installed by `enabledPlugins`, `.github-private` agents, repository instructions and hooks, MCP registry |
| Copilot SDK | `Sdk.sessionConfig`: the same policy, plugins, standards, agents and hook rules, applied in-process |
| Copilot cloud agent | `.github-private` agents, repository hooks (`bash`), cloud agent MCP configuration, rulesets |
| GitHub Agentic Workflows | rendered `.github/workflows/*.md` that import the same agents, plugins, MCP servers and evals |

## Quick start

Prerequisites: the [.NET 10 SDK](https://dotnet.microsoft.com/download), the [GitHub CLI](https://cli.github.com) and, for
agentic workflows, its [gh-aw](https://github.com/github/gh-aw) extension. The Copilot SDK dependency downloads the matching
Copilot CLI runtime at build time.

```fsharp
open AgentPlatform.Policy
open AgentPlatform.Hooks
open AgentPlatform.Mcp
open AgentPlatform.Standards
open AgentPlatform.Plugins
open AgentPlatform.Agents
open AgentPlatform.Platform

let guardrails =
    hooks {
        rule (beforeTool "no-force-push" (shellCommand "git push --force") (block "Push a new commit instead."))
        rule (beforeTool "no-secrets" (reading "**/.env") (block "Secrets are off limits to agents."))
        rule (beforeTool "prod" (shellCommand "terraform apply") (requireApproval "Production changes need a person."))
        captureEverything
    }

let reviewer =
    agent {
        named "reviewer"
        useWhen "Review a diff against our standards."
        instructions "Return PASS or FAIL per standard, with file and line."
        uses [ readFiles; searchFiles ]
        follows [ "engineering" ]
        evaluatedBy [ question "cited" "Did every FAIL cite a file and line?" ]
    }

let acme =
    platform {
        organization "acme"
        platformHost "https://agents.acme.com"
        governedBy (policy { modelByDefault "auto"; noBypassModeByDefault; denyByDefault [ read "~/.ssh/**" ]; allowByDefault [ read "/**"; edit "/src/**" ] })
        standards [ standard { named "engineering"; rules [ "Every behaviour change ships with a test." ] } ]
        hooks guardrails
        plugins [ plugin { named "acme-governance"; describedAs "Central hooks."; carriesHooks } ]
        agents [ reviewer ]
    }

[<EntryPoint>]
let main argv = AgentPlatform.Cli.run acme argv
```

```text
dotnet run -- validate        every problem with the definition, before anything is written
dotnet run -- render          write every asset to .platform/ (deterministic, with platform.lock.json)
dotnet run -- diff --against <dir>   what changed since a previous render
dotnet run -- coverage        which control reaches which surface
dotnet run -- vocabulary      every builder, operation and choice this version of the DSL accepts
dotnet run -- plan            what GitHub settings would change (read-only; unreadable settings show as ACCESS)
dotnet run -- apply           apply that plan (rulesets, cloud agent access, content exclusion, variables, ...)
dotnet run -- publish         commit each rendered tree into the repository that serves it (gh CLI, org owner)
dotnet run -- serve           the platform service: hook decisions, OTLP collector, MCP registry
dotnet run -- hook-test preToolUse < payload.json
dotnet run -- sdk <agent> "<prompt>"
dotnet run -- drift --docs <github/docs clone> --gh-aw <github/gh-aw clone>
dotnet run -- evals --data <service data dir> [--aw-logs <gh aw logs dir>] [--judge]
```

See `examples/contoso/Platform.fs` for a complete organisation.

## Adopting it in your company

Until the packages are on nuget.org, build them from this repository and install the template:

```text
git clone https://github.com/garrettlondon1/agent-platform && cd agent-platform
dotnet pack src/AgentPlatform -c Release -o out/packages
dotnet pack templates/AgentPlatform.Templates.csproj -c Release -o out/packages
dotnet new install out/packages/AgentPlatform.Templates.0.2.0.nupkg
dotnet new agent-platform -n Contoso.Platform --org contoso --enterprise contoso --host https://agents.contoso.com --company "Contoso"
```

In the new repository, copy `out/packages/AgentPlatform.0.2.0.nupkg` into a `packages/` folder and add a `nuget.config`
that lists it as a source (`<add key="agent-platform-local" value="packages" />`), so CI restores the same package.

The template gives you a repository with `Platform.fs` (a working starter definition), a CI/CD workflow, the
`platform-change` agentic workflow and Dependabot. Then, once:

1. **Pilot from your machine.** As an organization owner (`gh auth refresh -s admin:org,admin:enterprise,copilot`):
   `dotnet run -- publish` creates and fills `.github-private`, `copilot-plugins`, `agentic-workflows` and
   `platform-endpoints`; `dotnet run -- apply` applies rulesets and settings. Start at `rollout Observe`.
2. **Production deploys.** Create a GitHub App on the organization and install it. Store its ID as the `PLATFORM_APP_ID`
   variable and its key as the `PLATFORM_APP_KEY` secret in a `production` environment with required reviewers, then set
   `deployedBy (PlatformApp <id>)`. CI deploys every merge and re-applies everything every six hours. The App needs
   *Contents*, *Workflows* and *Pull requests* (write) on repositories, and organization *Administration*, *Copilot agent
   settings*, *Copilot content exclusion*, *Agent variables* and *Actions variables* (write).
3. **Configuration source.** An enterprise owner selects the organization under Enterprise settings > AI controls >
   Agents (or runs `apply` with an enterprise-scoped token).
4. **Platform service.** Host `dotnet run -- serve --endpoints <platform-endpoints checkout>` at `platformHost`, behind
   your load balancer and certificate. It answers hook decisions, collects OTLP and serves the MCP registry.
5. **Manual steps.** `plan` prints the few settings that have no API: the MCP registry URL, the cloud agent firewall
   allowlist and organization custom instructions.
6. **Optional device enforcement.** Push `workstation/` through Intune or Jamf. Device-level settings take precedence
   over server-managed ones and also cover machines whose Copilot licence comes from another organization.

Server-managed settings reach only people whose Copilot usage is billed to your enterprise ("Usage billed to" in their
Copilot settings). Everyone else is unaffected until they switch, or until device-level settings reach their machine.

After that, every change is a pull request to `Platform.fs`. CI comments the assets that change and the settings plan;
merging deploys it.

## Rolling out safely, and keeping it tamper-proof

**One line sets how hard it bites.** `rollout Observe | Guard | Enforce` - the same definition at every stage:

| Stage | Managed settings | Hooks | Your rulesets |
| --- | --- | --- | --- |
| `Observe` | plugins, skills, agents, model, telemetry, MCP denylist. No permission rules, approvals, allowlists, strict marketplaces or sandbox | capture only; blocking rules record `would deny: ...` and never block | `evaluate` |
| `Guard` | everything, but the sandbox is never *required* | blocking | `evaluate` |
| `Enforce` | exactly as written | blocking | `active` |

**Pilot ring.** `enforceHooksOnlyFor [ "enterprise-team" ]` gives the hook-carrying plugins (fail-closed decisions) to
that enterprise team only, through a generated team file; everyone else gets every other plugin and setting. Repository
hooks are withheld while piloting, because a repository file reaches everyone who works in it.

**Nobody edits governance by hand - at every stage.** `staged` always adds two active rulesets:

- `agent-platform-rendered-repositories`: only the deployer (`deployedBy (PlatformApp id)`, or organization owners for a
  gh CLI pilot) can update the default branch of `.github-private`, the marketplace, `agentic-workflows` and
  `platform-endpoints`. Anyone can still open a pull request to suggest something; nothing lands except a deploy.
- `agent-platform-definition`: the definition repository changes only by pull request with an approving review and a green
  `render` check. With a platform App, not even owners can push; with `OrganizationOwners`, owners may only bypass by
  merging a pull request.

The deploy also runs every six hours, so a setting changed in the UI is put back.

**Changing it by issue.** Open an issue describing the change; someone with write access labels it `platform-change`.
The `platform-change` agentic workflow reads `agentp vocabulary` (generated from the exact AgentPlatform version the
repository builds against), edits `Platform.fs` - and only `Platform.fs` (`allowed-files`) - validates, renders, and opens a
draft pull request with the rendered-asset diff. A human reviews and merges it like any other change. Compile it once
with `gh aw compile` and commit the lock file.

**`plan` never guesses.** A setting the token cannot read is reported as `ACCESS` with the scope it needs, and `apply`
refuses to run on an incomplete plan.

## Publishing the packages

```text
dotnet pack src/AgentPlatform -c Release -o out/packages
dotnet pack templates/AgentPlatform.Templates.csproj -c Release -o out/packages
dotnet nuget push "out/packages/*.nupkg" --source <nuget.org or your internal feed> --api-key <key>
```

Bump `<Version>` in both projects for every release; NuGet caches a version forever.
## The vocabulary

| Builder | Says | Renders |
| --- | --- | --- |
| `policy { }` | every documented managed-settings key; `deny` enforces, `denyByDefault` lets enterprise teams override | `managed-settings.json`, team files, file-based and MDM payloads |
| `standard { }` | coding standards, optionally `appliesTo` globs | `copilot-instructions.md`, `*.instructions.md`, `AGENTS.md`, plugin skills, SDK system message, org instructions text |
| `mcpServer { }` / `mcp { }` | the approved MCP catalog and the blocklist | `allowedMcpServers` / `deniedMcpServers`, a v0.1 MCP registry, cloud agent MCP config, agent `mcp-servers`, plugin `.mcp.json`, gh-aw `mcp-servers:` |
| `hooks { }` | rules (`beforeTool`, `afterTool`, `on`) and capture | plugin `hooks.json`, machine `policy.d`, repository `.github/hooks`, `hook-rules.json` for the service |
| `skill { }` / `plugin { }` | skills and the bundles that carry everything to every machine | the marketplace repository |
| `agent { }` | a custom agent, its tools, MCP servers, standards and evals | `.github-private/agents/*.md`, plugin agents, SDK `CustomAgents` |
| `workflow { }` | the same agent on an event | gh-aw workflow markdown that compiles with `gh aw compile --strict` |
| `ruleset { }` / `githubSettings { }` | rulesets, content exclusion, cloud agent access and firewall | REST request bodies under `github/` |

## Output

```text
.github-private/      the enterprise "Configuration source": managed settings, team overrides, agents
copilot-plugins/      the marketplace every plugin installs from
repo-baseline/        instructions, AGENTS.md and .github/hooks for every repository
agentic-workflows/    gh-aw workflows, the shared governance import, gh aw env defaults
endpoints/            the MCP registry (v0.1) and hook-rules.json, served by the platform service
workstation/          file-based and MDM managed settings, machine-wide policy hooks
github/               REST bodies: rulesets, content exclusion, cloud agent configuration
coverage.json         control x surface matrix
platform.lock.json    version and SHA-256 of every file above
```

## Hooks, centralised

Admins write rules once. The platform decides how each rule reaches each surface:

| Event kind | Rendered as | Cost | On failure |
| --- | --- | --- | --- |
| deciding (`preToolUse`, `permissionRequest`) | `exec curl.exe` (CLI, app), `bash` curl (repository / cloud agent) to `/hooks?decide=1` | one small process, no shell, nothing to install | **closed**: an unreachable service denies the tool call |
| observing (session, prompt, tool results, stop, errors) | HTTP hook | no process at all | open |
| any, in Copilot SDK sessions | `SessionHooks`, same rule engine in-process | none | n/a |

Copilot refuses HTTP hooks whose URL resolves to a loopback, private or link-local address. For a platform host on an
internal network, add `captureOverCurl` to `hooks { }`; `validate` flags the problem if you forget.

## Staying current

`Drift.known` is the surface the DSL models. `drift` extracts the same surface from the current GitHub documentation
(managed settings keys and clients, overridable keys, sandbox and telemetry sub-keys, hook events, plugin and marketplace
fields, agent frontmatter, ruleset rule types, MCP registry surfaces, policy names, Copilot REST endpoints) and from gh-aw
(frontmatter fields, reserved tool names), and exits `2` on drift.

`.github/workflows/docs-drift.yml` runs it daily and opens (or updates) an issue with the report when something drifted.
If the repository variable `AGENTP_SELF_UPDATE` is `true` and a `COPILOT_GITHUB_TOKEN` secret (a fine-grained token with
Copilot Requests) is set, it also starts `agentp-self-update`, an agentic workflow that reads the documentation passage
behind each change, models it as typed F#, updates `Drift.known`, runs the tests and opens a draft pull request.

## What cannot be governed centrally (as documented)

- Cloud agent ignores `permissions.*`, `allowedMcpServers`, `deniedMcpServers`, `sandbox`, `telemetry` and the MCP
  registry. It is governed by repository hooks, its MCP configuration, its firewall and rulesets instead.
- Hooks do not run in Visual Studio, JetBrains, Eclipse or Xcode; managed settings and the MCP registry do.
- "Registry only" MCP enforcement matches server names and can be bypassed locally; the managed-settings allowlist is the
  enforced control (both are rendered).
- Organization custom instructions have no documented REST API; the text is rendered for an admin to paste.
- Command hook timeouts always fail open, even for `preToolUse` (hooks reference).

## License

[MIT](LICENSE). Contributions welcome: open an issue or a pull request; CI must pass.
