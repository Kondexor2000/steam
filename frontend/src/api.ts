export interface ServerSummary {
  service: string;
  serverName: string;
  game: string;
  sessions: number;
  uniquePlayers: number;
  totalHours: number;
  meanSessionMinutes: number;
  medianSessionMinutes: number;
}

export interface PlayerSummary {
  service: string;
  serverName: string;
  game: string;
  player: string;
  sessions: number;
  totalHours: number;
  meanSessionMinutes: number;
  medianSessionMinutes: number;
}

export interface AnalysisResult {
  servers: ServerSummary[];
  players: PlayerSummary[];
}

export interface RunningProcess {
  processName: string;
}

export interface TrackedGame {
  id: number;
  gameName: string;
  processName: string;
  totalSeconds: number;
  isRunning: boolean;
  isTracking: boolean;
  users: TrackedGameUser[];
}

export interface TrackedGameUser {
  id: number;
  userName: string;
  totalSeconds: number;
  isActive: boolean;
}

export interface TeamSpeakServer {
  id: number;
  name: string;
  host: string;
  queryPort: number;
  virtualServerId: number;
  queryUsername: string;
  lastSyncAt: string | null;
  lastError: string | null;
  activeClients: number;
}

export interface TeamSpeakSyncResult {
  activeClients: number;
  startedSessions: number;
  endedSessions: number;
}

interface LoginResult {
  token: string;
  expiresInSeconds: number;
  isAdministrator?: boolean;
  securityWarning?: string;
}

const apiBase = import.meta.env.VITE_API_URL ?? "";

async function readResponse<T>(response: Response): Promise<T> {
  const body = (await response.json()) as T & { error?: string };
  if (!response.ok) {
    throw new Error(body.error ?? "Nie udało się wykonać żądania.");
  }
  return body;
}

export async function authenticate(
  mode: "login" | "register",
  login: string,
  password: string,
): Promise<LoginResult | { login: string }> {
  const response = await fetch(`${apiBase}/api/auth/${mode}`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ login, password }),
  });
  return readResponse(response);
}

export async function analyzeCsv(token: string, file: File): Promise<AnalysisResult> {
  const form = new FormData();
  form.append("file", file);
  const response = await fetch(`${apiBase}/api/analysis/csv`, {
    method: "POST",
    headers: { Authorization: "Bearer " + token },
    body: form,
  });
  return readResponse<AnalysisResult>(response);
}

export async function getAnalysis(token: string): Promise<AnalysisResult> {
  const response = await fetch(`${apiBase}/api/analysis`, {
    headers: { Authorization: "Bearer " + token },
  });
  return readResponse<AnalysisResult>(response);
}

export async function getTeamSpeakServers(token: string): Promise<TeamSpeakServer[]> {
  const response = await fetch(`${apiBase}/api/teamspeak/servers`, {
    headers: { Authorization: "Bearer " + token },
  });
  return readResponse<TeamSpeakServer[]>(response);
}

export async function addTeamSpeakServer(
  token: string,
  server: {
    name: string;
    host: string;
    queryPort: number;
    virtualServerId: number;
    queryUsername: string;
    queryPassword: string;
  },
): Promise<void> {
  const response = await fetch(`${apiBase}/api/teamspeak/servers`, {
    method: "POST",
    headers: { Authorization: "Bearer " + token, "Content-Type": "application/json" },
    body: JSON.stringify(server),
  });
  await readResponse<{ id: number }>(response);
}

export async function testTeamSpeakServer(token: string, id: number): Promise<number> {
  const response = await fetch(`${apiBase}/api/teamspeak/servers/${id}/test`, {
    method: "POST",
    headers: { Authorization: "Bearer " + token },
  });
  const result = await readResponse<{ activeClients: number }>(response);
  return result.activeClients;
}

export async function syncTeamSpeakServer(
  token: string,
  id: number,
): Promise<TeamSpeakSyncResult> {
  const response = await fetch(`${apiBase}/api/teamspeak/servers/${id}/sync`, {
    method: "POST",
    headers: { Authorization: "Bearer " + token },
  });
  return readResponse<TeamSpeakSyncResult>(response);
}

export async function removeTeamSpeakServer(token: string, id: number): Promise<void> {
  const response = await fetch(`${apiBase}/api/teamspeak/servers/${id}`, {
    method: "DELETE",
    headers: { Authorization: "Bearer " + token },
  });
  if (!response.ok) {
    await readResponse(response);
  }
}

export async function getRunningProcesses(token: string): Promise<RunningProcess[]> {
  const response = await fetch(`${apiBase}/api/local-playtime/processes`, {
    headers: { Authorization: "Bearer " + token },
  });
  return readResponse<RunningProcess[]>(response);
}

export async function getTrackedGames(token: string): Promise<TrackedGame[]> {
  const response = await fetch(`${apiBase}/api/local-playtime/tracked-games`, {
    headers: { Authorization: "Bearer " + token },
  });
  return readResponse<TrackedGame[]>(response);
}

export async function addTrackedGame(
  token: string,
  gameName: string,
  processName: string,
): Promise<void> {
  const response = await fetch(`${apiBase}/api/local-playtime/tracked-games`, {
    method: "POST",
    headers: { Authorization: "Bearer " + token, "Content-Type": "application/json" },
    body: JSON.stringify({ gameName, processName }),
  });
  await readResponse<{ id: number }>(response);
}

export async function addGameUser(
  token: string,
  gameId: number,
  userName: string,
): Promise<void> {
  const response = await fetch(`${apiBase}/api/local-playtime/tracked-games/${gameId}/users`, {
    method: "POST",
    headers: { Authorization: "Bearer " + token, "Content-Type": "application/json" },
    body: JSON.stringify({ userName }),
  });
  await readResponse<{ id: number }>(response);
}

export async function setActiveGameUser(
  token: string,
  gameId: number,
  userId: number,
): Promise<void> {
  const response = await fetch(
    `${apiBase}/api/local-playtime/tracked-games/${gameId}/users/${userId}/active`,
    {
      method: "PUT",
      headers: { Authorization: "Bearer " + token },
    },
  );
  if (!response.ok) {
    await readResponse(response);
  }
}

export async function removeTrackedGame(token: string, id: number): Promise<void> {
  const response = await fetch(`${apiBase}/api/local-playtime/tracked-games/${id}`, {
    method: "DELETE",
    headers: { Authorization: "Bearer " + token },
  });
  if (!response.ok) {
    await readResponse(response);
  }
}

export interface LoginHistoryEntry {
  ipAddress: string | null;
  createdAt: string;
  isNewIp: boolean;
  backupStatus: "not-required" | "pending" | "created" | "failed";
}

export async function getLoginHistory(token: string): Promise<LoginHistoryEntry[]> {
  const response = await fetch(`${apiBase}/api/security/login-history`, {
    headers: { Authorization: "Bearer " + token },
  });
  return readResponse<LoginHistoryEntry[]>(response);
}

export async function fetchSecurityBackup(token: string): Promise<Blob> {
  const response = await fetch(`${apiBase}/api/security/backup`, {
    headers: { Authorization: "Bearer " + token },
  });
  if (!response.ok) {
    await readResponse<{ error?: string; detail?: string }>(response);
    throw new Error("Nie udało się pobrać backupu.");
  }
  return response.blob();
}
