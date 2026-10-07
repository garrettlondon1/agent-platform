---
name: standard-infrastructure
description: Infrastructure as code. Use whenever writing, changing or reviewing files matching **/*.tf, **/*.bicep, deploy/**.
---

Follow these Infrastructure as code rules for files matching **/*.tf, **/*.bicep, deploy/**:

- Every resource carries owner and cost-center tags.
- No public network access unless the pull request explains why.
- Run terraform fmt and validate before proposing a change.
