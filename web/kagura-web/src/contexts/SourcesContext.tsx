import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react';
import { api } from '@/api';
import type { Source } from '@/types';

interface SourcesContextValue {
  sources: Source[];
  loading: boolean;
  error: string | null;
  refresh: () => Promise<void>;
  syncVersion: number;
  markSynced: () => void;
}

const SourcesContext = createContext<SourcesContextValue | null>(null);

export function SourcesProvider({ children }: { children: ReactNode }) {
  const [sources, setSources] = useState<Source[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [syncVersion, setSyncVersion] = useState(0);

  const refresh = useCallback(async () => {
    setLoading(true);
    try {
      setSources(await api.sources.list());
      setError(null);
    } catch (e: any) {
      setError(e.message);
    } finally {
      setLoading(false);
    }
  }, []);

  const markSynced = useCallback(() => setSyncVersion(v => v + 1), []);

  useEffect(() => { refresh(); }, [refresh]);

  return (
    <SourcesContext.Provider value={{ sources, loading, error, refresh, syncVersion, markSynced }}>
      {children}
    </SourcesContext.Provider>
  );
}

export function useSources() {
  const ctx = useContext(SourcesContext);
  if (!ctx) throw new Error('useSources must be used inside SourcesProvider');
  return ctx;
}
