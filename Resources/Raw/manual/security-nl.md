# Beveiliging en privacy

CodeBadger werkt met lokale repositorybestanden, agentconfiguratie, externe AI-providers, MCP-servers en optioneel Slack. Controleer daarom welke data je deelt en welke bevoegdheden je inschakelt.

## Geheimen en lokale opslag

- API-sleutels voor providers en Slack-tokens worden veilig op het apparaat opgeslagen en horen niet in projectbestanden, prompts of Git-commits.
- Algemene instellingen en de ideeën-/takenlijst worden lokaal bewaard. Slack-sessie-ID's en threadkoppelingen worden lokaal opgeslagen om antwoorden naar de juiste sessie te routeren.
- Deel Slack-meldingen alleen met een workspace waarin projectinformatie gedeeld mag worden. CodeBadger stuurt Slack-antwoorden alleen door als ze van het ingestelde Slack-lid komen.
- De AI-provider ontvangt de inhoud die nodig is voor de gevraagde AI-functie, zoals een staged diff voor een commitbericht. Gebruik alleen een provider waarmee het delen van die repository-inhoud is toegestaan.

## Agent- en MCP-toegang

Agenten kunnen bestanden en tools gebruiken met de bevoegdheden van hun uitvoeringsomgeving. Een MCP-server kan toegang geven tot externe data of acties. Controleer de bron en configuratie van een server voordat je deze inschakelt. Globale agentconfiguratie kan meerdere projecten beïnvloeden; projectconfiguratie kan in Git terechtkomen.

## Sandbox en bestandsreview

Selecteer alleen bestanden die nodig zijn. Bestandsselectie is geen geheime-inhoudscan; detectie is niet gegarandeerd. In Copy/review-modus worden sandboxwijzigingen pas na jouw selectie toegepast op bestaande bestanden en worden originelen geback-upt. Controleer de inhoud en back-ups; meerdere bestanden worden niet als één atomaire transactie toegepast.

Als de selectie de sandboxlimieten overschrijdt, kan Direct-modus worden gebruikt: wijzigingen zijn dan onmiddellijk zichtbaar op de geselecteerde echte bestanden, zonder reviewkopie. Aanmaken, verwijderen en hernoemen zijn geblokkeerd, maar bestaande geselecteerde bestanden kunnen direct worden gewijzigd. Controleer de modusmelding en geselecteerde bestanden vóór de sessie.

Netwerkverzoeken en machtigingen moeten per geval worden beoordeeld. Sta alleen betrouwbare hosts en acties toe. Machtiging intrekken kan een sandboxsessie stoppen.

## Git-acties

Staging bepaalt wat in een commit terechtkomt. Bekijk de staged lijst en diff voordat je commit. **Undo selected** in de Git-module verwijdert niet-gestagede wijzigingen en kan daar niet ongedaan worden gemaakt. Pull, merge, cherry-pick en revert kunnen conflicten opleveren; los die zorgvuldig op voordat je verdergaat.

## Toegang intrekken

Schakel Slack-meldingen uit of trek Slack-app-tokens in om de Slack-verbinding te beëindigen. Verwijder of roteer AI-provider-sleutels via de betreffende provider en werk ze bij in CodeBadger. Schakel niet-vertrouwde MCP-servers uit. Stop een actieve sandboxsessie en beoordeel of verwerp de reviewwijzigingen. Bewaar back-ups zolang je die nodig hebt om wijzigingen te herstellen.
