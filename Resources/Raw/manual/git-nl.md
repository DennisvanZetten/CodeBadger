# Git-module

De Git-module geeft de branches, commits en bestandswijzigingen van één repository weer. Open hem via **Git overview** in het Git-paneel van een repositorytab. Bij het openen haalt CodeBadger eerst de remote-status op.

## Overzicht en status

Bovenaan staat de huidige branch. **Pull** toont hoeveel commits de branch achterloopt; **Push** toont hoeveel commits lokaal vóórstaan. De status onderaan toont gewijzigde, gestagede en niet-gestagede bestanden, plus de aantallen vóór en achter de upstream-branch. Met **Refresh** haalt CodeBadger de remote-status opnieuw op.

Als de branch achterloopt, controleert Push de remote-status en probeert zo nodig eerst een pull, maar alleen als de working tree schoon is. Bij niet-gecommitte wijzigingen wordt de push afgebroken zodat je die eerst kunt committen of stashen. Een branch zonder upstream kan, als een eenduidige remote beschikbaar is, tijdens het pushen een upstream krijgen.

## Branches en commits

De weergave **Branches & commits** toont lokale en remote branches naast de recente commitgeschiedenis.

- Selecteer een branch om ernaar over te schakelen. De huidige branch is gemarkeerd.
- Branches met `/` in de naam worden in groepen weergegeven.
- Selecteer een commit om de gewijzigde bestanden te bekijken. Selecteer een bestand om de diff van die commit te zien.
- Dubbelklik op een commit om naar een branch te schakelen die aan die commit is gekoppeld.
- Klik met de rechtermuisknop op een commit voor extra acties: daar een branch maken, detached naar de commit gaan, cherry-picken, reverten, een geschikte branch mergen of branchnaam/commitinformatie/SHA kopiëren.
- Sommige acties zijn alleen beschikbaar als de vereiste branch- of working-treestatus veilig is. Bij checkout van een commit en revert moeten lokale wijzigingen eerst zijn gecommit of gestasht. Voor cherry-pick controleert de module of de commit al op de huidige branch staat.

### Een branch maken

Klik op **New branch** om vanaf `HEAD` te beginnen, of klik met de rechtermuisknop op een commit en kies **Create branch here**. Geef de branch een geldige naam op. De nieuwe branch wordt meteen aangemaakt en uitgecheckt.

### Conflicten

Als Git conflicten rapporteert, toont de module de conflicterende bestanden. Kies **Conflict oplossen** voor een ingebouwde editor: vergelijk de huidige versie links met de binnenkomende versie rechts, kies per conflicterende regel een kant en bewerk het resultaat onderaan. **Alles van links/rechts** kiest een hele kant. Met **Opslaan en als opgelost markeren** wordt het resultaat opgeslagen en gestaged. **Openen in VS Code** blijft beschikbaar als alternatief. Voltooi daarna de merge/rebase volgens je Git-workflow.

## Wijzigingen bekijken en stagen

Schakel naar **Create commit** om de working tree te bekijken. Als er wijzigingen zijn, kan CodeBadger deze weergave bij het openen automatisch tonen.

- Selecteer een bestand onder **Unstaged** om de diff te bekijken.
- Gebruik het plusknopje om één bestand te stagen, of **Stage all** om alle niet-gestagede wijzigingen te stagen.
- Vink bestanden aan en gebruik **Stage selected** om alleen de selectie te stagen.
- Bestanden onder **Staged** zijn opgenomen in de volgende commit. Met het min-knopje haal je een bestand weer uit staging.
- **Undo selected** verwijdert geselecteerde niet-gestagede wijzigingen uit de working tree. Deze actie vraagt om bevestiging en is niet hetzelfde als unstagen.

Controleer de staged bestanden en de diff voordat je commit. Staging bepaalt precies welke wijzigingen in de commit terechtkomen.

## Een commit maken

1. Stage de bestanden die in de commit moeten.
2. Vul een titel van maximaal 72 tekens in; de titel is verplicht.
3. Voeg desgewenst een beschrijving toe.
4. Optioneel kun je **Generate with AI** gebruiken als de AI-provider in de instellingen is geconfigureerd en bereikbaar is. Controleer de gegenereerde tekst altijd zelf.
5. Selecteer **Commit**. Alleen de staged wijzigingen worden gecommit.

## Push en pull

- **Pull** voert `git pull --no-rebase` uit voor de huidige branch.
- **Push** haalt eerst remote-informatie op. Als lokale commits achterlopen en de working tree schoon is, probeert CodeBadger eerst een pull en daarna de push. Bij lokale wijzigingen wordt de operatie veilig afgebroken; commit of stash die wijzigingen en probeer opnieuw.
- Push vereist een upstream of een eenduidige standaardremote. Als die ontbreekt, meldt CodeBadger waarom de push niet is uitgevoerd.

Na een Git-operatie vernieuwt de module de status. Controleer bij fouten de getoonde Git-melding; pushen kan bijvoorbeeld niet vanuit een detached HEAD.

## Veilig werken

- Bekijk vóór committen welke bestanden staged zijn; niet-gestagede bestanden komen niet in die commit.
- **Undo selected** verwijdert wijzigingen en kan niet ongedaan worden gemaakt via de Git-module. Controleer de selectie voordat je bevestigt.
- Checkout van een commit of revert is geblokkeerd zolang er wijzigingen in de working tree staan.
- Conflicten kunnen ontstaan bij pull, merge, cherry-pick of revert. Los deze eerst op voordat je verdergaat.
