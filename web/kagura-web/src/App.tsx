import { BrowserRouter, Navigate, Route, Routes, useLocation } from 'react-router-dom';
import { SourcesPage } from '@/pages/SourcesPage';
import { WorkItemsPage } from '@/pages/WorkItemsPage';
import { WorkItemDetailPage } from '@/pages/WorkItemDetailPage';
import { TaskDetailPage } from '@/pages/TaskDetailPage';
import { AgentsPage } from '@/pages/AgentsPage';
import { PromptsPage } from '@/pages/PromptsPage';
import { SidebarInset, SidebarProvider, SidebarTrigger } from '@/components/ui/sidebar';
import { Separator } from '@/components/ui/separator';
import { TooltipProvider } from '@/components/ui/tooltip';
import { AppSidebar } from '@/components/AppSidebar';
import { AgentsChip } from '@/components/AgentsChip';
import { ThemeToggle } from '@/components/ThemeToggle';
import { SourcesProvider } from '@/contexts/SourcesContext';
import { ThemeProvider } from '@/contexts/ThemeContext';
import { AgentSessionsProvider } from '@/contexts/AgentSessionsContext';
import { SidebarAgentsProvider } from '@/contexts/SidebarAgentsContext';
import './App.css';

function AppMain() {
  const location = useLocation();
  const fullWidth = location.pathname.startsWith('/agents') || location.pathname.startsWith('/prompts');
  const mainClass = `flex flex-1 flex-col p-6 w-full min-h-0${fullWidth ? '' : ' max-w-[1400px]'}`;
  return (
    <main className={mainClass}>
      <Routes>
        <Route path="/" element={<Navigate to="/workitems" replace />} />
        <Route path="/sources" element={<SourcesPage />} />
        <Route path="/workitems" element={<WorkItemsPage />} />
        <Route path="/workitems/:id" element={<WorkItemDetailPage />} />
        <Route path="/workitems/:workItemId/tasks/:taskId" element={<TaskDetailPage />} />
        <Route path="/agents" element={<AgentsPage />} />
        <Route path="/prompts" element={<PromptsPage />} />
      </Routes>
    </main>
  );
}

export default function App() {
  return (
    <BrowserRouter>
      <ThemeProvider>
        <TooltipProvider>
          <SourcesProvider>
            <AgentSessionsProvider>
             <SidebarAgentsProvider>
              <SidebarProvider>
                <AppSidebar />
                <SidebarInset>
                <header className="flex h-14 shrink-0 items-center gap-2 border-b px-4">
                  <SidebarTrigger className="-ml-1" />
                  <Separator orientation="vertical" className="mr-2 h-4" />
                  <span className="text-sm text-muted-foreground">kagura orchestrator</span>
                  <div className="ml-auto flex items-center gap-2">
                    <AgentsChip />
                    <ThemeToggle />
                  </div>
                </header>
                <AppMain />
                </SidebarInset>
              </SidebarProvider>
             </SidebarAgentsProvider>
            </AgentSessionsProvider>
          </SourcesProvider>
        </TooltipProvider>
      </ThemeProvider>
    </BrowserRouter>
  );
}
