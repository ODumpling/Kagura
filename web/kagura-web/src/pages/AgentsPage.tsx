import { useEffect, useMemo } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { Bot, Sparkles, ScanSearch, Square, X, ExternalLink, Loader2 } from 'lucide-react';
import { AgentRunKind, AgentRunKindLabel } from '@/types';
import { useAgentSessions, type TrackedSession } from '@/contexts/AgentSessionsContext';
import { AgentTerminal } from '@/components/AgentTerminal';
import { Button } from '@/components/ui/button';
import { ScrollArea } from '@/components/ui/scroll-area';

function kindIcon(kind: AgentRunKind) {
  if (kind === AgentRunKind.Triage) return Sparkles;
  if (kind === AgentRunKind.AutoReview) return ScanSearch;
  return Bot;
}

function statusDot(status: TrackedSession['status']) {
  if (status === 'live') return 'bg-green-500';
  if (status === 'connecting') return 'bg-amber-500 animate-pulse';
  return 'bg-muted-foreground/50';
}

function ageString(startedAt: string): string {
  const seconds = Math.max(0, Math.floor((Date.now() - new Date(startedAt).getTime()) / 1000));
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h`;
  return `${Math.floor(hours / 24)}d`;
}

export function AgentsPage() {
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const { sessions, stop, dismiss } = useAgentSessions();

  const selectedRunId = params.get('run');
  const selected = useMemo(
    () => sessions.find((s) => s.run.runId === selectedRunId) ?? sessions[0] ?? null,
    [sessions, selectedRunId],
  );

  // Reload-safe: if URL has a runId, make sure it stays in the URL even after first hit.
  useEffect(() => {
    if (selected && selectedRunId !== selected.run.runId) {
      const next = new URLSearchParams(params);
      next.set('run', selected.run.runId);
      setParams(next, { replace: true });
    }
  }, [selected, selectedRunId, params, setParams]);

  function select(runId: string) {
    const next = new URLSearchParams(params);
    next.set('run', runId);
    setParams(next, { replace: false });
  }

  function jump(s: TrackedSession) {
    if (s.run.workItemId) {
      navigate(`/workitems/${s.run.workItemId}`);
    }
  }

  return (
    <div className="flex flex-1 min-h-0 gap-4">
      <aside className="w-[340px] shrink-0 rounded-lg border bg-card flex flex-col min-h-0">
        <div className="px-3 py-2 border-b text-xs uppercase tracking-wider text-muted-foreground">
          Sessions {sessions.length > 0 && <span className="ml-1 text-foreground">({sessions.length})</span>}
        </div>
        <ScrollArea className="flex-1">
          {sessions.length === 0 && (
            <div className="px-3 py-6 text-sm text-muted-foreground text-center">
              No active or recent sessions.
            </div>
          )}
          <ul className="divide-y">
            {sessions.map((s) => {
              const Icon = kindIcon(s.run.kind);
              const isSelected = selected?.run.runId === s.run.runId;
              const isExited = s.status === 'exited';
              return (
                <li
                  key={s.run.runId}
                  className={`px-3 py-2 cursor-pointer hover:bg-muted/40 ${isSelected ? 'bg-muted/60' : ''}`}
                  onClick={() => select(s.run.runId)}
                >
                  <div className="flex items-center gap-2 min-w-0">
                    <Icon className="size-3.5 shrink-0 text-muted-foreground" />
                    <span className="text-xs font-medium truncate" title={s.run.title}>
                      {AgentRunKindLabel[s.run.kind]}: {s.run.title || s.run.runId.slice(0, 8)}
                    </span>
                    <span className={`size-2 rounded-full shrink-0 ml-auto ${statusDot(s.status)}`} title={s.status} />
                  </div>
                  <div className="flex items-center gap-2 mt-1 text-[10px] text-muted-foreground">
                    {s.run.workItemExternalId && (
                      <code className="bg-muted px-1 py-0.5 rounded">{s.run.workItemExternalId}</code>
                    )}
                    <span>· {ageString(s.run.startedAt)}</span>
                    <div className="ml-auto flex items-center gap-1" onClick={(e) => e.stopPropagation()}>
                      {!isExited && s.run.kind === AgentRunKind.TaskAgent && (
                        <button
                          className="hover:text-destructive"
                          onClick={() => stop(s.run.runId).catch(() => {})}
                          title="Stop (kill) this session"
                        >
                          <Square className="size-3" />
                        </button>
                      )}
                      {isExited && (
                        <button
                          className="hover:text-foreground"
                          onClick={() => dismiss(s.run.runId)}
                          title="Dismiss this exited session"
                        >
                          <X className="size-3" />
                        </button>
                      )}
                      {s.run.workItemId && (
                        <button
                          className="hover:text-foreground"
                          onClick={() => jump(s)}
                          title="Jump to work item"
                        >
                          <ExternalLink className="size-3" />
                        </button>
                      )}
                    </div>
                  </div>
                </li>
              );
            })}
          </ul>
        </ScrollArea>
      </aside>

      <section className="flex-1 min-h-0 flex flex-col">
        {selected ? (
          <>
            <div className="flex items-center gap-2 mb-2">
              <h2 className="text-sm font-medium">
                {AgentRunKindLabel[selected.run.kind]}: {selected.run.title || selected.run.runId.slice(0, 8)}
              </h2>
              {selected.run.workItemExternalId && (
                <code className="text-[11px] bg-muted px-1.5 py-0.5 rounded">{selected.run.workItemExternalId}</code>
              )}
              <span className="text-[11px] text-muted-foreground">· started {ageString(selected.run.startedAt)} ago</span>
              <div className="ml-auto flex gap-1.5">
                {selected.status !== 'exited' && selected.run.kind === AgentRunKind.TaskAgent && (
                  <Button variant="ghost" size="sm" className="text-destructive h-7" onClick={() => stop(selected.run.runId)}>
                    <Square className="size-3.5" /> Stop
                  </Button>
                )}
                {selected.status === 'exited' && (
                  <Button variant="ghost" size="sm" className="h-7" onClick={() => dismiss(selected.run.runId)}>
                    <X className="size-3.5" /> Dismiss
                  </Button>
                )}
                {selected.run.workItemId && (
                  <Button variant="outline" size="sm" className="h-7" onClick={() => jump(selected)}>
                    <ExternalLink className="size-3.5" /> Jump
                  </Button>
                )}
              </div>
            </div>
            <AgentTerminal key={selected.run.runId} run={selected.run} className="flex-1 min-h-0" />
          </>
        ) : (
          <div className="flex-1 grid place-items-center rounded-lg border bg-card text-sm text-muted-foreground">
            <div className="flex flex-col items-center gap-2">
              <Loader2 className="size-4 animate-spin" />
              Waiting for a session…
            </div>
          </div>
        )}
      </section>
    </div>
  );
}
