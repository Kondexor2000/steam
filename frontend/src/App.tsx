import { useState, type FormEvent } from "react";
import {
  addGameUser,
  addTrackedGame,
  addTeamSpeakServer,
  analyzeCsv,
  authenticate,
  fetchSecurityBackup,
  getAnalysis,
  getLoginHistory,
  getRunningProcesses,
  getTeamSpeakServers,
  getTrackedGames,
  removeTeamSpeakServer,
  removeTrackedGame,
  setActiveGameUser,
  syncTeamSpeakServer,
  testTeamSpeakServer,
  type AnalysisResult,
  type LoginHistoryEntry,
  type RunningProcess,
  type TeamSpeakServer,
  type TrackedGame,
} from "./api";

export default function App() {
  const [mode, setMode] = useState<"login" | "register">("login");
  const [token, setToken] = useState<string | null>(null);
  const [isAdministrator, setIsAdministrator] = useState(false);
  const [login, setLogin] = useState("");
  const [password, setPassword] = useState("");
  const [file, setFile] = useState<File | null>(null);
  const [result, setResult] = useState<AnalysisResult | null>(null);
  const [processes, setProcesses] = useState<RunningProcess[]>([]);
  const [trackedGames, setTrackedGames] = useState<TrackedGame[]>([]);
  const [teamSpeakServers, setTeamSpeakServers] = useState<TeamSpeakServer[]>([]);
  const [loginHistory, setLoginHistory] = useState<LoginHistoryEntry[]>([]);
  const [gameName, setGameName] = useState("");
  const [processName, setProcessName] = useState("");
  const [teamSpeakName, setTeamSpeakName] = useState("");
  const [teamSpeakHost, setTeamSpeakHost] = useState("");
  const [teamSpeakPort, setTeamSpeakPort] = useState("10011");
  const [teamSpeakVirtualServerId, setTeamSpeakVirtualServerId] = useState("1");
  const [teamSpeakUsername, setTeamSpeakUsername] = useState("");
  const [teamSpeakPassword, setTeamSpeakPassword] = useState("");
  const [newUserNames, setNewUserNames] = useState<Record<number, string>>({});
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);

  async function submitAuth(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError("");
    setMessage("");
    setBusy(true);
    try {
      const response = await authenticate(mode, login, password);
      if ("token" in response) {
        setToken(response.token);
        setIsAdministrator(response.isAdministrator ?? false);
        setMessage(response.securityWarning ?? "");
        if (response.isAdministrator) {
          try {
            setTeamSpeakServers(await getTeamSpeakServers(response.token));
          } catch (cause) {
            setError(cause instanceof Error ? cause.message : "Nie udało się pobrać serwerów TeamSpeak.");
          }
        }
      } else {
        setMode("login");
        setMessage("Konto utworzone. Zaloguj się, aby kontynuować.");
      }
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Wystąpił nieoczekiwany błąd.");
    } finally {
      setBusy(false);
    }
  }

  async function submitFile(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!token || !file) return;
    setError("");
    setBusy(true);
    try {
      setResult(await analyzeCsv(token, file));
      setMessage("Dane CSV zostały zapisane w bazie lub były już wcześniej zaimportowane.");
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Wystąpił nieoczekiwany błąd.");
    } finally {
      setBusy(false);
    }
  }

  async function refreshLocalPlaytime() {
    if (!token) return;
    setError("");
    setBusy(true);
    try {
      const [availableProcesses, monitoredGames] = await Promise.all([
        getRunningProcesses(token),
        getTrackedGames(token),
      ]);
      setProcesses(availableProcesses);
      setTrackedGames(monitoredGames);
      if (!processName && availableProcesses.length > 0) {
        setProcessName(availableProcesses[0].processName);
      }
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Nie udało się odczytać lokalnych procesów.");
    } finally {
      setBusy(false);
    }
  }

  async function refreshLoginHistory() {
    if (!token) return;
    setError("");
    setBusy(true);
    try {
      setLoginHistory(await getLoginHistory(token));
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Nie udało się odczytać historii logowań.");
    } finally {
      setBusy(false);
    }
  }

  async function refreshTeamSpeakServers() {
    if (!token) return;
    setError("");
    setBusy(true);
    try {
      setTeamSpeakServers(await getTeamSpeakServers(token));
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Nie udało się pobrać serwerów TeamSpeak.");
    } finally {
      setBusy(false);
    }
  }

  async function submitTeamSpeakServer(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!token) return;
    setError("");
    setMessage("");
    setBusy(true);
    try {
      await addTeamSpeakServer(token, {
        name: teamSpeakName,
        host: teamSpeakHost,
        queryPort: Number(teamSpeakPort),
        virtualServerId: Number(teamSpeakVirtualServerId),
        queryUsername: teamSpeakUsername,
        queryPassword: teamSpeakPassword,
      });
      setTeamSpeakName("");
      setTeamSpeakHost("");
      setTeamSpeakUsername("");
      setTeamSpeakPassword("");
      setTeamSpeakServers(await getTeamSpeakServers(token));
      setMessage("Serwer dodany. Synchronizacja rozpocznie się automatycznie w ciągu 30 sekund.");
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Nie udało się dodać serwera TeamSpeak.");
    } finally {
      setBusy(false);
    }
  }

  async function checkTeamSpeakServer(id: number) {
    if (!token) return;
    setError("");
    setMessage("");
    setBusy(true);
    try {
      const activeClients = await testTeamSpeakServer(token, id);
      setMessage(`Połączenie działa. Na serwerze jest ${activeClients} aktywnych użytkowników.`);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Nie udało się połączyć z serwerem TeamSpeak.");
    } finally {
      setBusy(false);
    }
  }

  async function syncTeamSpeakServerNow(id: number) {
    if (!token) return;
    setError("");
    setMessage("");
    setBusy(true);
    try {
      const result = await syncTeamSpeakServer(token, id);
      setTeamSpeakServers(await getTeamSpeakServers(token));
      setResult(await getAnalysis(token));
      setMessage(
        `Synchronizacja zakończona: ${result.activeClients} aktywnych użytkowników, ` +
        `${result.startedSessions} nowych sesji i ${result.endedSessions} zakończonych.`,
      );
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Nie udało się zsynchronizować serwera.");
    } finally {
      setBusy(false);
    }
  }

  async function deleteTeamSpeakServer(id: number) {
    if (!token || !window.confirm("Usunąć serwer i zebrane z niego sesje?")) return;
    setError("");
    setBusy(true);
    try {
      await removeTeamSpeakServer(token, id);
      setTeamSpeakServers(await getTeamSpeakServers(token));
      setResult(await getAnalysis(token));
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Nie udało się usunąć serwera TeamSpeak.");
    } finally {
      setBusy(false);
    }
  }

  async function downloadBackup() {
    if (!token) return;
    setError("");
    setBusy(true);
    try {
      const backup = await fetchSecurityBackup(token);
      const url = URL.createObjectURL(backup);
      const link = document.createElement("a");
      link.href = url;
      link.download = "server-activity-backup.zip";
      document.body.append(link);
      link.click();
      link.remove();
      URL.revokeObjectURL(url);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Nie udało się pobrać backupu.");
    } finally {
      setBusy(false);
    }
  }

  async function startTracking(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!token || !gameName.trim() || !processName) return;
    setError("");
    setBusy(true);
    try {
      await addTrackedGame(token, gameName, processName);
      setGameName("");
      const monitoredGames = await getTrackedGames(token);
      setTrackedGames(monitoredGames);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Nie udało się rozpocząć monitorowania gry.");
    } finally {
      setBusy(false);
    }
  }

  async function stopTracking(id: number) {
    if (!token) return;
    setError("");
    setBusy(true);
    try {
      await removeTrackedGame(token, id);
      setTrackedGames(await getTrackedGames(token));
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Nie udało się zatrzymać monitorowania gry.");
    } finally {
      setBusy(false);
    }
  }

  async function resumeTracking(game: TrackedGame) {
    if (!token) return;
    setError("");
    setBusy(true);
    try {
      await addTrackedGame(token, game.gameName, game.processName);
      setTrackedGames(await getTrackedGames(token));
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Nie udało się wznowić monitorowania gry.");
    } finally {
      setBusy(false);
    }
  }

  async function submitGameUser(event: FormEvent<HTMLFormElement>, gameId: number) {
    event.preventDefault();
    if (!token) return;
    const userName = newUserNames[gameId]?.trim();
    if (!userName) return;
    setError("");
    setBusy(true);
    try {
      await addGameUser(token, gameId, userName);
      setNewUserNames(current => ({ ...current, [gameId]: "" }));
      setTrackedGames(await getTrackedGames(token));
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Nie udało się dodać użytkownika do gry.");
    } finally {
      setBusy(false);
    }
  }

  async function changeActiveGameUser(gameId: number, userId: number) {
    if (!token) return;
    setError("");
    setBusy(true);
    try {
      await setActiveGameUser(token, gameId, userId);
      setTrackedGames(await getTrackedGames(token));
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Nie udało się zmienić aktywnego użytkownika.");
    } finally {
      setBusy(false);
    }
  }

  function signOut() {
    setToken(null);
    setIsAdministrator(false);
    setResult(null);
    setFile(null);
    setLoginHistory([]);
    setTeamSpeakServers([]);
    setMessage("");
    setError("");
  }

  return (
    <main className="page">
      <header className="topbar">
        <a className="brand" href="/">PLAYER<span>DATA</span></a>
        {token && <button className="text-button" onClick={signOut}>Wyloguj</button>}
      </header>

      {!token ? (
        <section className="auth-card">
          <p className="eyebrow">ANALITYKA SERWERÓW</p>
          <h1>{mode === "login" ? "Witaj ponownie" : "Utwórz konto"}</h1>
          <p className="muted">Zaloguj się, aby analizować aktywność graczy.</p>
          <form onSubmit={submitAuth}>
            <label htmlFor="login">Login</label>
            <input id="login" autoComplete="username" value={login} onChange={event => setLogin(event.target.value)} required />
            <label htmlFor="password">Hasło</label>
            <input
              id="password"
              type="password"
              autoComplete={mode === "login" ? "current-password" : "new-password"}
              minLength={8}
              value={password}
              onChange={event => setPassword(event.target.value)}
              required
            />
            <button className="primary-button" disabled={busy}>
              {busy ? "Proszę czekać…" : mode === "login" ? "Zaloguj się" : "Zarejestruj"}
            </button>
          </form>
          <button
            className="text-button switch-mode"
            onClick={() => { setMode(mode === "login" ? "register" : "login"); setError(""); setMessage(""); }}
          >
            {mode === "login" ? "Nie masz konta? Zarejestruj się" : "Masz już konto? Zaloguj się"}
          </button>
          {message && <p role="status" className="success-message">{message}</p>}
          {error && <p role="alert" className="error-message">{error}</p>}
        </section>
      ) : (
        <section className="dashboard">
          <p className="eyebrow">PANEL ANALITYCZNY</p>
          <h1>Aktywność serwerów</h1>
          {message && <p role="status" className="success-message">{message}</p>}
          <section className="security-card" aria-labelledby="security-heading">
            <div className="security-heading">
              <div>
                <h2 id="security-heading">Bezpieczeństwo konta</h2>
                <p className="muted">Sprawdź adresy IP użyte podczas rejestracji i logowania.</p>
              </div>
              <div className="security-actions">
                <button className="text-button" onClick={refreshLoginHistory} disabled={busy}>
                  Historia logowań
                </button>
                {isAdministrator && (
                  <button className="text-button" onClick={downloadBackup} disabled={busy}>
                    Pobierz odszyfrowany backup
                  </button>
                )}
              </div>
            </div>
            {loginHistory.length > 0 && (
              <ul className="login-history">
                {loginHistory.map((entry, index) => (
                  <li key={`${entry.createdAt}-${index}`}>
                    <span>
                      <time dateTime={entry.createdAt}>
                        {new Date(entry.createdAt).toLocaleString()}
                      </time>
                      {" — "}
                      {entry.ipAddress ?? "Adres IP niedostępny"}
                      {entry.isNewIp ? " (nowy adres IP)" : ""}
                    </span>
                    {entry.backupStatus === "created" && <span>Backup utworzony</span>}
                    {entry.backupStatus === "failed" && <span className="error-message">Backup nieudany</span>}
                    {entry.backupStatus === "pending" && <span>Backup w toku</span>}
                  </li>
                ))}
              </ul>
            )}
          </section>
          <section className="security-card" aria-labelledby="teamspeak-heading">
            <div className="security-heading">
              <div>
                <h2 id="teamspeak-heading">Serwery TeamSpeak</h2>
                <p className="muted">
                  {isAdministrator
                    ? "Dodaj serwer raz. Aplikacja będzie cyklicznie pobierać aktywne połączenia."
                    : "Połączenia i zebrane statystyki serwerów TeamSpeak."}
                </p>
              </div>
              <button className="text-button" onClick={refreshTeamSpeakServers} disabled={busy}>
                Odśwież listę
              </button>
            </div>
            {isAdministrator && (
              <form className="teamspeak-form" onSubmit={submitTeamSpeakServer}>
                <label htmlFor="teamspeak-name">Nazwa serwera</label>
                <input id="teamspeak-name" value={teamSpeakName} onChange={event => setTeamSpeakName(event.target.value)} maxLength={100} required />
                <label htmlFor="teamspeak-host">Adres serwera TeamSpeak</label>
                <input id="teamspeak-host" value={teamSpeakHost} onChange={event => setTeamSpeakHost(event.target.value)} maxLength={253} placeholder="np. ts.example.com" required />
                <div className="teamspeak-fields">
                  <div>
                    <label htmlFor="teamspeak-port">Port ServerQuery</label>
                    <input id="teamspeak-port" type="number" min="1" max="65535" value={teamSpeakPort} onChange={event => setTeamSpeakPort(event.target.value)} required />
                  </div>
                  <div>
                    <label htmlFor="teamspeak-virtual-server">ID serwera wirtualnego</label>
                    <input id="teamspeak-virtual-server" type="number" min="1" value={teamSpeakVirtualServerId} onChange={event => setTeamSpeakVirtualServerId(event.target.value)} required />
                  </div>
                </div>
                <label htmlFor="teamspeak-username">Login ServerQuery</label>
                <input id="teamspeak-username" value={teamSpeakUsername} onChange={event => setTeamSpeakUsername(event.target.value)} maxLength={128} required />
                <label htmlFor="teamspeak-password">Hasło ServerQuery</label>
                <input id="teamspeak-password" type="password" autoComplete="new-password" value={teamSpeakPassword} onChange={event => setTeamSpeakPassword(event.target.value)} maxLength={256} required />
                <p className="file-hint">
                  Hasło jest przechowywane w postaci zaszyfrowanej. Nie jest potrzebny eksport bazy TeamSpeak.
                </p>
                <button className="primary-button" disabled={busy}>Dodaj serwer</button>
              </form>
            )}
            {teamSpeakServers.length > 0 ? (
              <div className="teamspeak-servers">
                {teamSpeakServers.map(server => (
                  <article className="teamspeak-server-card" key={server.id}>
                    <div>
                      <h3>{server.name}</h3>
                      <p className="muted">{server.host}:{server.queryPort} · ID serwera {server.virtualServerId}</p>
                      <p className="file-hint">
                        Ostatnia synchronizacja: {server.lastSyncAt ? new Date(server.lastSyncAt).toLocaleString() : "jeszcze nie wykonano"}
                        {" · "}{server.activeClients} aktywnych użytkowników
                      </p>
                      {server.lastError && <p className="error-message">{server.lastError}</p>}
                    </div>
                    {isAdministrator && (
                      <div className="security-actions">
                        <button className="text-button" onClick={() => checkTeamSpeakServer(server.id)} disabled={busy}>Testuj połączenie</button>
                        <button className="text-button" onClick={() => syncTeamSpeakServerNow(server.id)} disabled={busy}>Synchronizuj teraz</button>
                        <button className="text-button danger-button" onClick={() => deleteTeamSpeakServer(server.id)} disabled={busy}>Usuń serwer</button>
                      </div>
                    )}
                  </article>
                ))}
              </div>
            ) : (
              <p className="file-hint">Nie dodano jeszcze żadnego serwera TeamSpeak.</p>
            )}
          </section>
          <h2>Lokalny czas gry</h2>
          <p className="muted">
            Monitoruj proces gry na tym komputerze. Pomiar zacznie się od uruchomienia monitorowania.
          </p>
          <button className="text-button" onClick={refreshLocalPlaytime} disabled={busy}>
            {busy ? "Odczytuję…" : "Odśwież procesy i czas"}
          </button>
          <form className="upload-card" onSubmit={startTracking}>
            <label htmlFor="game-name">Nazwa gry</label>
            <input
              id="game-name"
              value={gameName}
              onChange={event => setGameName(event.target.value)}
              maxLength={100}
              required
            />
            <label htmlFor="game-process">Działający proces gry</label>
            <select
              id="game-process"
              value={processName}
              onChange={event => setProcessName(event.target.value)}
              required
            >
              <option value="">Odśwież listę procesów i wybierz grę</option>
              {processes.map(process => (
                <option key={process.processName} value={process.processName}>
                  {process.processName}
                </option>
              ))}
            </select>
            <button className="primary-button" disabled={busy || !gameName.trim() || !processName}>
              Monitoruj grę
            </button>
          </form>
          {trackedGames.length > 0 && (
            <div className="tracked-games">
              {trackedGames.map(game => (
                <article className="tracked-game-card" key={game.id}>
                  <div className="tracked-game-heading">
                    <div>
                      <h3>{game.gameName}</h3>
                      <p className="muted">{game.processName}</p>
                    </div>
                    <div className="tracked-game-status">
                      <span>{(game.totalSeconds / 3600).toFixed(2)} godz. łącznie</span>
                      <span>{!game.isTracking ? "Zatrzymana" : game.isRunning ? "Uruchomiona" : "Oczekiwanie"}</span>
                      {game.isTracking ? (
                        <button className="text-button" onClick={() => stopTracking(game.id)} disabled={busy}>
                          Zatrzymaj
                        </button>
                      ) : (
                        <button className="text-button" onClick={() => resumeTracking(game)} disabled={busy}>
                          Wznów
                        </button>
                      )}
                    </div>
                  </div>
                  <h4>Użytkownicy tej gry</h4>
                  {game.users.length > 0 ? (
                    <ul className="game-user-list">
                      {game.users.map(gameUser => (
                        <li key={gameUser.id}>
                          <span>{gameUser.userName}{gameUser.isActive ? " (aktywny)" : ""}</span>
                          <span>{(gameUser.totalSeconds / 3600).toFixed(2)} godz.</span>
                        </li>
                      ))}
                    </ul>
                  ) : (
                    <p className="muted">Dodaj użytkowników i wybierz, komu naliczać czas gry.</p>
                  )}
                  <form
                    className="game-user-form"
                    onSubmit={event => submitGameUser(event, game.id)}
                  >
                    <label htmlFor={`game-user-${game.id}`}>Dodaj użytkownika</label>
                    <input
                      id={`game-user-${game.id}`}
                      value={newUserNames[game.id] ?? ""}
                      onChange={event => setNewUserNames(current => ({
                        ...current,
                        [game.id]: event.target.value,
                      }))}
                      maxLength={100}
                      required
                    />
                    <button className="primary-button" disabled={busy || !newUserNames[game.id]?.trim()}>
                      Dodaj
                    </button>
                  </form>
                  <label htmlFor={`active-game-user-${game.id}`}>Aktywny użytkownik</label>
                  <select
                    id={`active-game-user-${game.id}`}
                    value={game.users.find(gameUser => gameUser.isActive)?.id ?? ""}
                    onChange={event => {
                      if (event.target.value) {
                        void changeActiveGameUser(game.id, Number(event.target.value));
                      }
                    }}
                    disabled={busy || game.users.length === 0}
                  >
                    <option value="">Wybierz użytkownika do naliczania czasu</option>
                    {game.users.map(gameUser => (
                      <option key={gameUser.id} value={gameUser.id}>{gameUser.userName}</option>
                    ))}
                  </select>
                  <p className="file-hint">
                    Czas uruchomionej gry jest naliczany aktywnemu użytkownikowi.
                  </p>
                </article>
              ))}
            </div>
          )}
          <div className="report-heading">
            <div>
              <h2>Raport aktywności</h2>
              <p className="muted">Raport łączy sesje pobrane z TeamSpeak z zaimportowanymi danymi historycznymi.</p>
            </div>
            <button className="text-button" onClick={async () => {
              if (!token) return;
              setError("");
              setBusy(true);
              try {
                setResult(await getAnalysis(token));
              } catch (cause) {
                setError(cause instanceof Error ? cause.message : "Nie udało się pobrać raportu.");
              } finally {
                setBusy(false);
              }
            }} disabled={busy}>Odśwież raport</button>
          </div>
          <form className="upload-card" onSubmit={submitFile}>
            <label htmlFor="csv-file">Plik CSV</label>
            <p className="file-hint">Import historycznych danych (np. kopii zapasowej lub serwera bez wcześniejszej integracji). Import nie zastępuje automatycznej synchronizacji.</p>
            <p className="file-hint">Kolumny: service, server_name, game, player, connected_at, disconnected_at</p>
            <input id="csv-file" type="file" accept=".csv,text/csv" onChange={event => setFile(event.target.files?.[0] ?? null)} required />
            <button className="primary-button" disabled={busy || !file}>
              {busy ? "Importuję…" : "Importuj historię CSV"}
            </button>
          </form>
          {error && <p role="alert" className="error-message">{error}</p>}
          {result && (
            <>
              <h2>Podsumowanie serwerów</h2>
              <div className="table-wrap">
                <table>
                  <thead><tr><th>Usługa</th><th>Serwer</th><th>Gra</th><th>Sesje</th><th>Gracze</th><th>Godziny</th><th>Śr. min</th><th>Mediana min</th></tr></thead>
                  <tbody>{result.servers.map((row, index) => (
                    <tr key={`${row.service}-${row.serverName}-${row.game}-${index}`}>
                      <td>{row.service}</td><td>{row.serverName}</td><td>{row.game || "—"}</td>
                      <td>{row.sessions}</td><td>{row.uniquePlayers}</td><td>{row.totalHours.toFixed(2)}</td>
                      <td>{row.meanSessionMinutes.toFixed(1)}</td><td>{row.medianSessionMinutes.toFixed(1)}</td>
                    </tr>
                  ))}</tbody>
                </table>
              </div>
              <h2>Podsumowanie graczy</h2>
              <div className="table-wrap">
                <table>
                  <thead><tr><th>Gracz</th><th>Usługa</th><th>Serwer</th><th>Gra</th><th>Sesje</th><th>Godziny</th><th>Śr. min</th><th>Mediana min</th></tr></thead>
                  <tbody>{result.players.map((row, index) => (
                    <tr key={`${row.player}-${row.service}-${row.serverName}-${row.game}-${index}`}>
                      <td>{row.player}</td><td>{row.service}</td><td>{row.serverName}</td><td>{row.game || "—"}</td>
                      <td>{row.sessions}</td><td>{row.totalHours.toFixed(2)}</td>
                      <td>{row.meanSessionMinutes.toFixed(1)}</td><td>{row.medianSessionMinutes.toFixed(1)}</td>
                    </tr>
                  ))}</tbody>
                </table>
              </div>
            </>
          )}
        </section>
      )}
    </main>
  );
}
