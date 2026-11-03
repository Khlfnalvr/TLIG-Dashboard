# Applies the notes in this folder to the GitHub releases (title + body).
# Requires GitHub CLI: gh auth login
$ErrorActionPreference = "Stop"
$repo = "Khlfnalvr/TLIG-Dashboard"
Get-Content "$PSScriptRoot\titles.tsv" -Encoding UTF8 | ForEach-Object {
    $tag, $title = $_ -split "`t", 2
    gh release edit $tag --repo $repo --title $title --notes-file "$PSScriptRoot\$tag.md"
    Write-Host "Updated $tag"
}
