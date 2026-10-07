Contoso engineering standards:
- Every behaviour change ships with a test that fails without it.
- Branch names and commit subjects start with the Jira key, e.g. PAY-142.
- Never commit secrets, .env files or generated credentials.
- Prefer small pull requests; one concern per pull request.

.NET services (for **/*.cs,**/*.fs,**/*.csproj,**/*.fsproj):
- Target net10.0 and enable nullable reference types.
- Use ILogger with structured templates; never string-interpolate log messages.
- Never log request bodies, card numbers, emails or tokens.

Infrastructure as code (for **/*.tf,**/*.bicep,deploy/**):
- Every resource carries owner and cost-center tags.
- No public network access unless the pull request explains why.
- Run terraform fmt and validate before proposing a change.
