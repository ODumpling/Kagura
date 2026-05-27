import type {
  Source, UpsertSource, WorkItemSummary, WorkItemDetail, AgentTaskDto, AgentRunDto,
} from './types';

const API = import.meta.env.VITE_API ?? 'http://localhost:5050';

async function http<T>(method: string, path: string, body?: unknown): Promise<T> {
  const res = await fetch(`${API}${path}`, {
    method,
    headers: body ? { 'content-type': 'application/json' } : undefined,
    body: body ? JSON.stringify(body) : undefined,
  });
  if (!res.ok) {
    const text = await res.text();
    throw new Error(`${method} ${path} → ${res.status}: ${text}`);
  }
  if (res.status === 204) return undefined as T;
  return await res.json();
}

export const api = {
  hubUrl: () => `${API}/hubs/agent`,

  sources: {
    list: () => http<Source[]>('GET', '/api/sources'),
    get: (id: string) => http<Source>('GET', `/api/sources/${id}`),
    create: (s: UpsertSource) => http<Source>('POST', '/api/sources', s),
    update: (id: string, s: UpsertSource) => http<Source>('PUT', `/api/sources/${id}`, s),
    remove: (id: string) => http<void>('DELETE', `/api/sources/${id}`),
    sync: (id: string) => http<{ added: number; updated: number; total: number }>('POST', `/api/sources/${id}/sync`),
    syncAll: () => http<unknown[]>('POST', '/api/sources/sync-all'),
  },

  workItems: {
    list: (sourceId?: string) => http<WorkItemSummary[]>('GET', `/api/workitems${sourceId ? `?sourceId=${sourceId}` : ''}`),
    get: (id: string) => http<WorkItemDetail>('GET', `/api/workitems/${id}`),
    triage: (id: string) => http<{ workItemId: string; taskCount: number; tasks: AgentTaskDto[] }>('POST', `/api/workitems/${id}/triage`),
    approve: (id: string) => http<unknown>('POST', `/api/workitems/${id}/triage/approve`),
    approveTask: (workItemId: string, taskId: string) =>
      http<AgentTaskDto>('POST', `/api/workitems/${workItemId}/tasks/${taskId}/approve`),
    updateTask: (workItemId: string, taskId: string, body: { title: string; description: string; order: number }) =>
      http<AgentTaskDto>('PUT', `/api/workitems/${workItemId}/tasks/${taskId}`, body),
    deleteTask: (workItemId: string, taskId: string) =>
      http<void>('DELETE', `/api/workitems/${workItemId}/tasks/${taskId}`),
  },

  agents: {
    listActive: () => http<AgentRunDto[]>('GET', '/api/agents'),
    start: (taskId: string) => http<AgentRunDto>('POST', `/api/agents/start/${taskId}`),
    stop: (runId: string) => http<void>('POST', `/api/agents/${runId}/stop`),
  },
};
