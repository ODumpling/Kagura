import { useCallback, useEffect, useState } from 'react';
import { useParams } from 'react-router-dom';
import { Sparkles, CheckCheck, Loader2, GitPullRequest, ExternalLink, Play, ScanSearch, Bot, OctagonX, AlertTriangle } from 'lucide-react';
import { api } from '@/api';
import { getConnection } from '@/signalr';
import { type AgentRunDto, AgentTaskStatus, type WorkItemDetail, WorkItemStatus, WorkItemStatusLabel } from '@/types';
import { AgentTerminalDialog } from '@/components/AgentTerminalDialog';
import { Markdown } from '@/components/Markdown';
import { TaskKanban } from '@/components/TaskKanban';
import { TaskReviewDialog } from '@/components/TaskReviewDialog';
import { PrPreviewPanel } from '@/components/PrPreviewPanel';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { ScrollArea } from '@/components/ui/scroll-area';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';

const wiStatusVariant: Record<WorkItemStatus, 'secondary' | 'default' | 'outline'> = {
  [WorkItemStatus.New]: 'outline',
  [WorkItemStatus.Triaged]: 'secondary',
  [WorkItemStatus.InProgress]: 'default',
  [WorkItemStatus.Merged]: 'default',
  [WorkItemStatus.PullRequested]: 'default',
  [WorkItemStatus.Done]: 'default',
  [WorkItemStatus.Cancelled]: 'outline',
  [WorkItemStatus.Closed]: 'secondary',
};

export function WorkItemDetailPage() {
  const { id } = useParams<{ id: string }>();
  const [item, setItem] = useState<WorkItemDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [runs, setRuns] = useState<Record<string, AgentRunDto>>({});
  const [terminalTaskId, setTerminalTaskId] = useState<string | null>(null);
  const [reviewTaskId, setReviewTaskId] = useState<string | null>(null);

  const reloadRuns = useCallback(async () => {
    try {
      const active = await api.agents.listActive();
      const map: Record<string, AgentRunDto> = {};
      for (const r of active) map[r.taskId] = r;
      setRuns(map);
    } catch { /* ignore */ }
  }, []);

  const reload = useCallback(async () => {
    if (!id) return;
    const [next] = await Promise.all([api.workItems.get(id), reloadRuns()]);
    setItem(next);
  }, [id, reloadRuns]);

  useEffect(() => { reload().catch(e => setError(e.message)); }, [reload]);

  // Subscribe to real-time work-item updates over SignalR.
  useEffect(() => {
    if (!id) return;
    let active = true;
    let cleanup = () => {};
    (async () => {
      const conn = await getConnection();
      if (!active) return;
      const onUpdate = (incomingId: string) => {
        if (incomingId !== id) return;
        reload().catch(() => {});
      };
      conn.on('workItemUpdated', onUpdate);
      await conn.invoke('JoinWorkItem', id);
      cleanup = () => {
        conn.off('workItemUpdated', onUpdate);
        conn.invoke('LeaveWorkItem', id).catch(() => {});
      };
    })().catch(() => {});
    return () => { active = false; cleanup(); };
  }, [id, reload]);

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
    try { await api.workItems.approveTask(item!.id, taskId); await reload(); setReviewTaskId(null); }
    catch (e: any) { setError(e.message); }
    finally { setBusy(null); }
  }
  async function rejectTask(taskId: string) {
    setBusy(taskId); setError(null);
    try { await api.workItems.updateTaskStatus(item!.id, taskId, AgentTaskStatus.Cancelled); await reload(); setReviewTaskId(null); }
    catch (e: any) { setError(e.message); }
    finally { setBusy(null); }
  }
  async function startTask(taskId: string) {
    setBusy(taskId); setError(null);
    try {
      const run = await api.agents.start(taskId);
      setRuns(r => ({ ...r, [taskId]: run }));
      setTerminalTaskId(taskId);
      await reload();
    } catch (e: any) { setError(e.message); }
    finally { setBusy(null); }
  }
  async function stopRun(taskId: string) {
    const run = runs[taskId]; if (!run) return;
    setBusy(taskId);
    try {
      await api.agents.stop(run.runId);
      setRuns(r => { const c = { ...r }; delete c[taskId]; return c; });
      setTerminalTaskId(t => t === taskId ? null : t);
      await reload();
    }
    catch (e: any) { setError(e.message); }
    finally { setBusy(null); }
  }
  async function resetTask(taskId: string) {
    setBusy(taskId); setError(null);
    try { await api.agents.reset(taskId); await reload(); }
    catch (e: any) { setError(e.message); }
    finally { setBusy(null); }
  }
  async function startAll() {
    if (!item) return;
    setBusy('start-all'); setError(null);
    try { await api.agents.startAll(item.id); /* kanban auto-refreshes via SignalR */ }
    catch (e: any) { setError(e.message); }
    finally { setBusy(null); }
  }
  async function autoReview() {
    if (!item) return;
    setBusy('auto-review'); setError(null);
    try {
      const result = await api.workItems.autoReview(item.id);
      await reload();
      if (result.flaggedForHuman > 0 && result.autoMerged === 0) {
        setError(`Auto-review flagged all ${result.flaggedForHuman} task(s) for human review.`);
      }
    }
    catch (e: any) { setError(e.message); }
    finally { setBusy(null); }
  }
  async function mergeTask(taskId: string) {
    setBusy(taskId); setError(null);
    try { await api.workItems.mergeTask(item!.id, taskId); await reload(); setReviewTaskId(null); }
    catch (e: any) { setError(e.message); }
    finally { setBusy(null); }
  }
  async function finishWorkItem() {
    if (!item) return;
    setBusy('finish'); setError(null);
    try {
      const result = await api.workItems.finish(item.id);
      await reload();
      if (result.pullRequestError) {
        setError(`Merged ${result.merged} task(s) but PR step failed: ${result.pullRequestError}`);
      }
    }
    catch (e: any) { setError(e.message); }
    finally { setBusy(null); }
  }
  async function moveTask(taskId: string, status: AgentTaskStatus) {
    setItem(prev => prev ? { ...prev, tasks: prev.tasks.map(t => t.id === taskId ? { ...t, status } : t) } : prev);
    setError(null);
    try { await api.workItems.updateTaskStatus(item!.id, taskId, status); await reload(); }
    catch (e: any) { setError(e.message); await reload(); }
  }
  async function startRalphLoop() {
    if (!item) return;
    setBusy('ralph-start'); setError(null);
    try { await api.workItems.ralphLoopStart(item.id); await reload(); }
    catch (e: any) { setError(e.message); }
    finally { setBusy(null); }
  }
  async function cancelRalphLoop() {
    if (!item) return;
    setBusy('ralph-cancel'); setError(null);
    try { await api.workItems.ralphLoopCancel(item.id); await reload(); }
    catch (e: any) { setError(e.message); }
    finally { setBusy(null); }
  }
  async function toggleInclude(taskId: string, include: boolean) {
    setItem(prev => prev ? { ...prev, tasks: prev.tasks.map(t => t.id === taskId ? { ...t, includeInPullRequest: include } : t) } : prev);
    setError(null);
    try { await api.workItems.setIncludeInPullRequest(item!.id, taskId, include); }
    catch (e: any) { setError(e.message); await reload(); }
  }

  const hasProposed = item.tasks.some(t => t.status === AgentTaskStatus.Proposed);
  const hasApproved = item.tasks.some(t => t.status === AgentTaskStatus.Approved);
  const hasRunning = item.tasks.some(t => t.status === AgentTaskStatus.Running);
  const hasReview = item.tasks.some(t => t.status === AgentTaskStatus.AwaitingReview);
  const reviewable = item.tasks.filter(t => t.status === AgentTaskStatus.AwaitingReview).length;
  const mergedCount = item.tasks.filter(t => t.status === AgentTaskStatus.Merged).length;
  const isClosed = item.status === WorkItemStatus.Closed;
  const canFinish = !hasRunning && (reviewable > 0 || mergedCount > 0) && item.status !== WorkItemStatus.PullRequested && !isClosed;

  const ralphActive = item.ralphLoopActive;
  const ralphTaskCountOk = item.tasks.length > 0 && item.tasks.length <= 3;
  const allMerged = item.tasks.length > 0 && item.tasks.every(t => t.status === AgentTaskStatus.Merged);
  const canShowRalph =
    !isClosed &&
    item.status !== WorkItemStatus.PullRequested &&
    item.tasks.length > 0 &&
    !allMerged &&
    (item.status === WorkItemStatus.Triaged || item.status === WorkItemStatus.InProgress);
  const ralphDisabledTooltip = !ralphTaskCountOk
    ? `Ralph Loop supports up to 3 tasks per work item; this one has ${item.tasks.length}.`
    : undefined;

  const ralphCurrent = (() => {
    if (!ralphActive) return null;
    const running = item.tasks.find(t => t.status === AgentTaskStatus.Running);
    if (running) return { label: `Running task '${running.title}'`, attempt: running.retryAttempts + 1 };
    const review = item.tasks
      .filter(t => t.status === AgentTaskStatus.AwaitingReview)
      .sort((a, b) => a.order - b.order)[0];
    if (review) return { label: `Merging task '${review.title}'`, attempt: review.retryAttempts + 1 };
    if (item.tasks.every(t => t.status === AgentTaskStatus.Merged)) return { label: 'Opening pull request…', attempt: 1 };
    const next = item.tasks
      .filter(t => t.status === AgentTaskStatus.Approved)
      .sort((a, b) => a.order - b.order)[0];
    if (next) return { label: `Starting task '${next.title}'`, attempt: next.retryAttempts + 1 };
    return { label: 'Working…', attempt: 1 };
  })();

  return (
    <div className="flex flex-1 flex-col gap-6 min-h-0">
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
          {ralphActive ? (
            <Button
              variant="destructive"
              onClick={cancelRalphLoop}
              disabled={busy !== null}
            >
              {busy === 'ralph-cancel' ? <Loader2 className="animate-spin" /> : <OctagonX />}
              {busy === 'ralph-cancel' ? 'Cancelling…' : 'Cancel Ralph Loop'}
            </Button>
          ) : (
            <>
              <Button
                variant="outline"
                onClick={runTriage}
                disabled={busy !== null || isClosed}
                title={isClosed ? 'Work item is closed' : undefined}
              >
                {busy === 'triage' ? <Loader2 className="animate-spin" /> : <Sparkles />}
                {busy === 'triage' ? 'Triaging…' : 'Triage'}
              </Button>
              {canShowRalph && (
                <Button
                  onClick={startRalphLoop}
                  disabled={busy !== null || !ralphTaskCountOk}
                  title={ralphDisabledTooltip}
                >
                  {busy === 'ralph-start' ? <Loader2 className="animate-spin" /> : <Bot />}
                  {busy === 'ralph-start'
                    ? 'Starting…'
                    : item.ralphLoopHaltReason
                      ? 'Retry Ralph Loop'
                      : 'Ralph Loop'}
                </Button>
              )}
              {hasProposed && (
                <Button variant="outline" onClick={approveAll} disabled={busy !== null}>
                  {busy === 'approve' ? <Loader2 className="animate-spin" /> : <CheckCheck />}
                  {busy === 'approve' ? 'Approving…' : 'Approve all'}
                </Button>
              )}
              {hasApproved && (
                <Button variant="outline" onClick={startAll} disabled={busy !== null}>
                  {busy === 'start-all' ? <Loader2 className="animate-spin" /> : <Play />}
                  {busy === 'start-all' ? 'Queuing…' : 'Start all'}
                </Button>
              )}
              {hasReview && (
                <Button variant="outline" onClick={autoReview} disabled={busy !== null}>
                  {busy === 'auto-review' ? <Loader2 className="animate-spin" /> : <ScanSearch />}
                  {busy === 'auto-review' ? 'Reviewing…' : 'Auto-review'}
                </Button>
              )}
              {canFinish && (
                <Button variant="outline" onClick={finishWorkItem} disabled={busy !== null}>
                  {busy === 'finish' ? <Loader2 className="animate-spin" /> : <GitPullRequest />}
                  {busy === 'finish'
                    ? 'Finishing…'
                    : reviewable > 0
                      ? `Finish (merge ${reviewable} + PR)`
                      : 'Open PR'}
                </Button>
              )}
              {item.pullRequestUrl && (
                <Button variant="outline" asChild>
                  <a href={item.pullRequestUrl} target="_blank" rel="noreferrer">
                    <ExternalLink /> View PR
                  </a>
                </Button>
              )}
            </>
          )}
        </div>
      </div>

      {error && (
        <div className="rounded-md border border-destructive/40 bg-destructive/10 px-3 py-2 text-sm">
          {error}
        </div>
      )}

      {ralphActive && ralphCurrent && (
        <div className="rounded-md border border-blue-500/40 bg-blue-500/10 px-3 py-2 text-sm flex items-center gap-2">
          <Bot className="size-4 animate-pulse" />
          <span className="font-medium">Ralph Loop:</span>
          <span>{ralphCurrent.label}</span>
          <span className="text-muted-foreground text-xs">attempt {ralphCurrent.attempt}/3</span>
        </div>
      )}

      {!ralphActive && item.ralphLoopHaltReason && (
        <div className="rounded-md border border-destructive/40 bg-destructive/10 px-3 py-2 text-sm flex items-start gap-2">
          <AlertTriangle className="size-4 mt-0.5 shrink-0" />
          <div className="flex-1">
            <div className="font-medium">Ralph Loop halted</div>
            <div className="text-xs text-muted-foreground mt-0.5">{item.ralphLoopHaltReason}</div>
          </div>
        </div>
      )}

      {busy === 'triage' && (
        <div className="rounded-md border bg-muted/40 px-3 py-2 text-sm flex items-center gap-2">
          <Loader2 className="size-4 animate-spin" />
          Running triage… asking Claude to break this work item into tasks. This can take a moment.
        </div>
      )}

      <Tabs defaultValue="board" className="flex flex-1 flex-col min-h-0">
        <TabsList className="shrink-0">
          <TabsTrigger value="body">Body</TabsTrigger>
          <TabsTrigger value="board">
            Board
            {item.tasks.length > 0 && (
              <Badge variant="secondary" className="ml-1.5 h-5 px-1.5 text-[10px]">{item.tasks.length}</Badge>
            )}
          </TabsTrigger>
        </TabsList>

        <TabsContent value="body" className="flex flex-1 flex-col min-h-0">
          <Card className="flex flex-1 flex-col min-h-0">
            <CardContent className="flex-1 min-h-0 pt-6">
              <ScrollArea className="h-full rounded-md border bg-muted/30 p-4">
                {item.body
                  ? <Markdown>{item.body}</Markdown>
                  : <p className="text-xs text-muted-foreground italic">(empty)</p>}
              </ScrollArea>
            </CardContent>
          </Card>
        </TabsContent>

        <TabsContent value="board">
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
                  onReset={resetTask}
                  onOpenTerminal={setTerminalTaskId}
                  onOpenTask={setReviewTaskId}
                  onToggleInclude={toggleInclude}
                />
              )}
            </CardContent>
          </Card>

          <div className="mt-4">
            <PrPreviewPanel
              workItemId={item.id}
              selectedTaskIds={item.tasks
                .filter(t => t.status === AgentTaskStatus.AwaitingReview && t.includeInPullRequest)
                .map(t => t.id)}
            />
          </div>
        </TabsContent>
      </Tabs>

      <TaskReviewDialog
        task={reviewTaskId ? item.tasks.find(t => t.id === reviewTaskId) ?? null : null}
        busy={busy}
        onClose={() => setReviewTaskId(null)}
        onApprove={approveTask}
        onReject={rejectTask}
        onMerge={mergeTask}
      />

      <AgentTerminalDialog
        task={terminalTaskId ? item.tasks.find(t => t.id === terminalTaskId) ?? null : null}
        run={terminalTaskId ? runs[terminalTaskId] ?? null : null}
        busy={busy}
        onClose={() => setTerminalTaskId(null)}
        onStop={stopRun}
      />
    </div>
  );
}
