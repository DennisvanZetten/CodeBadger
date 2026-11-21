# Agent-Terminals und Sitzungen

Ein Repository-Tab kombiniert den Projektordner, ein Terminal für den ausgewählten Agenten und den Git-Status. Beim ersten Öffnen des Terminals startet CodeBadger den Agenten im Repository-Ordner. Das integrierte Terminal ist unter Windows verfügbar.

## Agent starten und verwenden

1. Aktivieren Sie ein Repository und prüfen Sie, ob der Pfad existiert und der ausgewählte Agent verfügbar ist.
2. Öffnen Sie den Repository-Tab. CodeBadger startet den konfigurierten Agenten im Projektordner.
3. Geben Sie Ihre Eingabe im Terminal ein und senden Sie Prompts wie gewohnt an den ausgewählten Agenten. Das Terminal unterstützt interaktive Eingaben und zeigt die Prozessausgabe.
4. Wechseln Sie über einen anderen Repository-Tab zwischen Projektterminals.

Wird der Agent-Befehl nicht gefunden, zeigt CodeBadger eine Warnung. Installieren Sie den Agenten oder wählen Sie unter **Settings → Repositories** einen anderen aus. Ist ein zuvor geöffnetes Terminal geschlossen, kann auch der Prozess beendet sein; starten Sie den Agenten dann erneut.

## OpenCode-Sitzungen fortsetzen

Für OpenCode ohne Sandbox verfolgt CodeBadger die ausgewählte Sitzung. Eine über die Aufgabenliste oder Slack gestartete OpenCode-Sitzung kann im zugehörigen Repository-Terminal fortgesetzt werden. Das Terminal startet OpenCode mit der Sitzungs-ID; den Sitzungsverlauf verwaltet OpenCode.

Eine mit **Start in new session** gestartete Aufgabe erstellt eine neue OpenCode-Sitzung und verknüpft sie mit dem Aufgabeneintrag. Eine über Slack mit `!new` gestartete Sitzung ist mit ihrem Slack-Thread verknüpft. Wenn Sie eine Sitzung über den Aufgabenlink öffnen, wählt CodeBadger das zugehörige Repository und die Sitzung aus.

Das Repository-Terminal ist ein interaktives Agent-Terminal und keine Übersicht aller Sitzungen. Verwenden Sie die Befehle des jeweiligen Agenten im Terminal, um andere Sitzungen anzusehen oder zu öffnen. Terminalausgabe wird möglicherweise nicht wiederhergestellt, wenn der Terminalprozess beendet wurde.

## Aktivität und Fragen

Bei unterstützten OpenCode-Sitzungen ohne Standalone-Modus zeigt CodeBadger die Aktivität am Repository-Tab an:

- Ein Spinner bedeutet, dass eine Sitzung arbeitet.
- Ein Frage-Symbol bedeutet, dass OpenCode auf eine Berechtigung oder Eingabe wartet.
- Wenn Slack aktiviert ist, können unterstützte Sitzungen eine Abschlussmeldung erhalten. Offene Fragen werden zuerst bearbeitet; danach kann die abgeschlossene Antwort gemeldet werden.

Die Aktivitätserkennung benötigt unter Windows den verwalteten OpenCode-Dienst. Standalone-OpenCode, explizit ausgewählte Server und andere Agent-Tools werden nicht auf dieselbe Weise überwacht.

## Voraussetzungen für Terminal und OpenCode-Aktivität

Das Terminal startet den ausgewählten Agent-Befehl mit dem Repository als Arbeitsverzeichnis. Für OpenCode-Aktivitätsanzeigen fragt CodeBadger den verwalteten OpenCode-Dienst separat über den Befehl `opencode api` ab. Daher werden OpenCode-Prozesse mit `--standalone` oder `--server` nicht in dieser Aktivitätsüberwachung erfasst.

## Terminalhinweise

- Lassen Sie den Repository-Tab verfügbar, solange Sie das Terminal verwenden. Beim Schließen oder Auskoppeln von Fenstern verwaltet CodeBadger das Terminal entsprechend der Repository-Sitzung.
- Entfernen oder verschieben Sie den Projektordner nicht, während ein Terminal oder Agent darin arbeitet.
- Agent-Ausgaben können Projektinhalte oder Tool-Ergebnisse enthalten. Teilen Sie Terminalausgaben nur über freigegebene Kanäle.
- Git-Aktionen finden Sie über **Git overview** im Repository-Arbeitsbereich; siehe das separate Git-Modul-Handbuch.

## Sandbox-Terminals

Ist die OpenCode-Sandbox für ein Repository aktiviert, verwendet CodeBadger statt des normalen Agent-Terminals den Sandbox-Ablauf. Wählen Sie zuerst Dateien aus und lesen Sie den Hinweis zum Zugriffsmodus. Lesen Sie vor dem Start **OpenCode-Sandbox**.
