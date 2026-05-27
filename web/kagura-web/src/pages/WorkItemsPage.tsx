import { useEffect, useMemo, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { X } from 'lucide-react';
import { api } from '@/api';
import { type WorkItemSummary, WorkItemStatus, WorkItemStatusLabel } from '@/types';
import { useSources } from '@/contexts/SourcesContext';
import { Card, CardContent } from '@/components/ui/card';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';

const statusVariant: Record<WorkItemStatus, 'secondary' | 'default' | 'outline' | 'destructive'> = {
  [WorkItemStatus.New]: 'outline',
  [WorkItemStatus.Triaged]: 'secondary',
  [WorkItemStatus.InProgress]: 'default',
  [WorkItemStatus.Merged]: 'default',
  [WorkItemStatus.PullRequested]: 'default',
  [WorkItemStatus.Done]: 'default',
  [WorkItemStatus.Cancelled]: 'outline',
  [WorkItemStatus.Closed]: 'secondary',
};

export function WorkItemsPage() {
  const [items, setItems] = useState<WorkItemSummary[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [searchParams, setSearchParams] = useSearchParams();
  const sourceId = searchParams.get('sourceId') ?? undefined;
  const { sources } = useSources();

  const activeSource = useMemo(
    () => sources.find(s => s.id === sourceId),
    [sources, sourceId]
  );

  useEffect(() => {
    api.workItems.list(sourceId).then(setItems).catch(e => setError(e.message));
  }, [sourceId]);

  function clearFilter() {
    searchParams.delete('sourceId');
    setSearchParams(searchParams, { replace: true });
  }

  return (
    <div className="space-y-4">
      <div className="flex justify-between items-start">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">
            {activeSource ? activeSource.name : 'All work items'}
          </h1>
          <div className="flex items-center gap-2 mt-1">
            <p className="text-sm text-muted-foreground">
              {items.length} item{items.length === 1 ? '' : 's'}
              {activeSource && <span> in this source</span>}
            </p>
            {activeSource && (
              <Button variant="ghost" size="sm" className="h-6 text-xs" onClick={clearFilter}>
                <X /> Clear filter
              </Button>
            )}
          </div>
        </div>
      </div>

      {error && (
        <div className="rounded-md border border-destructive/40 bg-destructive/10 px-3 py-2 text-sm">
          {error}
        </div>
      )}

      <Card>
        <CardContent className="p-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>ID</TableHead>
                <TableHead>Title</TableHead>
                <TableHead>Source</TableHead>
                <TableHead>Status</TableHead>
                <TableHead className="text-right">Tasks</TableHead>
                <TableHead>Updated</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {items.map(w => (
                <TableRow key={w.id}>
                  <TableCell>
                    <code className="text-xs bg-muted px-1.5 py-0.5 rounded">{w.externalId}</code>
                  </TableCell>
                  <TableCell>
                    <Link to={`/workitems/${w.id}`} className="hover:underline font-medium">
                      {w.title}
                    </Link>
                  </TableCell>
                  <TableCell className="text-muted-foreground text-sm">{w.sourceName}</TableCell>
                  <TableCell>
                    <Badge variant={statusVariant[w.status]}>{WorkItemStatusLabel[w.status]}</Badge>
                  </TableCell>
                  <TableCell className="text-right tabular-nums">{w.taskCount}</TableCell>
                  <TableCell className="text-muted-foreground text-xs">
                    {new Date(w.updatedAt).toLocaleString()}
                  </TableCell>
                </TableRow>
              ))}
              {items.length === 0 && (
                <TableRow>
                  <TableCell colSpan={6} className="text-center text-muted-foreground py-8">
                    No work items. Sync a source to import some.
                  </TableCell>
                </TableRow>
              )}
            </TableBody>
          </Table>
        </CardContent>
      </Card>
    </div>
  );
}
