import type {
  Source, UpsertSource, WorkItemSummary, WorkItemDetail, AgentTaskDto, AgentRunDto,
  FinishWorkItemResult, WorkItemPreview, AutoReviewResult,
  GrillState, WorkItemComment, FinalizeGrillResult,
} from './types';

const API = import.meta.env.VITE_API ?? 'http://localhost:5253';

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
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
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
    list: (sourceId?: string, status?: number, includeClosed?: boolean) => {
      const params = new URLSearchParams();
      if (sourceId) params.set('sourceId', sourceId);
      if (status !== undefined) params.set('status', String(status));
      if (includeClosed) params.set('includeClosed', 'true');
      const qs = params.toString();
      return http<WorkItemSummary[]>('GET', `/api/workitems${qs ? `?${qs}` : ''}`);
    },
    get: (id: string) => http<WorkItemDetail>('GET', `/api/workitems/${id}`),
    triage: (id: string) => http<{ workItemId: string; taskCount: number; tasks: AgentTaskDto[] }>('POST', `/api/workitems/${id}/triage`),
    approve: (id: string) => http<unknown>('POST', `/api/workitems/${id}/triage/approve`),
    approveTask: (workItemId: string, taskId: string) =>
      http<AgentTaskDto>('POST', `/api/workitems/${workItemId}/tasks/${taskId}/approve`),
    updateTask: (workItemId: string, taskId: string, body: { title: string; description: string; order: number }) =>
      http<AgentTaskDto>('PUT', `/api/workitems/${workItemId}/tasks/${taskId}`, body),
    updateTaskStatus: (workItemId: string, taskId: string, status: number) =>
      http<AgentTaskDto>('PATCH', `/api/workitems/${workItemId}/tasks/${taskId}/status`, { status }),
    deleteTask: (workItemId: string, taskId: string) =>
      http<void>('DELETE', `/api/workitems/${workItemId}/tasks/${taskId}`),
    mergeTask: (workItemId: string, taskId: string) =>
      http<AgentTaskDto>('POST', `/api/workitems/${workItemId}/tasks/${taskId}/merge`),
    setIncludeInPullRequest: (workItemId: string, taskId: string, includeInPullRequest: boolean) =>
      http<AgentTaskDto>('PATCH', `/api/workitems/${workItemId}/tasks/${taskId}/include`, { includeInPullRequest }),
    preview: (workItemId: string, taskIds: string[]) => {
      const qs = taskIds.map(id => `taskIds=${encodeURIComponent(id)}`).join('&');
      return http<WorkItemPreview>('GET', `/api/workitems/${workItemId}/preview${qs ? `?${qs}` : ''}`);
    },
    finish: (id: string) =>
      http<FinishWorkItemResult>('POST', `/api/workitems/${id}/finish`),
    autoReview: (id: string) =>
      http<AutoReviewResult>('POST', `/api/workitems/${id}/auto-review`),
    ralphLoopStart: (id: string) =>
      http<void>('POST', `/api/workitems/${id}/ralph-loop`),
    ralphLoopCancel: (id: string) =>
      http<void>('POST', `/api/workitems/${id}/ralph-loop/cancel`),
  },

  grill: {
    get: (workItemId: string) =>
      http<GrillState>('GET', `/api/workitems/${workItemId}/grill`),
    start: (workItemId: string) =>
      http<WorkItemComment>('POST', `/api/workitems/${workItemId}/grill/start`),
    postComment: (workItemId: string, content: string) =>
      http<WorkItemComment[]>('POST', `/api/workitems/${workItemId}/grill/comments`, { content }),
    finalize: (workItemId: string) =>
      http<FinalizeGrillResult>('POST', `/api/workitems/${workItemId}/grill/finalize`),
    reset: (workItemId: string) =>
      http<void>('POST', `/api/workitems/${workItemId}/grill/reset`),
  },

  agents: {
    listActive: () => http<AgentRunDto[]>('GET', '/api/agents'),
    start: (taskId: string) => http<AgentRunDto>('POST', `/api/agents/start/${taskId}`),
    startAll: (workItemId: string) => http<{ queued: number }>('POST', `/api/agents/start-all/${workItemId}`),
    stop: (runId: string) => http<void>('POST', `/api/agents/${runId}/stop`),
    reset: (taskId: string) => http<void>('POST', `/api/agents/reset/${taskId}`),
  },
};
