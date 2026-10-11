import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import App from "./App";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

it("logs in, uploads a CSV file and displays analysis summaries", async () => {
  const user = userEvent.setup();
  const fetchMock = vi.fn()
    .mockResolvedValueOnce(new Response(JSON.stringify({ token: "test-token" }), { status: 200 }))
    .mockResolvedValueOnce(new Response(JSON.stringify({
      servers: [{
        service: "game",
        serverName: "Survival",
        game: "Minecraft",
        sessions: 1,
        uniquePlayers: 1,
        totalHours: 2,
        meanSessionMinutes: 120,
        medianSessionMinutes: 120,
      }],
      players: [{
        service: "game",
        serverName: "Survival",
        game: "Minecraft",
        player: "Ala",
        sessions: 1,
        totalHours: 2,
        meanSessionMinutes: 120,
        medianSessionMinutes: 120,
      }],
    }), { status: 200 }));
  vi.stubGlobal("fetch", fetchMock);
  render(<App />);

  await user.type(screen.getByLabelText("Login"), "ala");
  await user.type(screen.getByLabelText("Hasło"), "correct-horse");
  await user.click(screen.getByRole("button", { name: "Zaloguj się" }));

  const file = new File([
    "service,server_name,game,player,connected_at,disconnected_at",
  ], "sessions.csv", { type: "text/csv" });
  await user.upload(screen.getByLabelText("Plik CSV"), file);
  const uploadForm = screen.getByLabelText("Plik CSV").closest("form");
  expect(uploadForm).not.toBeNull();
  fireEvent.submit(uploadForm!);

  await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));
  expect(await screen.findByRole("heading", { name: "Podsumowanie serwerów" })).toBeInTheDocument();
  expect(screen.getAllByText("Survival")).toHaveLength(2);
  expect(screen.getAllByText("Minecraft")).toHaveLength(2);
  expect(screen.getByText("Ala")).toBeInTheDocument();
  expect(fetchMock.mock.calls[1][1]).toMatchObject({
    method: "POST",
    headers: { Authorization: "Bearer test-token" },
  });
});

it("adds game users and lets the selected user accumulate playtime", async () => {
  const user = userEvent.setup();
  const game = {
    id: 7,
    gameName: "Minecraft",
    processName: "Minecraft.Windows",
    totalSeconds: 0,
    isRunning: true,
    isTracking: true,
    users: [
      { id: 10, userName: "Ala", totalSeconds: 0, isActive: true },
    ],
  };
  const fetchMock = vi.fn()
    .mockResolvedValueOnce(new Response(JSON.stringify({ token: "test-token" }), { status: 200 }))
    .mockResolvedValueOnce(new Response(JSON.stringify([{ processName: "Minecraft.Windows" }]), { status: 200 }))
    .mockResolvedValueOnce(new Response(JSON.stringify([game]), { status: 200 }))
    .mockResolvedValueOnce(new Response(JSON.stringify({ id: 11 }), { status: 201 }))
    .mockResolvedValueOnce(new Response(JSON.stringify([{
      ...game,
      users: [...game.users, { id: 11, userName: "Bartek", totalSeconds: 0, isActive: false }],
    }]), { status: 200 }))
    .mockResolvedValueOnce(new Response(null, { status: 204 }))
    .mockResolvedValueOnce(new Response(JSON.stringify([{
      ...game,
      users: [
        { ...game.users[0], isActive: false },
        { id: 11, userName: "Bartek", totalSeconds: 0, isActive: true },
      ],
    }]), { status: 200 }));
  vi.stubGlobal("fetch", fetchMock);
  render(<App />);

  await user.type(screen.getByLabelText("Login"), "ala");
  await user.type(screen.getByLabelText("Hasło"), "correct-horse");
  await user.click(screen.getByRole("button", { name: "Zaloguj się" }));
  await user.click(await screen.findByRole("button", { name: "Odśwież procesy i czas" }));

  const userInput = await screen.findByLabelText("Dodaj użytkownika");
  await user.type(userInput, "Bartek");
  await user.click(screen.getByRole("button", { name: "Dodaj" }));
  expect(await screen.findAllByText("Bartek")).not.toHaveLength(0);

  await user.selectOptions(screen.getByLabelText("Aktywny użytkownik"), "11");
  expect(await screen.findByText("Bartek (aktywny)")).toBeInTheDocument();
  expect(fetchMock.mock.calls.map(([url]) => url)).toContain(
    "/api/local-playtime/tracked-games/7/users/11/active",
  );
});

it("shows login IP history to the signed-in user", async () => {
  const user = userEvent.setup();
  const fetchMock = vi.fn()
    .mockResolvedValueOnce(new Response(JSON.stringify({
      token: "test-token",
      isAdministrator: false,
    }), { status: 200 }))
    .mockResolvedValueOnce(new Response(JSON.stringify([{
      ipAddress: "192.0.2.11",
      createdAt: "2026-10-05T16:00:00Z",
      isNewIp: true,
      backupStatus: "created",
    }]), { status: 200 }));
  vi.stubGlobal("fetch", fetchMock);
  render(<App />);

  await user.type(screen.getByLabelText("Login"), "ala");
  await user.type(screen.getByLabelText("Hasło"), "correct-horse");
  await user.click(screen.getByRole("button", { name: "Zaloguj się" }));
  await user.click(await screen.findByRole("button", { name: "Historia logowań" }));

  expect(await screen.findByText(/192\.0\.2\.11 \(nowy adres IP\)/)).toBeInTheDocument();
  expect(screen.getByText("Backup utworzony")).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Pobierz odszyfrowany backup" })).not.toBeInTheDocument();
  expect(fetchMock.mock.calls[1][0]).toBe("/api/security/login-history");
});

it("shows the backup download action only for an administrator", async () => {
  const user = userEvent.setup();
  const fetchMock = vi.fn().mockResolvedValueOnce(new Response(JSON.stringify({
    token: "admin-token",
    isAdministrator: true,
  }), { status: 200 })).mockResolvedValueOnce(new Response(JSON.stringify([]), { status: 200 }));
  vi.stubGlobal("fetch", fetchMock);
  render(<App />);

  await user.type(screen.getByLabelText("Login"), "admin");
  await user.type(screen.getByLabelText("Hasło"), "correct-horse");
  await user.click(screen.getByRole("button", { name: "Zaloguj się" }));

  expect(await screen.findByRole("button", { name: "Pobierz odszyfrowany backup" })).toBeInTheDocument();
});

it("loads TeamSpeak servers for administrators and synchronizes from the panel", async () => {
  const user = userEvent.setup();
  const server = {
    id: 4,
    name: "Główny serwer",
    host: "ts.example.com",
    queryPort: 10011,
    virtualServerId: 1,
    queryUsername: "query-user",
    lastSyncAt: null,
    lastError: null,
    activeClients: 0,
  };
  const fetchMock = vi.fn()
    .mockResolvedValueOnce(new Response(JSON.stringify({
      token: "admin-token",
      isAdministrator: true,
    }), { status: 200 }))
    .mockResolvedValueOnce(new Response(JSON.stringify([server]), { status: 200 }))
    .mockResolvedValueOnce(new Response(JSON.stringify({
      activeClients: 2,
      startedSessions: 1,
      endedSessions: 0,
    }), { status: 200 }))
    .mockResolvedValueOnce(new Response(JSON.stringify([{
      ...server,
      activeClients: 2,
      lastSyncAt: "2026-10-11T12:00:00Z",
    }]), { status: 200 }))
    .mockResolvedValueOnce(new Response(JSON.stringify({ servers: [], players: [] }), { status: 200 }));
  vi.stubGlobal("fetch", fetchMock);
  render(<App />);

  await user.type(screen.getByLabelText("Login"), "admin");
  await user.type(screen.getByLabelText("Hasło"), "correct-horse");
  await user.click(screen.getByRole("button", { name: "Zaloguj się" }));

  expect(await screen.findByRole("heading", { name: "Główny serwer" })).toBeInTheDocument();
  await user.click(screen.getByRole("button", { name: "Synchronizuj teraz" }));

  expect(await screen.findByText(/2 aktywnych użytkowników, 1 nowych sesji/)).toBeInTheDocument();
  expect(fetchMock.mock.calls.map(([url]) => url)).toContain(
    "/api/teamspeak/servers/4/sync",
  );
});
