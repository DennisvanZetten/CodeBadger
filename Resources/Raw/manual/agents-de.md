# Agenten und Projektkonfiguration

CodeBadger kann für jedes aktive Repository ein Agent-Terminal starten. Der ausgewählte Agent muss auf dem Gerät installiert und über den erwarteten Befehl verfügbar sein.

## Agent auswählen und Verfügbarkeit prüfen

Wählen Sie unter **Settings → Repositories** pro Repository einen Agenten: OpenCode, Claude Code, Codex oder Hermes. Wenn CodeBadger den Befehl nicht findet, erscheint eine Warnung und die Sitzung kann nicht gestartet werden. Installieren Sie den Agenten oder wählen Sie einen anderen.

In den allgemeinen Einstellungen gibt es außerdem **Agent management**. Wählen Sie dort einen Agenten, um dessen globale Konfigurationsdatei und gefundene MCP-Server anzuzeigen. Pfad und Verfügbarkeitsstatus helfen bei der Ermittlung der aktiven Konfiguration.

## MCP-Server

MCP-Server verbinden einen Agenten mit externen Tools oder Datenquellen. CodeBadger kann gefundene Server anzeigen und, sofern unterstützt, ihren Aktivierungsstatus ändern.

- Allgemeines **Agent management** steuert die globale Agent-Konfiguration.
- In der Repository-Konfiguration verwalten Sie projektspezifische Server. Das Format hängt vom Agenten ab: OpenCode und Claude Code verwenden JSON, Codex TOML und Hermes YAML.
- Für Ticket-Abläufe kann ein Repository einen bestimmten MCP-Server auswählen. Dieser muss in der zugehörigen Projektkonfiguration verfügbar und aktiviert sein.
- Claude Code speichert MCP-Definitionen in `.mcp.json` und den Aktivierungsstatus für dieses Repository in `.claude/settings.local.json`.
- Die Codex-Projektkonfiguration liegt in `.codex/config.toml`. Codex lädt Projekteinstellungen nur für vertrauenswürdige Projekte.
- OpenCode liest Projekteinstellungen aus `opencode.json` oder `.opencode/opencode.json`.

Vor Änderungen an vorhandenen Konfigurationsdateien erstellt CodeBadger eine Sicherung mit Zeitstempel. Konfigurationen können nur bearbeitet werden, wenn der Agent verfügbar ist. Prüfen Sie angepasste JSON-, TOML- oder YAML-Inhalte vor dem Speichern sorgfältig.

## Projekt-Skills

Die OpenCode- und Codex-Projektkonfigurationsfenster enthalten einen Tab für Projekt-Skills. Sie können einen Skill auswählen, seinen Markdown-Inhalt bearbeiten und speichern oder einen neuen Skill erstellen. Codex-Skills liegen unter `.agents/skills`, OpenCode-Skills unter `.opencode/skills`. Neue Skill-IDs sollten Kleinbuchstaben, Ziffern und Bindestriche verwenden. Skills liefern wiederverwendbare Projektanweisungen und ersetzen keine allgemeine Agent-Konfiguration.

## Claude Code und Repository-Konfiguration

Claude-Code-Projekteinstellungen sind pro Repository über die Schaltfläche neben der Agent-Auswahl verfügbar. Dort verwalten Sie die MCP-Konfiguration und können die JSON-Konfiguration anzeigen oder bearbeiten. Ist der Agent nicht verfügbar, ist die Konfiguration schreibgeschützt.

## Sicherheit und Prüfung

- Globale Agent-Einstellungen können mehrere Repositories beeinflussen; ändern Sie sie mit Bedacht.
- Projektkonfiguration kann in Git eingecheckt werden. Prüfen Sie, welche Einstellungen und Serverdefinitionen Sie committen.
- Speichern Sie keine API-Schlüssel oder anderen Geheimnisse in Konfigurationsdateien oder Skills.
- Ein aktivierter MCP-Server kann dem Agenten Zugriff auf externe Aktionen oder Daten geben. Aktivieren Sie nur vertrauenswürdige Server.
