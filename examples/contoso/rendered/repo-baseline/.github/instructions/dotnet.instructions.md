---
applyTo: "**/*.cs,**/*.fs,**/*.csproj,**/*.fsproj"
---

# .NET services

- Target net10.0 and enable nullable reference types.
- Use ILogger with structured templates; never string-interpolate log messages.
- Never log request bodies, card numbers, emails or tokens.
