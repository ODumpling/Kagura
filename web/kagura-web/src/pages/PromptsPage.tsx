import { useEffect, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { ChevronDown, ChevronRight, RotateCcw } from 'lucide-react';
import { api } from '@/api';
import { Role, RoleLabel, RoleDescription, type RolePrompt, type Source } from '@/types';
import { useSources } from '@/contexts/SourcesContext';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { ScrollArea } from '@/components/ui/scroll-area';
import { Textarea } from '@/components/ui/textarea';

const ROLE_ORDER: Role[] = [Role.Triage, Role.Task, Role.AutoReview, Role.Grill, Role.MergeResolver];

type Selection = { sourceId: string; role: Role };

export function PromptsPage() {
  const { sources } = useSources();
  const [searchParams, setSearchParams] = useSearchParams();

  const [promptsBySource, setPromptsBySource] = useState<Map<string, Map<Role, RolePrompt>>>(new Map());
  const [loadingSource, setLoadingSource] = useState<string | null>(null);
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [selection, setSelection] = useState<Selection | null>(null);
  const [draft, setDraft] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [resetting, setResetting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Restore selection from URL once sources are loaded.
  useEffect(() => {
    if (selection || sources.length === 0) return;
    const sourceId = searchParams.get('sourceId');
    const roleParam = searchParams.get('role');
    if (sourceId && roleParam && sources.some(s => s.id === sourceId)) {
      const role = Number(roleParam) as Role;
      if (ROLE_ORDER.includes(role)) {
        setSelection({ sourceId, role });
        setExpanded(prev => {
          const next = new Set(prev);
          next.add(sourceId);
          return next;
        });
        return;
      }
    }
    const first = sources[0];
    setSelection({ sourceId: first.id, role: Role.Triage });
    setExpanded(new Set([first.id]));
  }, [sources, selection, searchParams]);

  // Persist selection to URL.
  useEffect(() => {
    if (!selection) return;
    const next = new URLSearchParams(searchParams);
    next.set('sourceId', selection.sourceId);
    next.set('role', String(selection.role));
    setSearchParams(next, { replace: true });
  }, [selection]); // eslint-disable-line react-hooks/exhaustive-deps

  // Load prompts for each source the first time it's needed (expanded or selected).
  async function ensureLoaded(sourceId: string) {
    if (promptsBySource.has(sourceId) || loadingSource === sourceId) return;
    setLoadingSource(sourceId);
    try {
      const rows = await api.sources.listPrompts(sourceId);
      const map = new Map<Role, RolePrompt>();
      rows.forEach(r => map.set(r.role, r));
      setPromptsBySource(prev => {
        const next = new Map(prev);
        next.set(sourceId, map);
        return next;
      });
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoadingSource(null);
    }
  }

  // Load prompts for expanded sources and the selected source.
  useEffect(() => {
    expanded.forEach(id => { ensureLoaded(id); });
  }, [expanded]); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    if (selection) ensureLoaded(selection.sourceId);
  }, [selection]); // eslint-disable-line react-hooks/exhaustive-deps

  // Reset draft whenever selection changes so we always show the server value.
  useEffect(() => {
    setDraft(null);
    setError(null);
  }, [selection?.sourceId, selection?.role]);

  const selectedSource = useMemo<Source | null>(
    () => (selection ? sources.find(s => s.id === selection.sourceId) ?? null : null),
    [selection, sources],
  );
  const selectedPrompt = useMemo<RolePrompt | null>(() => {
    if (!selection) return null;
    return promptsBySource.get(selection.sourceId)?.get(selection.role) ?? null;
  }, [selection, promptsBySource]);

  const serverText = selectedPrompt?.promptText ?? '';
  const currentText = draft ?? serverText;
  const isDirty = draft !== null && draft !== serverText;
  const isOverride = selectedPrompt?.isOverride ?? false;
  const promptLoading = selection && !selectedPrompt && loadingSource === selection.sourceId;

  function toggleExpanded(sourceId: string) {
    setExpanded(prev => {
      const next = new Set(prev);
      if (next.has(sourceId)) next.delete(sourceId);
      else next.add(sourceId);
      return next;
    });
  }

  function select(sourceId: string, role: Role) {
    setSelection({ sourceId, role });
    setExpanded(prev => {
      const next = new Set(prev);
      next.add(sourceId);
      return next;
    });
  }

  async function save() {
    if (!selection || draft === null) return;
    setSaving(true);
    setError(null);
    try {
      const updated = await api.sources.setPrompt(selection.sourceId, selection.role, draft);
      setPromptsBySource(prev => {
        const next = new Map(prev);
        const inner = new Map(next.get(selection.sourceId) ?? new Map());
        inner.set(selection.role, updated);
        next.set(selection.sourceId, inner);
        return next;
      });
      setDraft(null);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setSaving(false);
    }
  }

  async function resetToDefault() {
    if (!selection || !selectedPrompt?.isOverride) return;
    if (!confirm(`Reset the ${RoleLabel[selection.role]} prompt to the built-in default? Your customisation is removed.`)) return;
    setResetting(true);
    setError(null);
    try {
      const updated = await api.sources.resetPrompt(selection.sourceId, selection.role);
      setPromptsBySource(prev => {
        const next = new Map(prev);
        const inner = new Map(next.get(selection.sourceId) ?? new Map());
        inner.set(selection.role, updated);
        next.set(selection.sourceId, inner);
        return next;
      });
      setDraft(null);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setResetting(false);
    }
  }

  return (
    <div className="flex flex-1 min-h-0 flex-col gap-4">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Prompts</h1>
        <p className="text-sm text-muted-foreground">
          Per-Source overrides for the prompt each Role uses. Unchanged Roles use the current built-in default.
        </p>
      </div>

      {error && (
        <div className="rounded-md border border-destructive/40 bg-destructive/10 px-3 py-2 text-sm text-destructive-foreground">
          {error}
        </div>
      )}

      <div className="flex flex-1 min-h-0 gap-4">
        <aside className="w-[320px] shrink-0 rounded-lg border bg-card flex flex-col min-h-0">
          <div className="px-3 py-2 border-b text-xs uppercase tracking-wider text-muted-foreground">
            Sources {sources.length > 0 && <span className="ml-1 text-foreground">({sources.length})</span>}
          </div>
          <ScrollArea className="flex-1">
            {sources.length === 0 && (
              <div className="px-3 py-6 text-sm text-muted-foreground text-center">
                No sources yet.
              </div>
            )}
            <ul className="divide-y">
              {sources.map(s => {
                const isExpanded = expanded.has(s.id);
                const promptMap = promptsBySource.get(s.id);
                const overrideCount = promptMap
                  ? Array.from(promptMap.values()).filter(p => p.isOverride).length
                  : 0;
                return (
                  <li key={s.id}>
                    <button
                      type="button"
                      onClick={() => toggleExpanded(s.id)}
                      className="w-full flex items-center gap-2 px-3 py-2 text-left hover:bg-muted/40"
                    >
                      {isExpanded
                        ? <ChevronDown className="size-3.5 shrink-0 text-muted-foreground" />
                        : <ChevronRight className="size-3.5 shrink-0 text-muted-foreground" />}
                      <span className="text-sm font-medium truncate flex-1" title={s.name}>{s.name}</span>
                      {overrideCount > 0 && (
                        <Badge variant="secondary" className="text-[10px] h-4 px-1.5">
                          {overrideCount} custom
                        </Badge>
                      )}
                    </button>
                    {isExpanded && (
                      <ul className="pb-1">
                        {ROLE_ORDER.map(role => {
                          const prompt = promptMap?.get(role);
                          const isSelected = selection?.sourceId === s.id && selection.role === role;
                          const customised = prompt?.isOverride;
                          return (
                            <li key={role}>
                              <button
                                type="button"
                                onClick={() => select(s.id, role)}
                                className={`w-full flex items-center gap-2 pl-9 pr-3 py-1.5 text-left text-sm hover:bg-muted/40 ${isSelected ? 'bg-muted/60' : ''}`}
                              >
                                <span className="truncate flex-1">{RoleLabel[role]}</span>
                                {customised && (
                                  <span className="size-1.5 rounded-full bg-primary shrink-0" title="Customised" />
                                )}
                              </button>
                            </li>
                          );
                        })}
                      </ul>
                    )}
                  </li>
                );
              })}
            </ul>
          </ScrollArea>
        </aside>

        <section className="flex-1 min-w-0 min-h-0 flex flex-col rounded-lg border bg-card">
          {!selectedSource || !selection ? (
            <div className="flex-1 grid place-items-center text-sm text-muted-foreground">
              Select a prompt from the list.
            </div>
          ) : (
            <>
              <div className="px-4 py-3 border-b flex items-start justify-between gap-3">
                <div className="min-w-0">
                  <div className="flex items-center gap-2 flex-wrap">
                    <h2 className="text-sm font-semibold">
                      {selectedSource.name}
                      <span className="text-muted-foreground"> · </span>
                      {RoleLabel[selection.role]}
                    </h2>
                    {isOverride
                      ? <Badge variant="outline" className="text-xs">Customised</Badge>
                      : <Badge variant="secondary" className="text-xs">Using default</Badge>}
                  </div>
                  <p className="text-xs text-muted-foreground mt-0.5">{RoleDescription[selection.role]}</p>
                </div>
                <Button
                  variant="outline"
                  size="sm"
                  onClick={resetToDefault}
                  disabled={!isOverride || resetting || saving}
                  title={isOverride ? 'Delete the override and fall back to the built-in default' : 'No override to reset'}
                >
                  <RotateCcw /> Reset to default
                </Button>
              </div>

              <div className="flex-1 min-h-0 p-4 flex flex-col gap-3">
                {promptLoading ? (
                  <div className="flex-1 grid place-items-center text-sm text-muted-foreground">
                    Loading…
                  </div>
                ) : (
                  <Textarea
                    className="flex-1 min-h-0 font-mono text-xs resize-none"
                    value={currentText}
                    onChange={(e) => setDraft(e.target.value)}
                  />
                )}

                <div className="flex items-center justify-between gap-2">
                  <p className="text-xs text-muted-foreground">
                    Placeholders like <code>{'{{TITLE}}'}</code>, <code>{'{{BODY}}'}</code>, <code>{'{{SUBMIT_TOOL}}'}</code> are
                    interpolated at Agent spawn time.
                  </p>
                  <div className="flex gap-2">
                    {isDirty && (
                      <Button variant="ghost" size="sm" onClick={() => setDraft(null)} disabled={saving}>
                        Cancel
                      </Button>
                    )}
                    <Button size="sm" onClick={save} disabled={!isDirty || saving}>
                      {saving ? 'Saving…' : 'Save'}
                    </Button>
                  </div>
                </div>
              </div>
            </>
          )}
        </section>
      </div>
    </div>
  );
}
