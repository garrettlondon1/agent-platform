/// Small helpers for writing JSON that reads well in a pull request: keys stay in the
/// order they were written, and nothing appears that the author did not say.
module AgentPlatform.Json

open System.Text.Json
open System.Text.Json.Nodes

let str (s: string) : JsonNode = JsonValue.Create s
let bool (b: bool) : JsonNode = JsonValue.Create b
let num (n: int) : JsonNode = JsonValue.Create n

let arr (items: JsonNode seq) : JsonNode =
    let a = JsonArray()
    for i in items do a.Add i
    a

let strs (items: string seq) = items |> Seq.map str |> arr

/// An object from (key, value) pairs; `None` values are dropped so optional settings
/// that were never set never show up in the output.
let obj (pairs: (string * JsonNode option) list) : JsonNode =
    let o = JsonObject()
    for k, v in pairs do
        match v with
        | Some node -> o[k] <- node
        | None -> ()
    o

let objOf (pairs: (string * JsonNode) seq) : JsonNode =
    pairs |> Seq.map (fun (k, v) -> k, Some v) |> List.ofSeq |> obj

let private options =
    JsonSerializerOptions(WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

let render (node: JsonNode) = node.ToJsonString options + "\n"

let parse (text: string) = JsonNode.Parse text

/// Reads a property path from a JsonElement without throwing.
let tryProp (path: string list) (e: JsonElement) =
    let rec go (e: JsonElement) path =
        match path with
        | [] -> Some e
        | p :: rest ->
            if e.ValueKind = JsonValueKind.Object then
                match e.TryGetProperty(p: string) with
                | true, v -> go v rest
                | _ -> None
            else None
    go e path

let tryString path e =
    tryProp path e
    |> Option.bind (fun v ->
        match v.ValueKind with
        | JsonValueKind.String -> Some(v.GetString())
        | JsonValueKind.Number -> Some(v.GetRawText())
        | JsonValueKind.True -> Some "true"
        | JsonValueKind.False -> Some "false"
        | _ -> None)

let tryFloat path e =
    tryProp path e
    |> Option.bind (fun v ->
        match v.ValueKind with
        | JsonValueKind.Number -> Some(v.GetDouble())
        | JsonValueKind.String ->
            match System.Double.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture) with
            | true, d -> Some d
            | _ -> None
        | _ -> None)
