import { useNavigate, useLocation } from 'react-router-dom';
import { Bot, Sparkles, ScanSearch, ExternalLink } from 'lucide-react';
import { useAgentSessions } from '@/contexts/AgentSessionsContext';
import { AgentRunKind, AgentRunKindLabel, type AgentRunDto } from '@/types';
import { Button } from '@/components/ui/button';
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu';

function kindIcon(kind: AgentRunKind) {
  if (kind === AgentRunKind.Triage) return Sparkles;
  if (kind === AgentRunKind.AutoReview) return ScanSearch;
  return Bot;
}

function currentWorkItemIdFromPath(pathname: string): string | null {
  const m = pathname.match(/^\/workitems\/([^/]+)/);
  return m ? m[1] : null;
}

export function AgentsChip() {
  const { active, requestModal } = useAgentSessions();
  const navigate = useNavigate();
  const location = useLocation();
  const sameWiId = currentWorkItemIdFromPath(location.pathname);

  function smartOpen(run: AgentRunDto) {
    if (sameWiId && run.workItemId === sameWiId) {
      requestModal(run);
    } else {
      navigate(`/agents?run=${run.runId}`);
    }
  }

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="outline" size="sm" className="h-7 gap-1.5">
          <Bot className="size-3.5" />
          <span className="text-xs">Agents</span>
          {active.length > 0 && (
            <span className="rounded-full bg-primary text-primary-foreground text-[10px] px-1.5 py-0.5 leading-none">
              {active.length}
            </span>
          )}
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-72">
        {active.length === 0 && (
          <DropdownMenuItem disabled className="text-xs text-muted-foreground">
            No active sessions
          </DropdownMenuItem>
        )}
        {active.map((s) => {
          const Icon = kindIcon(s.run.kind);
          return (
            <DropdownMenuItem key={s.run.runId} onClick={() => smartOpen(s.run)} className="gap-2">
              <Icon className="size-3.5 shrink-0 text-muted-foreground" />
              <div className="flex flex-col min-w-0">
                <span className="text-xs truncate">
                  {AgentRunKindLabel[s.run.kind]}: {s.run.title || s.run.runId.slice(0, 8)}
                </span>
                {s.run.workItemExternalId && (
                  <span className="text-[10px] text-muted-foreground truncate">
                    {s.run.workItemExternalId}
                  </span>
                )}
              </div>
              <span
                className={`ml-auto size-2 rounded-full shrink-0 ${
                  s.status === 'live' ? 'bg-green-500' : 'bg-amber-500 animate-pulse'
                }`}
              />
            </DropdownMenuItem>
          );
        })}
        <DropdownMenuSeparator />
        <DropdownMenuItem onClick={() => navigate('/agents')} className="text-xs gap-1.5">
          <ExternalLink className="size-3.5" />
          View all →
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
