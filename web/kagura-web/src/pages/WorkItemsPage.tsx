import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { api } from '../api';
import { type WorkItemSummary, WorkItemStatusLabel } from '../types';
import { card, errorBox, muted, tableClass, tdClass, thClass, workItemBadge } from '../ui';

export function WorkItemsPage() {
  const [items, setItems] = useState<WorkItemSummary[]>([]);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api.workItems.list().then(setItems).catch(e => setError(e.message));
  }, []);

  return (
    <div>
      <div className="flex justify-between items-start mb-4">
        <h2 className="text-xl font-semibold m-0">Work items</h2>
      </div>

      {error && <div className={errorBox}>{error}</div>}

      <div className={`${card} overflow-hidden`}>
        <table className={tableClass}>
          <thead>
            <tr>
              <th className={thClass}>External ID</th>
              <th className={thClass}>Title</th>
              <th className={thClass}>Source</th>
              <th className={thClass}>Status</th>
              <th className={thClass}>Tasks</th>
              <th className={thClass}>Updated</th>
            </tr>
          </thead>
          <tbody>
            {items.map(w => (
              <tr key={w.id}>
                <td className={tdClass}>
                  <code className="px-1.5 py-0.5 rounded bg-slate-800 text-xs">{w.externalId}</code>
                </td>
                <td className={tdClass}>
                  <Link to={`/workitems/${w.id}`} className="text-sky-400 hover:text-sky-300 hover:underline">
                    {w.title}
                  </Link>
                </td>
                <td className={`${tdClass} ${muted}`}>{w.sourceName}</td>
                <td className={tdClass}>
                  <span className={workItemBadge(w.status)}>{WorkItemStatusLabel[w.status]}</span>
                </td>
                <td className={tdClass}>{w.taskCount}</td>
                <td className={`${tdClass} ${muted}`}>{new Date(w.updatedAt).toLocaleString()}</td>
              </tr>
            ))}
            {items.length === 0 && (
              <tr><td colSpan={6} className={`${tdClass} ${muted} text-center py-6`}>
                No work items. Sync a source to import some.
              </td></tr>
            )}
          </tbody>
        </table>
      </div>
    </div>
  );
}
