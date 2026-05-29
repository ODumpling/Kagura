import { useCallback, useEffect, useState } from 'react';
import { HelpCircle, Loader2 } from 'lucide-react';
import { api } from '@/api';
import { getConnection } from '@/signalr';
import type { ReviewPrompt } from '@/types';
import { Button } from '@/components/ui/button';
import { Card, CardContent } from '@/components/ui/card';

interface Props {
  workItemId: string;
}

export function AutoReviewPromptPanel({ workItemId }: Props) {
  const [prompts, setPrompts] = useState<ReviewPrompt[]>([]);
  const [busyPromptId, setBusyPromptId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const reload = useCallback(async () => {
    try {
      const next = await api.reviewPrompts.list(workItemId);
      setPrompts(next);
    } catch (e: any) {
      setError(e.message);
    }
  }, [workItemId]);

  useEffect(() => { reload(); }, [reload]);

  useEffect(() => {
    let cancelled = false;
    let unsub = () => {};
    (async () => {
      const conn = await getConnection();
      if (cancelled) return;
      const onRaised = (prompt: ReviewPrompt) => {
        if (prompt.workItemId !== workItemId) return;
        setPrompts(prev => prev.some(p => p.id === prompt.id) ? prev : [...prev, prompt]);
      };
      const onResolved = (resp: { promptId: string; workItemId: string }) => {
        if (resp.workItemId !== workItemId) return;
        setPrompts(prev => prev.filter(p => p.id !== resp.promptId));
      };
      conn.on('reviewPromptRaised', onRaised);
      conn.on('reviewPromptResolved', onResolved);
      unsub = () => {
        conn.off('reviewPromptRaised', onRaised);
        conn.off('reviewPromptResolved', onResolved);
      };
    })().catch(() => {});
    return () => { cancelled = true; unsub(); };
  }, [workItemId]);

  async function respond(prompt: ReviewPrompt, optionId: string) {
    setBusyPromptId(prompt.id);
    setError(null);
    try {
      await api.reviewPrompts.respond(workItemId, prompt.id, optionId);
      setPrompts(prev => prev.filter(p => p.id !== prompt.id));
    } catch (e: any) {
      setError(e.message);
    } finally {
      setBusyPromptId(null);
    }
  }

  if (prompts.length === 0 && !error) return null;

  return (
    <div className="flex flex-col gap-2">
      {error && (
        <div className="rounded-md border border-destructive/40 bg-destructive/10 px-3 py-2 text-sm">{error}</div>
      )}
      {prompts.map(prompt => (
        <Card key={prompt.id} className="border-amber-500/40 bg-amber-500/5">
          <CardContent className="pt-6 flex flex-col gap-3">
            <div className="flex items-start gap-2">
              <HelpCircle className="size-4 mt-0.5 text-amber-600 shrink-0" />
              <div className="flex-1">
                <div className="text-xs uppercase tracking-wider text-muted-foreground font-medium">
                  Auto-review needs input
                </div>
                <div className="mt-1 text-sm">{prompt.question}</div>
              </div>
            </div>
            <div className="flex flex-wrap gap-2 pl-6">
              {prompt.options.map(opt => (
                <Button
                  key={opt.id}
                  size="sm"
                  variant="outline"
                  disabled={busyPromptId !== null}
                  onClick={() => respond(prompt, opt.id)}
                  title={opt.description ?? undefined}
                  className="flex flex-col items-start h-auto py-2 min-w-32"
                >
                  <span className="font-medium flex items-center gap-1.5">
                    {busyPromptId === prompt.id ? <Loader2 className="size-3 animate-spin" /> : null}
                    {opt.label}
                  </span>
                  {opt.description && (
                    <span className="text-[11px] text-muted-foreground font-normal">{opt.description}</span>
                  )}
                </Button>
              ))}
            </div>
          </CardContent>
        </Card>
      ))}
    </div>
  );
}
