# Repository-Einrichtung

Ein Repository verknüpft einen Projektordner mit einem Agenten in CodeBadger. Aktive Repositories erscheinen als Tabs auf dem Startbildschirm.

## Repository hinzufügen

1. Öffnen Sie **Settings → Repositories**.
2. Wählen Sie **Add**.
3. Geben Sie einen aussagekräftigen Namen ein. Er wird für den Tab und in CodeBadger-Fenstern verwendet.
4. Wählen Sie mit der Ordnerschaltfläche neben **Path** den Projektordner aus oder geben Sie den Pfad direkt ein. Wählen Sie den Git-Projektordner, keine einzelne Datei.
5. Wählen Sie unter **Agent tool** den Agenten für dieses Repository: OpenCode, Claude Code, Codex oder Hermes.
6. Lassen Sie den Aktivierungsschalter eingeschaltet, damit das Repository auf dem Startbildschirm angezeigt wird.
7. Wählen Sie unten **Save**.

Der Projektordner muss vorhanden sein. Wenn der ausgewählte Agent-Befehl nicht installiert ist oder nicht gefunden wird, zeigt CodeBadger eine Warnung an und kann den Agenten nicht starten. Ein Ordner kann auch dann eingetragen werden, wenn CodeBadger dort kein Git-Repository initialisiert hat; das Git-Modul funktioniert nur, wenn Git den Pfad als Repository erkennt.

## Repositories verwalten

- Deaktivieren Sie ein Repository, um seinen Tab vorübergehend auszublenden. Einstellungen und Dateien bleiben erhalten.
- Ändern Sie Name oder Pfad, wenn ein Projekt verschoben wurde. Stellen Sie nach einer Pfadänderung sicher, dass der Ordner existiert.
- Mit den Pfeilen ändern Sie die Reihenfolge der Repository-Tabs.
- Beim Entfernen wird das Repository aus den CodeBadger-Einstellungen gelöscht, nicht jedoch der Projektordner oder die Git-Historie.
- Ziehen Sie Repository-Tabs, um sie neu anzuordnen. Ein Tab kann in ein eigenes Fenster ausgekoppelt und später wieder angedockt werden.

## Repository-spezifische Optionen

### Agent-Konfiguration

Je nach Agent erscheint neben der Auswahl eine Schaltfläche für Einstellungen. Damit verwalten Sie die Projektkonfiguration. Bei OpenCode können Sie beispielsweise MCP-Server und Projekt-Skills anzeigen oder bearbeiten. Vor Änderungen an einer vorhandenen Agent-Konfiguration erstellt CodeBadger eine Sicherung. Die Projektkonfiguration kann nur bearbeitet werden, wenn der betreffende Agent verfügbar ist.

### Tickets

Tickets können optional verpflichtend sein; als Ticketsystem stehen beispielsweise Jira und GitHub Issues zur Auswahl. Wenn Tickets aktiviert sind, wählen Sie auch den MCP-Server aus, der für dieses Repository in der Agent-Projektkonfiguration eingerichtet ist. Diese Option wird beim Starten und Ausarbeiten von Aufgaben verwendet.

### KI-Story-Vorlage

Die Story-Vorlage beschreibt die Struktur, die die KI für eine Aufgabe verwenden soll, zum Beispiel User Story, Kontext, Akzeptanzkriterien, technische Hinweise und ausgeschlossene Inhalte. Sie wird in den ersten Prompt aufgenommen, wenn Sie eine Aufgabe für dieses Repository starten.

### OpenCode-Sandbox

Für OpenCode-Repositories können Sie die Sandbox aktivieren. Sie führt OpenCode in einer isolierten Umgebung mit eigenen Einstellungen und Berechtigungen aus. Verwalten Sie Berechtigungen und Sandbox-Optionen über **Permissions** und **Settings**. Slack-Commit- und Push-Befehle sind nur für OpenCode-Repositories ohne Sandbox verfügbar.

### Slack-Benachrichtigungen

Wenn Slack-Benachrichtigungen eingerichtet sind, können Sie automatische Abschlussmeldungen für einzelne OpenCode-Repositories stummschalten. Dadurch werden künftige automatische Abschlussmeldungen für dieses Repository unterdrückt, Sitzungen aber nicht beendet und Antworten in bestehenden Slack-Threads nicht blockiert. Weitere Informationen finden Sie im Handbuch **Slack-Benachrichtigungen**.

## Repository öffnen

Wählen Sie den Repository-Tab auf dem Startbildschirm. Der Arbeitsbereich zeigt das Agent-Terminal und, sofern der Pfad ein gültiges Git-Repository ist, den Git-Status. Der Tab zeigt den aktuellen Branch, geänderte Dateien sowie Commits vor und hinter dem Remote-Branch. Mit **Git overview** öffnen Sie das Git-Modul.

## Fehlerbehebung

- **Path does not exist:** Wählen Sie den richtigen Projektordner erneut aus; verschobene Ordner werden nicht automatisch erkannt.
- **Agent command not found:** Installieren Sie den ausgewählten Agenten oder wählen Sie einen verfügbaren Agenten.
- **Git status unavailable:** Prüfen Sie, ob Git installiert ist und der Pfad auf einen Git-Working-Tree verweist.
- **Repository fehlt auf dem Startbildschirm:** Prüfen Sie in den Einstellungen, ob es aktiv ist und einen vorhandenen Pfad hat.
