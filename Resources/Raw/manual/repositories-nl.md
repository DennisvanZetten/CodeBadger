# Repositories instellen

Een repository koppelt een projectmap aan een agent in CodeBadger. Actieve repositories verschijnen als tab op het startscherm.

## Repository toevoegen

1. Open **Instellingen → Repositories**.
2. Klik op **Toevoegen**.
3. Vul een herkenbare naam in. De naam wordt gebruikt voor de tab en in CodeBadger-vensters.
4. Kies de projectmap met het mapknopje naast **Pad**. Je kunt het pad ook rechtstreeks invullen. Kies de map van het Git-project, niet een los bestand.
5. Kies bij **Agent tool** de agent die je voor deze repository wilt starten: OpenCode, Claude Code, Codex of Hermes.
6. Laat de schakelaar voor actief aan om de repository op het startscherm te tonen.
7. Klik onderaan op **Opslaan**.

De projectmap moet bestaan. Als het gekozen agentcommando niet is geïnstalleerd of niet wordt gevonden, toont CodeBadger een waarschuwing en kan de agent niet starten. Een bestaand pad kan ook worden gebruikt zonder dat CodeBadger daar zelf een Git-repository van maakt; de Git-module werkt alleen als Git het pad herkent als repository.

## Repositories beheren

- Zet een repository op inactief om de tab tijdelijk van het startscherm te verbergen. De repository-instellingen en bestanden blijven behouden.
- Pas de naam of het pad aan als het project is verplaatst. Controleer na een padwijziging dat de map bestaat.
- Gebruik de pijlen om de volgorde van repositorytabs te wijzigen.
- Verwijderen haalt de repository uit de CodeBadger-instellingen; het verwijdert de projectmap en Git-geschiedenis niet.
- De repositorytabs kun je herschikken door ze te verslepen. Je kunt een repositorytab ook loskoppelen naar een apart venster en later weer terugplaatsen.

## Opties per repository

### Agentinstellingen

Afhankelijk van de gekozen agent kan naast de agentkeuze een instellingenknop verschijnen. Daarmee beheer je projectconfiguratie; bij OpenCode kun je onder andere MCP-servers en projectspecifieke skills bekijken of aanpassen. CodeBadger maakt bij wijzigingen aan bestaande agentconfiguratie eerst een back-up. Projectconfiguratie kan alleen worden bewerkt als de betreffende agent beschikbaar is.

### Tickets

Je kunt tickets optioneel verplicht stellen en een ticketsysteem kiezen, zoals Jira of GitHub Issues. Wanneer tickets zijn ingeschakeld, selecteer je ook de MCP-server die voor deze repository in de agentconfiguratie is ingesteld. Deze optie wordt gebruikt bij het starten en vormgeven van taken.

### AI-storytemplate

De storytemplate beschrijft de indeling die de AI moet gebruiken om een taak uit te werken, bijvoorbeeld gebruikersverhaal, context, acceptatiecriteria, technische notities en wat buiten scope valt. Deze template wordt opgenomen in de eerste prompt wanneer je een taak voor deze repository start.

### OpenCode-sandbox

Voor OpenCode-repositories kun je de sandbox inschakelen. De sandbox draait OpenCode in een afgeschermde omgeving met eigen instellingen en machtigingen. Configureer machtigingen en sandboxopties via de knoppen **Permissions** en **Settings**. Slack-commit- en pushcommando's zijn alleen beschikbaar voor niet-gesandboxte OpenCode-repositories.

### Slack-meldingen

Als Slack-meldingen zijn ingesteld, kun je per OpenCode-repository automatische Slack-klaarmeldingen dempen. Dit stopt toekomstige automatische klaarberichten voor die repository, maar beëindigt geen sessies en blokkeert geen antwoorden in bestaande Slack-threads. Zie de handleiding **Slack-meldingen** voor de configuratie en commando's.

## Een repository openen

Selecteer de repositorytab op het startscherm. De projectwerkruimte toont de agentterminal en, als het pad een geldige Git-repository is, de Git-status. De tab toont onder meer de huidige branch en het aantal wijzigingen, commits vóór en commits achter de remote. Klik op **Git overview** om de Git-module te openen.

## Problemen oplossen

- **Pad bestaat niet:** kies de juiste projectmap opnieuw; verplaatste mappen worden niet automatisch gevonden.
- **Agent command not found:** installeer de gekozen agent of kies een agent die beschikbaar is.
- **Git-status niet beschikbaar:** controleer of Git is geïnstalleerd en of het pad naar een Git-working tree wijst.
- **Repository ontbreekt op het startscherm:** open de instellingen en controleer of de repository actief is en een bestaand pad heeft.
