import { AgentTaskStatus, WorkItemStatus } from './types';

export const btn =
  'inline-flex items-center gap-1 px-3 py-1.5 rounded border border-slate-700 bg-slate-800 text-slate-200 text-sm hover:bg-slate-700 hover:border-sky-500 disabled:opacity-50 disabled:cursor-not-allowed';

export const btnDanger = `${btn} text-red-400 hover:text-red-300 hover:border-red-500`;

export const btnTab = (active: boolean) =>
  `px-3 py-1 rounded text-xs border ${
    active
      ? 'bg-sky-600 text-white border-sky-600'
      : 'bg-slate-800 text-slate-300 border-slate-700 hover:bg-slate-700'
  }`;

export const input =
  'block w-full mt-1 px-2 py-1.5 rounded bg-slate-950 text-slate-200 border border-slate-700 focus:border-sky-500 focus:outline-none text-sm';

export const label = 'block my-3 text-xs text-slate-400';

export const card = 'rounded-md border border-slate-700 bg-slate-900';

export const muted = 'text-slate-400';

export const errorBox =
  'my-2 px-3 py-2 rounded bg-red-950/60 border border-red-800 text-red-200 text-sm';

export const tableClass = 'w-full border-collapse text-sm';
export const thClass = 'px-3 py-2 text-left text-slate-400 font-medium border-b border-slate-700';
export const tdClass = 'px-3 py-2 border-b border-slate-800';

const workItemBadgeMap: Record<WorkItemStatus, string> = {
  [WorkItemStatus.New]: 'text-slate-400 border-slate-700',
  [WorkItemStatus.Triaged]: 'text-sky-400 border-sky-500/50',
  [WorkItemStatus.InProgress]: 'text-amber-400 border-amber-500/50',
  [WorkItemStatus.Merged]: 'text-green-400 border-green-500/50',
  [WorkItemStatus.PullRequested]: 'text-purple-400 border-purple-500/50',
  [WorkItemStatus.Done]: 'text-green-500 border-green-500/50',
  [WorkItemStatus.Cancelled]: 'text-slate-500 border-slate-700',
};

const taskBadgeMap: Record<AgentTaskStatus, string> = {
  [AgentTaskStatus.Proposed]: 'text-slate-400 border-slate-700',
  [AgentTaskStatus.Approved]: 'text-sky-400 border-sky-500/50',
  [AgentTaskStatus.Running]: 'text-amber-400 border-amber-500/50',
  [AgentTaskStatus.AwaitingReview]: 'text-purple-400 border-purple-500/50',
  [AgentTaskStatus.Merged]: 'text-green-400 border-green-500/50',
  [AgentTaskStatus.Failed]: 'text-red-400 border-red-500/50',
  [AgentTaskStatus.Cancelled]: 'text-slate-500 border-slate-700',
};

const badgeBase = 'inline-block px-2 py-0.5 rounded-full text-[11px] border bg-slate-800/60';

export const workItemBadge = (s: WorkItemStatus) => `${badgeBase} ${workItemBadgeMap[s]}`;
export const taskBadge = (s: AgentTaskStatus) => `${badgeBase} ${taskBadgeMap[s]}`;
