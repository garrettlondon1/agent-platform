---
name: standard-engineering
description: Contoso engineering standards. Use whenever writing, changing or reviewing any code.
---

Follow these Contoso engineering standards rules for any code:

- Every behaviour change ships with a test that fails without it.
- Branch names and commit subjects start with the Jira key, e.g. PAY-142.
- Never commit secrets, .env files or generated credentials.
- Prefer small pull requests; one concern per pull request.
