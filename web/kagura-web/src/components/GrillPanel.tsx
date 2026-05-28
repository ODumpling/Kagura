import { useCallback, useEffect, useRef, useState } from 'react';
import { Bot, Loader2, MessageCircle, RotateCcw, Send, Sparkles, User } from 'lucide-react';
import { api } from '@/api';
import { GrillStatus, WorkItemCommentRole, type GrillState, type WorkItemComment } from '@/types';
import { Markdown } from '@/components/Markdown';
import { Button } from '@/components/ui/button';
import { Card, CardContent } from '@/components/ui/card';
import { Textarea } from '@/components/ui/textarea';
import { cn } from '@/lib/utils';

interface GrillPanelProps {
  workItemId: string;
  workItemTitle: string;
  status: GrillStatus;
  onChanged?: () => void;
  disabled?: boolean;
  disabledReason?: string;
}

export function GrillPanel({ workItemId, status, onChanged, disabled, disabledReason }: GrillPanelProps) {
  const [state, setState] = useState<GrillState | null>(null);
  const [draft, setDraft] = useState('');
  const [busy, setBusy] = useState<'load' | 'send' | 'start' | 'finalize' | 'reset' | null>(null);
  const [error, setError] = useState<string | null>(null);
  const scrollRef = useRef<HTMLDivElement>(null);

  const reload = useCallback(async () => {
    setBusy('load');
    try {
      const s = await api.grill.get(workItemId);
      setState(s);
    } catch (e: any) {
      setError(e.message);
    } finally {
      setBusy(null);
    }
  }, [workItemId]);

  useEffect(() => { reload(); }, [reload]);

  useEffect(() => {
    if (scrollRef.current) scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
  }, [state?.comments.length]);

  async function appendComments(next: WorkItemComment[]) {
    setState(prev => prev ? { ...prev, comments: [...prev.comments, ...next], status: GrillStatus.Active } : prev);
  }

  async function send() {
    if (!draft.trim() || disabled) return;
    const content = draft.trim();
    setDraft('');
    setBusy('send'); setError(null);
    try {
      const replies = await api.grill.postComment(workItemId, content);
      await appendComments(replies);
      onChanged?.();
    } catch (e: any) {
      setError(e.message);
      setDraft(content);
    } finally {
      setBusy(null);
    }
  }

  async function start() {
    if (disabled) return;
    setBusy('start'); setError(null);
    try {
      const reply = await api.grill.start(workItemId);
      await appendComments([reply]);
      onChanged?.();
    } catch (e: any) {
      setError(e.message);
    } finally {
      setBusy(null);
    }
  }

  async function finalize() {
    setBusy('finalize'); setError(null);
    try {
      const result = await api.grill.finalize(workItemId);
      setState(prev => prev ? { ...prev, status: result.status, originalBody: result.originalBody } : prev);
      onChanged?.();
    } catch (e: any) {
      setError(e.message);
    } finally {
      setBusy(null);
    }
  }

  async function reset() {
    if (!confirm('Reset the grill conversation? This deletes all comments and restores the original body if it was finalized.')) return;
    setBusy('reset'); setError(null);
    try {
      await api.grill.reset(workItemId);
      setState({ workItemId, status: GrillStatus.None, originalBody: null, comments: [] });
      onChanged?.();
    } catch (e: any) {
      setError(e.message);
    } finally {
      setBusy(null);
    }
  }

  const isFinalized = status === GrillStatus.Finalized || state?.status === GrillStatus.Finalized;
  const inputDisabled = disabled || isFinalized || busy === 'send' || busy === 'finalize';
  const hasComments = (state?.comments.length ?? 0) > 0;

  return (
    <Card className="flex flex-1 flex-col min-h-0">
      <CardContent className="flex flex-1 flex-col min-h-0 pt-6 gap-3">
        {disabled && disabledReason && (
          <div className="rounded-md border border-muted px-3 py-2 text-xs text-muted-foreground">{disabledReason}</div>
        )}

        {error && (
          <div className="rounded-md border border-destructive/40 bg-destructive/10 px-3 py-2 text-sm">{error}</div>
        )}

        {isFinalized && (
          <div className="rounded-md border border-green-500/40 bg-green-500/10 px-3 py-2 text-xs flex items-center gap-2">
            <Sparkles className="size-3.5" />
            <span>Grill finalized — the work item body has been rewritten from this conversation.</span>
          </div>
        )}

        <div ref={scrollRef} className="flex-1 min-h-0 overflow-y-auto rounded-md border bg-muted/20 p-3 space-y-3">
          {!hasComments && busy !== 'load' && (
            <div className="flex flex-col items-center justify-center h-full text-center gap-3 py-8">
              <MessageCircle className="size-8 text-muted-foreground" />
              <div className="space-y-1">
                <div className="text-sm font-medium">Grill this work item</div>
                <div className="text-xs text-muted-foreground max-w-sm">
                  Claude will interview you one question at a time — each with a recommended answer —
                  until the work item is fleshed out. Finalize when you're done to replace the body.
                </div>
              </div>
              <Button onClick={start} disabled={inputDisabled || busy === 'start'}>
                {busy === 'start' ? <Loader2 className="animate-spin" /> : <Sparkles />}
                {busy === 'start' ? 'Starting…' : 'Start grilling'}
              </Button>
            </div>
          )}
          {state?.comments.map(c => (
            <CommentBubble key={c.id} comment={c} />
          ))}
          {busy === 'send' && (
            <div className="flex items-center gap-2 text-xs text-muted-foreground">
              <Loader2 className="size-3.5 animate-spin" /> Claude is thinking…
            </div>
          )}
        </div>

        <div className="flex flex-col gap-2">
          <Textarea
            value={draft}
            onChange={e => setDraft(e.target.value)}
            placeholder={isFinalized ? 'Grill finalized.' : 'Reply to the interviewer…'}
            rows={3}
            disabled={inputDisabled || !hasComments}
            onKeyDown={e => {
              if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) {
                e.preventDefault();
                send();
              }
            }}
          />
          <div className="flex items-center justify-between gap-2">
            <div className="text-[11px] text-muted-foreground">⌘/Ctrl + Enter to send</div>
            <div className="flex gap-2">
              {hasComments && !isFinalized && (
                <Button variant="outline" size="sm" onClick={reset} disabled={busy !== null}>
                  {busy === 'reset' ? <Loader2 className="animate-spin" /> : <RotateCcw />}
                  Reset
                </Button>
              )}
              {hasComments && !isFinalized && (
                <Button variant="outline" size="sm" onClick={finalize} disabled={busy !== null}>
                  {busy === 'finalize' ? <Loader2 className="animate-spin" /> : <Sparkles />}
                  {busy === 'finalize' ? 'Finalizing…' : 'Finalize → body'}
                </Button>
              )}
              {hasComments && (
                <Button size="sm" onClick={send} disabled={inputDisabled || !draft.trim()}>
                  {busy === 'send' ? <Loader2 className="animate-spin" /> : <Send />}
                  Send
                </Button>
              )}
            </div>
          </div>
        </div>
      </CardContent>
    </Card>
  );
}

function CommentBubble({ comment }: { comment: WorkItemComment }) {
  const isAssistant = comment.role === WorkItemCommentRole.Assistant;
  return (
    <div className={cn('flex gap-2', isAssistant ? 'flex-row' : 'flex-row-reverse')}>
      <div className={cn(
        'shrink-0 size-7 rounded-full grid place-items-center text-xs',
        isAssistant ? 'bg-primary/10 text-primary' : 'bg-muted text-foreground',
      )}>
        {isAssistant ? <Bot className="size-4" /> : <User className="size-4" />}
      </div>
      <div className={cn(
        'rounded-lg border px-3 py-2 max-w-[85%]',
        isAssistant ? 'bg-background' : 'bg-primary/5 border-primary/20',
      )}>
        <Markdown>{comment.content}</Markdown>
      </div>
    </div>
  );
}
