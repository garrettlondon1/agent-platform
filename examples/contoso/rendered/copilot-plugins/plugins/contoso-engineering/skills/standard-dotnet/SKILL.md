---
name: standard-dotnet
description: .NET services. Use whenever writing, changing or reviewing files matching **/*.cs, **/*.fs, **/*.csproj, **/*.fsproj.
---

Follow these .NET services rules for files matching **/*.cs, **/*.fs, **/*.csproj, **/*.fsproj:

- Target net10.0 and enable nullable reference types.
- Use ILogger with structured templates; never string-interpolate log messages.
- Never log request bodies, card numbers, emails or tokens.
