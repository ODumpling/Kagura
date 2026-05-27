import { useState } from 'react';
import type { DragEvent } from 'react';
import { Play, Square, Terminal as TerminalIcon, Check, Loader2, RotateCcw } from 'lucide-react';
import { type AgentRunDto, type AgentTaskDto, AgentTaskStatus } from '@/types';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Checkbox } from '@/components/ui/checkbox';

interface Column {
  status: AgentTaskStatus;
  label: string;
  accent: string;
  alwaysShow: boolean;
}

const COLUMNS: Column[] = [
  { status: AgentTaskStatus.Proposed, label: 'Proposed', accent: 'border-muted-foreground/30', alwaysShow: true },
  { status: AgentTaskStatus.Approved, label: 'Approved', accent: 'border-blue-500/40', alwaysShow: true },
  { status: AgentTaskStatus.Running, label: 'Running', accent: 'border-amber-500/50', alwaysShow: true },
  { status: AgentTaskStatus.AwaitingReview, label: 'Review', accent: 'border-purple-500/40', alwaysShow: true },
  { status: AgentTaskStatus.Merged, label: 'Merged', accent: 'border-green-500/40', alwaysShow: true },
  { status: AgentTaskStatus.Failed, label: 'Failed', accent: 'border-destructive/40', alwaysShow: false },
  { status: AgentTaskStatus.Cancelled, label: 'Cancelled', accent: 'border-muted-foreground/20', alwaysShow: false },
];

interface Props {
  tasks: AgentTaskDto[];
  runs: Record<string, AgentRunDto>;
  busy: string | null;
  onMove: (taskId: string, status: AgentTaskStatus) => void;
  onApprove: (taskId: string) => void;
  onStart: (taskId: string) => void;
  onStop: (taskId: string) => void;
  onReset: (taskId: string) => void;
  onOpenTerminal: (taskId: string) => void;
  onOpenTask: (taskId: string) => void;
  onToggleInclude: (taskId: string, include: boolean) => void;
}

export function TaskKanban({ tasks, runs, busy, onMove, onApprove, onStart, onStop, onReset, onOpenTerminal, onOpenTask, onToggleInclude }: Props) {
  const [dragOver, setDragOver] = useState<AgentTaskStatus | null>(null);

  const visibleColumns = COLUMNS.filter(c => c.alwaysShow || tasks.some(t => t.status === c.status));

  function handleDrop(e: DragEvent, target: AgentTaskStatus) {
    e.preventDefault();
    setDragOver(null);
    const taskId = e.dataTransfer.getData('text/plain');
    if (!taskId) return;
    const task = tasks.find(t => t.id === taskId);
    if (!task || task.status === target) return;
    if (target === AgentTaskStatus.Running) return;
    if (task.status === AgentTaskStatus.Running) return;
    onMove(taskId, target);
  }

  return (
    <div className="flex gap-3 overflow-x-auto pb-2">
      {visibleColumns.map(col => {
        const colTasks = tasks.filter(t => t.status === col.status).sort((a, b) => a.order - b.order);
        const droppable = col.status !== AgentTaskStatus.Running;
        const highlighted = dragOver === col.status && droppable;
        return (
          <div
            key={col.status}
            className={`flex-1 min-w-[220px] rounded-lg border-2 ${col.accent} ${highlighted ? 'bg-muted/60' : 'bg-muted/20'} p-3 flex flex-col transition-colors`}
            onDragOver={(e) => { if (droppable) { e.preventDefault(); setDragOver(col.status); } }}
            onDragLeave={() => setDragOver(s => s === col.status ? null : s)}
            onDrop={(e) => handleDrop(e, col.status)}
          >
            <div className="flex items-center justify-between mb-3 px-1">
              <span className="text-xs uppercase tracking-wider font-semibold text-muted-foreground">{col.label}</span>
              <Badge variant="outline" className="text-xs">{colTasks.length}</Badge>
            </div>
            <div className="space-y-2 flex-1">
              {colTasks.length === 0 && (
                <div className="text-xs text-muted-foreground/50 italic px-1 py-2">—</div>
              )}
              {colTasks.map(t => {
                const run = runs[t.id];
                const canStart = t.status === AgentTaskStatus.Approved;
                const draggable = t.status !== AgentTaskStatus.Running;
                return (
                  <div
                    key={t.id}
                    draggable={draggable}
                    onDragStart={(e) => e.dataTransfer.setData('text/plain', t.id)}
                    onClick={() => onOpenTask(t.id)}
                    role="button"
                    tabIndex={0}
                    onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onOpenTask(t.id); } }}
                    className={`rounded-md border bg-background p-2 shadow-sm hover:border-foreground/30 hover:bg-muted/40 transition-colors ${draggable ? 'cursor-grab active:cursor-grabbing' : 'cursor-pointer'}`}
                  >
                    <div className="text-sm font-medium">{t.order}. {t.title}</div>
                    {t.description && <p className="text-xs text-muted-foreground mt-1 line-clamp-3">{t.description}</p>}
                    {t.status === AgentTaskStatus.AwaitingReview && (
                      <label
                        className="mt-2 flex items-center gap-1.5 text-xs text-muted-foreground cursor-pointer w-fit"
                        onClick={(e) => e.stopPropagation()}
                      >
                        <Checkbox
                          checked={t.includeInPullRequest}
                          onCheckedChange={(v) => onToggleInclude(t.id, v === true)}
                        />
                        <span>Include in PR</span>
                      </label>
                    )}
                    <div className="flex gap-1 mt-2 flex-wrap" onClick={(e) => e.stopPropagation()}>
                      {t.status === AgentTaskStatus.Proposed && (
                        <Button size="sm" variant="outline" className="h-7 px-2 text-xs" onClick={() => onApprove(t.id)} disabled={busy !== null}>
                          {busy === t.id ? <Loader2 className="animate-spin" /> : <Check />}
                          Approve
                        </Button>
                      )}
                      {canStart && !run && (
                        <Button size="sm" className="h-7 px-2 text-xs" onClick={() => onStart(t.id)} disabled={busy === t.id}>
                          {busy === t.id ? <Loader2 className="animate-spin" /> : <Play />}
                          Start
                        </Button>
                      )}
                      {run && (
                        <>
                          <Button variant="outline" size="sm" className="h-7 px-2 text-xs" onClick={() => onOpenTerminal(t.id)}>
                            <TerminalIcon /> Terminal
                          </Button>
                          <Button variant="ghost" size="sm" className="h-7 px-2 text-xs text-destructive" onClick={() => onStop(t.id)} disabled={busy === t.id}>
                            <Square /> Stop
                          </Button>
                        </>
                      )}
                      {t.status === AgentTaskStatus.Running && !run && (
                        <Button variant="outline" size="sm" className="h-7 px-2 text-xs" onClick={() => onReset(t.id)} disabled={busy === t.id} title="No live agent for this task. Reset moves it back to Approved.">
                          {busy === t.id ? <Loader2 className="animate-spin" /> : <RotateCcw />}
                          Reset
                        </Button>
                      )}
                    </div>
                  </div>
                );
              })}
            </div>
          </div>
        );
      })}
    </div>
  );
}
