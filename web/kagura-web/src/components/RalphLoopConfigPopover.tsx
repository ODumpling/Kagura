import { useEffect, useState } from 'react';
import { Loader2 } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import { Switch } from '@/components/ui/switch';
import { Label } from '@/components/ui/label';

export interface RalphLoopConfig {
  autoApproveTriage: boolean;
  autoReviewEnabled: boolean;
}

interface Props {
  trigger: React.ReactNode;
  initial: RalphLoopConfig;
  mode: 'start' | 'apply';
  busy?: boolean;
  onSubmit: (config: RalphLoopConfig) => void | Promise<void>;
  open?: boolean;
  onOpenChange?: (open: boolean) => void;
}

export function RalphLoopConfigPopover({ trigger, initial, mode, busy, onSubmit, open, onOpenChange }: Props) {
  const [internalOpen, setInternalOpen] = useState(false);
  const isOpen = open ?? internalOpen;
  const setOpen = onOpenChange ?? setInternalOpen;
  const [autoApproveTriage, setAutoApproveTriage] = useState(initial.autoApproveTriage);
  const [autoReviewEnabled, setAutoReviewEnabled] = useState(initial.autoReviewEnabled);

  useEffect(() => {
    if (isOpen) {
      setAutoApproveTriage(initial.autoApproveTriage);
      setAutoReviewEnabled(initial.autoReviewEnabled);
    }
  }, [isOpen, initial.autoApproveTriage, initial.autoReviewEnabled]);

  async function handleSubmit() {
    await onSubmit({ autoApproveTriage, autoReviewEnabled });
    setOpen(false);
  }

  return (
    <Popover open={isOpen} onOpenChange={setOpen}>
      <PopoverTrigger asChild>{trigger}</PopoverTrigger>
      <PopoverContent className="w-80">
        <div className="flex flex-col gap-3">
          <div className="font-medium text-sm">
            {mode === 'start' ? 'Start Ralph Loop' : 'Ralph Loop settings'}
          </div>
          <div className="flex items-start justify-between gap-3">
            <div className="flex flex-col gap-0.5">
              <Label htmlFor="auto-approve-triage" className="text-sm">Auto-approve triage proposals</Label>
              <span className="text-xs text-muted-foreground">Skip the manual approval step after triage.</span>
            </div>
            <Switch
              id="auto-approve-triage"
              checked={autoApproveTriage}
              onCheckedChange={setAutoApproveTriage}
              disabled={busy}
            />
          </div>
          <div className="flex items-start justify-between gap-3">
            <div className="flex flex-col gap-0.5">
              <Label htmlFor="auto-review-enabled" className="text-sm">Auto-review tasks</Label>
              <span className="text-xs text-muted-foreground">Let Ralph run auto-review on completed tasks.</span>
            </div>
            <Switch
              id="auto-review-enabled"
              checked={autoReviewEnabled}
              onCheckedChange={setAutoReviewEnabled}
              disabled={busy}
            />
          </div>
          <div className="flex justify-end pt-1">
            <Button size="sm" onClick={handleSubmit} disabled={busy}>
              {busy && <Loader2 className="animate-spin" />}
              {mode === 'start' ? 'Start' : 'Apply'}
            </Button>
          </div>
        </div>
      </PopoverContent>
    </Popover>
  );
}
