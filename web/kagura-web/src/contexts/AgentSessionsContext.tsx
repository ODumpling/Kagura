import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from 'react';
import { Terminal } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import '@xterm/xterm/css/xterm.css';
import { api } from '@/api';
import { base64ToBytes, bytesToBase64, getConnection } from '@/signalr';
import { AgentRunKind, type AgentRunDto } from '@/types';

export type SessionStatus = 'connecting' | 'live' | 'exited';

export interface TrackedSession {
  run: AgentRunDto;
  status: SessionStatus;
  exitCode: number | null;
  exitedAt: string | null;
  /** Set true once user dismisses an exited session — UI hides it after this. */
  dismissed: boolean;
}

interface TerminalHandle {
  term: Terminal;
  fit: FitAddon;
  /** Off-screen DOM host so we can move the terminal between mount points without re-attaching. */
  host: HTMLDivElement;
  ro: ResizeObserver | null;
  inputUnsub: { dispose: () => void } | null;
  resizeUnsub: { dispose: () => void } | null;
}

interface AgentSessionsContextValue {
  sessions: TrackedSession[];
  /** Active (live or connecting) — derived but exported for convenience. */
  active: TrackedSession[];
  /** Get or lazily-create the terminal handle for a run. */
  acquire: (run: AgentRunDto) => TerminalHandle;
  /** Mount the handle's host element into a container; returns cleanup that detaches without disposing. */
  attach: (runId: string, container: HTMLElement) => () => void;
  refresh: () => Promise<void>;
  stop: (runId: string) => Promise<void>;
  dismiss: (runId: string) => void;
  getStatus: (runId: string) => SessionStatus;
  /** When the chip wants the in-place modal to open for a same-WI session, it sets this. */
  modalRequest: AgentRunDto | null;
  requestModal: (run: AgentRunDto) => void;
  clearModalRequest: () => void;
}

const AgentSessionsContext = createContext<AgentSessionsContextValue | null>(null);

export function AgentSessionsProvider({ children }: { children: ReactNode }) {
  const [sessions, setSessions] = useState<Record<string, TrackedSession>>({});
  const [modalRequest, setModalRequest] = useState<AgentRunDto | null>(null);
  const handles = useRef<Map<string, TerminalHandle>>(new Map());
  const joined = useRef<Set<string>>(new Set());

  const upsert = useCallback((runId: string, patch: (prev: TrackedSession | undefined) => TrackedSession) => {
    setSessions((s) => ({ ...s, [runId]: patch(s[runId]) }));
  }, []);

  const acquire = useCallback((run: AgentRunDto): TerminalHandle => {
    const existing = handles.current.get(run.runId);
    if (existing) return existing;

    const host = document.createElement('div');
    host.className = 'h-full w-full';
    const term = new Terminal({
      cursorBlink: true,
      fontFamily: 'Menlo, Monaco, "Courier New", monospace',
      fontSize: 13,
      theme: { background: '#0e1116', foreground: '#d8dee9' },
      convertEol: false,
      scrollback: 5000,
    });
    const fit = new FitAddon();
    term.loadAddon(fit);
    term.open(host);

    const handle: TerminalHandle = { term, fit, host, ro: null, inputUnsub: null, resizeUnsub: null };
    handles.current.set(run.runId, handle);

    // Only task-agent kinds accept input; others are read-only.
    if (run.kind === AgentRunKind.TaskAgent) {
      handle.inputUnsub = term.onData((d) => {
        const bytes = new TextEncoder().encode(d);
        getConnection()
          .then((c) => c.invoke('Input', run.runId, bytesToBase64(bytes)))
          .catch(() => {});
      });
      handle.resizeUnsub = term.onResize(({ cols, rows }) => {
        getConnection()
          .then((c) => c.invoke('Resize', run.runId, cols, rows))
          .catch(() => {});
      });
    }

    // Seed tracked-session state if absent.
    upsert(run.runId, (prev) =>
      prev ?? {
        run,
        status: 'connecting',
        exitCode: run.exitCode,
        exitedAt: run.alive ? null : new Date().toISOString(),
        dismissed: false,
      },
    );

    // Join the SignalR group once per run.
    if (!joined.current.has(run.runId)) {
      joined.current.add(run.runId);
      getConnection()
        .then((conn) => conn.invoke('Join', run.runId))
        .then(() => {
          upsert(run.runId, (prev) =>
            prev ? { ...prev, status: prev.status === 'exited' ? 'exited' : 'live' } : prev!,
          );
        })
        .catch(() => {
          joined.current.delete(run.runId);
        });
    }

    return handle;
  }, [upsert]);

  const attach = useCallback((runId: string, container: HTMLElement): (() => void) => {
    const handle = handles.current.get(runId);
    if (!handle) return () => {};
    container.appendChild(handle.host);
    try { handle.fit.fit(); } catch { /* ignore */ }

    const ro = new ResizeObserver(() => {
      try { handle.fit.fit(); } catch { /* ignore */ }
    });
    ro.observe(container);
    handle.ro = ro;

    return () => {
      ro.disconnect();
      handle.ro = null;
      if (handle.host.parentElement === container) container.removeChild(handle.host);
    };
  }, []);

  const getStatus = useCallback((runId: string): SessionStatus =>
    sessions[runId]?.status ?? 'connecting',
  [sessions]);

  const refresh = useCallback(async () => {
    try {
      const list = await api.agents.listActive();
      setSessions((prev) => {
        const next: Record<string, TrackedSession> = { ...prev };
        for (const r of list) {
          const existing = next[r.runId];
          next[r.runId] = existing
            ? { ...existing, run: r }
            : {
                run: r,
                status: r.alive ? 'connecting' : 'exited',
                exitCode: r.exitCode,
                exitedAt: r.alive ? null : new Date().toISOString(),
                dismissed: false,
              };
        }
        // Mark anything no longer in the server's active list as exited if we still hold it.
        const activeIds = new Set(list.map((r) => r.runId));
        for (const id of Object.keys(next)) {
          if (!activeIds.has(id) && next[id].status !== 'exited') {
            next[id] = { ...next[id], status: 'exited', exitedAt: next[id].exitedAt ?? new Date().toISOString() };
          }
        }
        return next;
      });
    } catch { /* ignore */ }
  }, []);

  const stop = useCallback(async (runId: string) => {
    await api.agents.stop(runId);
    upsert(runId, (prev) => prev ? { ...prev, status: 'exited', exitedAt: prev.exitedAt ?? new Date().toISOString() } : prev!);
  }, [upsert]);

  const dismiss = useCallback((runId: string) => {
    const handle = handles.current.get(runId);
    if (handle) {
      handle.inputUnsub?.dispose();
      handle.resizeUnsub?.dispose();
      handle.ro?.disconnect();
      try { handle.term.dispose(); } catch { /* ignore */ }
      handles.current.delete(runId);
    }
    joined.current.delete(runId);
    getConnection()
      .then((c) => c.invoke('Leave', runId))
      .catch(() => {});
    setSessions((s) => {
      const next = { ...s };
      delete next[runId];
      return next;
    });
  }, []);

  // Wire SignalR data/exit + workItemUpdated handlers once.
  useEffect(() => {
    let cancelled = false;
    let unsub = () => {};
    (async () => {
      const conn = await getConnection();
      if (cancelled) return;
      const onData = (runId: string, b64: string) => {
        const handle = handles.current.get(runId);
        if (!handle) return;
        handle.term.write(base64ToBytes(b64));
      };
      const onExit = (runId: string, code: number | null) => {
        upsert(runId, (prev) => prev
          ? { ...prev, status: 'exited', exitCode: code, exitedAt: prev.exitedAt ?? new Date().toISOString() }
          : prev!);
      };
      const onWorkItemUpdated = () => { refresh().catch(() => {}); };
      conn.on('data', onData);
      conn.on('exit', onExit);
      conn.on('workItemUpdated', onWorkItemUpdated);
      unsub = () => {
        conn.off('data', onData);
        conn.off('exit', onExit);
        conn.off('workItemUpdated', onWorkItemUpdated);
      };
    })().catch(() => {});

    return () => { cancelled = true; unsub(); };
  }, [upsert, refresh]);

  // Initial load + periodic refresh fallback.
  useEffect(() => {
    refresh().catch(() => {});
    const t = setInterval(() => { refresh().catch(() => {}); }, 15000);
    return () => clearInterval(t);
  }, [refresh]);

  const list = useMemo(
    () => Object.values(sessions).filter((s) => !s.dismissed).sort((a, b) =>
      (b.run.startedAt ?? '').localeCompare(a.run.startedAt ?? '')),
    [sessions],
  );
  const active = useMemo(() => list.filter((s) => s.status !== 'exited'), [list]);

  const requestModal = useCallback((run: AgentRunDto) => setModalRequest(run), []);
  const clearModalRequest = useCallback(() => setModalRequest(null), []);

  const value: AgentSessionsContextValue = useMemo(() => ({
    sessions: list,
    active,
    acquire,
    attach,
    refresh,
    stop,
    dismiss,
    getStatus,
    modalRequest,
    requestModal,
    clearModalRequest,
  }), [list, active, acquire, attach, refresh, stop, dismiss, getStatus, modalRequest, requestModal, clearModalRequest]);

  return (
    <AgentSessionsContext.Provider value={value}>
      {children}
    </AgentSessionsContext.Provider>
  );
}

export function useAgentSessions() {
  const ctx = useContext(AgentSessionsContext);
  if (!ctx) throw new Error('useAgentSessions must be used inside AgentSessionsProvider');
  return ctx;
}
