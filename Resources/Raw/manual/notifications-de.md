# Handbuch für Benachrichtigungen

Dieses Handbuch besteht aus drei Kapiteln: allgemeine Informationen zu Benachrichtigungen und Befehlen, Slack-Einrichtung und Discord-Einrichtung.

## 1. Allgemeines

### Ein aktiver Benachrichtigungskanal

CodeBadger unterstützt Slack und Discord, verwendet jedoch **immer nur einen Benachrichtigungskanal gleichzeitig**. Wählen Sie unter **Settings → Notifications** bei **Notification channel** entweder Slack oder Discord aus. Auch wenn beide Dienste eingerichtet sind, ist nur der ausgewählte Dienst aktiv. **Enable session notifications** schaltet den ausgewählten Kanal ein oder aus. Ein Wechsel wird nach dem Speichern wirksam; CodeBadger beendet zuerst die bisherige Verbindung.

### Datenschutz und Sicherheit

Eine Benachrichtigung kann eine Zusammenfassung einer OpenCode-Antwort und Projektinformationen enthalten. Verbinden Sie nur einen Slack-Workspace oder Discord-Server, in dem Sie diese Informationen teilen dürfen. Halten Sie Bot- und App-Tokens geheim. CodeBadger speichert Tokens sicher auf diesem Gerät und nicht in der normalen Einstellungsdatei. Als zusätzliche Sicherheitsprüfung verarbeitet CodeBadger nur Nachrichten der von Ihnen eingetragenen Benutzer-ID.

### Voraussetzungen und Verhalten

- CodeBadger muss laufen, um OpenCode-Sitzungen zu überwachen, Abschlussmeldungen zu senden, Fragen zu empfangen und Slack- oder Discord-Antworten an OpenCode weiterzuleiten. Andere Agent-Tools werden nicht überwacht. Bei vollständig geschlossener App werden keine Nachrichten verarbeitet.
- Für eine überwachte Sitzung veröffentlicht CodeBadger eine Nachricht im ausgewählten Kanal und verknüpft damit einen Thread. Antworten Sie **im Sitzungsthread**, um genau diese OpenCode-Sitzung fortzusetzen. Eine eigenständige Kanalnachricht wird keiner Sitzung zugeordnet.
- Eine Sitzung erscheint in der Liste für Sitzungsbenachrichtigungen, sobald CodeBadger einen Thread damit verknüpft hat.

### Befehle und Antworten

- Senden Sie `!new` als eigenständige Kanalnachricht, um eine OpenCode-Sitzung zu starten. CodeBadger erstellt einen Thread mit einer Repositoryliste. Antworten Sie mit einer Repositorynummer und senden Sie danach Ihre Aufgabe als nächste Thread-Nachricht. Angezeigt werden nur aktive, nicht sandboxgeschützte OpenCode-Repositories.
- Senden Sie `!todo <Text>`, um einen Eintrag zur CodeBadger-Liste hinzuzufügen. Der erste Satz (bis zu 160 Zeichen) wird zum Titel, der restliche Text zur Beschreibung. Optionale Typpräfixe sind `idea:`, `wish:`, `task:`, `bug:` und `story:`. Standardmäßig wird `story` verwendet.
- Senden Sie `!todos` im Kanal oder Sitzungsthread, um offene Punkte aufzulisten. Bei mehr als zehn Einträgen zeigt CodeBadger die zehn neuesten nach Erstellungsdatum.
- Senden Sie `!help` als eigenständige Kanalnachricht für Kanalbefehle oder in einem Sitzungsthread für Sitzungsbefehle. Im `!new`-Auswahlthread erklärt `!help` die Repositoryauswahl und das Senden der Aufgabe.
- Beginnen Sie eine Sitzungsantwort mit `!build` oder `!plan`, gefolgt von einem Leerzeichen und Ihrer Anweisung, um den Agenten für diesen Turn zu wechseln. Beispiel: `!plan Vergleiche zwei Implementierungsansätze`. CodeBadger wechselt zuerst den Agenten und sendet dann den Text nach dem Befehl. Weitere Turns verwenden diesen Agenten, bis Sie erneut wechseln. Senden Sie nicht nur `!build` oder `!plan`; fügen Sie eine Anweisung hinzu.
- Senden Sie `!commit` in einem Sitzungsthread, um alle Repositoryänderungen zu stagen und zu committen. Wenn KI-Commitnachrichten konfiguriert sind, versucht CodeBadger automatisch eine zu erstellen. Andernfalls fragt CodeBadger nach einem Titel; senden Sie ihn als nächste Thread-Antwort, optional mit der Beschreibung in der folgenden Zeile.
- Senden Sie `!push` in einem Sitzungsthread, um mit derselben sicheren Prüfung des Remote-Branches wie im Git-Dialog zu pushen.
- OpenCode-Berechtigungsanfragen erscheinen im Sitzungsthread. Verwenden Sie `!allow` für eine einmalige Genehmigung, `!always` zum Merken der Genehmigung oder `!deny [Grund]` zum Ablehnen. Bei mehreren offenen Anfragen verwenden Sie die angezeigte Nummer, zum Beispiel `!allow 2`. Die in der Anfrage angezeigte ID wird ebenfalls unterstützt.
- OpenCode-Formulare zeigen nummerierte Formulare, Felder und Optionen. Bei einem Formular mit einem Feld antworten Sie mit `!answer <Optionsnummer>`. Bei mehreren Feldern verwenden Sie `!answer <Feldnummer> <Optionsnummer>`. Bei mehreren offenen Formularen stellen Sie die Formularnummer voran. Feldantworten trennen Sie mit Semikolons. Nach der Option „Andere“ können Sie eigenen Text hinzufügen. Mehrfachauswahl akzeptiert mit `|` getrennte Optionsnummern. Die bestehende Syntax `!answer <id> key=value; key2=value` bleibt verfügbar.
- Jede gewöhnliche Thread-Antwort, die kein erkanntes Kommando ist, wird als Prompt an die verknüpfte OpenCode-Sitzung gesendet.

### Benachrichtigungen stummschalten

- Aktivieren Sie unter **Settings → Repositories** die Option **Mute session notifications for this repository**, um zukünftige automatische Abschlussmeldungen für alle Sitzungen dieses Repositorys zu stoppen. Deaktivieren Sie die Option, um sie wieder zu aktivieren.
- Um eine einzelne Sitzung stummzuschalten, klicken Sie mit der rechten Maustaste auf den Repository-Tab und wählen **Session notifications**. Dort können Sie jede verknüpfte Sitzung mit **Mute session** oder **Unmute session** stummschalten bzw. wieder aktivieren. Die Option ist nur verfügbar, wenn der ausgewählte Benachrichtigungsdienst verbunden und das Repository nicht stummgeschaltet ist.
- Die Stummschaltung betrifft nur automatische Abschlussmeldungen. Sie stoppt weder den Agenten noch löscht sie Nachrichten oder blockiert Antworten in einem bestehenden Sitzungsthread.

### Integration deaktivieren oder Zugriff widerrufen

Deaktivieren Sie **Enable session notifications**, um Benachrichtigungen auszuschalten. Um den Zugriff vollständig zu widerrufen, entfernen Sie die App bzw. den Bot aus dem Slack-Workspace oder Discord-Server und widerrufen Sie die Tokens. Fügen Sie Tokens niemals in Chats ein und committen Sie sie nicht in Git.

## 2. Slack

### Slack-App erstellen

1. Öffnen Sie [api.slack.com/apps](https://api.slack.com/apps) und wählen Sie **Create New App** → **From scratch**.
2. Vergeben Sie einen Namen, zum Beispiel **CodeBadger**, und wählen Sie den Workspace für die Benachrichtigungen.
3. Öffnen Sie **OAuth & Permissions** und fügen Sie unter **Bot Token Scopes** Folgendes hinzu:
   - `chat:write` zum Veröffentlichen von Benachrichtigungen und Thread-Antworten.
   - `channels:read` und `channels:history` zum Auflisten öffentlicher Kanäle und Empfangen ihrer Nachrichten.
   - `groups:read` und `groups:history` zum Auflisten privater Kanäle und Empfangen ihrer Nachrichten, falls erforderlich.
4. Aktivieren Sie unter **Event Subscriptions** Events und abonnieren Sie die Bot-Events `message.channels` und `message.groups` (letzteres gilt für private Kanäle).
5. Aktivieren Sie **Socket Mode**. CodeBadger verwendet Socket Mode; eine öffentliche Request URL ist nicht erforderlich.
6. Erstellen Sie unter **Basic Information → App-Level Tokens** ein App-Token mit `connections:write`. Es beginnt mit `xapp-`.
7. Kehren Sie zu **OAuth & Permissions** zurück und klicken Sie auf **Install to Workspace** (oder **Reinstall to Workspace**, wenn Sie nach der Installation Scopes ergänzt haben). Erteilen Sie die Zustimmung und kopieren Sie das **Bot User OAuth Token**, das mit `xoxb-` beginnt.
8. Laden Sie den Bot in den gewünschten Slack-Kanal ein, zum Beispiel mit `/invite @CodeBadger`. Der Bot kann nur in Kanälen posten, denen er beigetreten ist.

Wenn ein Administrator die App genehmigen oder installieren muss, bitten Sie den Slack-Administrator, die App und die genannten Berechtigungen freizugeben.

### Slack-Mitglieder-ID finden

Öffnen Sie in der Slack-Desktop-App Ihr Profil, wählen Sie **More** und dann **Copy member ID**. Die ID beginnt normalerweise mit `U`. CodeBadger verwendet sie als Sicherheitsprüfung: Nur Nachrichten dieses Slack-Benutzers werden an OpenCode weitergeleitet.

### CodeBadger für Slack konfigurieren

1. Öffnen Sie **Settings → Notifications**.
2. Wählen Sie **Slack** als **Notification channel**. Falls Discord ebenfalls eingerichtet ist, bleibt dadurch nur Slack aktiv.
3. Fügen Sie das `xoxb-`-Token unter **Slack bot token** und das `xapp-`-Token unter **Slack app-level token** ein. Die Tokens werden sicher auf dem Gerät gespeichert, nicht in der normalen Einstellungsdatei.
4. Geben Sie Ihre Slack-Mitglieder-ID ein.
5. Klicken Sie auf **Load Slack channels** und wählen Sie einen Kanal, in dem der Bot Mitglied ist. Kanäle, denen der Bot nicht beigetreten ist, können nicht ausgewählt werden.
6. Klicken Sie auf **Test Slack connection**, aktivieren Sie **Send OpenCode completion notifications to Slack** und speichern Sie.

Der Status zeigt, ob CodeBadger über Socket Mode mit Slack verbunden ist. CodeBadger muss laufen, um Antworten und OpenCode-Fragen zu empfangen und Sitzungen zu überwachen.

## 3. Discord

### Discord-App und Bot erstellen und einladen

1. Öffnen Sie das [Discord Developer Portal](https://discord.com/developers/applications) und wählen Sie **New Application**. Vergeben Sie zum Beispiel den Namen **CodeBadger**.
2. Öffnen Sie den Bereich **Bot** und erstellen Sie den Bot, falls dies nicht automatisch geschehen ist. Kopieren Sie sein Token über **Reset Token** oder **View Token**. Behandeln Sie es wie ein Passwort; jeder mit diesem Token kann den Bot steuern.
3. Aktivieren Sie unter **Bot → Privileged Gateway Intents** den **Message Content Intent** und speichern Sie. CodeBadger benötigt diesen privilegierten Intent, um Servernachrichtentexte zu empfangen.
4. Öffnen Sie **OAuth2 → URL Generator**. Wählen Sie unter **Scopes** `bot` und unter **Bot Permissions**:
   - **View Channels** und **Read Message History**, damit der Bot den Kanal sehen und Kontext lesen kann.
   - **Send Messages**, um Benachrichtigungen zu veröffentlichen.
   - **Create Public Threads** und **Send Messages in Threads**, um Sitzungsthreads zu erstellen und darin zu antworten.
5. Öffnen Sie die erzeugte URL im Browser, wählen Sie Ihren Server und autorisieren Sie den Bot. Sie benötigen die Berechtigung, Bots zum Server hinzuzufügen.
6. Prüfen Sie in den Server- und Kanaleinstellungen, ob der Bot diese Berechtigungen tatsächlich hat. Kanalüberschreibungen können Serverberechtigungen einschränken.

### Discord-IDs finden

Aktivieren Sie im Discord-Client **User Settings → Advanced → Developer Mode**. Klicken Sie mit der rechten Maustaste auf den Server, in den der Bot eingeladen wurde, und wählen Sie **Copy Server ID**. Kopieren Sie auch Ihre eigene Benutzer-ID über Ihr Profil/Kontextmenü mit **Copy User ID**. CodeBadger nutzt Ihre Benutzer-ID als Sicherheitsprüfung und verarbeitet nur Nachrichten von Ihnen.

### CodeBadger für Discord konfigurieren

1. Öffnen Sie **Settings → Notifications**.
2. Wählen Sie **Discord** als **Notification channel**. Auch wenn Slack eingerichtet ist, bleibt dann nur Discord aktiv.
3. Fügen Sie das Bot-Token unter **Discord bot token** ein.
4. Geben Sie die kopierte Server-ID unter **Discord server (guild) ID** ein.
5. Klicken Sie auf **Load Discord channels** und wählen Sie einen Text- oder Ankündigungskanal, auf den der Bot zugreifen kann.
6. Geben Sie Ihre Discord-Benutzer-ID unter **Your Discord user ID** ein.
7. Klicken Sie auf **Test Discord connection**, aktivieren Sie **Enable session notifications** und speichern Sie.

Der Status zeigt, ob CodeBadger mit dem Discord Gateway verbunden ist. Wenn Laden oder Testen fehlschlägt, prüfen Sie Bot-Token, Server-ID, Kanalberechtigungen und **Message Content Intent**. CodeBadger muss laufen, um Nachrichten und Antworten zu verarbeiten. Wenn beide Dienste eingerichtet sind, bestimmt **Notification channel**, welche Integration aktiv ist.
