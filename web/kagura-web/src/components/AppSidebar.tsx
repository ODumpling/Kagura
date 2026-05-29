import { NavLink, useLocation } from 'react-router-dom';
import { FileText, GitMerge, GitBranch, FolderGit2, ListTodo, RefreshCw, Plus, Settings, Bot, X, AlertCircle, Loader2, Flame, ShieldCheck, GitPullRequestArrow, Wand2, MessageSquare } from 'lucide-react';
import { useAgentSessions } from '@/contexts/AgentSessionsContext';
import { useSidebarAgents, type SidebarAgent } from '@/contexts/SidebarAgentsContext';
import {
  Sidebar, SidebarContent, SidebarFooter, SidebarGroup, SidebarGroupContent, SidebarGroupLabel,
  SidebarHeader, SidebarMenu, SidebarMenuButton, SidebarMenuItem, SidebarMenuSub, SidebarMenuSubButton,
  SidebarMenuSubItem, SidebarSeparator,
} from '@/components/ui/sidebar';
import { Button } from '@/components/ui/button';
import { useSources } from '@/contexts/SourcesContext';
import { AgentRunKind, AgentRunKindLabel, SourceType, type AgentRunDto } from '@/types';
import { api } from '@/api';
import { useMemo, useState } from 'react';
import { AgentTerminalDialog } from '@/components/AgentTerminalDialog';

const sourceIcons: Record<SourceType, typeof FileText> = {
  [SourceType.Markdown]: FileText,
  [SourceType.GitHub]: GitMerge,
  [SourceType.AzureDevOps]: GitBranch,
  [SourceType.Beads]: FolderGit2,
};

export function AppSidebar() {
  const { sources, refresh, markSynced } = useSources();
  const { active } = useAgentSessions();
  const { bySource: agentsBySource, remove: removeAgent } = useSidebarAgents();
  const location = useLocation();
  const [syncing, setSyncing] = useState(false);

  const currentSourceId = new URLSearchParams(location.search).get('sourceId');

  async function syncAll() {
    setSyncing(true);
    try { await api.sources.syncAll(); await refresh(); markSynced(); }
    finally { setSyncing(false); }
  }

  return (
    <Sidebar collapsible="icon">
      <SidebarHeader>
        <div className="flex items-center gap-2 px-2 py-1">
          <div className="h-7 w-7 rounded-md bg-primary flex items-center justify-center text-primary-foreground font-bold">K</div>
          <span className="font-semibold text-lg group-data-[collapsible=icon]:hidden">Kagura</span>
        </div>
      </SidebarHeader>

      <SidebarContent>
        <SidebarGroup>
          <SidebarGroupLabel>Navigation</SidebarGroupLabel>
          <SidebarGroupContent>
            <SidebarMenu>
              <SidebarMenuItem>
                <NavLink to="/workitems" end>
                  {({ isActive }) => (
                    <SidebarMenuButton isActive={isActive} tooltip="All work items">
                      <ListTodo />
                      <span>Work items</span>
                    </SidebarMenuButton>
                  )}
                </NavLink>
              </SidebarMenuItem>
              <SidebarMenuItem>
                <NavLink to="/agents">
                  {({ isActive }) => (
                    <SidebarMenuButton isActive={isActive} tooltip="Agent sessions">
                      <Bot />
                      <span>Agents</span>
                      {active.length > 0 && (
                        <span className="ml-auto rounded-full bg-primary text-primary-foreground text-[10px] px-1.5 py-0.5 leading-none group-data-[collapsible=icon]:hidden">
                          {active.length}
                        </span>
                      )}
                    </SidebarMenuButton>
                  )}
                </NavLink>
              </SidebarMenuItem>
              <SidebarMenuItem>
                <NavLink to="/prompts">
                  {({ isActive }) => (
                    <SidebarMenuButton isActive={isActive} tooltip="Prompts">
                      <MessageSquare />
                      <span>Prompts</span>
                    </SidebarMenuButton>
                  )}
                </NavLink>
              </SidebarMenuItem>
              <SidebarMenuItem>
                <NavLink to="/sources">
                  {({ isActive }) => (
                    <SidebarMenuButton isActive={isActive} tooltip="Manage sources">
                      <Settings />
                      <span>Manage sources</span>
                    </SidebarMenuButton>
                  )}
                </NavLink>
              </SidebarMenuItem>
            </SidebarMenu>
          </SidebarGroupContent>
        </SidebarGroup>

        <SidebarSeparator />

        <SidebarGroup>
          <SidebarGroupLabel className="flex justify-between items-center">
            <span>Sources</span>
            <Button
              variant="ghost"
              size="icon"
              className="h-5 w-5 group-data-[collapsible=icon]:hidden"
              onClick={syncAll}
              disabled={syncing}
              title="Sync all sources"
            >
              <RefreshCw className={syncing ? 'animate-spin' : ''} />
            </Button>
          </SidebarGroupLabel>
          <SidebarGroupContent>
            <SidebarMenu>
              {sources.length === 0 && (
                <div className="px-2 py-1 text-xs text-muted-foreground group-data-[collapsible=icon]:hidden">
                  None yet
                </div>
              )}
              {sources.map(s => {
                const Icon = sourceIcons[s.type] ?? FileText;
                const isActive = currentSourceId === s.id;
                const agents = agentsBySource[s.id] ?? [];
                return (
                  <SidebarMenuItem key={s.id}>
                    <NavLink to={`/workitems?sourceId=${s.id}`}>
                      <SidebarMenuButton isActive={isActive} tooltip={s.name}>
                        <Icon />
                        <span className="truncate">{s.name}</span>
                        {agents.length > 0 && (
                          <span className="ml-auto rounded-full bg-primary/15 text-primary text-[10px] px-1.5 py-0.5 leading-none group-data-[collapsible=icon]:hidden">
                            {agents.length}
                          </span>
                        )}
                        {!s.enabled && (
                          <span className="ml-auto text-[10px] text-muted-foreground">off</span>
                        )}
                      </SidebarMenuButton>
                    </NavLink>
                    {agents.length > 0 && (
                      <SidebarMenuSub className="group-data-[collapsible=icon]:hidden">
                        {agents.map(a => (
                          <SidebarAgentNode key={a.runId} agent={a} onDismiss={() => removeAgent(a.runId)} />
                        ))}
                      </SidebarMenuSub>
                    )}
                  </SidebarMenuItem>
                );
              })}
            </SidebarMenu>
          </SidebarGroupContent>
        </SidebarGroup>
      </SidebarContent>

      <SidebarFooter>
        <SidebarMenu>
          <SidebarMenuItem>
            <NavLink to="/sources?new=1">
              <SidebarMenuButton tooltip="Add source">
                <Plus />
                <span>Add source</span>
              </SidebarMenuButton>
            </NavLink>
          </SidebarMenuItem>
        </SidebarMenu>
      </SidebarFooter>
    </Sidebar>
  );
}

// Per CONTEXT.md → "Role": each Agent's role has its own icon + accent colour in the
// sidebar so the live tree is scannable. Failure-state still wins (red AlertCircle) regardless
// of role; the per-role styling applies to the live indicator.
const roleStyle: Record<AgentRunKind, { Icon: typeof Loader2; className: string }> = {
  [AgentRunKind.TaskAgent]: { Icon: Loader2, className: 'animate-spin opacity-60' },
  [AgentRunKind.Triage]: { Icon: Wand2, className: 'text-blue-500' },
  [AgentRunKind.AutoReview]: { Icon: ShieldCheck, className: 'text-emerald-500' },
  [AgentRunKind.Grill]: { Icon: Flame, className: 'text-orange-500' },
  [AgentRunKind.MergeResolver]: { Icon: GitPullRequestArrow, className: 'text-purple-500' },
};

function SidebarAgentNode({ agent, onDismiss }: { agent: SidebarAgent; onDismiss: () => void }) {
  const failed = agent.lifecycle === 'failed';
  const label = AgentRunKindLabel[agent.kind] ?? 'Agent';
  const style = roleStyle[agent.kind] ?? roleStyle[AgentRunKind.TaskAgent];
  const Icon = failed ? AlertCircle : style.Icon;
  const iconClass = failed ? 'text-destructive' : style.className;
  const exitSuffix = failed && agent.exitCode !== null ? ` (exit ${agent.exitCode})` : '';
  const primary = agent.taskTitle ?? `${label}${agent.workItemExternalId ? ` #${agent.workItemExternalId}` : ''}`;
  // TaskAgent rows deep-link to the task detail page so users land on the focused
  // view (title, status, branch/worktree, live terminal). Other roles still drop
  // the user on the work item.
  const to = agent.kind === AgentRunKind.TaskAgent && agent.taskId
    ? `/workitems/${agent.workItemId}/tasks/${agent.taskId}?runId=${agent.runId}`
    : `/workitems/${agent.workItemId}?runId=${agent.runId}`;

  const { sessions, stop } = useAgentSessions();
  const [open, setOpen] = useState(false);
  const [stopping, setStopping] = useState(false);

  // Prefer the tracked AgentRunDto from AgentSessionsContext (full DTO with taskId,
  // worktreePath, etc.). Fall back to a synthesised DTO from the sidebar event so the
  // dialog still works during the brief window before listActive populates.
  const run: AgentRunDto = useMemo(() => {
    const tracked = sessions.find((s) => s.run.runId === agent.runId)?.run;
    if (tracked) return tracked;
    return {
      runId: agent.runId,
      taskId: '',
      workItemId: agent.workItemId,
      kind: agent.kind,
      title: agent.workItemTitle,
      workItemExternalId: agent.workItemExternalId,
      worktreePath: '',
      processId: 0,
      startedAt: agent.startedAt,
      alive: !failed,
      exitCode: agent.exitCode,
    };
  }, [sessions, agent, failed]);

  async function handleStop(runId: string) {
    setStopping(true);
    try { await stop(runId); }
    finally { setStopping(false); }
  }

  // TaskAgent rows deep-link to the task detail page (issue #84) so users land on
  // a focused view with branch/worktree + live terminal. All other roles open the
  // terminal dialog in place (per #85) since they don't have a dedicated page.
  const isTaskAgentRow = agent.kind === AgentRunKind.TaskAgent && !!agent.taskId;
  const tooltip = `${label} — ${agent.workItemTitle}${agent.taskTitle ? `\n${agent.taskTitle}` : ''}\n${agent.statusLine}${exitSuffix}`;

  return (
    <SidebarMenuSubItem>
      <SidebarMenuSubButton asChild title={tooltip}>
        {isTaskAgentRow ? (
          <NavLink to={to} className="flex-1 min-w-0">
            <Icon className={`h-3 w-3 shrink-0 ${iconClass}`} />
            <div className="flex flex-col items-start min-w-0 leading-tight">
              <span className="truncate text-[12px] max-w-[10rem]">{primary}</span>
              <span className="truncate text-[10px] text-muted-foreground max-w-[10rem]">
                {failed ? `failed${exitSuffix}` : agent.statusLine}
              </span>
            </div>
          </NavLink>
        ) : (
          <button
            type="button"
            onClick={(e) => { e.preventDefault(); e.stopPropagation(); setOpen(true); }}
            className="flex-1 min-w-0 text-left"
          >
            <Icon className={`h-3 w-3 shrink-0 ${iconClass}`} />
            <div className="flex flex-col items-start min-w-0 leading-tight">
              <span className="truncate text-[12px] max-w-[10rem]">{primary}</span>
              <span className="truncate text-[10px] text-muted-foreground max-w-[10rem]">
                {failed ? `failed${exitSuffix}` : agent.statusLine}
              </span>
            </div>
          </button>
        )}
      </SidebarMenuSubButton>
      {failed && (
        <button
          type="button"
          onClick={(e) => { e.preventDefault(); e.stopPropagation(); onDismiss(); }}
          className="absolute right-1 top-1 rounded p-0.5 text-muted-foreground hover:bg-accent"
          title="Dismiss"
        >
          <X className="h-3 w-3" />
        </button>
      )}
      <AgentTerminalDialog
        wide
        run={open ? run : null}
        busy={stopping}
        onClose={() => setOpen(false)}
        onStop={handleStop}
      />
    </SidebarMenuSubItem>
  );
}
