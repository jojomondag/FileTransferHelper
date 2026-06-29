# FileTransferHelper

Ett grafiskt program för att skicka filer och mappar från Windows-datorn till en Raspberry Pi i samma lokala nätverk.

Programmet är omskrivet till C# och Avalonia. Det använder SFTP över SSH. På Raspberry Pi behöver SSH vara aktiverat:

```bash
sudo raspi-config
```

Välj `Interface Options` -> `SSH` -> `Enable`.

## Starta på Windows

Öppna PowerShell i den här mappen och kör:

```powershell
.\run.ps1
```

Skriptet bygger och startar Avalonia-appen via `dotnet run`.

## Användning

1. Starta appen. Sparade enheter visas direkt och uppdateras automatiskt i bakgrunden.
2. Välj en hittad enhet i listan.
3. Ange användarnamn och lösenord första gången.
4. Klicka på `Anslut`. Nästa gång fylls sparade uppgifter i och appen ansluter automatiskt när det går.
5. Lägg till filer eller mappar från din stationära dator.
6. Välj destination på Raspberry Pi.
7. Klicka på `Skicka`.

Om automatisk upptäckt inte hittar din Pi, kontrollera att Raspberry Pi är påslagen, att SSH är aktiverat och att datorn är på samma lokala nätverk.

Destinationen visar mappar och alla filer under den öppnade mappen. Mappar har `/` efter namnet och kan öppnas med dubbelklick. Filer i undermappar visas med relativ sökväg, till exempel `Säsong 1/avsnitt.mkv`. Du kan också skriva eller ändra sökvägen manuellt och trycka Enter eller `Öppna sökväg`.

Knappen `Ta bort dubbletter` söker rekursivt under den öppnade destinationen efter filer med namn som `fil (1).ext` eller `fil (2).ext`. En fil tas bara bort om originalfilen finns i samma mapp och har samma storlek. Appen visar en bekräftelse innan något raderas.

När en överföring körs igen läser programmet först fjärrmappen och bygger en plan. Filer som redan finns på Raspberry Pi med samma namn och storlek hoppas över. Om en fil med samma namn finns men har annan storlek, skrivs den inte över. Den nya kopian får i stället ett namn som `fil (1).ext`.

Hittade enheter sparas i `devices.json`. Nästa gång appen startar visas de direkt, och appen snabbkontrollerar sparade IP-adresser i bakgrunden innan den gör en längre nätverkssökning.

Efter en lyckad anslutning sparas användarnamn per enhet i `devices.json`. Lösenord sparas inte i klartext där, utan via Windows Credential Manager.

## Diagnostik

Varje gång appen söker eller uppdaterar enheter skrivs en logg till:

```text
discovery.log
```

Vid misslyckad filöverföring skrivs detaljer till:

```text
transfer.log
```

Den loggen visar destinationen, varje fil som skickas, vilken fil som stoppade och komplett felspårning.

Om VPN eller nätverk bryter anslutningen under en överföring försöker appen återansluta och fortsätta automatiskt.

## Projektstruktur

Avalonia-appen finns i:

```text
FileTransferHelper.Avalonia/
```

De viktigaste delarna är:

```text
Models/
Services/
ViewModels/
Views/
```
