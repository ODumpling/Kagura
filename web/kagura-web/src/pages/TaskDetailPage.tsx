import { useCallback, useEffect, useMemo, useState } from 'react';
import { useParams, useSearchParams, Link } from 'react-router-dom';
import { ArrowLeft, GitBranch, FolderGit2, Loader2, Terminal as TerminalIcon } from 'lucide-react';
import { api } from '@/api';
import { getConnection } from '@/signalr';
import {
  AgentRunKind,
  type AgentRunDto,
  AgentTaskStatus,
  AgentTaskStatusLabel,
  type AgentTaskDto,
  type WorkItemDetail,
} from '@/types';
import { useAgentSessions } from '@/contexts/AgentSessionsContext';
import { AgentTerminal } from '@/components/AgentTerminal';
import { Badge } from '@/components/ui/badge';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Button } from '@/components/ui/button';

const statusVariant: Record<AgentTaskStatus, 'secondary' | 'default' | 'outline' | 'destructive'> = {
  [AgentTaskStatus.Proposed]: 'outline',
  [AgentTaskStatus.Approved]: 'secondary',
  [AgentTaskStatus.Running]: 'default',
  [AgentTaskStatus.AwaitingReview]: 'secondary',
  [AgentTaskStatus.Merged]: 'default',
  [AgentTaskStatus.Failed]: 'destructive',
  [AgentTaskStatus.Cancelled]: 'outline',
};

export function TaskDetailPage() {
  const { workItemId, taskId } = useParams<{ workItemId: string; taskId: string }>();
  const [params] = useSearchParams();
  const requestedRunId = params.get('runId');
  const [workItem, setWorkItem] = useState<WorkItemDetail | null>(null);
  const [task, setTask] = useState<AgentTaskDto | null>(null);
  const [runForTask, setRunForTask] = useState<AgentRunDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const { sessions } = useAgentSessions();

  const reload = useCallback(async () => {
    if (!workItemId || !taskId) return;
    try {
      const [wi, active] = await Promise.all([
        api.workItems.get(workItemId),
        api.agents.listActive(),
      ]);
      setWorkItem(wi);
      setTask(wi.tasks.find(t => t.id === taskId) ?? null);
      // Prefer the requested runId if it's still active; otherwise pick any active TaskAgent for this task.
      const preferred = requestedRunId
        ? active.find(r => r.runId === requestedRunId)
        : undefined;
      const fallback = active.find(r => r.taskId === taskId && r.kind === AgentRunKind.TaskAgent);
      setRunForTask(preferred ?? fallback ?? null);
    } catch (e: any) {
      setError(e.message);
    }
  }, [workItemId, taskId, requestedRunId]);

  useEffect(() => { reload().catch(e => setError(e.message)); }, [reload]);

  // Subscribe to work-item updates so status/branch/worktree changes are reflected live.
  useEffect(() => {
    if (!workItemId) return;
    let active = true;
    let cleanup = () => {};
    (async () => {
      const conn = await getConnection();
      if (!active) return;
      const onUpdate = (incomingId: string) => {
        if (incomingId !== workItemId) return;
        reload().catch(() => {});
      };
      conn.on('workItemUpdated', onUpdate);
      await conn.invoke('JoinWorkItem', workItemId);
      cleanup = () => {
        conn.off('workItemUpdated', onUpdate);
        conn.invoke('LeaveWorkItem', workItemId).catch(() => {});
      };
    })().catch(() => {});
    return () => { active = false; cleanup(); };
  }, [workItemId, reload]);

  // If a SignalR-tracked session matches this task, prefer the freshest one (carries live status).
  const sessionForRun = useMemo(() => {
    if (!runForTask) return null;
    return sessions.find(s => s.run.runId === runForTask.runId) ?? null;
  }, [sessions, runForTask]);

  if (error && !workItem) {
    return <div className="rounded-md border border-destructive/40 bg-destructive/10 px-3 py-2 text-sm">{error}</div>;
  }
  if (!workItem || !task) {
    return <div className="text-muted-foreground text-sm">Loading…</div>;
  }

  return (
    <div className="flex flex-1 flex-col gap-6 min-h-0">
      <div>
        <Link
          to={`/workitems/${workItem.id}`}
          className="inline-flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground"
        >
          <ArrowLeft className="size-3" />
          {workItem.title}
        </Link>
        <div className="mt-1 text-sm text-muted-foreground">
          {workItem.sourceName} ·{' '}
          <code className="text-xs bg-muted px-1.5 py-0.5 rounded">{workItem.externalId}</code>
        </div>
        <h1 className="text-2xl font-semibold tracking-tight mt-1">{task.title}</h1>
        <div className="flex items-center gap-2 mt-2 flex-wrap">
          <Badge variant="outline" className="text-xs">#{task.order}</Badge>
          <Badge variant={statusVariant[task.status]} className="text-xs">
            {AgentTaskStatusLabel[task.status]}
          </Badge>
          {task.retryAttempts > 0 && (
            <span className="text-xs text-muted-foreground">attempt {task.retryAttempts + 1}</span>
          )}
        </div>
      </div>

      {error && (
        <div className="rounded-md border border-destructive/40 bg-destructive/10 px-3 py-2 text-sm">
          {error}
        </div>
      )}

      <Card>
        <CardHeader>
          <CardTitle className="text-sm uppercase tracking-wider text-muted-foreground">
            Branch / Worktree
          </CardTitle>
        </CardHeader>
        <CardContent className="space-y-2 text-sm">
          <div className="flex items-center gap-2">
            <GitBranch className="size-3.5 text-muted-foreground shrink-0" />
            {task.branchName
              ? <code className="rounded bg-muted px-1.5 py-0.5 text-xs">{task.branchName}</code>
              : <span className="text-xs text-muted-foreground italic">(no branch yet)</span>}
          </div>
          <div className="flex items-center gap-2">
            <FolderGit2 className="size-3.5 text-muted-foreground shrink-0" />
            {task.worktreePath
              ? <code className="rounded bg-muted px-1.5 py-0.5 text-xs break-all">{task.worktreePath}</code>
              : <span className="text-xs text-muted-foreground italic">(no worktree yet)</span>}
          </div>
          {task.lastFailureReason && (
            <div className="mt-2 rounded-md border border-destructive/40 bg-destructive/5 px-2 py-1.5 text-xs">
              <span className="font-semibold uppercase tracking-wider text-destructive">Last failure: </span>
              {task.lastFailureReason}
            </div>
          )}
        </CardContent>
      </Card>

      <Card className="flex flex-1 flex-col min-h-0">
        <CardHeader className="flex flex-row items-center gap-2">
          <TerminalIcon className="size-4 text-muted-foreground" />
          <CardTitle className="text-sm uppercase tracking-wider text-muted-foreground">
            Agent terminal
          </CardTitle>
          {sessionForRun && (
            <span className="ml-auto text-[11px] text-muted-foreground">
              run <code className="bg-muted px-1 py-0.5 rounded">{sessionForRun.run.runId.slice(0, 8)}</code>
            </span>
          )}
        </CardHeader>
        <CardContent className="flex flex-1 flex-col min-h-0">
          {runForTask ? (
            <AgentTerminal key={runForTask.runId} run={runForTask} className="flex-1 min-h-0" />
          ) : (
            <div className="flex-1 grid place-items-center rounded-md border bg-muted/20 text-sm text-muted-foreground min-h-[200px]">
              <div className="flex flex-col items-center gap-2">
                {task.status === AgentTaskStatus.Running ? (
                  <>
                    <Loader2 className="size-4 animate-spin" />
                    Waiting for agent session…
                  </>
                ) : (
                  <span>No active agent session for this task.</span>
                )}
                <Button asChild variant="outline" size="sm" className="mt-2">
                  <Link to={`/workitems/${workItem.id}`}>Open work item</Link>
                </Button>
              </div>
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  );
}
