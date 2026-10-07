/// The DSL's own vocabulary, read from the compiled library: every builder operation, every choice (union
/// case) and every helper function, with its XML documentation. `agentp vocabulary` prints it, so people and
/// the platform-change agent work from what this version of AgentPlatform actually accepts - never a guess.
module AgentPlatform.Vocabulary

open System
open System.IO
open System.Reflection
open System.Text
open System.Xml.Linq
open Microsoft.FSharp.Core.CompilerServices
open Microsoft.FSharp.Reflection

let private assembly = typeof<AgentPlatform.Platform.Platform>.Assembly

let private docs =
    lazy (
        let xml = Path.ChangeExtension(assembly.Location, ".xml")
        if File.Exists xml then
            XDocument.Load(xml).Descendants(XName.Get "member")
            |> Seq.map (fun m ->
                let summary = m.Element(XName.Get "summary")
                m.Attribute(XName.Get "name").Value, (if isNull summary then "" else summary.Value))
            |> Seq.map (fun (k, v) -> k, String.Join(" ", v.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries) |> Array.map _.Trim()))
            |> Seq.toList
        else [])

let private doc (prefixes: string list) =
    docs.Value
    |> List.tryFind (fun (k, _) -> prefixes |> List.exists (fun p -> k = p || k.StartsWith(p + "(")))
    |> Option.map snd
    |> Option.filter (String.IsNullOrWhiteSpace >> not)

let private typeName (t: Type) =
    let rec go (t: Type) =
        if t.IsGenericType then
            let args = t.GetGenericArguments() |> Array.map go
            let name = t.Name.Split('`')[0]
            match name, args with
            | "FSharpList", [| a |] -> $"{a} list"
            | "FSharpOption", [| a |] -> $"{a} option"
            | "Tuple", xs -> "(" + String.Join(" * ", xs) + ")"
            | n, xs -> n + "<" + String.Join(", ", xs) + ">"
        else
            match t.Name with
            | "String" -> "string" | "Int32" -> "int" | "Boolean" -> "bool" | "Double" -> "float" | n -> n
    go t

let private isModule (t: Type) =
    t.GetCustomAttributes(typeof<CompilationMappingAttribute>, false)
    |> Seq.cast<CompilationMappingAttribute>
    |> Seq.exists (fun a -> a.SourceConstructFlags = SourceConstructFlags.Module)

let markdown () =
    let sb = StringBuilder()
    let line (s: string) = sb.AppendLine s |> ignore
    let types = assembly.GetExportedTypes() |> Array.sortBy _.FullName
    line $"# AgentPlatform {assembly.GetName().Version} vocabulary"
    line ""
    line "## Builders (the `name { ... }` blocks and the operations they accept)"
    for t in types |> Array.filter (fun t -> t.Name.EndsWith "Builder") do
        line ""
        let block = t.Name.Replace("Builder", "").ToLowerInvariant()
        line $"### {block} {{ }}  ({t.FullName})"
        for m in t.GetMethods() |> Array.sortBy _.MetadataToken do
            match m.GetCustomAttribute<CustomOperationAttribute>() |> box |> Option.ofObj with
            | None -> ()
            | Some o ->
                let op = o :?> CustomOperationAttribute
                let args = m.GetParameters() |> Array.skip 1 |> Array.map (fun p -> typeName p.ParameterType)
                let signature = if args.Length = 0 then op.Name else op.Name + " " + String.Join(" ", args |> Array.map (fun a -> $"<{a}>"))
                let d = doc [ $"M:{t.FullName}.{m.Name}" ] |> Option.map (fun d -> " - " + d) |> Option.defaultValue ""
                line $"- `{signature}`{d}"
    line ""
    line "## Choices (union types: write the case name)"
    for t in types |> Array.filter (fun t -> FSharpType.IsUnion(t, BindingFlags.Public) && not t.IsGenericType) do
        let cases = FSharpType.GetUnionCases t
        if cases.Length > 1 then
            line ""
            line $"### {t.Name}  ({t.FullName})"
            for c in cases do
                let fields = c.GetFields() |> Array.map (fun f -> $"{f.Name}: {typeName f.PropertyType}")
                let shape = if fields.Length = 0 then c.Name else c.Name + " of " + String.Join(" * ", fields)
                let d = doc [ $"P:{t.FullName}.{c.Name}"; $"M:{t.FullName}.New{c.Name}"; $"T:{t.FullName}.{c.Name}" ] |> Option.map (fun d -> " - " + d) |> Option.defaultValue ""
                line $"- `{shape}`{d}"
    line ""
    line "## Helper functions"
    for t in types |> Array.filter isModule do
        let fns =
            t.GetMethods(BindingFlags.Public ||| BindingFlags.Static)
            |> Array.filter (fun m -> not m.IsSpecialName && not (m.Name.StartsWith "get_"))
            |> Array.sortBy _.MetadataToken
        if fns.Length > 0 then
            line ""
            line $"### {t.FullName}"
            for m in fns do
                let args = m.GetParameters() |> Array.map (fun p -> $"({p.Name}: {typeName p.ParameterType})")
                let d = doc [ $"M:{t.FullName}.{m.Name}" ] |> Option.map (fun d -> " - " + d) |> Option.defaultValue ""
                let shown = String.Join(" ", args)
                line $"- `{m.Name} {shown}` -> {typeName m.ReturnType}{d}"
    sb.ToString()
