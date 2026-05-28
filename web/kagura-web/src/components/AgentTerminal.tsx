import { useEffect, useRef } from 'react';
import { useAgentSessions } from '@/contexts/AgentSessionsContext';
import { AgentRunKind, AgentRunKindLabel, type AgentRunDto } from '@/types';

interface Props {
  run: AgentRunDto;
  className?: string;
}

export function AgentTerminal({ run, className }: Props) {
  const containerRef = useRef<HTMLDivElement>(null);
  const { acquire, attach, getStatus } = useAgentSessions();
  const status = getStatus(run.runId);

  useEffect(() => {
    if (!containerRef.current) return;
    acquire(run);
    const cleanup = attach(run.runId, containerRef.current);
    return cleanup;
  }, [run, acquire, attach]);

  const statusColor =
    status === 'live' ? 'text-green-500'
    : status === 'connecting' ? 'text-amber-500'
    : 'text-muted-foreground';
  const statusLabel =
    status === 'live' ? '● live'
    : status === 'connecting' ? '· connecting'
    : '○ exited';
  const readOnly = run.kind !== AgentRunKind.TaskAgent;

  return (
    <div className={`rounded-md border bg-[#0e1116] p-2 flex flex-col min-h-0 ${className ?? 'h-[480px]'}`}>
      <div className="flex items-center gap-2 px-2 py-1 shrink-0 text-[11px]">
        <span className={statusColor}>{statusLabel}</span>
        <span className="text-muted-foreground">·</span>
        <span className="text-muted-foreground">{AgentRunKindLabel[run.kind]}</span>
        {readOnly && (
          <span className="ml-auto rounded bg-muted/40 px-1.5 py-0.5 text-[10px] uppercase tracking-wider text-muted-foreground">
            read-only
          </span>
        )}
      </div>
      <div ref={containerRef} className="flex-1 min-h-0" />
    </div>
  );
}
