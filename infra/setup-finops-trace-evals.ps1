# Infrastructure de trois fonctions : registre de consommation persistant (Firestore), traces (Cloud Trace),
# barrière d'évaluation avant production (compte github-evaluator), plus le rôle administrateur de l'application.
# Voir docs/DEPLOIEMENT.md. À exécuter par un propriétaire du projet, AVANT de pousser le code qui s'en sert.
# Idempotent : chaque ressource est créée seulement si elle n'existe pas, chaque droit peut être réattribué sans risque.
#
#   & .\infra\setup-finops-trace-evals.ps1                       # administrateur = compte gcloud actif
#   & .\infra\setup-finops-trace-evals.ps1 -Admins a@x.com,b@y.com

param([string[]] $Admins = @())

$ErrorActionPreference = 'Continue'   # gcloud écrit ses messages normaux sur stderr ; le succès se lit à $LASTEXITCODE
$project  = 'eap-portfolio'
$region   = 'europe-west9'
$database = 'eap-finops'
$runtime  = "eap-api-runtime@$project.iam.gserviceaccount.com"
$evaluator = "github-evaluator@$project.iam.gserviceaccount.com"
# Seul ce dépôt (identifiant numérique), et seulement main (condition du fournisseur github-oidc), peut emprunter le compte.
$github   = 'principalSet://iam.googleapis.com/projects/920819552333/locations/global/workloadIdentityPools/github/attribute.repository_id/1449833259'
if ($Admins.Count -eq 0) { $Admins = @((& gcloud.cmd config get-value account 2>$null).Trim()) }

$failed = 0
function Step([string] $what, [string[]] $gcloudArgs, [int] $tries = 4) {
    for ($try = 1; $try -le $tries; $try++) {
        $argv = $gcloudArgs + @("--project=$project", '--quiet')   # arguments passés un par un
        $out = & gcloud.cmd @argv 2>&1 | Out-String
        if ($LASTEXITCODE -eq 0) { Write-Host "OK     $what" -ForegroundColor Green; return }
        if ($try -lt $tries) { Start-Sleep -Seconds 10; continue }
        $script:failed++
        Write-Host "ÉCHEC  $what" -ForegroundColor Red
        Write-Host (($out -split "`n" | Where-Object { $_ -match 'ERROR' }) -join "`n")
    }
}
function Exists([string[]] $gcloudArgs) {
    $argv = $gcloudArgs + @("--project=$project", '--quiet')
    & gcloud.cmd @argv 2>&1 | Out-Null
    return $LASTEXITCODE -eq 0
}

Write-Host "`n--- Services" -ForegroundColor Cyan
Step 'API Firestore, Telemetry (export OTLP) et Cloud Trace (consultation)' @('services','enable','firestore.googleapis.com','telemetry.googleapis.com','cloudtrace.googleapis.com') 1

Write-Host "`n--- Registre de consommation (Firestore, Paris)" -ForegroundColor Cyan
if (Exists @('firestore','databases','describe',"--database=$database")) { Write-Host "OK     Base $database déjà présente" -ForegroundColor Green }
else { Step "Base Firestore $database ($region), protégée contre la suppression" @('firestore','databases','create',"--database=$database","--location=$region",'--type=firestore-native','--delete-protection') 2 }
# Rétention : chaque document porte expireAt (7 jours pour les compteurs par utilisateur, ~13 mois pour les agrégats).
foreach ($group in 'usage_daily','usage_user_daily') {
    Step "Suppression automatique (TTL) de $group à expireAt" @('firestore','fields','ttls','update','expireAt',"--collection-group=$group","--database=$database",'--enable-ttl','--async')
}
Step "API : écriture dans Firestore (rôle datastore.user)" @('projects','add-iam-policy-binding',$project,"--member=serviceAccount:$runtime",'--role=roles/datastore.user','--condition=None')

Write-Host "`n--- Traces (Cloud Trace)" -ForegroundColor Cyan
Step "API : envoi de traces (rôle telemetry.tracesWriter)" @('projects','add-iam-policy-binding',$project,"--member=serviceAccount:$runtime",'--role=roles/telemetry.tracesWriter','--condition=None')

Write-Host "`n--- Barrière d'évaluation (github-evaluator)" -ForegroundColor Cyan
if (Exists @('iam','service-accounts','describe',$evaluator)) { Write-Host "OK     Compte github-evaluator déjà présent" -ForegroundColor Green }
else { Step 'Compte github-evaluator (lecture des 2 clés d''API, rien d''autre)' @('iam','service-accounts','create','github-evaluator','--display-name=GitHub Actions evaluator','--description=Lit les cles d API pour les campagnes d evaluation ; rien d autre') 1 }
Step 'Ce dépôt GitHub (main) peut emprunter github-evaluator' @('iam','service-accounts','add-iam-policy-binding',$evaluator,'--role=roles/iam.workloadIdentityUser',"--member=$github",'--condition=None')
foreach ($secret in 'gemini-api-key','anthropic-api-key') {
    Step "github-evaluator peut lire le secret $secret (et seulement lui)" @('secrets','add-iam-policy-binding',$secret,"--member=serviceAccount:$evaluator",'--role=roles/secretmanager.secretAccessor','--condition=None')
}

Write-Host "`n--- Administrateurs de l'application (tableau FinOps)" -ForegroundColor Cyan
# Variables d'environnement de Cloud Run, hors du dépôt public ; conservées par les déploiements suivants.
$envVars = (0..($Admins.Count - 1) | ForEach-Object { "Auth__Admins__$_=$($Admins[$_].Trim())" }) -join ','
Step "Administrateur(s) : $($Admins -join ', ')" @('run','services','update','eap-api',"--region=$region","--update-env-vars=$envVars") 1

if ($failed -eq 0) { Write-Host "`nTout est en place. Le code qui s'en sert peut être déployé." -ForegroundColor Green }
else { Write-Host "`n$failed étape(s) en échec : relancer le script (il est idempotent)." -ForegroundColor Yellow; exit 1 }
