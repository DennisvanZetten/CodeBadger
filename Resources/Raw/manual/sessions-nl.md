# Agentterminals en sessies

Een repositorytab combineert de projectmap, een terminal voor de gekozen agent en de Git-status. CodeBadger start de agent in de repositorymap wanneer de terminal voor het eerst wordt geopend. De ingebouwde terminal is beschikbaar op Windows.

## Een agent starten en gebruiken

1. Activeer een repository en controleer of het pad bestaat en de gekozen agent beschikbaar is.
2. Open de repositorytab. CodeBadger start de geconfigureerde agent in de projectmap.
3. Typ in de terminal en verzend de prompt zoals je in de gekozen agent gewend bent. De terminal ondersteunt interactieve invoer en toont de uitvoer van het agentproces.
4. Selecteer een andere repositorytab om tussen projectterminals te wisselen.

Als de agentopdracht niet wordt gevonden, verschijnt een waarschuwing. Installeer de agent of kies een andere via **Instellingen → Repositories**. Als een eerder geopende terminal is afgesloten, kan het proces beëindigd zijn en moet je de agent opnieuw starten.

## OpenCode-sessies hervatten

Voor niet-gesandboxt OpenCode houdt CodeBadger de geselecteerde sessie bij. Een OpenCode-sessie die via de takenlijst of Slack is gestart, kan in de bijbehorende repositoryterminal worden hervat. De terminal start OpenCode met de sessie-ID; de sessiegeschiedenis komt uit OpenCode.

Een taak die via **Start in new session** wordt gestart, maakt een nieuwe OpenCode-sessie en koppelt die aan het taakitem. Een sessie die via Slack met `!new` wordt gestart, wordt aan de Slack-thread gekoppeld. Bij het openen van de sessie vanuit de taaklink selecteert CodeBadger de betreffende repository en sessie.

De repositoryterminal is een interactieve agentterminal, geen lijstscherm voor alle OpenCode-sessies. Gebruik de functies van OpenCode zelf om binnen de terminal andere sessies te bekijken of te openen. CodeBadger herstelt de terminaluitvoer niet noodzakelijk als het terminalproces is afgesloten.

## Activiteit en vragen

Voor ondersteunde, niet-standalone OpenCode-sessies laat CodeBadger activiteit in de repositorytab zien:

- Een spinner betekent dat er een sessie werkt.
- Een vraag-icoon betekent dat OpenCode wacht op toestemming of invoer.
- Bij ingeschakeld Slack kunnen ondersteunde sessies een klaarbericht krijgen. Een open vraag wordt eerst afgehandeld; daarna kan de voltooide reactie worden gemeld.

Activiteitsherkenning is afhankelijk van de beheerde OpenCode-service op Windows. Standalone-OpenCode, expliciet gekozen servers en andere agenttools worden niet op dezelfde manier gevolgd.

## Versies van de agentterminal en de OpenCode-activiteit

De terminal start de gekozen agentopdracht met de repository als werkmap. CodeBadger gebruikt voor OpenCode-activiteitsstatus daarnaast de beheerde OpenCode-service via de `opencode api`-opdracht. Daarom is een OpenCode-proces dat is gestart met `--standalone` of `--server` niet inbegrepen in deze activiteitsbewaking.

## Terminalinstellingen

- Houd de repositorytab actief zolang je de terminal gebruikt. Bij het sluiten of loskoppelen van vensters beheert CodeBadger de terminal volgens de bijbehorende repositorysessie.
- Verwijder of verplaats de projectmap niet terwijl de terminal of een agent daar actief in werkt.
- Agentuitvoer kan projectinhoud of toolresultaten bevatten. Deel terminalinhoud alleen via goedgekeurde kanalen.
- Git-acties zijn beschikbaar via **Git overview** in de repositorywerkruimte; zie de aparte Git-handleiding.

## Sandboxterminals

Als de OpenCode-sandbox voor een repository is ingeschakeld, wordt de sandboxworkflow gebruikt in plaats van de gewone agentterminal. Selecteer eerst bestanden en lees de modusmelding. Zie **OpenCode-sandbox** voordat je een sessie start.
