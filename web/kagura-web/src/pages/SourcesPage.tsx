import { useEffect, useState } from 'react';
import { api } from '../api';
import { type Source, type UpsertSource, SourceType, SourceTypeLabel } from '../types';

const blankSource = (): UpsertSource => ({
  name: '',
  type: SourceType.Markdown,
  localRepoPath: '',
  config: { issuesPath: '.devflow/issues' },
  enabled: true,
});

function defaultConfigFor(type: SourceType): Record<string, unknown> {
  switch (type) {
    case SourceType.Markdown: return { issuesPath: '.devflow/issues' };
    case SourceType.GitHub: return { owner: '', repo: '', token: '', labels: '' };
    case SourceType.AzureDevOps: return { organization: '', project: '', pat: '', query: '' };
    case SourceType.Beads: return { status: '' };
  }
}

export function SourcesPage() {
  const [sources, setSources] = useState<Source[]>([]);
  const [editing, setEditing] = useState<{ id?: string; draft: UpsertSource } | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function reload() {
    setSources(await api.sources.list());
  }

  useEffect(() => { reload().catch(e => setError(e.message)); }, []);

  async function save() {
    if (!editing) return;
    setError(null);
    try {
      if (editing.id) await api.sources.update(editing.id, editing.draft);
      else await api.sources.create(editing.draft);
      setEditing(null);
      await reload();
    } catch (e: any) { setError(e.message); }
  }

  async function syncOne(id: string) {
    setBusy(id); setError(null);
    try { await api.sources.sync(id); await reload(); }
    catch (e: any) { setError(e.message); }
    finally { setBusy(null); }
  }

  async function syncAll() {
    setBusy('all'); setError(null);
    try { await api.sources.syncAll(); await reload(); }
    catch (e: any) { setError(e.message); }
    finally { setBusy(null); }
  }

  async function remove(id: string) {
    if (!confirm('Delete this source? WorkItems are removed too.')) return;
    setError(null);
    try { await api.sources.remove(id); await reload(); }
    catch (e: any) { setError(e.message); }
  }

  return (
    <div>
      <div className="page-header">
        <h2>Sources</h2>
        <div className="actions">
          <button onClick={() => setEditing({ draft: blankSource() })}>+ Add source</button>
          <button onClick={syncAll} disabled={busy !== null}>Sync all</button>
        </div>
      </div>
      {error && <div className="error">{error}</div>}

      <table className="table">
        <thead><tr><th>Name</th><th>Type</th><th>Repo</th><th>Last synced</th><th></th></tr></thead>
        <tbody>
          {sources.map(s => (
            <tr key={s.id}>
              <td><strong>{s.name}</strong> {!s.enabled && <span className="muted">(disabled)</span>}</td>
              <td>{SourceTypeLabel[s.type]}</td>
              <td className="muted">{s.localRepoPath}</td>
              <td className="muted">{s.lastSyncedAt ? new Date(s.lastSyncedAt).toLocaleString() : '—'}</td>
              <td>
                <button onClick={() => syncOne(s.id)} disabled={busy === s.id}>Sync</button>{' '}
                <button onClick={() => setEditing({ id: s.id, draft: { name: s.name, type: s.type, localRepoPath: s.localRepoPath, config: s.config, enabled: s.enabled } })}>Edit</button>{' '}
                <button onClick={() => remove(s.id)} className="danger">Delete</button>
              </td>
            </tr>
          ))}
          {sources.length === 0 && <tr><td colSpan={5} className="muted">No sources yet.</td></tr>}
        </tbody>
      </table>

      {editing && (
        <div className="modal">
          <div className="modal-card">
            <h3>{editing.id ? 'Edit source' : 'New source'}</h3>
            <label>Name<input value={editing.draft.name} onChange={e => setEditing({ ...editing, draft: { ...editing.draft, name: e.target.value } })} /></label>
            <label>Type<select value={editing.draft.type} onChange={e => {
              const type = Number(e.target.value) as SourceType;
              setEditing({ ...editing, draft: { ...editing.draft, type, config: defaultConfigFor(type) } });
            }}>
              {Object.entries(SourceTypeLabel).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
            </select></label>
            <label>Local repo path<input value={editing.draft.localRepoPath} onChange={e => setEditing({ ...editing, draft: { ...editing.draft, localRepoPath: e.target.value } })} placeholder="/Users/you/Code/repo" /></label>
            <label>Config (JSON)<textarea rows={6} value={JSON.stringify(editing.draft.config, null, 2)} onChange={e => {
              try { setEditing({ ...editing, draft: { ...editing.draft, config: JSON.parse(e.target.value) } }); }
              catch { /* keep typing */ }
            }} /></label>
            <label className="checkbox"><input type="checkbox" checked={editing.draft.enabled} onChange={e => setEditing({ ...editing, draft: { ...editing.draft, enabled: e.target.checked } })} /> Enabled</label>
            <div className="actions">
              <button onClick={save}>Save</button>
              <button onClick={() => setEditing(null)}>Cancel</button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
