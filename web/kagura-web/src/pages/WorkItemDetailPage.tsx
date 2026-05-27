import { useCallback, useEffect, useState } from 'react';
import { useParams } from 'react-router-dom';
import { api } from '../api';
import { type AgentRunDto, AgentTaskStatus, AgentTaskStatusLabel, type WorkItemDetail, WorkItemStatusLabel } from '../types';
import { AgentTerminal } from '../components/AgentTerminal';

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

  if (!item) return <div>{error ? <div className="error">{error}</div> : 'Loading…'}</div>;

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
    <div className="workitem-detail">
      <div className="page-header">
        <div>
          <div className="muted">{item.sourceName} · <code>{item.externalId}</code></div>
          <h2>{item.title}</h2>
          <span className={`badge s${item.status}`}>{WorkItemStatusLabel[item.status]}</span>
          {item.labels && <span className="muted"> · {item.labels}</span>}
        </div>
        <div className="actions">
          <button onClick={runTriage} disabled={busy !== null}>Triage</button>
          {hasProposed && <button onClick={approveAll} disabled={busy !== null}>Approve all</button>}
        </div>
      </div>
      {error && <div className="error">{error}</div>}

      <div className="two-col">
        <section>
          <h3>Body</h3>
          <pre className="body">{item.body}</pre>
        </section>

        <section>
          <h3>Tasks</h3>
          {item.tasks.length === 0 && <div className="muted">No tasks yet. Run triage to propose them.</div>}
          <ol className="tasks">
            {item.tasks.map(t => {
              const run = runs[t.id];
              const canStart = t.status === AgentTaskStatus.Approved || t.status === AgentTaskStatus.AwaitingReview;
              return (
                <li key={t.id}>
                  <div className="task-head">
                    <strong>{t.title}</strong>
                    <span className={`badge t${t.status}`}>{AgentTaskStatusLabel[t.status]}</span>
                  </div>
                  <p className="muted">{t.description}</p>
                  <div className="actions">
                    {canStart && !run && <button onClick={() => startTask(t.id)} disabled={busy === t.id}>Start agent</button>}
                    {run && (
                      <>
                        <button onClick={() => setActiveTab(t.id)}>Open terminal</button>
                        <button onClick={() => stopRun(t.id)} className="danger" disabled={busy === t.id}>Stop</button>
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
        <section className="terminals">
          <div className="tabs">
            {Object.entries(runs).map(([taskId, run]) => {
              const task = item.tasks.find(t => t.id === taskId);
              return (
                <button
                  key={taskId}
                  onClick={() => setActiveTab(taskId)}
                  className={activeTab === taskId ? 'active' : ''}
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
