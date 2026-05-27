import { Check, GitMerge, Loader2, X } from 'lucide-react';
import { type AgentTaskDto, AgentTaskStatus, AgentTaskStatusLabel } from '@/types';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { ScrollArea } from '@/components/ui/scroll-area';
import { Markdown } from '@/components/Markdown';

const statusVariant: Record<AgentTaskStatus, 'secondary' | 'default' | 'outline' | 'destructive'> = {
  [AgentTaskStatus.Proposed]: 'outline',
  [AgentTaskStatus.Approved]: 'secondary',
  [AgentTaskStatus.Running]: 'default',
  [AgentTaskStatus.AwaitingReview]: 'secondary',
  [AgentTaskStatus.Merged]: 'default',
  [AgentTaskStatus.Failed]: 'destructive',
  [AgentTaskStatus.Cancelled]: 'outline',
};

interface Props {
  task: AgentTaskDto | null;
  busy: string | null;
  onClose: () => void;
  onApprove: (taskId: string) => void;
  onReject: (taskId: string) => void;
  onMerge: (taskId: string) => void;
}

export function TaskReviewDialog({ task, busy, onClose, onApprove, onReject, onMerge }: Props) {
  const open = task !== null;
  const isProposed = task?.status === AgentTaskStatus.Proposed;
  const isAwaitingReview = task?.status === AgentTaskStatus.AwaitingReview;
  const taskBusy = task !== null && busy === task.id;

  return (
    <Dialog open={open} onOpenChange={(v) => { if (!v) onClose(); }}>
      <DialogContent className="sm:max-w-2xl">
        {task && (
          <>
            <DialogHeader>
              <div className="flex items-center gap-2">
                <Badge variant="outline" className="text-xs">#{task.order}</Badge>
                <Badge variant={statusVariant[task.status]} className="text-xs">
                  {AgentTaskStatusLabel[task.status]}
                </Badge>
              </div>
              <DialogTitle className="pr-8">{task.title}</DialogTitle>
              {task.branchName && (
                <DialogDescription>
                  Branch <code className="rounded bg-muted px-1 py-0.5 text-xs">{task.branchName}</code>
                </DialogDescription>
              )}
            </DialogHeader>

            <ScrollArea className="max-h-[50vh] rounded-md border bg-muted/30 p-4">
              {task.description
                ? <Markdown>{task.description}</Markdown>
                : <p className="text-xs italic text-muted-foreground">(no description)</p>}
            </ScrollArea>

            {task.reviewNotes && (
              <div className="rounded-md border border-amber-500/40 bg-amber-500/5 p-3 text-sm">
                <div className="mb-1 text-xs font-semibold uppercase tracking-wider text-amber-600 dark:text-amber-400">
                  Auto-review notes
                </div>
                <p className="text-muted-foreground">{task.reviewNotes}</p>
              </div>
            )}

            <DialogFooter showCloseButton={!isProposed && !isAwaitingReview}>
              {isProposed && (
                <>
                  <Button
                    variant="outline"
                    onClick={() => onReject(task.id)}
                    disabled={taskBusy}
                  >
                    {taskBusy ? <Loader2 className="animate-spin" /> : <X />}
                    Reject
                  </Button>
                  <Button
                    onClick={() => onApprove(task.id)}
                    disabled={taskBusy}
                  >
                    {taskBusy ? <Loader2 className="animate-spin" /> : <Check />}
                    Approve
                  </Button>
                </>
              )}
              {isAwaitingReview && (
                <Button onClick={() => onMerge(task.id)} disabled={taskBusy}>
                  {taskBusy ? <Loader2 className="animate-spin" /> : <GitMerge />}
                  Merge into work item branch
                </Button>
              )}
            </DialogFooter>
          </>
        )}
      </DialogContent>
    </Dialog>
  );
}
