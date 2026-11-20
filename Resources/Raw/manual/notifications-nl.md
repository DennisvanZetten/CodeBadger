# Handleiding Slack-meldingen

In deze handleiding lees je hoe je Slack-meldingen instelt, de Slack-app aanmaakt en replies, agentwissels en dempknoppen gebruikt.

> **Privacy:** Een Slack-melding bevat een samenvatting van een OpenCode-antwoord. Verbind alleen een Slack-workspace waarin je projectinformatie mag delen. Houd beide Slack-tokens geheim.

## 1. Maak een Slack-app

1. Open [api.slack.com/apps](https://api.slack.com/apps) en kies **Create New App** → **From scratch**.
2. Geef de app bijvoorbeeld de naam **CodeBadger** en kies de workspace waarin je meldingen wilt ontvangen.
3. Open **OAuth & Permissions**. Voeg onder **Bot Token Scopes** toe:
   - `chat:write` — om samenvattingen en thread-replies te plaatsen.
   - `channels:read` en `channels:history` — om openbare kanalen te tonen en berichten daar te ontvangen.
   - `groups:read` en `groups:history` — om privékanalen te tonen en berichten daar te ontvangen, als je een privékanaal gebruikt.
4. Open **Event Subscriptions**, schakel events in en abonneer de bot op `message.channels` en `message.groups` (die laatste is voor privékanalen).
5. Open **Socket Mode** en schakel dit in. CodeBadger gebruikt Socket Mode; je hoeft dus geen publieke Request URL beschikbaar te maken.
6. Ga naar **Basic Information → App-Level Tokens** en maak een app-token met `connections:write`. Dit token begint met `xapp-`.
7. Ga terug naar **OAuth & Permissions** en klik **Install to Workspace** (of **Reinstall to Workspace** als je na installatie scopes hebt toegevoegd). Geef toestemming en kopieer de **Bot User OAuth Token**, dat begint met `xoxb-`.
8. Nodig de bot uit in het Slack-kanaal dat je wilt gebruiken. Typ bijvoorbeeld `/invite @CodeBadger` in dat kanaal. De bot kan alleen berichten plaatsen in kanalen waarvan hij lid is.

Moet een beheerder de app goedkeuren of installeren? Vraag de Slack-beheerder de app en bovenstaande rechten goed te keuren.

## 2. Zoek je Slack-lid-ID op

Open in de Slack-desktopapp je profiel, klik op **More** en kies **Copy member ID**. De ID begint meestal met `U`. CodeBadger gebruikt deze ID als veiligheidscontrole: alleen replies van deze Slack-gebruiker worden naar OpenCode doorgestuurd.

## 3. Configureer CodeBadger

1. Open **Instellingen → Meldingen**.
2. Laat **Slack** geselecteerd als meldingskanaal.
3. Plak het `xoxb-`-token bij **Slack bot token** en het `xapp-`-token bij **Slack app-level token**. De tokens worden veilig op het apparaat opgeslagen, niet in het gewone settingsbestand.
4. Vul je Slack-lid-ID in.
5. Klik op **Load Slack channels** en kies een kanaal waarin de bot lid is. Kanalen waarin de bot geen lid is, zijn niet selecteerbaar.
6. Klik op **Test Slack connection**. Zet **Send OpenCode completion notifications to Slack** aan en sla de instellingen op.

De status in dit paneel geeft aan of CodeBadger via Socket Mode met Slack is verbonden.

## 4. Meldingen en replies gebruiken

- Als een gevolgde OpenCode-sessie klaar is, plaatst CodeBadger een beknopte samenvatting in het gekozen Slack-kanaal. Het bericht vermeldt agent en sessie.
- Reageer **in de thread op het CodeBadger-bericht** om precies die OpenCode-sessie voort te zetten. Een nieuw kanaalbericht wordt niet aan een sessie gekoppeld.
- Stuur `!new` als los kanaalbericht om een nieuwe sessie te starten. Kies in de thread die CodeBadger terugstuurt een repository met het nummer en stuur daarna je opdracht als volgend threadbericht. Alleen actieve, niet-gesandboxte OpenCode-repositories worden getoond.
- Begin je thread-reply met `!build` of `!plan`, gevolgd door een spatie en je instructie, om voor die reply van agent te wisselen. Bijvoorbeeld: `!plan Vergelijk twee implementatieopties`. Eerst wordt de agent gewisseld; daarna ontvangt die de tekst na het commando. Volgende beurten blijven die agent gebruiken totdat je opnieuw wisselt.
- Stuur `!commit` in een sessiethread om alle repositorywijzigingen te stage'n en te committen. Als AI-commitberichten zijn geconfigureerd, genereert CodeBadger het bericht automatisch. Anders vraagt CodeBadger om een titel; stuur die als volgende threadreply, eventueel met de beschrijving op de volgende regel.
- Gebruik `!build` voor implementatiewerk en `!plan` voor planning en analyse. Stuur niet alleen `!build` of `!plan`; voeg er een instructie aan toe.

De Slack-reply moet komen van de Slack-lid-ID die in CodeBadger is ingesteld. CodeBadger moet actief zijn om Slack-replies te ontvangen en het afronden van sessies te bewaken.

## 5. Dempen per repository of sessie

Als Slack-meldingen verbonden zijn, open je **Instellingen → Repositories** om de instelling voor de hele repository te beheren:

- Schakel **Mute Slack notifications for this repository** in om toekomstige automatische klaar-meldingen voor alle sessies in die repository te stoppen. Schakel de optie uit om meldingen te hervatten.
- Wil je één sessie dempen? Klik met de rechtermuisknop op de repositorytab en kies **Session notifications**. Deze optie is alleen beschikbaar als Slack verbonden is en de repository niet gedempt is. In het venster staan Slack-gekoppelde sessies met per sessie **Mute session** en **Unmute session**.

Dempen geldt alleen voor automatische klaar-meldingen. Het stopt de agent niet, verwijdert geen Slack-berichten en blokkeert geen replies in een bestaande sessiethread.

## Opmerkingen en beperkingen

- De eerste versie ondersteunt Slack-klaar-meldingen voor OpenCode-sessies. Andere agenttools worden hiervoor niet gevolgd.
- CodeBadger moet draaien om het afronden van een sessie op te merken en Slack-replies te ontvangen. Als CodeBadger volledig is afgesloten, worden geen meldingen verstuurd.
- Een sessie verschijnt in de mute-lijst nadat CodeBadger er een Slack-thread aan heeft gekoppeld.
- Toegang intrekken kan door de meldingsschakelaar uit te zetten, de app uit de Slack-workspace te verwijderen of de tokens in te trekken. Plak tokens nooit in chat en commit ze niet naar Git.
