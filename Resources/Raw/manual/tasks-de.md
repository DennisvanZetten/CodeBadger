# Ideen und Aufgaben

Die Liste **Ideas and tasks** ist ein lokaler Ort, um Arbeit festzuhalten, die Sie später bearbeiten möchten. Öffnen Sie sie über das Ideen-/Aufgabenmenü in CodeBadger. Die Liste wird auf diesem Gerät gespeichert.

## Eintrag hinzufügen

1. Öffnen Sie **Ideas and tasks** und wählen Sie **New**.
2. Geben Sie einen Titel mit höchstens 160 Zeichen ein.
3. Wählen Sie einen Typ: **Idea**, **Wish**, **Task** oder **Bug**.
4. Fügen Sie optional eine Beschreibung mit maximal 2.000 Zeichen hinzu und speichern Sie.

Einträge können auch über Slack mit `!todo <text>` erstellt werden. Der erste Satz wird zum Titel, der restliche Text zur Beschreibung. Titel sind auf 160 Zeichen begrenzt; ist der erste Satz länger, wird er bei 160 Zeichen abgeschnitten und der Rest als Beschreibung verwendet. Optionale Typ-Präfixe sind `idea:`, `wish:`, `task:`, `bug:` und `story:`. Mit `!todos` werden offene Einträge in Slack angezeigt; bei mehr als zehn zeigt CodeBadger die zehn neuesten.

## Status und Aufräumen

Jeder Eintrag hat den Status **Open**, **In progress** oder **Done**. Ändern Sie ihn über die Statusauswahl am Eintrag. Die Einträge sind nach Status gruppiert und innerhalb der Gruppe nach Erstellungsdatum absteigend sortiert. Beim Löschen wird der Eintrag dauerhaft aus der lokalen Liste entfernt.

## Eine Aufgabe starten

Nur Einträge vom Typ **Task** können direkt aus der Liste als neue OpenCode-Sitzung gestartet werden.

1. Wählen Sie ein aktives, vorhandenes OpenCode-Repository ohne Sandbox.
2. Wählen Sie **Start in new session**.
3. Wählen Sie den Branch als Ausgangspunkt. Wenn ein `development`-Branch existiert, können Sie ihn oder den aktuellen Branch verwenden.
4. Wenn für das Repository Tickets erforderlich sind, wählen Sie eine Ticketoption:
   - vorhandenes Ticket verwenden und einen Branch erstellen;
   - über den konfigurierten MCP-Server ein Ticket erstellen und einen Branch anlegen;
   - vorhandenes Ticket und vorhandenen lokalen Branch verwenden.
5. CodeBadger checkt den vorhandenen Branch aus oder erstellt einen neuen, startet eine OpenCode-Sitzung im **Plan**-Modus und sendet die Aufgabe mit der Story-Vorlage des Repositorys als Kontext.

Das Branch-Präfix lässt sich unter **Settings → Other** einstellen. Ein neuer Branchname enthält nach Möglichkeit Präfix, Ticketnummer und einen Slug des Aufgabentitels.

Nach erfolgreichem Start erhält die Aufgabe den Status **In progress** und wird mit Repository, Branch, Ticket und Sitzung verknüpft. Öffnen Sie die verknüpfte Sitzung über den Sitzungslink am Aufgabeneintrag. Solange eine Sitzung verknüpft ist, kann die Aufgabe nicht erneut aus der Liste gestartet werden.

## Voraussetzungen und Hinweise

- Zum Starten sind ein aktives OpenCode-Repository, ein vorhandener Pfad und ein geeigneter Git-Branch für Branch-Operationen erforderlich.
- Dieser Ablauf erstellt weder einen Commit noch einen Push; verwenden Sie dafür das Git-Modul.
- Zum Erstellen eines Tickets müssen Ticketsystem und MCP-Server für das Repository konfiguriert und verfügbar sein.
- Abgeschlossene Arbeit in OpenCode ändert den Aufgabenstatus nicht automatisch. Aktualisieren Sie ihn in der Liste.
