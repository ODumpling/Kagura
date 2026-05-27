import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { api } from '../api';
import { type WorkItemSummary, WorkItemStatusLabel } from '../types';

export function WorkItemsPage() {
  const [items, setItems] = useState<WorkItemSummary[]>([]);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api.workItems.list().then(setItems).catch(e => setError(e.message));
  }, []);

  return (
    <div>
      <div className="page-header">
        <h2>Work items</h2>
      </div>
      {error && <div className="error">{error}</div>}

      <table className="table">
        <thead><tr><th>External ID</th><th>Title</th><th>Source</th><th>Status</th><th>Tasks</th><th>Updated</th></tr></thead>
        <tbody>
          {items.map(w => (
            <tr key={w.id}>
              <td><code>{w.externalId}</code></td>
              <td><Link to={`/workitems/${w.id}`}>{w.title}</Link></td>
              <td className="muted">{w.sourceName}</td>
              <td><span className={`badge s${w.status}`}>{WorkItemStatusLabel[w.status]}</span></td>
              <td>{w.taskCount}</td>
              <td className="muted">{new Date(w.updatedAt).toLocaleString()}</td>
            </tr>
          ))}
          {items.length === 0 && <tr><td colSpan={6} className="muted">No work items. Sync a source to import some.</td></tr>}
        </tbody>
      </table>
    </div>
  );
}
