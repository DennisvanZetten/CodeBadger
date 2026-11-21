# Slack-Benachrichtigungshandbuch

Dieses Handbuch erklärt die Einrichtung von Slack-Benachrichtigungen, das Erstellen der Slack-App sowie Antworten, Agentenwechsel und Stummschaltung.

> **Datenschutz:** Eine Slack-Benachrichtigung enthält eine Zusammenfassung einer OpenCode-Antwort. Verbinden Sie nur einen Slack-Workspace, in dem Sie Projektinformationen teilen dürfen. Halten Sie beide Slack-Tokens geheim.

## 1. Slack-App erstellen

1. Öffnen Sie [api.slack.com/apps](https://api.slack.com/apps) und wählen Sie **Create New App** → **From scratch**.
2. Geben Sie der App einen Namen, zum Beispiel **CodeBadger**, und wählen Sie den Workspace für die Benachrichtigungen.
3. Öffnen Sie **OAuth & Permissions**. Fügen Sie unter **Bot Token Scopes** Folgendes hinzu:
   - `chat:write` — Nachrichten und Thread-Antworten veröffentlichen.
   - `channels:read` und `channels:history` — öffentliche Kanäle auflisten und deren Nachrichten empfangen.
   - `groups:read` und `groups:history` — private Kanäle auflisten und deren Nachrichten empfangen, falls Sie einen privaten Kanal verwenden.
4. Öffnen Sie **Event Subscriptions**, aktivieren Sie Events und abonnieren Sie die Bot-Events `message.channels` und `message.groups` (letzteres gilt für private Kanäle).
5. Öffnen Sie **Socket Mode** und aktivieren Sie es. CodeBadger verwendet Socket Mode; eine öffentliche Request URL muss daher nicht eingerichtet werden.
6. Erstellen Sie unter **Basic Information → App-Level Tokens** ein App-Token mit dem Scope `connections:write`. Es beginnt mit `xapp-`.
7. Kehren Sie zu **OAuth & Permissions** zurück und klicken Sie auf **Install to Workspace** (oder **Reinstall to Workspace**, falls Sie nach der Installation Scopes ergänzt haben). Erteilen Sie die Zustimmung und kopieren Sie das **Bot User OAuth Token**. Es beginnt mit `xoxb-`.
8. Laden Sie den Bot in den gewünschten Slack-Kanal ein. Geben Sie dort beispielsweise `/invite @CodeBadger` ein. Der Bot kann nur in Kanälen posten, denen er beigetreten ist.

Wenn ein Administrator die App genehmigen oder installieren muss, bitten Sie den Slack-Administrator, die App und die genannten Berechtigungen freizugeben.

## 2. Slack-Mitglieder-ID finden

Öffnen Sie in der Slack-Desktop-App Ihr Profil, wählen Sie **More** und dann **Copy member ID**. Die ID beginnt normalerweise mit `U`. CodeBadger verwendet sie als Sicherheitsprüfung: Nur Antworten dieses Slack-Mitglieds werden an OpenCode weitergeleitet.

## 3. CodeBadger konfigurieren

1. Öffnen Sie **Settings → Notifications**.
2. Lassen Sie **Slack** als Benachrichtigungskanal ausgewählt.
3. Fügen Sie das `xoxb-`-Token unter **Slack bot token** und das `xapp-`-Token unter **Slack app-level token** ein. Die Tokens werden sicher auf dem Gerät gespeichert, nicht in der normalen Einstellungsdatei.
4. Geben Sie Ihre Slack-Mitglieder-ID ein.
5. Klicken Sie auf **Load Slack channels** und wählen Sie einen Kanal, in dem der Bot Mitglied ist. Kanäle, denen der Bot nicht beigetreten ist, können nicht ausgewählt werden.
6. Klicken Sie auf **Test Slack connection**. Aktivieren Sie **Send OpenCode completion notifications to Slack** und speichern Sie die Einstellungen.

Der Status in diesem Bereich zeigt, ob CodeBadger über Socket Mode mit Slack verbunden ist.

## 4. Benachrichtigungen und Antworten verwenden

- Wenn eine überwachte OpenCode-Sitzung abgeschlossen ist, veröffentlicht CodeBadger eine kurze Zusammenfassung im ausgewählten Slack-Kanal. Die Meldung nennt Agent und Sitzung.
- Antworten Sie **im Thread der CodeBadger-Nachricht**, um genau diese OpenCode-Sitzung fortzusetzen. Eine neue Kanalnachricht wird keiner Sitzung zugeordnet.
- Beginnen Sie Ihre Thread-Antwort mit `!build` oder `!plan`, gefolgt von einem Leerzeichen und Ihrer Anweisung, um den Agenten für diese Antwort zu wechseln. Beispiel: `!plan Vergleiche zwei Implementierungsansätze`. Zuerst wird der Agent gewechselt, anschließend erhält er den Text nach dem Befehl. Weitere Turns verwenden diesen Agenten, bis Sie erneut wechseln.
- Senden Sie `!commit` in einem Sitzungsthread, um alle Repository-Änderungen zu stagen und zu committen. Wenn KI-Commitnachrichten konfiguriert sind, erstellt CodeBadger die Nachricht automatisch. Andernfalls fragt CodeBadger nach einem Titel; senden Sie ihn als nächste Thread-Antwort, optional mit der Beschreibung in der folgenden Zeile.
- Senden Sie `!push` in einem Sitzungsthread, um mit derselben Remote-Prüfung und gegebenenfalls einem vorherigen Pull wie über die Push-Schaltfläche im Git-Dialog zu pushen.
- Senden Sie `!help` als eigenständige Kanalnachricht, um die Kanalbefehle anzuzeigen, oder in einem Sitzungsthread für die Sitzungsbefehle. Im Auswahldialog von `!new` erklärt `!help` die Repositoryauswahl und das Senden der Aufgabe.
- OpenCode-Berechtigungsanfragen erscheinen im Sitzungsthread. Antworten Sie mit `!allow <id>` für eine einmalige Genehmigung, `!always <id>` für eine dauerhafte Genehmigung oder `!deny <id> [Grund]` zum Ablehnen.
- OpenCode-Formulare erscheinen ebenfalls im Thread. Antworten Sie mit `!answer <id> key=value; key2=value`, verwenden Sie die angezeigten Feldschlüssel und trennen Sie Mehrfachauswahlen mit `|`.
- Senden Sie `!new` als eigenständige Kanalnachricht, um eine neue Sitzung zu starten. Antworten Sie im von CodeBadger erstellten Thread mit der Nummer eines Repositorys und senden Sie anschließend Ihre Aufgabe als nächste Thread-Antwort. Es werden nur aktive, nicht sandboxgeschützte OpenCode-Repositorys angezeigt.
- Senden Sie `!todos` im Kanal oder in einem Sitzungsthread, um offene Punkte aufzulisten. Bei mehr als zehn Einträgen zeigt CodeBadger die zehn neuesten nach Erstellungsdatum.
- Verwenden Sie `!build` für Implementierungsarbeiten und `!plan` für Planung und Analyse. Senden Sie nicht nur `!build` oder `!plan`; fügen Sie eine Anweisung hinzu.

Die Slack-Antwort muss von der in CodeBadger eingetragenen Mitglieder-ID stammen. CodeBadger muss laufen, um Slack-Antworten und OpenCode-Fragen zu empfangen und den Abschluss von Sitzungen zu überwachen.

## 5. Repository oder Sitzung stummschalten

Wenn Slack-Benachrichtigungen verbunden sind, öffnen Sie **Settings → Repositories**, um die Einstellung für das gesamte Repository zu verwalten:

- Aktivieren Sie **Mute Slack notifications for this repository**, um zukünftige automatische Abschlussmeldungen für alle Sitzungen dieses Repositorys zu stoppen. Deaktivieren Sie die Option, um sie wieder zu aktivieren.
- Um eine einzelne Sitzung stummzuschalten, klicken Sie mit der rechten Maustaste auf den Repository-Tab und wählen **Session notifications**. Diese Option ist nur verfügbar, wenn Slack verbunden und das Repository nicht stummgeschaltet ist. Der Dialog listet Slack-verknüpfte Sitzungen mit **Mute session** und **Unmute session** pro Sitzung auf.

Die Stummschaltung betrifft nur automatische Abschlussmeldungen. Sie beendet den Agenten nicht, löscht keine Slack-Nachrichten und blockiert keine Antworten im bestehenden Sitzungsthread.

## Hinweise und Einschränkungen

- Die erste Version unterstützt Slack-Abschlussmeldungen für OpenCode-Sitzungen. Andere Agent-Tools werden dafür nicht überwacht.
- CodeBadger muss laufen, um einen Sitzungsabschluss zu erkennen und Slack-Antworten zu empfangen. Bei vollständig geschlossener App werden keine Meldungen gesendet.
- Eine Sitzung erscheint in der Stummschaltungsliste, nachdem CodeBadger sie mit einem Slack-Thread verknüpft hat.
- Zum Widerrufen des Zugriffs deaktivieren Sie den Benachrichtigungsschalter, entfernen die App aus dem Slack-Workspace oder widerrufen die Tokens. Fügen Sie Tokens niemals in Chats ein und committen Sie sie nicht in Git.
