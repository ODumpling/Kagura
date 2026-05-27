export enum SourceType {
  Markdown = 0,
  GitHub = 1,
  AzureDevOps = 2,
  Beads = 3,
}

export enum WorkItemStatus {
  New = 0,
  Triaged = 1,
  InProgress = 2,
  Merged = 3,
  PullRequested = 4,
  Done = 5,
  Cancelled = 6,
  Closed = 7,
}

export enum AgentTaskStatus {
  Proposed = 0,
  Approved = 1,
  Running = 2,
  AwaitingReview = 3,
  Merged = 4,
  Failed = 5,
  Cancelled = 6,
}

export interface Source {
  id: string;
  name: string;
  type: SourceType;
  localRepoPath: string;
  config: Record<string, unknown>;
  enabled: boolean;
  lastSyncedAt: string | null;
  createdAt: string;
}

export interface UpsertSource {
  name: string;
  type: SourceType;
  localRepoPath: string;
  config: Record<string, unknown>;
  enabled: boolean;
}

export interface WorkItemSummary {
  id: string;
  sourceId: string;
  sourceName: string;
  externalId: string;
  title: string;
  status: WorkItemStatus;
  url: string | null;
  labels: string | null;
  branchName: string | null;
  pullRequestUrl: string | null;
  updatedAt: string;
  triagedAt: string | null;
  taskCount: number;
}

export interface AgentTaskDto {
  id: string;
  title: string;
  description: string;
  order: number;
  status: AgentTaskStatus;
  branchName: string | null;
  worktreePath: string | null;
  includeInPullRequest: boolean;
  reviewNotes: string | null;
}

export interface PreviewConflict {
  file: string;
  taskIds: string[];
}

export interface PreviewTaskSha {
  taskId: string;
  sha: string;
}

export interface PreviewStats {
  filesChanged: number;
  additions: number;
  deletions: number;
}

export interface WorkItemPreview {
  baseSha: string;
  headSha: string;
  unifiedDiff: string;
  conflicts: PreviewConflict[];
  taskShas: PreviewTaskSha[];
  stats: PreviewStats;
}

export interface AutoReviewItemResult {
  taskId: string;
  title: string;
  autoMerged: boolean;
  merged: boolean;
  reasoning: string;
}

export interface AutoReviewResult {
  reviewed: number;
  autoMerged: number;
  flaggedForHuman: number;
  items: AutoReviewItemResult[];
}

export interface WorkItemDetail extends WorkItemSummary {
  body: string;
  tasks: AgentTaskDto[];
}

export interface AgentRunDto {
  runId: string;
  taskId: string;
  workItemId: string;
  worktreePath: string;
  processId: number;
  startedAt: string;
  alive: boolean;
  exitCode: number | null;
}

export interface FinishWorkItemResult {
  id: string;
  status: WorkItemStatus;
  branchName: string | null;
  pullRequestUrl: string | null;
  merged: number;
  alreadyMerged: number;
  pullRequestError: string | null;
}

export const SourceTypeLabel: Record<SourceType, string> = {
  [SourceType.Markdown]: 'Markdown',
  [SourceType.GitHub]: 'GitHub',
  [SourceType.AzureDevOps]: 'Azure DevOps',
  [SourceType.Beads]: 'Beads',
};

export const WorkItemStatusLabel: Record<WorkItemStatus, string> = {
  [WorkItemStatus.New]: 'New',
  [WorkItemStatus.Triaged]: 'Triaged',
  [WorkItemStatus.InProgress]: 'In Progress',
  [WorkItemStatus.Merged]: 'Merged',
  [WorkItemStatus.PullRequested]: 'PR Open',
  [WorkItemStatus.Done]: 'Done',
  [WorkItemStatus.Cancelled]: 'Cancelled',
  [WorkItemStatus.Closed]: 'Closed',
};

export const AgentTaskStatusLabel: Record<AgentTaskStatus, string> = {
  [AgentTaskStatus.Proposed]: 'Proposed',
  [AgentTaskStatus.Approved]: 'Approved',
  [AgentTaskStatus.Running]: 'Running',
  [AgentTaskStatus.AwaitingReview]: 'Review',
  [AgentTaskStatus.Merged]: 'Merged',
  [AgentTaskStatus.Failed]: 'Failed',
  [AgentTaskStatus.Cancelled]: 'Cancelled',
};
