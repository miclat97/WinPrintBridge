# WinPrintBridge

Prosty serwer wydruku dla Windows (tablet), umożliwiający drukowanie z urządzeń mobilnych (Android/iOS) poprzez przeglądarkę.

## Wymagania

- System operacyjny: Windows 10/11 (Zalecane).
  - **Uwaga dotycząca Windows 8:** Aplikacja została napisana w .NET 10 zgodnie z życzeniem. .NET 6+ oficjalnie nie wspiera Windows 8. Może być wymagana aktualizacja systemu lub próba uruchomienia na własne ryzyko. Jeśli aplikacja nie uruchomi się na Windows 8, należy skompilować ją pod starszy framework (np. .NET 6 lub Framework 4.8), jednak kod źródłowy jest przygotowany pod najnowsze standardy.
- Drukarka zainstalowana w systemie (domyślna lub możliwość wyboru).
- Przeglądarka PDF (np. Adobe Reader) ustawiona jako domyślna (dla drukowania PDF).

## Instrukcja Publikacji (Single File)

Aby wygenerować pojedynczy plik `.exe` do przeniesienia na tablet:

1. Otwórz terminal w folderze projektu.
2. Uruchom komendę:

```bash
dotnet publish -c Release -r win-x86 --self-contained -p:PublishSingleFile=true -p:PublishReadyToRun=true
```

3. Plik wynikowy `WinPrintBridge.exe` znajdziesz w katalogu `bin/Release/net10.0/win-x86/publish/`.
4. Skopiuj ten plik (oraz ewentualnie folder `wwwroot` jeśli nie został wbudowany, choć w trybie default Web SDK pliki statyczne są w embed) na tablet.
   *Uwaga:* W trybie Single File pliki statyczne webowe (html) są zazwyczaj obsługiwane, ale upewnij się, że `wwwroot` jest obok pliku exe lub skonfigurowany jako zasób wbudowany.

   *Dla pewności w tym projekcie:* Skopiuj zarówno `WinPrintBridge.exe` jak i folder `wwwroot` na tablet.

## Uruchomienie

1. Podłącz drukarkę USB do tabletu.
2. Uruchom `WinPrintBridge.exe`.
3. Zezwól na dostęp w zaporze Windows (sieć prywatna).
4. Na telefonie wpisz adres IP tabletu i port (domyślnie 5000), np. `http://192.168.1.15:5000`.

## Funkcje

- Upload plików JPG, PNG, BMP, PDF.
- Podgląd przed wydrukiem.
- Drukowanie (JPG przez GDI+, PDF przez domyślny systemowy handler).
