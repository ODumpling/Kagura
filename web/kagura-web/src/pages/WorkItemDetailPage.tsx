import { useCallback, useEffect, useState } from 'react';
import { useParams } from 'react-router-dom';
import { api } from '../api';
import { type AgentRunDto, AgentTaskStatus, AgentTaskStatusLabel, type WorkItemDetail, WorkItemStatusLabel } from '../types';
import { AgentTerminal } from '../components/AgentTerminal';
import { btn, btnDanger, btnTab, card, errorBox, muted, taskBadge, workItemBadge } from '../ui';

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

  if (!item) return <div>{error ? <div className={errorBox}>{error}</div> : <span className={muted}>Loading…</span>}</div>;

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

  const hasProposed = item.tasks.some(t => t.status === AgentTaskStatus.Proposed);

  return (
    <div>
      <div className="flex justify-between items-start mb-4">
        <div>
          <div className={`${muted} text-sm`}>
            {item.sourceName} ·{' '}
            <code className="px-1.5 py-0.5 rounded bg-slate-800 text-xs">{item.externalId}</code>
          </div>
          <h2 className="text-2xl font-semibold mt-1 mb-2">{item.title}</h2>
          <span className={workItemBadge(item.status)}>{WorkItemStatusLabel[item.status]}</span>
          {item.labels && <span className={`${muted} text-xs ml-2`}>· {item.labels}</span>}
        </div>
        <div className="flex gap-2">
          <button className={btn} onClick={runTriage} disabled={busy !== null}>Triage</button>
          {hasProposed && <button className={btn} onClick={approveAll} disabled={busy !== null}>Approve all</button>}
        </div>
      </div>

      {error && <div className={errorBox}>{error}</div>}

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        <section>
          <h3 className="text-sm uppercase tracking-wider text-slate-400 mb-2">Body</h3>
          <pre className={`${card} p-3 whitespace-pre-wrap font-mono text-xs max-h-80 overflow-auto`}>
            {item.body}
          </pre>
        </section>

        <section>
          <h3 className="text-sm uppercase tracking-wider text-slate-400 mb-2">Tasks</h3>
          {item.tasks.length === 0 && (
            <div className={muted}>No tasks yet. Run triage to propose them.</div>
          )}
          <ol className="space-y-2 list-none p-0">
            {item.tasks.map(t => {
              const run = runs[t.id];
              const canStart = t.status === AgentTaskStatus.Approved || t.status === AgentTaskStatus.AwaitingReview;
              return (
                <li key={t.id} className={`${card} p-3`}>
                  <div className="flex justify-between items-center gap-2">
                    <strong className="text-slate-100">{t.order}. {t.title}</strong>
                    <span className={taskBadge(t.status)}>{AgentTaskStatusLabel[t.status]}</span>
                  </div>
                  <p className={`${muted} text-sm my-2`}>{t.description}</p>
                  <div className="flex gap-2">
                    {canStart && !run && (
                      <button className={btn} onClick={() => startTask(t.id)} disabled={busy === t.id}>
                        Start agent
                      </button>
                    )}
                    {run && (
                      <>
                        <button className={btn} onClick={() => setActiveTab(t.id)}>Open terminal</button>
                        <button className={btnDanger} onClick={() => stopRun(t.id)} disabled={busy === t.id}>Stop</button>
                      </>
                    )}
                  </div>
                </li>
              );
            })}
          </ol>
        </section>
      </div>

      {Object.keys(runs).length > 0 && (
        <section className="mt-8 pt-4 border-t border-slate-700">
          <div className="flex gap-1 flex-wrap mb-2">
            {Object.entries(runs).map(([taskId, run]) => {
              const task = item.tasks.find(t => t.id === taskId);
              return (
                <button
                  key={taskId}
                  onClick={() => setActiveTab(taskId)}
                  className={btnTab(activeTab === taskId)}
                >
                  {task?.title ?? run.runId.slice(0, 8)}
                </button>
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
        </section>
      )}
    </div>
  );
}
