import { useEffect, useState, type ReactNode } from 'react';
import { useSearchParams } from 'react-router-dom';
import { Plus, RefreshCw, Trash2 } from 'lucide-react';
import { api } from '@/api';
import { type UpsertSource, SourceType, SourceTypeLabel } from '@/types';
import { useSources } from '@/contexts/SourcesContext';
import { Button } from '@/components/ui/button';
import { Card, CardContent } from '@/components/ui/card';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import {
  Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle,
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
  config: defaultConfigFor(SourceType.Markdown),
  enabled: true,
});

function defaultConfigFor(type: SourceType): Record<string, unknown> {
  switch (type) {
    case SourceType.Markdown: return { issuesPath: '.devflow/issues' };
    case SourceType.GitHub: return { url: '', token: '', labels: '' };
    case SourceType.AzureDevOps: return { organization: '', project: '', pat: '', query: '' };
    case SourceType.Beads: return { status: '' };
  }
}

const sourceTypeDescriptions: Record<SourceType, string> = {
  [SourceType.Markdown]: 'Issues stored as .md files inside a local repo.',
  [SourceType.GitHub]: 'GitHub Issues from a repository via the REST API.',
  [SourceType.AzureDevOps]: 'Work items from an Azure DevOps project.',
  [SourceType.Beads]: 'Issues tracked via the local Beads CLI.',
};

function Field({ id, label, hint, children }: { id?: string; label: string; hint?: ReactNode; children: ReactNode }) {
  return (
    <div className="space-y-1.5">
      <Label htmlFor={id}>{label}</Label>
      {children}
      {hint && <p className="text-xs text-muted-foreground">{hint}</p>}
    </div>
  );
}

function ConfigFields({
  type,
  config,
  onChange,
}: {
  type: SourceType;
  config: Record<string, unknown>;
  onChange: (next: Record<string, unknown>) => void;
}) {
  const set = (key: string, value: unknown) => onChange({ ...config, [key]: value });
  const str = (key: string) => (config[key] as string) ?? '';

  switch (type) {
    case SourceType.Markdown:
      return (
        <Field
          id="cfg-issuesPath"
          label="Issues folder"
          hint="Folder of .md files, relative to the repo root (or an absolute path)."
        >
          <Input id="cfg-issuesPath" placeholder=".devflow/issues" value={str('issuesPath')}
            onChange={e => set('issuesPath', e.target.value)} />
        </Field>
      );
    case SourceType.GitHub:
      return (
        <>
          <Field id="cfg-url" label="Repository URL" hint="e.g. https://github.com/anthropics/claude-code">
            <Input id="cfg-url" placeholder="https://github.com/anthropics/claude-code" value={str('url')}
              onChange={e => set('url', e.target.value)} />
          </Field>
          <Field id="cfg-token" label="Personal access token" hint="Optional. Required for private repos.">
            <Input id="cfg-token" type="password" placeholder="ghp_…" autoComplete="off"
              value={str('token')} onChange={e => set('token', e.target.value)} />
          </Field>
          <Field id="cfg-labels" label="Labels" hint="Comma-separated. Leave blank to pull all issues.">
            <Input id="cfg-labels" placeholder="bug, triage" value={str('labels')}
              onChange={e => set('labels', e.target.value)} />
          </Field>
        </>
      );
    case SourceType.AzureDevOps:
      return (
        <>
          <div className="grid grid-cols-2 gap-3">
            <Field id="cfg-org" label="Organization">
              <Input id="cfg-org" placeholder="my-org" value={str('organization')}
                onChange={e => set('organization', e.target.value)} />
            </Field>
            <Field id="cfg-project" label="Project">
              <Input id="cfg-project" placeholder="my-project" value={str('project')}
                onChange={e => set('project', e.target.value)} />
            </Field>
          </div>
          <Field id="cfg-pat" label="Personal access token" hint="Optional. Required for private projects.">
            <Input id="cfg-pat" type="password" autoComplete="off"
              value={str('pat')} onChange={e => set('pat', e.target.value)} />
          </Field>
          <Field id="cfg-query" label="WIQL query" hint="Optional. Filters which work items are pulled.">
            <Textarea id="cfg-query" rows={3} className="font-mono text-xs"
              placeholder="SELECT [System.Id] FROM workitems WHERE [System.State] = 'Active'"
              value={str('query')} onChange={e => set('query', e.target.value)} />
          </Field>
        </>
      );
    case SourceType.Beads:
      return (
        <Field id="cfg-status" label="Status filter" hint="Optional. e.g. open, in-progress.">
          <Input id="cfg-status" placeholder="open" value={str('status')}
            onChange={e => set('status', e.target.value)} />
        </Field>
      );
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
            <DialogTitle>{editing?.id ? 'Edit source' : 'Add a new source'}</DialogTitle>
            <DialogDescription>
              Connect Kagura to a place where your issues live.
            </DialogDescription>
          </DialogHeader>
          {editing && (
            <div className="max-h-[60vh] space-y-4 overflow-y-auto pr-1">
              <Field
                id="type"
                label="Source type"
                hint={sourceTypeDescriptions[editing.draft.type]}
              >
                <Select
                  value={String(editing.draft.type)}
                  onValueChange={(v) => {
                    const type = Number(v) as SourceType;
                    setEditing({ ...editing, draft: { ...editing.draft, type, config: defaultConfigFor(type) } });
                  }}
                >
                  <SelectTrigger id="type"><SelectValue /></SelectTrigger>
                  <SelectContent>
                    {Object.entries(SourceTypeLabel).map(([k, v]) => (
                      <SelectItem key={k} value={k}>{v}</SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </Field>

              <Field id="name" label="Name" hint="A short label shown across Kagura.">
                <Input id="name" placeholder="my-repo issues" value={editing.draft.name}
                  onChange={e => setEditing({ ...editing, draft: { ...editing.draft, name: e.target.value } })} />
              </Field>

              <Field
                id="repo"
                label="Local repo path"
                hint="Absolute path to the working clone. Worktrees and agent runs use this directory."
              >
                <Input id="repo" placeholder="/Users/you/Code/repo" className="font-mono text-xs"
                  value={editing.draft.localRepoPath}
                  onChange={e => setEditing({ ...editing, draft: { ...editing.draft, localRepoPath: e.target.value } })} />
              </Field>

              <div className="rounded-md border bg-muted/30 p-3 space-y-3">
                <div className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
                  {SourceTypeLabel[editing.draft.type]} settings
                </div>
                <ConfigFields
                  type={editing.draft.type}
                  config={editing.draft.config}
                  onChange={(config) => setEditing({ ...editing, draft: { ...editing.draft, config } })}
                />
              </div>

              <div className="flex items-start justify-between gap-3 rounded-md border p-3">
                <div className="space-y-0.5">
                  <Label htmlFor="enabled" className="cursor-pointer">Enabled</Label>
                  <p className="text-xs text-muted-foreground">If off, this source is skipped during syncs.</p>
                </div>
                <Switch id="enabled" checked={editing.draft.enabled}
                  onCheckedChange={(checked) => setEditing({ ...editing, draft: { ...editing.draft, enabled: checked } })} />
              </div>
            </div>
          )}
          <DialogFooter>
            <Button variant="outline" onClick={() => setEditing(null)}>Cancel</Button>
            <Button onClick={save}>{editing?.id ? 'Save changes' : 'Add source'}</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
