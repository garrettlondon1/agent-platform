# Copies examples/contoso/Platform.fs into README.md between the full-example markers, so the README's complete
# example is always the one CI compiles, validates and renders. Run after editing the example; a test checks it.
$root = Split-Path $PSScriptRoot -Parent
$readmePath = Join-Path $root 'README.md'
$example = [IO.File]::ReadAllText((Join-Path $root 'examples/contoso/Platform.fs')).Replace("`r`n", "`n").TrimEnd()
$readme = [IO.File]::ReadAllText($readmePath).Replace("`r`n", "`n")
$start = '<!-- full-example:start -->'
$end = '<!-- full-example:end -->'
$i = $readme.IndexOf($start); $j = $readme.IndexOf($end)
if ($i -lt 0 -or $j -lt $i) { throw "README.md needs the $start / $end markers" }
$block = "$start`n" + '```fsharp' + "`n$example`n" + '```' + "`n"
[IO.File]::WriteAllText($readmePath, $readme.Substring(0, $i) + $block + $readme.Substring($j), [Text.UTF8Encoding]::new($false))
