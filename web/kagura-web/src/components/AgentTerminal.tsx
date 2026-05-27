import { useEffect, useRef, useState } from 'react';
import { Terminal } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import '@xterm/xterm/css/xterm.css';
import { base64ToBytes, bytesToBase64, getConnection } from '../signalr';

interface Props {
  runId: string;
  onExit?: (code: number | null) => void;
}

export function AgentTerminal({ runId, onExit }: Props) {
  const containerRef = useRef<HTMLDivElement>(null);
  const termRef = useRef<Terminal | null>(null);
  const fitRef = useRef<FitAddon | null>(null);
  const [status, setStatus] = useState<'connecting' | 'live' | 'exited'>('connecting');

  useEffect(() => {
    if (!containerRef.current) return;

    const term = new Terminal({
      cursorBlink: true,
      fontFamily: 'Menlo, Monaco, "Courier New", monospace',
      fontSize: 13,
      theme: { background: '#0e1116', foreground: '#d8dee9' },
      convertEol: false,
    });
    const fit = new FitAddon();
    term.loadAddon(fit);
    term.open(containerRef.current);
    fit.fit();

    termRef.current = term;
    fitRef.current = fit;

    let unsubData = () => {};
    let unsubExit = () => {};
    let disposed = false;

    (async () => {
      const conn = await getConnection();
      if (disposed) return;

      const onData = (incomingRunId: string, b64: string) => {
        if (incomingRunId !== runId) return;
        term.write(base64ToBytes(b64));
      };
      const onExitEvt = (incomingRunId: string, code: number | null) => {
        if (incomingRunId !== runId) return;
        setStatus('exited');
        onExit?.(code);
      };
      conn.on('data', onData);
      conn.on('exit', onExitEvt);
      unsubData = () => conn.off('data', onData);
      unsubExit = () => conn.off('exit', onExitEvt);

      await conn.invoke('Join', runId);
      setStatus('live');

      term.onData((d) => {
        const bytes = new TextEncoder().encode(d);
        conn.invoke('Input', runId, bytesToBase64(bytes)).catch(() => {});
      });
      term.onResize(({ cols, rows }) => {
        conn.invoke('Resize', runId, cols, rows).catch(() => {});
      });

      const ro = new ResizeObserver(() => {
        try { fit.fit(); } catch { /* ignore */ }
      });
      ro.observe(containerRef.current!);
      (term as any)._kaguraRo = ro;
    })();

    return () => {
      disposed = true;
      unsubData();
      unsubExit();
      const ro = (term as any)._kaguraRo as ResizeObserver | undefined;
      ro?.disconnect();
      getConnection().then((c) => c.invoke('Leave', runId).catch(() => {})).catch(() => {});
      term.dispose();
      termRef.current = null;
      fitRef.current = null;
    };
  }, [runId, onExit]);

  const statusColor =
    status === 'live' ? 'text-green-500'
    : status === 'connecting' ? 'text-amber-500'
    : 'text-muted-foreground';
  const statusLabel =
    status === 'live' ? '● live'
    : status === 'connecting' ? '· connecting'
    : '○ exited';

  return (
    <div className="rounded-md border bg-[#0e1116] p-2">
      <div className={`text-[11px] px-2 py-1 ${statusColor}`}>{statusLabel}</div>
      <div ref={containerRef} className="h-[480px]" />
    </div>
  );
}
