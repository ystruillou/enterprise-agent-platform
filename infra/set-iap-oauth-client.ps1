# Applique un client OAuth (créé dans APIs & Services > Identifiants) à IAP pour le service eap-api.
# Nécessaire dans un projet sans organisation : Google ne fournit pas de client OAuth géré.
# Le secret est saisi masqué, écrit le temps d'un appel dans un fichier temporaire, puis supprimé. Jamais affiché.
#
#   & .\infra\set-iap-oauth-client.ps1 -ClientId 1234567890-abc.apps.googleusercontent.com

param([Parameter(Mandatory)] [string] $ClientId)

$ErrorActionPreference = 'Continue'   # gcloud écrit ses messages normaux sur stderr
$project = 'eap-portfolio'
$region  = 'europe-west9'
$service = 'eap-api'

$secure = Read-Host 'Code secret du client OAuth (saisie masquée)' -AsSecureString
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
$file = Join-Path $env:TEMP ("iap-oauth-" + [guid]::NewGuid() + ".yaml")
try {
    $secret = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    if ([string]::IsNullOrWhiteSpace($secret)) { Write-Host 'Secret vide : rien n''est modifié.' -ForegroundColor Yellow; exit 1 }
    # Guillemets simples YAML (une apostrophe s'y écrit doublée) : le secret est pris tel quel.
    $yaml = "accessSettings:`n  oauthSettings:`n    clientId: '$($ClientId.Trim())'`n    clientSecret: '$($secret.Trim().Replace("'", "''"))'`n"
    [IO.File]::WriteAllText($file, $yaml, (New-Object Text.UTF8Encoding($false)))
    $out = & gcloud.cmd iap settings set $file "--project=$project" '--resource-type=cloud-run' "--region=$region" "--service=$service" --quiet 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0) { Write-Host "OK     Client OAuth appliqué à IAP pour $service" -ForegroundColor Green }
    else {
        # On n'affiche que les lignes d'erreur, jamais la réponse complète (qui pourrait reprendre la configuration).
        Write-Host 'ÉCHEC' -ForegroundColor Red
        Write-Host (($out -split "`n" | Where-Object { $_ -match 'ERROR' -and $_ -notmatch 'clientSecret' }) -join "`n")
        exit 1
    }
}
finally {
    Remove-Item $file -Force -ErrorAction SilentlyContinue
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    $secret = $null
}
Write-Host 'Attendez une minute, puis ouvrez https://eap-api-920819552333.europe-west9.run.app'
