/// The platform service: one process that every surface talks to.
///
///     POST /hooks            capture (HTTP hooks: CLI, Copilot app, cloud agent - no process spawned)
///     POST /hooks?decide=1   decision (cloud agent's bash/curl hook - fail-closed via `|| exit 2`)
///     POST /v1/traces|metrics|logs   OTLP/HTTP from every Copilot client, the SDK and gh-aw
///     GET  /v0.1/servers...  the MCP registry (static files rendered by `agentp render`)
///     GET  /health
///
/// HTTPS only: Copilot clients refuse OTLP over http://. The service issues its own CA on first
/// start (ca.pem) for proof environments; production fronts it with the corporate certificate.
module AgentPlatform.Service

open System
open System.IO
open System.IO.Compression
open System.Net
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open AgentPlatform.HookEvents
open AgentPlatform.Hooks

/// A CA plus a server certificate for the given names, kept in dataDir so restarts keep trust.
let certificates (dataDir: string) (hosts: string list) =
    let caPfx = Path.Combine(dataDir, "ca.pfx")
    if not (File.Exists caPfx) then
        use key = RSA.Create 2048
        let req = CertificateRequest("CN=Agent Platform CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
        req.CertificateExtensions.Add(X509BasicConstraintsExtension(true, false, 0, true))
        req.CertificateExtensions.Add(X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign ||| X509KeyUsageFlags.CrlSign, true))
        use created = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays -1.0, DateTimeOffset.UtcNow.AddYears 2)
        File.WriteAllBytes(caPfx, created.Export(X509ContentType.Pfx, ""))
    let ca = X509CertificateLoader.LoadPkcs12FromFile(caPfx, "", X509KeyStorageFlags.Exportable)
    File.WriteAllText(Path.Combine(dataDir, "ca.pem"), ca.ExportCertificatePem() + "\n")
    use leafKey = RSA.Create 2048
    let req = CertificateRequest($"CN={List.head hosts}", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
    let san = SubjectAlternativeNameBuilder()
    for h in hosts @ [ "localhost" ] |> List.distinct do san.AddDnsName h
    san.AddIpAddress IPAddress.Loopback
    req.CertificateExtensions.Add(san.Build())
    req.CertificateExtensions.Add(X509BasicConstraintsExtension(false, false, 0, false))
    req.CertificateExtensions.Add(X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature ||| X509KeyUsageFlags.KeyEncipherment, true))
    let serverAuth = OidCollection()
    serverAuth.Add(Oid "1.3.6.1.5.5.7.3.1") |> ignore
    req.CertificateExtensions.Add(X509EnhancedKeyUsageExtension(serverAuth, false))
    let serial = RandomNumberGenerator.GetBytes 16
    use issued = req.Create(ca, DateTimeOffset.UtcNow.AddDays -1.0, DateTimeOffset.UtcNow.AddDays 30.0, serial)
    use withKey = issued.CopyWithPrivateKey leafKey
    X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx, ""), "", X509KeyStorageFlags.Exportable)

type Options = {
    Port: int
    DataDir: string
    Hosts: string list
    /// Rendered `endpoints/` directory (MCP registry + hook-rules.json).
    Endpoints: string option
    Rules: HookSet
    Token: string option
}

type Store(dataDir: string) =
    let gate = obj ()
    let counts = Collections.Concurrent.ConcurrentDictionary<string, int>()
    do Directory.CreateDirectory dataDir |> ignore
    member _.Append(stream: string, line: string) =
        lock gate (fun () -> File.AppendAllText(Path.Combine(dataDir, $"{stream}.ndjson"), line + "\n"))
        counts.AddOrUpdate(stream, 1, fun _ n -> n + 1) |> ignore
    member _.Counts = counts

/// The one place a hook call is decided and recorded, whatever transport it came in on.
let handle (store: Store) (rules: HookSet) (eventName: string) (source: string) (body: string) (decide: bool) =
    let sw = Diagnostics.Stopwatch.StartNew()
    let payload =
        try JsonDocument.Parse(if String.IsNullOrWhiteSpace body then "{}" else body).RootElement.Clone()
        with _ -> JsonDocument.Parse(JsonSerializer.Serialize {| raw = body |}).RootElement.Clone()
    match parse (tryParseEvent eventName) source payload with
    | None -> "{}"
    | Some call ->
        // Rules always run (postToolUse context, agentStop keep-going ride on capture hooks too);
        // `decide` only says whether the caller can act on a permission decision.
        let verdict = evaluate rules.Rules call
        let verdict = if decide || not call.Event.CanDecide || call.Event = PostToolUse then verdict else { verdict with Decision = { verdict.Decision with Permission = None } }
        let output = toOutput call verdict.Decision
        let captured =
            match rules.Capture with
            | Some c -> List.contains call.Event c.Events || not verdict.Matched.IsEmpty
            | None -> not verdict.Matched.IsEmpty
        if captured then
            let content = rules.Capture |> Option.map _.IncludeContent |> Option.defaultValue true
            store.Append(
                "hooks",
                JsonSerializer.Serialize {|
                    received_at = DateTimeOffset.UtcNow
                    event = call.Event.Name
                    source = source
                    surface = call.Surface.Text
                    session_id = call.SessionId
                    tool = call.ToolName |> Option.toObj
                    decision = (match verdict.Decision.Permission with Some Allow -> "allow" | Some Deny -> "deny" | Some Ask -> "ask" | None -> null)
                    reason = verdict.Decision.Reason |> Option.toObj
                    rules = verdict.Matched |> List.map _.Name
                    would_have = verdict.WouldHave
                    decided_in_ms = sw.Elapsed.TotalMilliseconds
                    payload = (if content then payload else JsonDocument.Parse("{}").RootElement)
                |})
        output

let private readBody (ctx: HttpContext) =
    task {
        use ms = new MemoryStream()
        if ctx.Request.Headers.ContentEncoding.ToString().Contains "gzip" then
            use gz = new GZipStream(ctx.Request.Body, CompressionMode.Decompress)
            do! gz.CopyToAsync ms
        else
            do! ctx.Request.Body.CopyToAsync ms
        return ms.ToArray()
    }

let run (o: Options) =
    let store = Store o.DataDir
    let cert = certificates o.DataDir o.Hosts
    let mutable rules = o.Rules
    // Hot reload: the deploy workflow drops a new hook-rules.json; no restart needed.
    match o.Endpoints with
    | Some dir when File.Exists(Path.Combine(dir, "hook-rules.json")) ->
        let load () = try rules <- RuleFile.parse (File.ReadAllText(Path.Combine(dir, "hook-rules.json"))) with _ -> ()
        load ()
        let w = new FileSystemWatcher(dir, "hook-rules.json", EnableRaisingEvents = true)
        w.Changed.Add(fun _ -> Thread.Sleep 100; load ())
    | _ -> ()
    let builder = WebApplication.CreateSlimBuilder()
    builder.Logging.SetMinimumLevel LogLevel.Warning |> ignore
    builder.WebHost.ConfigureKestrel(fun k ->
        k.Limits.MaxRequestBodySize <- Nullable(64L * 1024L * 1024L)
        k.ListenAnyIP(o.Port, fun l -> l.UseHttps(cert) |> ignore))
    |> ignore
    let app = builder.Build()
    let authorized (ctx: HttpContext) =
        match o.Token with
        | None -> true
        | Some t ->
            let expected = Encoding.UTF8.GetBytes("Bearer " + t)
            let actual = Encoding.UTF8.GetBytes(ctx.Request.Headers.Authorization.ToString())
            CryptographicOperations.FixedTimeEquals(ReadOnlySpan expected, ReadOnlySpan actual)
    let otlp (stream: string) =
        RequestDelegate(fun ctx ->
            task {
                if not (authorized ctx) then ctx.Response.StatusCode <- 401
                else
                    let! body = readBody ctx
                    let ct = ctx.Request.ContentType |> Option.ofObj |> Option.defaultValue ""
                    if ct.Contains "protobuf" then
                        store.Append(stream, JsonSerializer.Serialize {| received_at = DateTimeOffset.UtcNow; user_agent = string ctx.Request.Headers.UserAgent; protobuf_base64 = Convert.ToBase64String body |})
                        ctx.Response.ContentType <- "application/x-protobuf"
                    else
                        use doc = JsonDocument.Parse body
                        store.Append(stream, JsonSerializer.Serialize {| received_at = DateTimeOffset.UtcNow; user_agent = string ctx.Request.Headers.UserAgent; otlp = doc.RootElement |})
                        ctx.Response.ContentType <- "application/json"
                        do! ctx.Response.WriteAsync "{\"partialSuccess\":{}}"
            }
            :> Task)
    app.MapPost("/v1/traces", otlp "otlp-traces") |> ignore
    app.MapPost("/v1/metrics", otlp "otlp-metrics") |> ignore
    app.MapPost("/v1/logs", otlp "otlp-logs") |> ignore
    app.MapPost(
        "/hooks",
        RequestDelegate(fun ctx ->
            task {
                if not (authorized ctx) then
                    // A deciding command hook exits non-zero on 401 (curl --fail), so an unauthenticated
                    // caller is denied rather than waved through.
                    ctx.Response.StatusCode <- 401
                else
                    let! body = readBody ctx
                    let header (n: string) = ctx.Request.Headers[n].ToString()
                    let ev = header "X-Agentp-Event"
                    let src = match header "X-Agentp-Source" with "" -> "http" | s -> s
                    let decide = ctx.Request.Query.ContainsKey "decide"
                    let out = handle store rules ev src (Encoding.UTF8.GetString body) decide
                    ctx.Response.ContentType <- "application/json"
                    do! ctx.Response.WriteAsync out
            }
            :> Task)
    ) |> ignore
    // MCP registry v0.1, with the CORS headers GitHub requires.
    match o.Endpoints with
    | Some dir ->
        let serve (relative: string) =
            RequestDelegate(fun ctx ->
                task {
                    ctx.Response.Headers["Access-Control-Allow-Origin"] <- "*"
                    ctx.Response.Headers["Access-Control-Allow-Methods"] <- "GET, OPTIONS"
                    ctx.Response.Headers["Access-Control-Allow-Headers"] <- "Authorization, Content-Type"
                    let path = Path.Combine(dir, relative + ".json")
                    if File.Exists path then
                        ctx.Response.ContentType <- "application/json"
                        do! ctx.Response.SendFileAsync path
                    else ctx.Response.StatusCode <- 404
                }
                :> Task)
        app.MapGet("/v0.1/servers", serve "v0.1/servers") |> ignore
        app.MapGet(
            "/v0.1/servers/{**rest}",
            RequestDelegate(fun ctx ->
                let rest = string ctx.Request.RouteValues["rest"]
                // names contain "/": /v0.1/servers/com.acme/jira/versions/latest
                let i = rest.LastIndexOf "/versions/"
                if i < 0 then (ctx.Response.StatusCode <- 404; Task.CompletedTask)
                else
                    let name = Uri.EscapeDataString(Uri.UnescapeDataString(rest.Substring(0, i)))
                    (serve $"v0.1/servers/{name}{rest.Substring i}").Invoke ctx)
        ) |> ignore
    | None -> ()
    app.MapGet("/health", Func<obj>(fun () -> box {| ok = true; rules = rules.Rules.Length; counts = store.Counts |})) |> ignore
    let ca = Path.Combine(o.DataDir, "ca.pem")
    printfn $"agentp service  https://{List.head o.Hosts}:{o.Port}  rules {rules.Rules.Length}  data {Path.GetFullPath o.DataDir}  CA {ca}"
    app.Run()
