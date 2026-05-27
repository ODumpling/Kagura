import { Square, Loader2 } from 'lucide-react';
import { type AgentRunDto, type AgentTaskDto } from '@/types';
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
  task: AgentTaskDto | null;
  run: AgentRunDto | null;
  busy: string | null;
  onClose: () => void;
  onStop: (taskId: string) => void;
}

export function AgentTerminalDialog({ task, run, busy, onClose, onStop }: Props) {
  const open = task !== null && run !== null;
  const taskBusy = task !== null && busy === task.id;

  return (
    <Dialog open={open} onOpenChange={(o) => { if (!o) onClose(); }}>
      <DialogContent className="sm:max-w-none w-[75vw] h-[75vh] flex flex-col">
        <DialogHeader className="shrink-0">
          <DialogTitle>
            {task ? `${task.order}. ${task.title}` : 'Agent terminal'}
          </DialogTitle>
        </DialogHeader>
        {run && (
          <AgentTerminal key={run.runId} runId={run.runId} className="flex-1 min-h-0" />
        )}
        <DialogFooter className="shrink-0">
          {task && (
            <Button
              variant="ghost"
              className="text-destructive"
              onClick={() => onStop(task.id)}
              disabled={taskBusy}
            >
              {taskBusy ? <Loader2 className="animate-spin" /> : <Square />}
              Stop
            </Button>
          )}
          <Button variant="outline" onClick={onClose}>Close</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
