import { useEffect, useState } from 'react';
import { RotateCcw } from 'lucide-react';
import { api } from '@/api';
import { Role, RoleLabel, RoleDescription, type RolePrompt, type Source } from '@/types';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import {
  Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle,
} from '@/components/ui/dialog';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { Textarea } from '@/components/ui/textarea';

/**
 * Source detail "Prompts" tab — one textarea per Role (Triage, Task, AutoReview, Grill,
 * MergeResolver). Per ADR 0002, the textarea shows the current text (override if any, else
 * the lazy built-in default) with a "Using default" badge when no override row exists, and
 * a "Reset to default" button that DELETEs the override.
 *
 * Built-in defaults are never written to the DB — the next save creates the override row,
 * a reset deletes it. The page stays in lockstep with the server's view of "is customised".
 */
const ROLE_ORDER: Role[] = [Role.Triage, Role.Task, Role.AutoReview, Role.Grill, Role.MergeResolver];

export function SourcePromptsDialog({
  source,
  open,
  onOpenChange,
}: {
  source: Source | null;
  open: boolean;
  onOpenChange: (open: boolean) => void;
}) {
  const [prompts, setPrompts] = useState<Map<Role, RolePrompt>>(new Map());
  // Pending edits keyed by role — separate from the server-truth map so users can edit
  // freely and only persist on Save.
  const [drafts, setDrafts] = useState<Map<Role, string>>(new Map());
  const [savingRole, setSavingRole] = useState<Role | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [activeRole, setActiveRole] = useState<Role>(Role.Triage);

  useEffect(() => {
    if (!open || !source) return;
    let cancelled = false;
    setError(null);
    setDrafts(new Map());
    api.sources.listPrompts(source.id)
      .then(rows => {
        if (cancelled) return;
        const map = new Map<Role, RolePrompt>();
        rows.forEach(r => map.set(r.role, r));
        setPrompts(map);
      })
      .catch((e: Error) => !cancelled && setError(e.message));
    return () => { cancelled = true; };
  }, [open, source]);

  if (!source) return null;

  const currentText = (role: Role) => drafts.get(role) ?? prompts.get(role)?.promptText ?? '';
  const isDirty = (role: Role) => drafts.has(role) && drafts.get(role) !== prompts.get(role)?.promptText;
  const isOverride = (role: Role) => prompts.get(role)?.isOverride ?? false;

  async function save(role: Role) {
    if (!source) return;
    const text = drafts.get(role);
    if (text === undefined) return;
    setSavingRole(role); setError(null);
    try {
      const updated = await api.sources.setPrompt(source.id, role, text);
      setPrompts(prev => {
        const next = new Map(prev);
        next.set(role, updated);
        return next;
      });
      setDrafts(prev => {
        const next = new Map(prev);
        next.delete(role);
        return next;
      });
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setSavingRole(null);
    }
  }

  async function reset(role: Role) {
    if (!source) return;
    if (!confirm(`Reset the ${RoleLabel[role]} prompt to the built-in default? Your customisation is removed.`)) return;
    setSavingRole(role); setError(null);
    try {
      const updated = await api.sources.resetPrompt(source.id, role);
      setPrompts(prev => {
        const next = new Map(prev);
        next.set(role, updated);
        return next;
      });
      setDrafts(prev => {
        const next = new Map(prev);
        next.delete(role);
        return next;
      });
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setSavingRole(null);
    }
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-[760px]">
        <DialogHeader>
          <DialogTitle>Prompts — {source.name}</DialogTitle>
          <DialogDescription>
            Customise the prompt each Role uses for this Source. Unchanged Roles use the
            current built-in default; resetting deletes the customisation.
          </DialogDescription>
        </DialogHeader>

        {error && (
          <div className="rounded-md border border-destructive/40 bg-destructive/10 px-3 py-2 text-sm text-destructive-foreground">
            {error}
          </div>
        )}

        <Tabs value={String(activeRole)} onValueChange={(v) => setActiveRole(Number(v) as Role)}>
          <TabsList className="w-full">
            {ROLE_ORDER.map(role => (
              <TabsTrigger key={role} value={String(role)}>
                {RoleLabel[role]}
                {isOverride(role) && <span className="ml-1.5 inline-block size-1.5 rounded-full bg-primary" />}
              </TabsTrigger>
            ))}
          </TabsList>

          {ROLE_ORDER.map(role => (
            <TabsContent key={role} value={String(role)} className="space-y-3 pt-4">
              <div className="flex items-start justify-between gap-3">
                <div>
                  <div className="flex items-center gap-2">
                    <h3 className="font-medium">{RoleLabel[role]}</h3>
                    {!isOverride(role) && (
                      <Badge variant="secondary" className="text-xs">Using default</Badge>
                    )}
                    {isOverride(role) && (
                      <Badge variant="outline" className="text-xs">Customised</Badge>
                    )}
                  </div>
                  <p className="text-xs text-muted-foreground mt-0.5">{RoleDescription[role]}</p>
                </div>
                <Button
                  variant="outline"
                  size="sm"
                  onClick={() => reset(role)}
                  disabled={!isOverride(role) || savingRole === role}
                  title={isOverride(role) ? 'Delete the override and fall back to the built-in default' : 'No override to reset'}
                >
                  <RotateCcw /> Reset to default
                </Button>
              </div>

              <Textarea
                rows={18}
                className="font-mono text-xs"
                value={currentText(role)}
                onChange={(e) => setDrafts(prev => {
                  const next = new Map(prev);
                  next.set(role, e.target.value);
                  return next;
                })}
              />

              <div className="flex items-center justify-between gap-2">
                <p className="text-xs text-muted-foreground">
                  Placeholders like <code>{'{{TITLE}}'}</code>, <code>{'{{BODY}}'}</code>, <code>{'{{SUBMIT_TOOL}}'}</code> are
                  interpolated at Agent spawn time.
                </p>
                <div className="flex gap-2">
                  {isDirty(role) && (
                    <Button
                      variant="ghost"
                      size="sm"
                      onClick={() => setDrafts(prev => {
                        const next = new Map(prev);
                        next.delete(role);
                        return next;
                      })}
                    >Cancel</Button>
                  )}
                  <Button
                    size="sm"
                    onClick={() => save(role)}
                    disabled={!isDirty(role) || savingRole === role}
                  >
                    {savingRole === role ? 'Saving…' : 'Save'}
                  </Button>
                </div>
              </div>
            </TabsContent>
          ))}
        </Tabs>

        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)}>Close</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
