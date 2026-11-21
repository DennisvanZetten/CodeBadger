# OpenCode-Sandbox

Die OpenCode-Sandbox startet OpenCode für ein Repository in einer isolierten Umgebung. Statt automatisch das gesamte Repository freizugeben, wählen Sie ausdrücklich aus, welche Dateien verfügbar sind. Öffnen Sie für ein OpenCode-Repository unter **Settings → Repositories** die Optionen **Permissions** und **Settings**.

## Modell und Dateien auswählen

Öffnen Sie **Settings** in den Sandbox-Optionen des OpenCode-Repositorys.

1. Wählen Sie das Modell für neue Sandbox-Sitzungen.
2. Wählen Sie die Dateien und Ordner aus, die OpenCode benötigt. Bei einem Ordner werden die enthaltenen Dateien mit ausgewählt.
3. Speichern Sie die Auswahl. Sensible Pfade und Links werden nicht als auswählbare Einträge angezeigt.

Allgemeine Sandbox-Grenzwerte finden Sie unter **Settings → Sandbox**. Standardwerte sind 64 MB pro Datei und 1.024 MB insgesamt; konfigurierbare Bereiche sind 1–512 MB pro Datei und 1–2.048 MB insgesamt. Die Grenzwerte gelten für neue Sitzungen.

## Zwei Zugriffsmodi

Beim Start misst CodeBadger die ausdrücklich ausgewählten Dateien:

- **Kopier-/Prüfmodus:** Liegt die Auswahl innerhalb beider Größenlimits, werden ausgewählte Dateien in eine temporäre, isolierte Arbeitskopie kopiert. Die Sandbox erhält keinen allgemeinen Netzwerkzugriff.
- **Direktmodus:** Überschreitet eine Datei oder die Gesamtauswahl ein Limit, verwendet CodeBadger temporären, eingeschränkten Zugriff auf die ausgewählten Originaldateien. Änderungen sind sofort im Repository sichtbar; Erstellen, Löschen und Umbenennen von Dateien ist gesperrt. In diesem Modus gibt es keine Prüfungskopie.

Der Modus wird automatisch ausgewählt. Höhere Grenzwerte können den Speicher- und Festplattenverbrauch erhöhen. Wählen Sie nur Dateien aus, die für die Aufgabe benötigt werden.

## Sitzung beenden und Änderungen prüfen

Beenden Sie die Sandbox nach der Arbeit mit **Stop sandbox**. Im Kopier-/Prüfmodus werden Änderungen zur Prüfung erfasst. Die Prüfung zeigt geänderte Dateien und deren Inhalt; Löschungen können angezeigt, aber nicht angewendet werden.

1. Prüfen Sie den vorgeschlagenen Inhalt jeder Datei.
2. Wählen Sie nur Änderungen aus, die übernommen werden sollen.
3. Wählen Sie **Apply selected modifications**.

CodeBadger übernimmt nur ausgewählte Änderungen in vorhandene Dateien und sichert die Originale. Vorher wird auf Konflikte geprüft; wurde eine Originaldatei inzwischen geändert, kann die Übernahme abgelehnt werden. Die Anwendung mehrerer Dateien ist keine atomare Transaktion. Prüfen Sie Sicherungen und Ergebnisse sorgfältig. Nicht übernommene Prüfänderungen bleiben verfügbar, bis Sie sie übernehmen oder ausdrücklich verwerfen. Vor dem Start einer weiteren Sandbox-Sitzung muss die Prüfung abgeschlossen oder verworfen sein.

Im Direktmodus werden Änderungen sofort in die ausgewählten Originaldateien geschrieben. Beim Stoppen wird für diesen Modus keine Prüfungskopie erstellt.

## Berechtigungen und Netzwerkzugriff

Sandbox-Berechtigungen gelten pro Repository. Der Berechtigungsdialog zeigt erfasste Ursprünge. Wird eine Berechtigung widerrufen, wird die zugehörige Sandbox-Sitzung beendet; Host-API-Schlüssel werden nicht angezeigt.

Der Netzwerkzugriff ist standardmäßig eingeschränkt. Wenn die Sandbox Zugriff anfordert, der eine Genehmigung benötigt, prüfen Sie die Anfrage und erlauben Sie ihn nur, wenn Host und Ziel vertrauenswürdig sind. Geben Sie keine Geheimnisse oder nicht für die Sitzung bestimmten Projektdateien frei.

## Wichtige Einschränkungen

- Die Dateiauswahl bedeutet nicht, dass Dateiinhalte auf Geheimnisse geprüft wurden. Prüfen Sie selbst, was Sie freigeben; eine Geheimniserkennung ist nicht garantiert.
- Reparse Points, Links, unsichere Pfade und zu große Auswahlen werden abgelehnt.
- Die Sandbox ist unter Windows verfügbar und für OpenCode vorgesehen.
- Im Direktmodus wirken sich Aktionen sofort auf die ausgewählten Originaldateien aus. Prüfen Sie vor dem Fortfahren den Modushinweis.
