# Ideeën en taken

De lijst **Ideeën en taken** is een lokale plek om werk te bewaren dat je later wilt oppakken. Open de lijst via het ideeën-/takenmenu in CodeBadger. De lijst wordt op dit apparaat opgeslagen.

## Een punt toevoegen

1. Open **Ideeën en taken** en kies **Nieuw**.
2. Vul een titel in van maximaal 160 tekens.
3. Kies een type: **Idee**, **Wens**, **Taak** of **Bug**.
4. Voeg desgewenst een beschrijving toe (maximaal 2000 tekens) en sla op.

Items kunnen ook vanuit Slack worden toegevoegd met `!todo <tekst>`. De eerste zin wordt de titel en eventuele resterende tekst de beschrijving. Titels zijn maximaal 160 tekens; is de eerste zin langer, dan wordt die op 160 tekens afgekapt en gaat de rest naar de beschrijving. Een typeprefix zoals `idea:`, `wish:`, `task:`, `bug:` of `story:` is optioneel. Gebruik `!todos` om de open punten in Slack op te vragen; bij meer dan tien toont CodeBadger de tien nieuwste.

## Status en opruimen

Elk punt heeft de status **Open**, **Bezig** of **Klaar**. Gebruik de statuskeuze op het item om deze bij te werken. Items worden gegroepeerd op status en binnen de groep van nieuw naar oud gesorteerd. Verwijderen haalt het item definitief uit de lokale lijst.

## Een taak starten

Alleen items van het type **Taak** kunnen vanuit deze lijst direct als nieuwe OpenCode-sessie worden gestart.

1. Kies een geschikt actief, bestaand en niet-gesandboxt OpenCode-repository.
2. Klik op **Start in new session**.
3. CodeBadger vraagt welke branch als basis dient. Als een `development`-branch beschikbaar is, kun je die of de huidige branch kiezen.
4. Als voor de repository tickets verplicht zijn, kies je een ticketoptie:
   - een bestaand ticket met een nieuwe branch;
   - een nieuw ticket aanmaken via de geconfigureerde MCP-server en een branch maken;
   - een bestaand ticket en een bestaande lokale branch gebruiken.
5. CodeBadger checkt de bestaande branch uit of maakt een branch aan, start een OpenCode-sessie in **Plan**-modus en stuurt de taak met de repository-storytemplate als context.

De branchprefix is instelbaar onder **Instellingen → Overig**. Een nieuwe branch bevat zo mogelijk de prefix, het ticketnummer en een slug van de taaknaam.

Na een succesvolle start krijgt de taak de status **Bezig** en wordt deze gekoppeld aan repository, branch, ticket en sessie. Open de gekoppelde sessie via de sessielink op het taakitem. De taak kan niet opnieuw vanuit de lijst worden gestart zolang de sessie eraan gekoppeld is.

## Voorwaarden en aandachtspunten

- Voor het starten zijn een actief OpenCode-repository, een bestaand pad en een schone geschikte Git-branch nodig voor branchwissels.
- Deze workflow maakt zelf geen commit of push; gebruik daarvoor de Git-module.
- Een ticket aanmaken vereist dat het ticketsysteem en de MCP-server voor de repository zijn ingesteld en bereikbaar zijn.
- Het afwerken van de taak in OpenCode verandert de taakstatus niet automatisch. Werk de status in de lijst zelf bij.
