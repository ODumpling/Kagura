import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { api } from './api';

let conn: HubConnection | null = null;
let starting: Promise<HubConnection> | null = null;

export async function getConnection(): Promise<HubConnection> {
  if (conn && conn.state === HubConnectionState.Connected) return conn;
  if (starting) return starting;

  starting = (async () => {
    const c = new HubConnectionBuilder()
      .withUrl(api.hubUrl())
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();
    await c.start();
    conn = c;
    return c;
  })();
  try {
    return await starting;
  } finally {
    starting = null;
  }
}

export function base64ToBytes(b64: string): Uint8Array {
  const bin = atob(b64);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

export function bytesToBase64(bytes: Uint8Array): string {
  let bin = '';
  for (let i = 0; i < bytes.length; i++) bin += String.fromCharCode(bytes[i]);
  return btoa(bin);
}
