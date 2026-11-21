# KI- und Anwendungseinstellungen

Öffnen Sie in der Anwendung **Settings**, um allgemeine Optionen, KI-Commits, Benachrichtigungen und Sandbox-Grenzwerte zu verwalten. Die meisten Einstellungen werden lokal auf diesem Gerät gespeichert.

## KI für Commitnachrichten

Unter **AI provider** konfigurieren Sie Anbieter und Modell für die Generierung von Commitnachrichten im Git-Modul.

1. Wählen Sie einen Anbieter wie OpenAI, Azure OpenAI, Anthropic, Google Gemini, Ollama oder Custom URL.
2. Geben Sie bei Bedarf Anbieter-URL und API-Schlüssel ein. API-Schlüssel werden sicher auf dem Gerät gespeichert.
3. Testen Sie die Verbindung und laden Sie die verfügbaren Modelle.
4. Wählen Sie ein Modell und legen Sie optional das maximale Eingabe-Tokenbudget und eigene Anweisungen fest.

Eigene Anweisungen können beispielsweise eine Commit-Konvention oder Sprache vorgeben. Die KI-Generierung ist optional; Sie können die Commitnachricht jederzeit selbst schreiben. Prüfen Sie generierte Titel und Beschreibungen vor dem Commit.

## Benachrichtigungen

Unter **Notifications** konfigurieren Sie Töne für abgeschlossene Sitzungen und Agent-Fragen sowie Slack. Slack-Tokens werden sicher gespeichert. Informationen zu App-Berechtigungen, Verbindung und Befehlen finden Sie im separaten Handbuch **Slack-Benachrichtigungen**.

## Sandbox-Grenzwerte

Unter **Sandbox** legen Sie die maximale Größe pro ausgewählter Datei und für die gesamte Auswahl fest. Diese Grenzwerte gelten für neue Sandbox-Sitzungen. Überschreitet die explizite Auswahl einen der Grenzwerte, kann die Sandbox automatisch den Direktmodus verwenden. Änderungen werden dann sofort in den ausgewählten Originaldateien sichtbar. Weitere Informationen und Sicherheitshinweise finden Sie unter **OpenCode-Sandbox**.

## Weitere Einstellungen

- **Task branch prefix:** wird für Branches verwendet, die für Aufgaben erstellt werden.
- **Language:** wählen Sie Englisch, Niederländisch, Deutsch oder Französisch. Oberfläche und Handbücher wechseln, soweit Übersetzungen verfügbar sind.
- **Theme:** wählen Sie ein verfügbares helles oder dunkles Design.
- **Toast duration:** legt fest, wie lange Statusmeldungen sichtbar bleiben.
- **Completion sound** und **Question sound:** wählen und aktivieren/deaktivieren Sie die Töne unabhängig voneinander. Bei der Auswahl wird eine Vorschau abgespielt.

Wählen Sie **Save**, um allgemeine Einstellungen zu speichern. API-Schlüssel bleiben im sicheren Speicher des Geräts und gehören nicht in Projektdateien oder Commits.
