# Droits d'accès à eap-api derrière Identity-Aware Proxy. Voir docs/DEPLOIEMENT.md, section « Connexion des utilisateurs ».
# Prérequis : IAP activé une première fois sur le service depuis la console Cloud Run (onglet Sécurité) : dans un projet
# sans organisation, Google ne permet pas de créer le client OAuth en ligne de commande.
# À exécuter par un propriétaire du projet. Idempotent.
#
#   & .\infra\grant-iap-access.ps1                                  # accès pour vous-même (compte gcloud actif)
#   & .\infra\grant-iap-access.ps1 -Users alice@example.com,bob@example.com

param([string[]] $Users = @())

# Pas de 'Stop' : gcloud écrit ses messages normaux sur stderr (PowerShell 5.1 les prendrait pour des erreurs fatales).
$ErrorActionPreference = 'Continue'
$project = 'eap-portfolio'
$number  = '920819552333'
$region  = 'europe-west9'
$service = 'eap-api'
if ($Users.Count -eq 0) { $Users = @((& gcloud.cmd config get-value account 2>$null).Trim()) }

$grants = @(
    # IAP appelle le service avec sa propre identité : sans ce droit, toutes les requêtes échouent.
    @{ What = "Le proxy IAP peut appeler $service"
       Args = @('run','services','add-iam-policy-binding',$service,"--region=$region",'--role=roles/run.invoker',"--member=serviceAccount:service-$number@gcp-sa-iap.iam.gserviceaccount.com") }
    # Test de santé du déploiement continu : il passe par IAP comme un utilisateur.
    @{ What = "Le déploiement continu peut passer IAP (test de santé)"
       Args = @('iap','web','add-iam-policy-binding','--resource-type=cloud-run',"--service=$service","--region=$region",'--role=roles/iap.httpsResourceAccessor',"--member=serviceAccount:github-deployer@$project.iam.gserviceaccount.com",'--condition=None') }
)
foreach ($u in $Users) {
    $grants += @{ What = "Accès utilisateur : $u"
                  Args = @('iap','web','add-iam-policy-binding','--resource-type=cloud-run',"--service=$service","--region=$region",'--role=roles/iap.httpsResourceAccessor',"--member=user:$u",'--condition=None') }
}

# L'agent de service IAP doit exister avant qu'on lui donne un droit (créé à la première activation, recréé ici au besoin).
& gcloud.cmd beta services identity create --service=iap.googleapis.com "--project=$project" --quiet 2>&1 | Out-Null

$failed = 0
foreach ($g in $grants) {
    for ($try = 1; $try -le 4; $try++) {
        $argv = $g.Args + @("--project=$project", '--quiet')   # arguments passés un par un
        $out = & gcloud.cmd @argv 2>&1 | Out-String
        if ($LASTEXITCODE -eq 0) { Write-Host "OK     $($g.What)" -ForegroundColor Green; break }
        if ($try -lt 4) { Start-Sleep -Seconds 10; continue }
        $failed++
        Write-Host "ÉCHEC  $($g.What)" -ForegroundColor Red
        Write-Host (($out -split "`n" | Where-Object { $_ -match 'ERROR' }) -join "`n")
    }
}

# La clé d'annotation contient des points : lue depuis le JSON plutôt que par un chemin --format.
$iap = (& gcloud.cmd run services describe $service "--region=$region" "--project=$project" --format=json 2>$null | ConvertFrom-Json).metadata.annotations.'run.googleapis.com/iap-enabled'
Write-Host "IAP activé sur le service : $(if ($iap) { $iap } else { 'non détecté (vérifier dans la console)' })"
if ($failed -eq 0) { Write-Host "`nAccès IAP en place." -ForegroundColor Green } else { exit 1 }
