import { Square, Loader2 } from 'lucide-react';
import { AgentRunKind, AgentRunKindLabel, type AgentRunDto } from '@/types';
import {
  Dialog,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { Button } from '@/components/ui/button';
import { AgentTerminal } from '@/components/AgentTerminal';

interface Props {
  run: AgentRunDto | null;
  busy: boolean;
  onClose: () => void;
  onStop?: (runId: string) => void;
}

export function AgentTerminalDialog({ run, busy, onClose, onStop }: Props) {
  const open = run !== null;
  const canStop = run?.alive && run.kind === AgentRunKind.TaskAgent && onStop !== undefined;

  return (
    <Dialog open={open} onOpenChange={(o) => { if (!o) onClose(); }}>
      <DialogContent className="sm:max-w-none w-[95vw] h-[95vh] max-w-[95vw] max-h-[95vh] flex flex-col">
        <DialogHeader className="shrink-0">
          <DialogTitle>
            {run ? `${AgentRunKindLabel[run.kind]} — ${run.title || run.runId.slice(0, 8)}` : 'Agent terminal'}
          </DialogTitle>
        </DialogHeader>
        {run && (
          <AgentTerminal key={run.runId} run={run} className="flex-1 min-h-0" />
        )}
        <DialogFooter className="shrink-0">
          {canStop && (
            <Button
              variant="ghost"
              className="text-destructive"
              onClick={() => onStop?.(run!.runId)}
              disabled={busy}
            >
              {busy ? <Loader2 className="animate-spin" /> : <Square />}
              Stop
            </Button>
          )}
          <Button variant="outline" onClick={onClose}>Close</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
