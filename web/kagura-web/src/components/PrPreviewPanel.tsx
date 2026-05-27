import { useCallback, useEffect, useRef, useState } from 'react';
import { ChevronDown, ChevronRight, GitPullRequest, Loader2, RefreshCw, AlertTriangle } from 'lucide-react';
import { Diff, Hunk, parseDiff, type FileData } from 'react-diff-view';
import 'react-diff-view/style/index.css';
import { api } from '@/api';
import type { WorkItemPreview } from '@/types';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';

const LINE_LIMIT = 1000;
const TOGGLE_DEBOUNCE_MS = 250;

interface Props {
  workItemId: string;
  selectedTaskIds: string[];
}

type DiffType = 'add' | 'delete' | 'modify' | 'rename' | 'copy';

function fileChangeCount(file: FileData): number {
  let n = 0;
  for (const h of file.hunks) n += h.changes.length;
  return n;
}

function shortSha(sha: string | null | undefined): string {
  if (!sha) return '—';
  return sha.length >= 7 ? sha.slice(0, 7) : sha;
}

export function PrPreviewPanel({ workItemId, selectedTaskIds }: Props) {
  const [expanded, setExpanded] = useState(false);
  const [viewType, setViewType] = useState<'unified' | 'split'>('unified');
  const [preview, setPreview] = useState<WorkItemPreview | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [showAllFiles, setShowAllFiles] = useState(false);
  const debounceRef = useRef<number | null>(null);
  const prevExpandedRef = useRef(false);
  const prevKeyRef = useRef('');
  const selectedTaskIdsRef = useRef(selectedTaskIds);
  const selectedKey = selectedTaskIds.slice().sort().join(',');

  useEffect(() => { selectedTaskIdsRef.current = selectedTaskIds; });

  const load = useCallback(async () => {
    setError(null);
    const ids = selectedTaskIdsRef.current;
    if (ids.length === 0) {
      setPreview(null);
      return;
    }
    setLoading(true);
    try {
      const result = await api.workItems.preview(workItemId, ids);
      setPreview(result);
      setShowAllFiles(false);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      setError(msg);
      setPreview(null);
    } finally {
      setLoading(false);
    }
  }, [workItemId]);

  useEffect(() => {
    const wasExpanded = prevExpandedRef.current;
    const wasKey = prevKeyRef.current;
    prevExpandedRef.current = expanded;
    prevKeyRef.current = selectedKey;
    if (!expanded) return;
    const justExpanded = !wasExpanded;
    const keyChanged = wasKey !== selectedKey;
    if (justExpanded) {
      load();
      return;
    }
    if (keyChanged) {
      if (debounceRef.current !== null) window.clearTimeout(debounceRef.current);
      debounceRef.current = window.setTimeout(() => { load(); }, TOGGLE_DEBOUNCE_MS);
    }
    return () => {
      if (debounceRef.current !== null) {
        window.clearTimeout(debounceRef.current);
        debounceRef.current = null;
      }
    };
  }, [expanded, selectedKey, load]);

  function toggleExpanded() {
    setExpanded(e => !e);
  }

  function handleRefresh() {
    if (debounceRef.current !== null) {
      window.clearTimeout(debounceRef.current);
      debounceRef.current = null;
    }
    load();
  }

  const allFiles: FileData[] = preview ? parseDiff(preview.unifiedDiff ?? '') : [];
  const visibleFiles: FileData[] = [];
  let runningLines = 0;
  for (const f of allFiles) {
    if (showAllFiles) { visibleFiles.push(f); continue; }
    if (runningLines >= LINE_LIMIT) break;
    visibleFiles.push(f);
    runningLines += fileChangeCount(f);
  }
  const hiddenFiles = allFiles.length - visibleFiles.length;

  const n = selectedTaskIds.length;
  const summaryLabel = n === 0
    ? 'Show preview (no tasks selected)'
    : `Show preview (${n} task${n === 1 ? '' : 's'} ready for review)`;

  return (
    <Card>
      <CardHeader className="flex flex-row items-center justify-between cursor-pointer select-none" onClick={toggleExpanded}>
        <div className="flex items-center gap-2">
          {expanded ? <ChevronDown className="size-4" /> : <ChevronRight className="size-4" />}
          <GitPullRequest className="size-4 text-muted-foreground" />
          <span className="text-sm font-medium">{summaryLabel}</span>
        </div>
        {expanded && preview && (
          <div className="flex items-center gap-3 text-xs text-muted-foreground" onClick={(e) => e.stopPropagation()}>
            <span>
              <span className="text-foreground">{preview.stats.filesChanged}</span> files ·{' '}
              <span className="text-emerald-600">+{preview.stats.additions}</span>{' '}
              <span className="text-red-600">−{preview.stats.deletions}</span>
            </span>
          </div>
        )}
      </CardHeader>

      {expanded && (
        <CardContent className="space-y-3">
          <div className="flex flex-wrap items-center justify-between gap-2 rounded-md border bg-muted/30 px-3 py-2 text-xs">
            <div className="flex items-center gap-3">
              <span className="text-muted-foreground">Base</span>
              <code className="rounded bg-background px-1.5 py-0.5">{shortSha(preview?.baseSha)}</code>
              <span className="text-muted-foreground">→ Head</span>
              <code className="rounded bg-background px-1.5 py-0.5">{shortSha(preview?.headSha)}</code>
            </div>
            <div className="flex items-center gap-2">
              <div className="flex rounded-md border bg-background overflow-hidden">
                <button
                  type="button"
                  className={`px-2 py-1 text-xs ${viewType === 'unified' ? 'bg-muted font-medium' : 'text-muted-foreground hover:text-foreground'}`}
                  onClick={() => setViewType('unified')}
                >
                  Unified
                </button>
                <button
                  type="button"
                  className={`px-2 py-1 text-xs ${viewType === 'split' ? 'bg-muted font-medium' : 'text-muted-foreground hover:text-foreground'}`}
                  onClick={() => setViewType('split')}
                >
                  Split
                </button>
              </div>
              <Button
                variant="outline"
                size="sm"
                className="h-7 px-2 text-xs"
                onClick={handleRefresh}
                disabled={loading || selectedTaskIds.length === 0}
              >
                {loading ? <Loader2 className="animate-spin" /> : <RefreshCw />}
                Refresh
              </Button>
            </div>
          </div>

          {selectedTaskIds.length === 0 ? (
            <div className="rounded-md border bg-muted/20 px-3 py-6 text-center text-sm text-muted-foreground">
              Select at least one task to preview
            </div>
          ) : error ? (
            <div className="rounded-md border border-destructive/40 bg-destructive/10 px-3 py-2 text-sm">{error}</div>
          ) : loading && !preview ? (
            <div className="flex items-center gap-2 rounded-md border bg-muted/20 px-3 py-6 text-sm text-muted-foreground">
              <Loader2 className="size-4 animate-spin" /> Computing prospective merge…
            </div>
          ) : preview ? (
            <>
              {preview.conflicts.length > 0 && (
                <div className="rounded-md border border-amber-500/50 bg-amber-500/10 px-3 py-2 text-xs">
                  <div className="flex items-center gap-1.5 font-medium">
                    <AlertTriangle className="size-3.5" />
                    {preview.conflicts.length} conflicted file{preview.conflicts.length === 1 ? '' : 's'}
                    <span className="text-muted-foreground font-normal">— conflict markers appear inline in the diff</span>
                  </div>
                  <ul className="mt-1 space-y-0.5 pl-5 list-disc">
                    {preview.conflicts.map(c => (
                      <li key={c.file}><code className="text-[11px]">{c.file}</code></li>
                    ))}
                  </ul>
                </div>
              )}

              {allFiles.length === 0 ? (
                <div className="rounded-md border bg-muted/20 px-3 py-6 text-center text-sm text-muted-foreground">
                  No changes
                </div>
              ) : (
                <div className="space-y-3">
                  {visibleFiles.map((file, i) => {
                    const path = file.newPath || file.oldPath || `file-${i}`;
                    return (
                      <div key={`${path}-${i}`} className="rounded-md border bg-background overflow-hidden">
                        <div className="border-b bg-muted/40 px-3 py-1.5 text-xs">
                          <code>{path}</code>
                          <Badge variant="outline" className="ml-2 h-5 px-1.5 text-[10px]">{file.type}</Badge>
                        </div>
                        <Diff
                          viewType={viewType}
                          diffType={file.type as DiffType}
                          hunks={file.hunks}
                        >
                          {hunks => hunks.map(h => <Hunk key={h.content} hunk={h} />)}
                        </Diff>
                      </div>
                    );
                  })}
                  {hiddenFiles > 0 && (
                    <Button variant="outline" size="sm" onClick={() => setShowAllFiles(true)} className="w-full">
                      Show remaining {hiddenFiles} file{hiddenFiles === 1 ? '' : 's'}
                    </Button>
                  )}
                </div>
              )}
            </>
          ) : null}
        </CardContent>
      )}
    </Card>
  );
}
