# Sicherheit und Datenschutz

CodeBadger arbeitet mit lokalen Repository-Dateien, Agent-Konfigurationen, externen KI-Anbietern, MCP-Servern und optional Slack. Prüfen Sie daher, welche Daten Sie teilen und welche Berechtigungen Sie aktivieren.

## Geheimnisse und lokale Speicherung

- API-Schlüssel für Anbieter und Slack-Tokens werden sicher auf dem Gerät gespeichert. Sie gehören nicht in Projektdateien, Prompts oder Git-Commits.
- Allgemeine Einstellungen und die Ideen-/Aufgabenliste werden lokal gespeichert. Slack-Sitzungs-IDs und Thread-Verknüpfungen werden lokal gespeichert, damit Antworten der richtigen Sitzung zugeordnet werden.
- Senden Sie Slack-Benachrichtigungen nur an einen Workspace, in dem das Teilen von Projektinformationen erlaubt ist. CodeBadger leitet Slack-Antworten nur vom konfigurierten Slack-Mitglied weiter.
- Ein KI-Anbieter erhält die für die angeforderte Funktion benötigten Inhalte, beispielsweise einen gestagten Diff für eine Commitnachricht. Verwenden Sie nur einen Anbieter, bei dem das Teilen dieser Repository-Inhalte zulässig ist.

## Agent- und MCP-Zugriff

Agenten können Dateien und Tools mit den Berechtigungen ihrer Ausführungsumgebung verwenden. Ein MCP-Server kann Zugriff auf externe Daten oder Aktionen bieten. Prüfen Sie Quelle und Konfiguration, bevor Sie einen Server aktivieren. Globale Agent-Einstellungen können mehrere Projekte beeinflussen; Projektkonfiguration kann in Git eingecheckt werden.

## Sandbox und Datei-Prüfung

Wählen Sie nur die für die Aufgabe benötigten Dateien aus. Die Dateiauswahl ist keine Geheimnisprüfung; eine Erkennung ist nicht garantiert. Im Kopier-/Prüfmodus werden Sandbox-Änderungen erst nach Ihrer Auswahl auf vorhandene Dateien angewendet; Originale werden gesichert. Prüfen Sie Inhalte und Sicherungen. Die Anwendung mehrerer Dateien ist keine atomare Transaktion.

Überschreitet die Auswahl die Sandbox-Grenzwerte, kann der Direktmodus verwendet werden: Änderungen werden sofort in den ausgewählten Originaldateien sichtbar, ohne Prüfungskopie. Erstellen, Löschen und Umbenennen von Dateien ist gesperrt, vorhandene ausgewählte Dateien können aber direkt geändert werden. Prüfen Sie vor dem Start den Modushinweis und die Dateiauswahl.

Prüfen Sie Netzwerkanfragen und Berechtigungen im Einzelfall. Erlauben Sie nur vertrauenswürdige Hosts und Aktionen. Das Widerrufen einer Berechtigung kann eine Sandbox-Sitzung beenden.

## Git-Operationen

Durch Staging wird festgelegt, was in einen Commit gelangt. Prüfen Sie vor dem Commit die gestagten Dateien und den Diff. **Undo selected** im Git-Modul verwirft nicht gestagte Änderungen und kann dort nicht rückgängig gemacht werden. Pull, Merge, Cherry-Pick und Revert können Konflikte verursachen; lösen Sie diese sorgfältig, bevor Sie fortfahren.

## Zugriff widerrufen

Deaktivieren Sie Slack-Benachrichtigungen oder widerrufen Sie Slack-App-Tokens, um die Verbindung zu beenden. Entfernen oder rotieren Sie KI-Anbieterschlüssel beim Anbieter und aktualisieren Sie sie in CodeBadger. Deaktivieren Sie nicht vertrauenswürdige MCP-Server. Beenden Sie aktive Sandbox-Sitzungen und prüfen oder verwerfen Sie erfasste Änderungen. Bewahren Sie Sicherungen auf, solange Sie Änderungen möglicherweise wiederherstellen müssen.
