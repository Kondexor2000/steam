# TeamSpeak i serwery gier — analiza aktywności graczy

Aplikacja zapisuje sesje w lokalnej bazie SQLite i może cyklicznie pobierać listę aktywnych użytkowników z serwerów TeamSpeak przez ServerQuery. Administrator dodaje serwer i dane konta Query w panelu; od tej pory synchronizacja odbywa się automatycznie, a raport łączy zebrane sesje z zaimportowaną historią CSV. Import CSV pozostaje dodatkiem do przenoszenia starych danych.

ServerQuery udostępnia bieżący stan serwera, a nie historyczną bazę sesji. Aplikacja rejestruje czas połączeń od rozpoczęcia synchronizacji; sprawdza stan co 30 sekund, więc czas rozłączenia jest przybliżony do interwału. Dane sprzed podłączenia serwera można uzupełnić importem CSV.

## Dane demonstracyjne

`data/sample_sessions.csv` zawiera wyłącznie fikcyjne dane do demonstracji. Nie opisuje rzeczywistych graczy ani serwerów.

## Uruchomienie

Sklonuj repozytorium i przejdź do katalogu projektu:

```powershell
git clone https://github.com/Kondexor2000/steam.git
cd steam
```

Wymagany Python 3.10+.

```powershell
py -m venv .venv
.venv\Scripts\Activate.ps1
python -m pip install -e .
server-activity
```

Można wskazać własny plik i ścieżki raportów:

```powershell
server-activity data/my_sessions.csv --output reports/servers.csv --player-output reports/players.csv
```

Albo bez instalowania pakietu:

```powershell
python -m pip install -r requirements.txt
$env:PYTHONPATH = "src"
python -m server_activity.cli data/sample_sessions.csv
```

## Format CSV

Plik musi zawierać nagłówki:

`service,server_name,game,player,connected_at,disconnected_at`

- `service`: `teamspeak` albo `game` (wielkość liter nie ma znaczenia);
- `server_name`: nazwa serwera, np. TeamSpeak lub nazwa instancji gry;
- `game`: nazwa gry dla `service=game`, puste pole dla TeamSpeak;
- `player`: nazwa gracza/użytkownika;
- `connected_at`, `disconnected_at`: daty i czasy parsowalne przez pandas, najlepiej ISO 8601 z przesunięciem strefy, np. `2026-09-01T18:00:00+02:00`.

Każdy wiersz opisuje jedną zakończoną sesję. Jeżeli logi zawierają tylko zdarzenia dołączenia/rozłączenia, trzeba najpierw sparować je w sesje. Nakładające się sesje są liczone niezależnie; aplikacja nie deduplikuje ani nie scala połączeń.

## Raporty i zakres MVP

Polecenie tworzy dwa pliki CSV:

- `reports/server-summary.csv` — liczba sesji i unikalnych graczy, łączny czas w godzinach, średni i medianowy czas sesji dla każdej usługi/serwera/gry;
- `reports/player-summary.csv` — te same metryki czasu i sesji w podziale na gracza, usługę, serwer i grę.

Walidacja sprawdza wymagane kolumny, niepuste nazwy serwera/gracza, obsługiwany typ usługi, nazwę gry dla sesji gier oraz poprawność i kolejność znaczników czasu.

## Docker i Jenkins

Analizator Python można nadal zbudować i uruchomić niezależnie:

```sh
docker build -t server-activity-analysis .
docker run --rm -v "$PWD/reports:/app/reports" server-activity-analysis
```

API C# oraz aplikację React można zbudować i uruchomić razem:

```sh
docker compose up --build
```

Otwórz `http://localhost:8082`; endpoint API `http://localhost:8082/health` jest dostępny przez reverse proxy front-endu. Port hosta można zmienić przez zmienną `FRONTEND_PORT`, np. `$env:FRONTEND_PORT = "8083"` w PowerShell albo `FRONTEND_PORT=8083 docker compose up --build` w powłoce POSIX. SQLite i backupy API są przechowywane w wolumenie `api-data`. Aby włączyć szyfrowane backupy, ustaw przed uruchomieniem Compose `SECURITY_BACKUP_KEY` (trwały klucz Base64 o długości 32 bajtów) oraz opcjonalnie `SECURITY_ADMIN_LOGINS`. Nie zapisuj klucza w repozytorium. Zatrzymanie usług: `docker compose down`; dane pozostają w wolumenie.

Kontener API nie ma dostępu do procesów gier na hoście. Funkcja lokalnego pomiaru czasu gry wymaga uruchomienia API bezpośrednio na monitorowanym komputerze, zgodnie z instrukcją niżej.

Pipeline `Jenkinsfile` weryfikuje analizator Python, uruchamia testy .NET i front-endu oraz buduje obrazy Docker dla wszystkich komponentów. Agent Jenkins musi mieć Python 3, .NET 8 SDK, Node.js 20+, npm, powłokę `sh` i Docker z Compose v2. Raporty Python z `reports/` są archiwizowane jako artefakty.

## WebAPI C# i React

`webapi/` udostępnia API do zarządzania serwerami TeamSpeak, zbierania sesji, importu CSV oraz pomiaru czasu działania wybranych gier na lokalnym komputerze. `frontend/` zawiera aplikację React z panelem administratora i raportem zapisanych sesji.

Wymagane: .NET 8 SDK, Node.js 20+ i npm.

Uruchom API:

```powershell
dotnet run --project webapi
```

Uruchom frontend w drugim terminalu:

```powershell
cd frontend
npm install
npm run dev
```

Vite nasłuchuje na wszystkich interfejsach sieciowych. Na tym komputerze otwórz `http://localhost:5173`, a na innym urządzeniu w tej samej sieci użyj adresu LAN komputera, np. `http://192.168.1.22:5173` (Vite wypisuje wykryte adresy sieciowe po uruchomieniu). Jeśli połączenie nie działa, zezwól na ruch przychodzący na porcie `5173` w zaporze systemu. Domyślne proxy przekazuje żądania do API na `http://localhost:5000` z komputera hostującego Vite; można je zmienić przez `VITE_API_PROXY`. Konto jest zapisywane w lokalnej bazie SQLite `webapi/data/users.db`, a hasło przechowywane jako hash ASP.NET Identity. Token dostępu jest ważny przez godzinę i znika po restarcie API.

Backupy bezpieczeństwa wymagają trwałego klucza Base64 o długości 32 bajtów. Wygeneruj go raz w PowerShell i zachowaj poza repozytorium:

```powershell
[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:Security__BackupKey = "<wygenerowany-klucz-base64>"
$env:Security__AdminLogins = "admin1,admin2"
```

Loginy administratorów z `Security__AdminLogins` mają dostęp do zarządzania serwerami TeamSpeak. Konto ServerQuery podaje się w panelu; jego hasło jest szyfrowane przez ASP.NET Data Protection i nie wraca w odpowiedziach API. Nie usuwaj katalogu `webapi/data/keys` (w Dockerze: wolumenu `api-data`), bo zawiera klucze potrzebne do odszyfrowania zapisanych haseł. Klucze Data Protection są dołączane do zaszyfrowanego backupu.

### Podłączenie TeamSpeak

1. Uruchom API i zaloguj się kontem wymienionym w `Security__AdminLogins`.
2. W panelu „Serwery TeamSpeak” wpisz nazwę, host, port ServerQuery (domyślnie `10011`), ID serwera wirtualnego (domyślnie `1`) i dane konta ServerQuery.
3. Otwórz port ServerQuery w zaporze serwera tak, aby był osiągalny z komputera lub kontenera uruchamiającego API. Nie wystawiaj go publicznie: surowe połączenie TCP nie szyfruje danych logowania. Użyj zaufanej sieci, VPN albo tunelu SSH i w panelu podaj adres/port dostępny po stronie API. Kliknij „Testuj połączenie”, a następnie „Synchronizuj teraz”; dalsze synchronizacje odbywają się automatycznie co 30 sekund.
4. Raport aktywności prezentuje zarówno dane zebrane przez aplikację, jak i zaimportowane rekordy CSV.

Serwer TeamSpeak nie musi udostępniać swojej wewnętrznej bazy danych aplikacji. Potrzebny jest włączony ServerQuery i konto z uprawnieniami do odczytu listy klientów na wybranym serwerze wirtualnym. Jeśli serwer nie jest już aktywny lub potrzebna jest historia sprzed podłączenia aplikacji, użyj importu CSV w panelu.

Ustaw te zmienne ponownie przed każdym uruchomieniem API. Nie generuj nowego klucza przy każdym uruchomieniu — jego utrata uniemożliwia odszyfrowanie istniejących backupów. Po udanym logowaniu z nowego adresu IP API tworzy zaszyfrowany backup obu baz: kont (w tym hashy haseł i historii logowań) oraz czasu gry. Jeśli tworzenie kopii się nie powiedzie, frontend pokaże ostrzeżenie, a API ponowi próbę przy kolejnym logowaniu. Odszyfrowany plik ZIP mogą pobierać wyłącznie użytkownicy wymienieni w `Security__AdminLogins`.

API:

- `POST /api/auth/register` — JSON `{ "login": "...", "password": "..." }`; hasło musi mieć co najmniej 8 znaków;
- `POST /api/auth/login` — zwraca token Bearer, informację o roli administratora i ewentualne ostrzeżenie o backupie;
- `GET /api/security/login-history` — wymaga tokenu Bearer; zwraca do 100 ostatnich wpisów historii logowań bieżącego konta;
- `GET /api/security/backup` — wymaga tokenu Bearer konta wymienionego w `Security__AdminLogins`; pobiera odszyfrowany backup ZIP;
- `POST /api/analysis/csv` — wymaga tokenu Bearer; przyjmuje plik CSV w polu formularza `file` (maksymalnie 5 MB);
- `GET /api/analysis` — wymaga tokenu Bearer; zwraca raport ze wszystkich zapisanych sesji;
- `GET /api/teamspeak/servers` — wymaga tokenu Bearer; zwraca skonfigurowane serwery bez haseł;
- `POST /api/teamspeak/servers` — wymaga roli administratora; dodaje serwer i szyfruje dane dostępu;
- `POST /api/teamspeak/servers/{id}/test` — wymaga administratora; testuje połączenie ServerQuery;
- `POST /api/teamspeak/servers/{id}/sync` — wymaga administratora; pobiera bieżących użytkowników i aktualizuje zapis sesji;
- `DELETE /api/teamspeak/servers/{id}` — wymaga administratora; usuwa serwer i przypisane do niego sesje;
- `GET /api/teamspeak/analysis` — wymaga tokenu Bearer; zwraca raport zapisanych sesji;
- `GET /api/local-playtime/processes` — wymaga tokenu Bearer; zwraca nazwy procesów widocznych w bieżącej sesji systemu;
- `GET /api/local-playtime/tracked-games` — wymaga tokenu Bearer; zwraca monitorowane gry, zebrany czas oraz przypisanych użytkowników i ich czas;
- `POST /api/local-playtime/tracked-games` — wymaga tokenu Bearer; JSON `{ "gameName": "Minecraft", "processName": "Minecraft.Windows" }` rozpoczyna lub wznawia monitoring procesu;
- `POST /api/local-playtime/tracked-games/{id}/users` — wymaga tokenu Bearer; JSON `{ "userName": "Ala" }` dodaje użytkownika do monitorowanej gry;
- `PUT /api/local-playtime/tracked-games/{id}/users/{userId}/active` — wymaga tokenu Bearer; przypisuje czas gry wskazanemu użytkownikowi;
- `DELETE /api/local-playtime/tracked-games/{id}` — zatrzymuje monitoring; zachowuje dotychczas zebrany czas;
- `GET /health` — kontrola dostępności.

Lokalny pomiar działa, gdy API .NET jest uruchomione bezpośrednio na tym komputerze, w tej samej sesji Windows co gry. Po zalogowaniu w aplikacji odśwież listę procesów, wybierz proces gry, podaj jej nazwę i rozpocznij monitoring. Do każdej monitorowanej gry można dodać wielu użytkowników i wybrać aktywnego; czas działania procesu jest przypisywany tylko temu użytkownikowi. API sprawdza procesy co 5 sekund; naliczany jest wyłącznie czas zarejestrowany od włączenia monitorowania (nie da się odzyskać wcześniejszej historii). Zakończenie sesji jest przybliżone do interwału próbkowania, a czas pozostaje zapisany lokalnie w `webapi/data/playtime.db`. Uruchomienie API w Dockerze nie daje mu dostępu do procesów hosta — uruchamiaj je przez `dotnet run --project webapi` na komputerze, który ma być monitorowany.

Testy jednostkowe i integracyjne:

```powershell
dotnet test webapi.tests
cd frontend
npm test
npm run build
```

## Dalsze kroki

1. Dodać import innych formatów archiwalnych oraz źródeł logów po ustaleniu ich formatów i uprawnień.
2. Rozszerzyć raporty o trendy dzienne, aktywność równoległą i wizualizacje.
