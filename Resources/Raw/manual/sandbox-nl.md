# OpenCode-sandbox

De OpenCode-sandbox start OpenCode voor een repository in een geïsoleerde omgeving. In plaats van automatisch de hele repository te delen, selecteer je expliciet welke bestanden beschikbaar zijn. De sandboxinstellingen open je via **Instellingen → Repositories → Permissions** en **Settings**.

## Model en bestanden kiezen

Open **Settings** bij de sandbox van een OpenCode-repository.

1. Kies het model voor nieuwe sandbox-sessies.
2. Vink de bestanden en mappen aan die OpenCode nodig heeft. Een mapselectie neemt de onderliggende bestanden mee.
3. Sla de selectie op. Gevoelige paden en links worden niet als selecteerbare entries getoond.

De algemene sandboxlimieten staan in **Instellingen → Sandbox**. Standaard is de limiet 64 MB per bestand en 1024 MB totaal; de instelbare grenzen zijn 1–512 MB per bestand en 1–2048 MB totaal. De limieten gelden voor nieuwe sessies.

## Twee toegangsmodi

CodeBadger meet de expliciet geselecteerde bestanden bij het starten:

- **Copy/review-modus:** als de selectie binnen beide groottelimieten past, worden de geselecteerde bestanden naar een tijdelijke, afgeschermde werkkopie gekopieerd. De sandbox krijgt geen algemene netwerktoegang.
- **Direct-modus:** als één bestand of de totale selectie de limiet overschrijdt, gebruikt CodeBadger tijdelijke, beperkte toegang tot de geselecteerde echte bestanden. Wijzigingen zijn dan meteen zichtbaar in de repository; bestanden aanmaken, verwijderen en hernoemen is geblokkeerd. Er is in deze modus geen reviewkopie.

De modus wordt automatisch gekozen; limieten verhogen kan het geheugen- en schijfgebruik vergroten. Beperk de selectie tot wat de taak nodig heeft.

## Sessie stoppen en wijzigingen beoordelen

Stop na het werk de sandbox met **Stop sandbox**. In copy/review-modus worden wijzigingen vastgelegd voor beoordeling. De review toont gewijzigde bestanden en hun inhoud; verwijderingen kunnen worden getoond maar niet toegepast.

1. Controleer de voorgestelde inhoud per bestand.
2. Selecteer alleen wijzigingen die je wilt toepassen.
3. Kies **Apply selected modifications**.

CodeBadger past alleen geselecteerde wijzigingen toe op bestaande bestanden en maakt back-ups van originelen. Er vindt een conflictcontrole plaats; als de oorspronkelijke bestanden intussen veranderden, kan toepassing worden geweigerd. Toepassen van meerdere bestanden is niet één atomaire transactie. Bekijk back-ups en resultaten zorgvuldig. Niet-toegepaste reviewwijzigingen blijven beschikbaar totdat je ze toepast of expliciet weggooit. Je moet eerst de review afronden of weggooien voordat een nieuwe sandbox-sessie start.

Direct-modus schrijft wijzigingen onmiddellijk naar de geselecteerde echte bestanden. Stoppen maakt daarvoor geen reviewkopie.

## Machtigingen en netwerktoegang

Sandboxmachtigingen zijn repositorygebonden. De toestemmingsdialoog toont geregistreerde origins. Een toestemming intrekken stopt de bijbehorende sandboxrepositorysessie; host-API-sleutels worden niet getoond.

Netwerktoegang is standaard afgeschermd. Als een sandbox toegang nodig heeft die een goedkeuring vereist, beoordeel het verzoek en geef alleen toegang als de host en bestemming vertrouwd zijn. Deel geen geheimen of projectbestanden die niet voor die sessie bedoeld zijn.

## Belangrijke beperkingen

- Bestandsselectie betekent niet dat de inhoud op geheimen is gecontroleerd; controleer zelf wat je deelt. Detectie van geheimen wordt niet gegarandeerd.
- Reparse points, links, onveilige paden en overmatige selecties worden geweigerd.
- De sandbox werkt op Windows en is bedoeld voor OpenCode.
- De exacte acties in Direct-modus zijn direct van invloed op de echte geselecteerde bestanden. Controleer daarom de modusmelding voordat je verdergaat.
