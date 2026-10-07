# Acme Corp agent platform

Every GitHub Copilot agent Acme Corp runs is governed from `Platform.fs`: in VS Code, Visual Studio, JetBrains and the
CLI, through the Copilot SDK, as Copilot cloud agent, and as agentic workflows.

```text
dotnet run -- validate        check the definition
dotnet run -- render          write every asset to .platform/
dotnet run -- coverage        which control reaches which surface
dotnet run -- plan            what `apply` would change in GitHub settings
dotnet run -- serve           run the platform service locally (hook decisions, OTLP, MCP registry)
dotnet run -- hook-test preToolUse < payload.json
dotnet run -- drift --docs <github/docs clone>
```

## How a change reaches developers

1. Edit `Platform.fs` in a pull request.
2. CI validates, renders, and comments the assets that change and the GitHub settings plan.
3. On merge, CI:
   - publishes `copilot-plugins`, `.github-private`, `agentic-workflows` and `platform-endpoints` as commits;
   - compiles the agentic workflows with `gh aw compile --strict`;
   - applies GitHub settings (configuration source, rulesets, cloud agent access, content exclusion);
   - optionally opens a baseline pull request in every repository.
4. Copilot clients pick up managed settings within about an hour. Restarting the client or signing in again makes
   them load immediately.

## One-time setup

| # | Step | Who |
| --- | --- | --- |
| 1 | Create this repository from `dotnet new agent-platform`, set `organization`, `enterprise` and `platformHost` in `Platform.fs` | platform admin |
| 2 | Pilot from your machine: `gh auth refresh -s admin:org,admin:enterprise,copilot`, then `dotnet run -- publish` (creates and fills `.github-private`, `copilot-plugins`, `agentic-workflows`, `platform-endpoints`) and `dotnet run -- apply` (rulesets and settings). Start at `rollout Observe` | org owner |
| 3 | Production: create a GitHub App on the organization (permissions below) and install it. Store `PLATFORM_APP_ID` as a variable and `PLATFORM_APP_KEY` as a secret in a `production` environment with required reviewers, and set `deployedBy (PlatformApp <id>)`. CI then deploys every merge and re-applies everything every six hours | org owner |
| 4 | Enterprise settings > AI controls > Agents > Configuration source: select this organization (or let `apply` do it with an enterprise-scoped token) | enterprise owner |
| 5 | Host the platform service at `platformHost`: `dotnet run -- serve --endpoints <platform-endpoints checkout>` behind your load balancer and certificate | platform team |
| 6 | Work through the `manual` lines `plan` prints (MCP registry URL, cloud agent firewall, organization custom instructions) | org owner |
| 7 | Optional: push `workstation/` through Intune/Jamf for device-level enforcement, which also covers machines whose Copilot licence comes from somewhere else | IT |

GitHub App permissions (exactly what the deploy job requests): repository *Contents*, *Workflows* and *Pull requests*
(read & write); organization *Administration* (rulesets), *Copilot agent settings*, *Copilot content exclusion*, *Agent
variables* and *Actions variables* (read & write). Selecting the configuration source needs an enterprise owner token
with *Enterprise AI controls* (write); the rendered repositories are created once by an owner (step 2).

## Upgrading

Dependabot proposes new `AgentPlatform` versions. A new version models new GitHub settings and fixes rendering. The PR
comment shows exactly which assets the upgrade changes before you merge.
