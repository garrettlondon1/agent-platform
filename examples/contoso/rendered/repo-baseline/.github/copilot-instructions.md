# Contoso engineering standards

These instructions are managed centrally by the agent platform. Change them in the platform definition, not here.

## Contoso engineering standards

- Every behaviour change ships with a test that fails without it.
- Branch names and commit subjects start with the Jira key, e.g. PAY-142.
- Never commit secrets, .env files or generated credentials.
- Prefer small pull requests; one concern per pull request.

## File-specific standards

- .NET services: `**/*.cs,**/*.fs,**/*.csproj,**/*.fsproj` (see `.github/instructions/dotnet.instructions.md`)
- Infrastructure as code: `**/*.tf,**/*.bicep,deploy/**` (see `.github/instructions/infrastructure.instructions.md`)
