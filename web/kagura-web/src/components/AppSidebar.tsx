import { NavLink, useLocation } from 'react-router-dom';
import { FileText, GitMerge, GitBranch, FolderGit2, ListTodo, RefreshCw, Plus, Settings, Bot } from 'lucide-react';
import { useAgentSessions } from '@/contexts/AgentSessionsContext';
import {
  Sidebar, SidebarContent, SidebarFooter, SidebarGroup, SidebarGroupContent, SidebarGroupLabel,
  SidebarHeader, SidebarMenu, SidebarMenuButton, SidebarMenuItem, SidebarSeparator,
} from '@/components/ui/sidebar';
import { Button } from '@/components/ui/button';
import { useSources } from '@/contexts/SourcesContext';
import { SourceType } from '@/types';
import { api } from '@/api';
import { useState } from 'react';

const sourceIcons: Record<SourceType, typeof FileText> = {
  [SourceType.Markdown]: FileText,
  [SourceType.GitHub]: GitMerge,
  [SourceType.AzureDevOps]: GitBranch,
  [SourceType.Beads]: FolderGit2,
};

export function AppSidebar() {
  const { sources, refresh } = useSources();
  const { active } = useAgentSessions();
  const location = useLocation();
  const [syncing, setSyncing] = useState(false);

  const currentSourceId = new URLSearchParams(location.search).get('sourceId');

  async function syncAll() {
    setSyncing(true);
    try { await api.sources.syncAll(); await refresh(); }
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
                return (
                  <SidebarMenuItem key={s.id}>
                    <NavLink to={`/workitems?sourceId=${s.id}`}>
                      <SidebarMenuButton isActive={isActive} tooltip={s.name}>
                        <Icon />
                        <span className="truncate">{s.name}</span>
                        {!s.enabled && (
                          <span className="ml-auto text-[10px] text-muted-foreground">off</span>
                        )}
                      </SidebarMenuButton>
                    </NavLink>
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
