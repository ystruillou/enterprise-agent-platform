# Image de l'API pour Cloud Run. Construite par Cloud Build (gcloud run deploy --source), pas besoin de Docker en local.
# Les clés d'API ne sont JAMAIS dans l'image : Cloud Run les injecte depuis Secret Manager au démarrage.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restauration d'abord (couche mise en cache tant que les projets ne changent pas)
COPY src/Eap.Core/Eap.Core.csproj src/Eap.Core/
COPY src/Eap.Api/Eap.Api.csproj src/Eap.Api/
RUN dotnet restore src/Eap.Api/Eap.Api.csproj
COPY src/Eap.Core/ src/Eap.Core/
COPY src/Eap.Api/ src/Eap.Api/
RUN dotnet publish src/Eap.Api/Eap.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Fiches agents, prompts et bases de connaissances : versionnés avec le code, livrés dans l'image.
COPY agents/ /app/agents/
ENV Platform__AgentsPath=/app/agents \
    ASPNETCORE_HTTP_PORTS=8080
# Utilisateur non root fourni par l'image Microsoft
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Eap.Api.dll"]
