# Handleiding meldingen

Deze handleiding bestaat uit drie hoofdstukken: algemene informatie over meldingen en commando's, Slack instellen en Discord instellen.

## 1. Algemeen

### Eén actief meldingskanaal

CodeBadger ondersteunt Slack en Discord, maar gebruikt **maximaal één meldingskanaal tegelijk**. Kies in **Instellingen → Meldingen** bij **Notification channel** Slack óf Discord. Ook als je gegevens voor beide diensten hebt ingevuld, is alleen de geselecteerde dienst actief. De schakelaar **Enable session notifications** schakelt het geselecteerde kanaal in of uit. Een kanaalwissel wordt actief nadat je de instellingen opslaat; CodeBadger stopt dan eerst de oude verbinding.

### Privacy en beveiliging

Een melding kan een samenvatting van een OpenCode-antwoord en informatie over je project bevatten. Verbind alleen een Slack-workspace of Discord-server waarin je die informatie mag delen. Bewaar bot- en app-tokens geheim. CodeBadger slaat tokens veilig op dit apparaat op, niet in het gewone instellingenbestand. Voor extra veiligheid worden berichten alleen verwerkt als ze afkomstig zijn van de gebruikers-ID die je zelf hebt ingesteld.

### Voorwaarden en gedrag

- CodeBadger moet draaien om OpenCode-sessies te volgen, klaar-meldingen te versturen, vragen te ontvangen en antwoorden vanuit Slack of Discord naar OpenCode door te sturen. Als CodeBadger volledig is afgesloten, worden geen meldingen verwerkt.
- Deze meldingen volgen OpenCode-sessies. Andere agenttools worden hiervoor niet gevolgd.
- Bij een gevolgde sessie plaatst CodeBadger een bericht in het gekozen kanaal en koppelt daar een thread aan. Reageer **in de thread** om precies die OpenCode-sessie voort te zetten. Een los bericht in het kanaal wordt niet aan een sessie gekoppeld.
- Een sessie verschijnt in de lijst voor sessiemeldingen nadat CodeBadger er een thread aan heeft gekoppeld.

### Commando's en antwoorden

- Stuur `!new` als los kanaalbericht om een nieuwe OpenCode-sessie te starten. CodeBadger toont een keuzelijst in een thread. Antwoord daar met het nummer van een repository en stuur daarna je opdracht als volgend threadbericht. Alleen actieve, niet-gesandboxte OpenCode-repositories worden getoond.
- Stuur `!todo <tekst>` om een punt toe te voegen aan de CodeBadger-lijst. De eerste zin (maximaal 160 tekens) wordt de titel; resterende tekst wordt de beschrijving. Optionele typeprefixen zijn `idea:`, `wish:`, `task:`, `bug:` en `story:`. Zonder prefix wordt het type `story` gebruikt.
- Stuur `!todos` als kanaalbericht of in een sessiethread om open punten op te halen. Bij meer dan tien punten toont CodeBadger de tien nieuwste, op aanmaakdatum gesorteerd.
- Stuur `!help` als los kanaalbericht voor kanaalcommando's, of in een sessiethread voor sessiecommando's. In de `!new`-keuzethread legt `!help` uit hoe je een repository kiest en de opdracht verstuurt.
- Begin een sessieantwoord met `!build` of `!plan`, gevolgd door een spatie en je instructie, om voor die beurt van agent te wisselen. Bijvoorbeeld: `!plan Vergelijk twee implementatieopties`. CodeBadger wisselt eerst van agent en stuurt daarna de tekst na het commando. Volgende beurten blijven die agent gebruiken totdat je opnieuw wisselt. Stuur niet alleen `!build` of `!plan`; voeg een opdracht toe.
- Stuur `!commit` in een sessiethread om alle repositorywijzigingen te stagen en te committen. Als AI-commitberichten zijn geconfigureerd, probeert CodeBadger automatisch een bericht te maken. Lukt dat niet, dan vraagt CodeBadger om een titel; stuur die als volgende threadreply, eventueel met de beschrijving op de volgende regel.
- Stuur `!push` in een sessiethread om te pushen met dezelfde veilige controle op de remote-branch als in de Git-modal.
- OpenCode-toestemmingsvragen verschijnen in de sessiethread. Gebruik `!allow` voor eenmalige toestemming, `!always` om toestemming te onthouden of `!deny [reden]` om te weigeren. Als er meerdere verzoeken openstaan, gebruik je het getoonde nummer, bijvoorbeeld `!allow 2`. De ID uit de vraag wordt ook ondersteund.
- OpenCode-formulieren tonen genummerde formulieren, velden en opties. Voor één formulier met één veld antwoord je met `!answer <optienummer>`. Voor meerdere velden gebruik je `!answer <veldnummer> <optienummer>`. Als meerdere formulieren openstaan, geef je eerst het formuliernummer op. Combineer antwoorden op velden met puntkomma's. Bij de optie ‘Anders’ kun je je eigen tekst toevoegen. Meerkeuzevelden accepteren optienummers gescheiden door `|`. De bestaande syntax `!answer <id> sleutel=waarde; sleutel2=waarde` blijft ondersteund.
- Een gewone threadreply die geen herkend commando is, wordt als opdracht naar de gekoppelde OpenCode-sessie gestuurd.

### Meldingen dempen

- Open **Instellingen → Repositories** en schakel **Mute session notifications for this repository** in om toekomstige automatische klaar-meldingen voor alle sessies van die repository te stoppen. Schakel de instelling uit om meldingen te hervatten.
- Wil je één sessie dempen, klik dan met de rechtermuisknop op de repositorytab en kies **Session notifications**. In het venster kun je per gekoppelde sessie **Mute session** of **Unmute session** kiezen. Deze optie is alleen beschikbaar als de geselecteerde meldingsdienst verbonden is en de repository niet al gedempt is.
- Dempen geldt alleen voor automatische klaar-meldingen. Het stopt de agent niet, verwijdert geen bestaande berichten en blokkeert geen replies in een bestaande sessiethread.

### Koppeling uitschakelen of intrekken

Je kunt meldingen uitschakelen met **Enable session notifications**. Om toegang volledig in te trekken, verwijder je de bot/app uit de Slack-workspace of Discord-server en trek je de bijbehorende tokens in. Plak tokens nooit in chat en commit ze niet naar Git.

## 2. Slack

### Maak een Slack-app

1. Open [api.slack.com/apps](https://api.slack.com/apps) en kies **Create New App** → **From scratch**.
2. Geef de app bijvoorbeeld de naam **CodeBadger** en kies de workspace waarin je meldingen wilt ontvangen.
3. Open **OAuth & Permissions**. Voeg onder **Bot Token Scopes** toe:
   - `chat:write` — om meldingen en thread-replies te plaatsen.
   - `channels:read` en `channels:history` — om openbare kanalen te tonen en berichten daar te ontvangen.
   - `groups:read` en `groups:history` — om privékanalen te tonen en berichten daar te ontvangen als je een privékanaal gebruikt.
4. Open **Event Subscriptions**, schakel events in en abonneer de bot op `message.channels` en `message.groups` (die laatste is voor privékanalen).
5. Open **Socket Mode** en schakel dit in. CodeBadger gebruikt Socket Mode; je hoeft dus geen publieke Request URL beschikbaar te maken.
6. Ga naar **Basic Information → App-Level Tokens** en maak een app-token met `connections:write`. Dit token begint met `xapp-`.
7. Ga terug naar **OAuth & Permissions** en klik op **Install to Workspace** (of **Reinstall to Workspace** als je na installatie scopes hebt toegevoegd). Geef toestemming en kopieer de **Bot User OAuth Token**, dat begint met `xoxb-`.
8. Nodig de bot uit in het Slack-kanaal dat je wilt gebruiken. Typ bijvoorbeeld `/invite @CodeBadger` in dat kanaal. De bot kan alleen berichten plaatsen in kanalen waarvan hij lid is.

Moet een beheerder de app goedkeuren of installeren? Vraag de Slack-beheerder de app en bovenstaande rechten goed te keuren.

### Zoek je Slack-lid-ID op

Open in de Slack-desktopapp je profiel, klik op **More** en kies **Copy member ID**. De ID begint meestal met `U`. CodeBadger gebruikt deze ID als veiligheidscontrole: alleen berichten van deze Slack-gebruiker worden naar OpenCode doorgestuurd.

### Configureer CodeBadger voor Slack

1. Open **Instellingen → Meldingen**.
2. Kies **Slack** als **Notification channel**. Als Discord ook is ingesteld, zorgt deze keuze ervoor dat alleen Slack actief is.
3. Plak het `xoxb-`-token bij **Slack bot token** en het `xapp-`-token bij **Slack app-level token**. De tokens worden veilig op het apparaat opgeslagen, niet in het gewone settingsbestand.
4. Vul je Slack-lid-ID in.
5. Klik op **Load Slack channels** en kies een kanaal waarin de bot lid is. Kanalen waarin de bot geen lid is, zijn niet selecteerbaar.
6. Klik op **Test Slack connection**. Zet **Send OpenCode completion notifications to Slack** aan en sla de instellingen op.

De status in dit paneel geeft aan of CodeBadger via Socket Mode met Slack is verbonden. CodeBadger moet actief zijn om replies en OpenCode-vragen te ontvangen en sessies te volgen.

## 3. Discord

### Maak een Discord-app en bot

1. Open de [Discord Developer Portal](https://discord.com/developers/applications) en kies **New Application**. Geef de applicatie bijvoorbeeld de naam **CodeBadger**.
2. Open de sectie **Bot** en maak de bot aan als dat nog niet automatisch is gebeurd. Kopieer de bot-token via **Reset Token** of **View Token**. Behandel dit token als een wachtwoord; iedereen met dit token kan de bot besturen.
3. Ga in **Bot → Privileged Gateway Intents** naar **Message Content Intent**, schakel deze in en sla de wijziging op. CodeBadger heeft deze privileged intent nodig om de tekst van serverberichten te ontvangen.
4. Open **OAuth2 → URL Generator**. Selecteer bij **Scopes** de scope `bot`. Selecteer bij **Bot Permissions**:
   - **View Channels** en **Read Message History** — om het gekozen kanaal te kunnen zien en eerdere context te lezen.
   - **Send Messages** — om meldingen te plaatsen.
   - **Create Public Threads** en **Send Messages in Threads** — om per sessie threads te maken en daarin te antwoorden.
5. Kopieer de gegenereerde URL, open deze in je browser, selecteer de server en autoriseer de bot. Je moet rechten hebben om bots aan die server toe te voegen.
6. Controleer in de serverinstellingen en kanaalinstellingen dat de bot deze rechten daadwerkelijk heeft. Een kanaaloverrule kan serverrechten beperken.

### Zoek je Discord-ID's op

Schakel in de Discord-client **User Settings → Advanced → Developer Mode** in. Klik met rechts op de server waarin de bot is uitgenodigd en kies **Copy Server ID**. Kopieer ook je eigen gebruikers-ID via je profiel/contextmenu met **Copy User ID**. CodeBadger gebruikt jouw gebruikers-ID als veiligheidscontrole: alleen berichten van die gebruiker worden verwerkt.

### Configureer CodeBadger voor Discord

1. Open **Instellingen → Meldingen**.
2. Kies **Discord** als **Notification channel**. Ook als Slack is ingesteld, wordt dan alleen Discord actief.
3. Plak de bot-token in **Discord bot token**.
4. Vul de gekopieerde server-ID in bij **Discord server (guild) ID**.
5. Klik op **Load Discord channels** en kies een tekst- of aankondigingskanaal waar de bot toegang toe heeft.
6. Vul je Discord-gebruikers-ID in bij **Your Discord user ID**.
7. Klik op **Test Discord connection**. Schakel **Enable session notifications** in en sla de instellingen op.

De status toont of CodeBadger met de Discord Gateway verbonden is. Als laden of testen mislukt, controleer dan de bot-token, server-ID, kanaaltoegang en de **Message Content Intent**. CodeBadger moet actief blijven om berichten en replies te verwerken. Bij de keuze tussen Slack en Discord is alleen de waarde van **Notification channel** bepalend voor welke integratie actief is.
