import { useCallback, useEffect, useState } from 'react';
import { useParams } from 'react-router-dom';
import { Sparkles, CheckCheck, Loader2 } from 'lucide-react';
import { api } from '@/api';
import { type AgentRunDto, AgentTaskStatus, type WorkItemDetail, WorkItemStatus, WorkItemStatusLabel } from '@/types';
import { AgentTerminal } from '@/components/AgentTerminal';
import { TaskKanban } from '@/components/TaskKanban';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { Separator } from '@/components/ui/separator';
import { ScrollArea } from '@/components/ui/scroll-area';

const wiStatusVariant: Record<WorkItemStatus, 'secondary' | 'default' | 'outline'> = {
  [WorkItemStatus.New]: 'outline',
  [WorkItemStatus.Triaged]: 'secondary',
  [WorkItemStatus.InProgress]: 'default',
  [WorkItemStatus.Merged]: 'default',
  [WorkItemStatus.PullRequested]: 'default',
  [WorkItemStatus.Done]: 'default',
  [WorkItemStatus.Cancelled]: 'outline',
};

export function WorkItemDetailPage() {
  const { id } = useParams<{ id: string }>();
  const [item, setItem] = useState<WorkItemDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [runs, setRuns] = useState<Record<string, AgentRunDto>>({});
  const [activeTab, setActiveTab] = useState<string | null>(null);

  const reload = useCallback(async () => {
    if (!id) return;
    setItem(await api.workItems.get(id));
  }, [id]);

  useEffect(() => { reload().catch(e => setError(e.message)); }, [reload]);

  useEffect(() => {
    api.agents.listActive().then(active => {
      const map: Record<string, AgentRunDto> = {};
      for (const r of active) map[r.taskId] = r;
      setRuns(map);
    }).catch(() => {});
  }, [item?.id]);

  if (!item) {
    return error
      ? <div className="rounded-md border border-destructive/40 bg-destructive/10 px-3 py-2 text-sm">{error}</div>
      : <div className="text-muted-foreground text-sm">Loading…</div>;
  }

  async function runTriage() {
    setBusy('triage'); setError(null);
    try { await api.workItems.triage(item!.id); await reload(); }
    catch (e: any) { setError(e.message); }
    finally { setBusy(null); }
  }
  async function approveAll() {
    setBusy('approve'); setError(null);
    try { await api.workItems.approve(item!.id); await reload(); }
    catch (e: any) { setError(e.message); }
    finally { setBusy(null); }
  }
  async function approveTask(taskId: string) {
    setBusy(taskId); setError(null);
    try { await api.workItems.approveTask(item!.id, taskId); await reload(); }
    catch (e: any) { setError(e.message); }
    finally { setBusy(null); }
  }
  async function startTask(taskId: string) {
    setBusy(taskId); setError(null);
    try {
      const run = await api.agents.start(taskId);
      setRuns(r => ({ ...r, [taskId]: run }));
      setActiveTab(taskId);
      await reload();
    } catch (e: any) { setError(e.message); }
    finally { setBusy(null); }
  }
  async function stopRun(taskId: string) {
    const run = runs[taskId]; if (!run) return;
    setBusy(taskId);
    try { await api.agents.stop(run.runId); setRuns(r => { const c = { ...r }; delete c[taskId]; return c; }); }
    catch (e: any) { setError(e.message); }
    finally { setBusy(null); }
  }
  async function moveTask(taskId: string, status: AgentTaskStatus) {
    setItem(prev => prev ? { ...prev, tasks: prev.tasks.map(t => t.id === taskId ? { ...t, status } : t) } : prev);
    setError(null);
    try { await api.workItems.updateTaskStatus(item!.id, taskId, status); await reload(); }
    catch (e: any) { setError(e.message); await reload(); }
  }

  const hasProposed = item.tasks.some(t => t.status === AgentTaskStatus.Proposed);

  return (
    <div className="space-y-6">
      <div className="flex justify-between items-start">
        <div>
          <div className="text-sm text-muted-foreground">
            {item.sourceName} ·{' '}
            <code className="text-xs bg-muted px-1.5 py-0.5 rounded">{item.externalId}</code>
          </div>
          <h1 className="text-2xl font-semibold tracking-tight mt-1">{item.title}</h1>
          <div className="flex items-center gap-2 mt-2">
            <Badge variant={wiStatusVariant[item.status]}>{WorkItemStatusLabel[item.status]}</Badge>
            {item.labels && <span className="text-xs text-muted-foreground">{item.labels}</span>}
          </div>
        </div>
        <div className="flex gap-2">
          <Button variant="outline" onClick={runTriage} disabled={busy !== null}>
            {busy === 'triage' ? <Loader2 className="animate-spin" /> : <Sparkles />}
            {busy === 'triage' ? 'Triaging…' : 'Triage'}
          </Button>
          {hasProposed && (
            <Button onClick={approveAll} disabled={busy !== null}>
              {busy === 'approve' ? <Loader2 className="animate-spin" /> : <CheckCheck />}
              {busy === 'approve' ? 'Approving…' : 'Approve all'}
            </Button>
          )}
        </div>
      </div>

      {error && (
        <div className="rounded-md border border-destructive/40 bg-destructive/10 px-3 py-2 text-sm">
          {error}
        </div>
      )}

      {busy === 'triage' && (
        <div className="rounded-md border bg-muted/40 px-3 py-2 text-sm flex items-center gap-2">
          <Loader2 className="size-4 animate-spin" />
          Running triage… asking Claude to break this work item into tasks. This can take a moment.
        </div>
      )}

      <Card>
        <CardHeader><CardTitle className="text-sm uppercase tracking-wider text-muted-foreground">Body</CardTitle></CardHeader>
        <CardContent>
          <ScrollArea className="h-48 rounded-md border bg-muted/30 p-3">
            <pre className="text-xs font-mono whitespace-pre-wrap">{item.body || '(empty)'}</pre>
          </ScrollArea>
        </CardContent>
      </Card>

      <Card>
        <CardHeader className="flex flex-row items-center justify-between">
          <CardTitle className="text-sm uppercase tracking-wider text-muted-foreground">Board</CardTitle>
          {item.tasks.length > 0 && (
            <span className="text-xs text-muted-foreground">Drag cards between columns to update status</span>
          )}
        </CardHeader>
        <CardContent>
          {item.tasks.length === 0 ? (
            <div className="text-sm text-muted-foreground">No tasks yet. Run triage to propose them.</div>
          ) : (
            <TaskKanban
              tasks={item.tasks}
              runs={runs}
              busy={busy}
              onMove={moveTask}
              onApprove={approveTask}
              onStart={startTask}
              onStop={stopRun}
              onOpenTerminal={setActiveTab}
            />
          )}
        </CardContent>
      </Card>

      {Object.keys(runs).length > 0 && (
        <>
          <Separator />
          <Card>
            <CardHeader><CardTitle className="text-sm uppercase tracking-wider text-muted-foreground">Active agents</CardTitle></CardHeader>
            <CardContent>
              <div className="flex gap-1 flex-wrap mb-3">
                {Object.entries(runs).map(([taskId, run]) => {
                  const task = item.tasks.find(t => t.id === taskId);
                  return (
                    <Button
                      key={taskId}
                      variant={activeTab === taskId ? 'default' : 'outline'}
                      size="sm"
                      onClick={() => setActiveTab(taskId)}
                    >
                      {task?.title ?? run.runId.slice(0, 8)}
                    </Button>
                  );
                })}
              </div>
              {activeTab && runs[activeTab] && (
                <AgentTerminal
                  key={runs[activeTab].runId}
                  runId={runs[activeTab].runId}
                  onExit={() => reload()}
                />
              )}
            </CardContent>
          </Card>
        </>
      )}
    </div>
  );
}
