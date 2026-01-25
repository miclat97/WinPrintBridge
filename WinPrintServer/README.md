# WinPrint Server (.NET Framework 4.7.2)

Serwer wydruku zaprojektowany specjalnie dla starszych systemów (Windows 8/8.1, Windows 7) oraz nowszych, rozwiązujący problem zależności UCRT.

## Dlaczego ta wersja?

Poprzednia wersja (.NET Core/10/8) wymagała zainstalowania aktualizacji Universal C Runtime, która często sprawia problemy na starszych tabletach z Windows 8. Ta wersja korzysta z **.NET Framework 4.7.2**, który jest natywnie obsługiwany przez system lub łatwy do doinstalowania i nie wymaga zewnętrznych bibliotek C++.

## Wymagania

- Windows 7, 8, 8.1, 10 lub 11.
- .NET Framework 4.7.2 (zazwyczaj zainstalowany w systemie, jeśli nie - pobierz ze strony Microsoft).
- Drukarka zainstalowana w systemie.
- Przeglądarka PDF (np. Adobe Reader) ustawiona jako domyślna.

## Instrukcja Uruchomienia

1. Pobierz plik `WinPrintServer.exe` z folderu `bin/Debug/net472/`.
2. Pobierz folder `wwwroot` i umieść go w tym samym katalogu co plik `.exe`.
3. Uruchom `WinPrintServer.exe` (zalecane: Uruchom jako Administrator, aby nasłuchiwać na porcie sieciowym).
   - Jeśli pojawi się błąd "Access Denied" przy starcie serwera, uruchom jako Admin lub wykonaj w CMD:
     `netsh http add urlacl url=http://+:5000/ user=Wszyscy` (lub nazwa Twojego użytkownika).
4. Zezwól aplikacji na dostęp w Zaporze Windows (Sieć prywatna).
5. Otwórz przeglądarkę na telefonie i wejdź na IP tabletu, np. `http://192.168.1.15:5000`.

## Funkcje

- Lekki serwer HTTP oparty na `System.Net.HttpListener` (brak ciężkich zależności ASP.NET).
- Upload i druk plików JPG, PNG, PDF.
