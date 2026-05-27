import { useEffect, useState } from 'react';
import { api } from '../api';
import { type Source, type UpsertSource, SourceType, SourceTypeLabel } from '../types';
import { btn, btnDanger, card, errorBox, input, label, muted, tableClass, tdClass, thClass } from '../ui';

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

  async function reload() { setSources(await api.sources.list()); }
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
      <div className="flex justify-between items-start mb-4">
        <h2 className="text-xl font-semibold m-0">Sources</h2>
        <div className="flex gap-2">
          <button className={btn} onClick={() => setEditing({ draft: blankSource() })}>+ Add source</button>
          <button className={btn} onClick={syncAll} disabled={busy !== null}>Sync all</button>
        </div>
      </div>

      {error && <div className={errorBox}>{error}</div>}

      <div className={`${card} overflow-hidden`}>
        <table className={tableClass}>
          <thead>
            <tr>
              <th className={thClass}>Name</th>
              <th className={thClass}>Type</th>
              <th className={thClass}>Repo</th>
              <th className={thClass}>Last synced</th>
              <th className={thClass}></th>
            </tr>
          </thead>
          <tbody>
            {sources.map(s => (
              <tr key={s.id}>
                <td className={tdClass}>
                  <strong>{s.name}</strong>{' '}
                  {!s.enabled && <span className={muted}>(disabled)</span>}
                </td>
                <td className={tdClass}>{SourceTypeLabel[s.type]}</td>
                <td className={`${tdClass} ${muted}`}>{s.localRepoPath}</td>
                <td className={`${tdClass} ${muted}`}>
                  {s.lastSyncedAt ? new Date(s.lastSyncedAt).toLocaleString() : '—'}
                </td>
                <td className={`${tdClass} space-x-2 whitespace-nowrap`}>
                  <button className={btn} onClick={() => syncOne(s.id)} disabled={busy === s.id}>Sync</button>
                  <button className={btn} onClick={() => setEditing({ id: s.id, draft: { name: s.name, type: s.type, localRepoPath: s.localRepoPath, config: s.config, enabled: s.enabled } })}>Edit</button>
                  <button className={btnDanger} onClick={() => remove(s.id)}>Delete</button>
                </td>
              </tr>
            ))}
            {sources.length === 0 && (
              <tr><td colSpan={5} className={`${tdClass} ${muted} text-center py-6`}>No sources yet.</td></tr>
            )}
          </tbody>
        </table>
      </div>

      {editing && (
        <div className="fixed inset-0 bg-black/60 flex items-center justify-center z-50">
          <div className={`${card} p-5 w-[520px] max-w-[90vw] max-h-[90vh] overflow-auto`}>
            <h3 className="text-lg font-semibold mt-0 mb-3">{editing.id ? 'Edit source' : 'New source'}</h3>

            <label className={label}>Name
              <input className={input} value={editing.draft.name}
                onChange={e => setEditing({ ...editing, draft: { ...editing.draft, name: e.target.value } })} />
            </label>

            <label className={label}>Type
              <select className={input} value={editing.draft.type}
                onChange={e => {
                  const type = Number(e.target.value) as SourceType;
                  setEditing({ ...editing, draft: { ...editing.draft, type, config: defaultConfigFor(type) } });
                }}>
                {Object.entries(SourceTypeLabel).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
              </select>
            </label>

            <label className={label}>Local repo path
              <input className={input} value={editing.draft.localRepoPath}
                onChange={e => setEditing({ ...editing, draft: { ...editing.draft, localRepoPath: e.target.value } })}
                placeholder="/Users/you/Code/repo" />
            </label>

            <label className={label}>Config (JSON)
              <textarea className={`${input} font-mono`} rows={6}
                value={JSON.stringify(editing.draft.config, null, 2)}
                onChange={e => {
                  try { setEditing({ ...editing, draft: { ...editing.draft, config: JSON.parse(e.target.value) } }); }
                  catch { /* keep typing */ }
                }} />
            </label>

            <label className="flex items-center gap-2 my-3 text-sm text-slate-300">
              <input type="checkbox" checked={editing.draft.enabled}
                onChange={e => setEditing({ ...editing, draft: { ...editing.draft, enabled: e.target.checked } })} />
              Enabled
            </label>

            <div className="flex gap-2 justify-end mt-4">
              <button className={btn} onClick={() => setEditing(null)}>Cancel</button>
              <button className={btn} onClick={save}>Save</button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
