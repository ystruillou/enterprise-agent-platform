# Donne au compte github-deployer les 5 droits nécessaires au déploiement continu, chacun sur la ressource
# la plus étroite possible. Voir docs/DEPLOIEMENT.md, section « Déploiement continu ».
# À exécuter par un propriétaire du projet, après gcloud auth login. Idempotent : peut être relancé sans risque.

# Pas de 'Stop' : gcloud écrit ses messages normaux sur stderr, que PowerShell 5.1 prendrait pour une erreur fatale.
# Le succès se lit au code de retour ($LASTEXITCODE).
$ErrorActionPreference = 'Continue'
$project  = 'eap-portfolio'
$region   = 'europe-west9'
$deployer = "github-deployer@$project.iam.gserviceaccount.com"
$runtime  = "eap-api-runtime@$project.iam.gserviceaccount.com"
$member   = "serviceAccount:$deployer"
# Seul ce dépôt GitHub (identifiant numérique, insensible aux renommages) peut emprunter le compte.
# La branche est restreinte en amont : le fournisseur github-oidc a pour condition
#   assertion.repository_id == '1449833259' && assertion.ref == 'refs/heads/main'
# donc un jeton émis pour une autre branche ou une PR est refusé dès l'échange, avant même cette liaison.
$github   = 'principalSet://iam.googleapis.com/projects/920819552333/locations/global/workloadIdentityPools/github/attribute.repository_id/1449833259'

$grants = @(
    @{ What = '1/5 Ce dépôt GitHub peut emprunter github-deployer'
       Args = @('iam','service-accounts','add-iam-policy-binding',$deployer,'--role=roles/iam.workloadIdentityUser',"--member=$github",'--condition=None') }
    @{ What = "2/5 Pousser des images dans le seul dépôt d'images du projet"
       Args = @('artifacts','repositories','add-iam-policy-binding','cloud-run-source-deploy',"--location=$region",'--role=roles/artifactregistry.writer',"--member=$member",'--condition=None') }
    @{ What = '3/5 Déployer le seul service eap-api'
       Args = @('run','services','add-iam-policy-binding','eap-api',"--region=$region",'--role=roles/run.developer',"--member=$member") }
    @{ What = '4/5 Appeler eap-api (test de santé après déploiement)'
       Args = @('run','services','add-iam-policy-binding','eap-api',"--region=$region",'--role=roles/run.invoker',"--member=$member") }
    @{ What = "5/5 Attacher l'identité eap-api-runtime au service (et aucune autre)"
       Args = @('iam','service-accounts','add-iam-policy-binding',$runtime,'--role=roles/iam.serviceAccountUser',"--member=$member",'--condition=None') }
)

$failed = 0
foreach ($g in $grants) {
    # Les droits IAM mettent parfois quelques secondes à se propager : jusqu'à 4 tentatives.
    for ($try = 1; $try -le 4; $try++) {
        # Arguments passés un par un (splatting d'une variable) à gcloud.cmd : passés en un bloc, gcloud les lisait
        # comme une seule commande inconnue (« Invalid choice »).
        $argv = $g.Args + @("--project=$project", '--quiet')
        $out = & gcloud.cmd @argv 2>&1 | Out-String
        if ($LASTEXITCODE -eq 0) { Write-Host "OK     $($g.What)" -ForegroundColor Green; break }
        if ($try -lt 4) { Start-Sleep -Seconds 10; continue }
        $failed++
        Write-Host "ÉCHEC  $($g.What)" -ForegroundColor Red
        Write-Host (($out -split "`n" | Where-Object { $_ -match 'ERROR' }) -join "`n")
    }
}

if ($failed -eq 0) { Write-Host "`nLes 5 droits sont en place." -ForegroundColor Green }
else { Write-Host "`n$failed droit(s) en échec : relancer le script (il est idempotent)." -ForegroundColor Yellow; exit 1 }
