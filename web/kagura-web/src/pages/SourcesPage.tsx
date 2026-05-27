import { useEffect, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { Plus, RefreshCw, Trash2 } from 'lucide-react';
import { api } from '@/api';
import { type UpsertSource, SourceType, SourceTypeLabel } from '@/types';
import { useSources } from '@/contexts/SourcesContext';
import { Button } from '@/components/ui/button';
import { Card, CardContent } from '@/components/ui/card';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import {
  Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle,
} from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Switch } from '@/components/ui/switch';
import { Textarea } from '@/components/ui/textarea';

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
  const { sources, refresh } = useSources();
  const [searchParams, setSearchParams] = useSearchParams();
  const [editing, setEditing] = useState<{ id?: string; draft: UpsertSource } | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (searchParams.get('new') === '1' && !editing) {
      setEditing({ draft: blankSource() });
      searchParams.delete('new');
      setSearchParams(searchParams, { replace: true });
    }
  }, [searchParams, setSearchParams, editing]);

  async function save() {
    if (!editing) return;
    setError(null);
    try {
      if (editing.id) await api.sources.update(editing.id, editing.draft);
      else await api.sources.create(editing.draft);
      setEditing(null);
      await refresh();
    } catch (e: any) { setError(e.message); }
  }

  async function syncOne(id: string) {
    setBusy(id); setError(null);
    try { await api.sources.sync(id); await refresh(); }
    catch (e: any) { setError(e.message); }
    finally { setBusy(null); }
  }

  async function remove(id: string) {
    if (!confirm('Delete this source? WorkItems are removed too.')) return;
    setError(null);
    try { await api.sources.remove(id); await refresh(); }
    catch (e: any) { setError(e.message); }
  }

  return (
    <div className="space-y-4">
      <div className="flex justify-between items-start">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Sources</h1>
          <p className="text-sm text-muted-foreground">Trackers and repos Kagura pulls issues from.</p>
        </div>
        <Button onClick={() => setEditing({ draft: blankSource() })}>
          <Plus /> Add source
        </Button>
      </div>

      {error && (
        <div className="rounded-md border border-destructive/40 bg-destructive/10 px-3 py-2 text-sm text-destructive-foreground">
          {error}
        </div>
      )}

      <Card>
        <CardContent className="p-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Type</TableHead>
                <TableHead>Repo</TableHead>
                <TableHead>Last synced</TableHead>
                <TableHead className="text-right">Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {sources.map(s => (
                <TableRow key={s.id}>
                  <TableCell>
                    <div className="font-medium">{s.name}</div>
                    {!s.enabled && <span className="text-xs text-muted-foreground">disabled</span>}
                  </TableCell>
                  <TableCell>{SourceTypeLabel[s.type]}</TableCell>
                  <TableCell className="text-muted-foreground font-mono text-xs">{s.localRepoPath}</TableCell>
                  <TableCell className="text-muted-foreground text-xs">
                    {s.lastSyncedAt ? new Date(s.lastSyncedAt).toLocaleString() : '—'}
                  </TableCell>
                  <TableCell className="text-right space-x-2 whitespace-nowrap">
                    <Button variant="outline" size="sm" onClick={() => syncOne(s.id)} disabled={busy === s.id}>
                      <RefreshCw className={busy === s.id ? 'animate-spin' : ''} /> Sync
                    </Button>
                    <Button variant="outline" size="sm" onClick={() => setEditing({
                      id: s.id,
                      draft: { name: s.name, type: s.type, localRepoPath: s.localRepoPath, config: s.config, enabled: s.enabled },
                    })}>Edit</Button>
                    <Button variant="ghost" size="sm" onClick={() => remove(s.id)} className="text-destructive">
                      <Trash2 />
                    </Button>
                  </TableCell>
                </TableRow>
              ))}
              {sources.length === 0 && (
                <TableRow>
                  <TableCell colSpan={5} className="text-center text-muted-foreground py-8">
                    No sources yet.
                  </TableCell>
                </TableRow>
              )}
            </TableBody>
          </Table>
        </CardContent>
      </Card>

      <Dialog open={!!editing} onOpenChange={(open) => !open && setEditing(null)}>
        <DialogContent className="sm:max-w-[560px]">
          <DialogHeader>
            <DialogTitle>{editing?.id ? 'Edit source' : 'New source'}</DialogTitle>
          </DialogHeader>
          {editing && (
            <div className="space-y-3">
              <div>
                <Label htmlFor="name">Name</Label>
                <Input id="name" value={editing.draft.name}
                  onChange={e => setEditing({ ...editing, draft: { ...editing.draft, name: e.target.value } })} />
              </div>
              <div>
                <Label>Type</Label>
                <Select
                  value={String(editing.draft.type)}
                  onValueChange={(v) => {
                    const type = Number(v) as SourceType;
                    setEditing({ ...editing, draft: { ...editing.draft, type, config: defaultConfigFor(type) } });
                  }}
                >
                  <SelectTrigger><SelectValue /></SelectTrigger>
                  <SelectContent>
                    {Object.entries(SourceTypeLabel).map(([k, v]) => (
                      <SelectItem key={k} value={k}>{v}</SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>
              <div>
                <Label htmlFor="repo">Local repo path</Label>
                <Input id="repo" placeholder="/Users/you/Code/repo" value={editing.draft.localRepoPath}
                  onChange={e => setEditing({ ...editing, draft: { ...editing.draft, localRepoPath: e.target.value } })} />
              </div>
              <div>
                <Label htmlFor="config">Config (JSON)</Label>
                <Textarea id="config" rows={6} className="font-mono text-xs"
                  value={JSON.stringify(editing.draft.config, null, 2)}
                  onChange={e => {
                    try { setEditing({ ...editing, draft: { ...editing.draft, config: JSON.parse(e.target.value) } }); }
                    catch { /* keep typing */ }
                  }} />
              </div>
              <div className="flex items-center gap-2">
                <Switch id="enabled" checked={editing.draft.enabled}
                  onCheckedChange={(checked) => setEditing({ ...editing, draft: { ...editing.draft, enabled: checked } })} />
                <Label htmlFor="enabled">Enabled</Label>
              </div>
            </div>
          )}
          <DialogFooter>
            <Button variant="outline" onClick={() => setEditing(null)}>Cancel</Button>
            <Button onClick={save}>Save</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
