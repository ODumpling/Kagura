import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from 'react';
import { getConnection } from '@/signalr';
import type { AgentSidebarEventDto } from '@/types';

/// Live state for the sidebar tree. Per CONTEXT.md → "Source tree (navigation)":
/// Sources are the roots, Agents are children grouped by their WorkItem's Source.
/// SignalR-driven: `agentAppeared` adds, `agentDismissed` removes, `agentStatusChanged`
/// updates the status line, `exit` flips an Agent into a "stopped" state.
///
/// Auto-dismiss-on-success is implemented server-side — the backend calls
/// `AgentDismissedAsync` after a successful MCP submission. Failures (PTY exit without
/// submission) keep the row present until the user dismisses explicitly.

export type SidebarAgentLifecycle = 'live' | 'failed';

export interface SidebarAgent extends AgentSidebarEventDto {
  lifecycle: SidebarAgentLifecycle;
  exitCode: number | null;
}

interface SidebarAgentsContextValue {
  agents: SidebarAgent[];
  /** Group agents by sourceId — handy for the sidebar tree renderer. */
  bySource: Record<string, SidebarAgent[]>;
  /** Programmatically remove an agent from the sidebar (used by the dismiss button on failed rows). */
  remove: (runId: string) => void;
}

const SidebarAgentsContext = createContext<SidebarAgentsContextValue | null>(null);

export function SidebarAgentsProvider({ children }: { children: ReactNode }) {
  const [agents, setAgents] = useState<Record<string, SidebarAgent>>({});

  const remove = useCallback((runId: string) => {
    setAgents((prev) => {
      if (!(runId in prev)) return prev;
      const { [runId]: _gone, ...rest } = prev;
      void _gone;
      return rest;
    });
  }, []);

  useEffect(() => {
    let cancelled = false;
    let unsub = () => {};
    (async () => {
      const conn = await getConnection();
      if (cancelled) return;

      const onAppeared = (evt: AgentSidebarEventDto) => {
        setAgents((prev) => ({
          ...prev,
          [evt.runId]: { ...evt, lifecycle: 'live', exitCode: null },
        }));
      };
      const onDismissed = (runId: string) => {
        setAgents((prev) => {
          if (!(runId in prev)) return prev;
          const { [runId]: _gone, ...rest } = prev;
          void _gone;
          return rest;
        });
      };
      const onStatusChanged = (runId: string, statusLine: string) => {
        setAgents((prev) => prev[runId] ? { ...prev, [runId]: { ...prev[runId], statusLine } } : prev);
      };
      const onExit = (runId: string, code: number | null) => {
        // Per CONTEXT.md → "Agent lifecycle": failure (non-zero exit, no MCP submission) lingers.
        // The backend dispatches `agentDismissed` on success, so any `exit` we see here without
        // a follow-up dismiss means the run failed and should linger. Surface that visually.
        setAgents((prev) => prev[runId]
          ? { ...prev, [runId]: { ...prev[runId], lifecycle: 'failed', exitCode: code } }
          : prev);
      };

      conn.on('agentAppeared', onAppeared);
      conn.on('agentDismissed', onDismissed);
      conn.on('agentStatusChanged', onStatusChanged);
      conn.on('exit', onExit);
      unsub = () => {
        conn.off('agentAppeared', onAppeared);
        conn.off('agentDismissed', onDismissed);
        conn.off('agentStatusChanged', onStatusChanged);
        conn.off('exit', onExit);
      };
    })().catch(() => {});

    return () => { cancelled = true; unsub(); };
  }, []);

  const list = useMemo(
    () => Object.values(agents).sort((a, b) => a.startedAt.localeCompare(b.startedAt)),
    [agents],
  );

  const bySource = useMemo(() => {
    const out: Record<string, SidebarAgent[]> = {};
    for (const a of list) (out[a.sourceId] ??= []).push(a);
    return out;
  }, [list]);

  const value: SidebarAgentsContextValue = useMemo(
    () => ({ agents: list, bySource, remove }),
    [list, bySource, remove],
  );

  return (
    <SidebarAgentsContext.Provider value={value}>
      {children}
    </SidebarAgentsContext.Provider>
  );
}

export function useSidebarAgents() {
  const ctx = useContext(SidebarAgentsContext);
  if (!ctx) throw new Error('useSidebarAgents must be used inside SidebarAgentsProvider');
  return ctx;
}
