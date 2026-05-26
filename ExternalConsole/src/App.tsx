import { useEffect, useRef, useState, type KeyboardEvent } from 'react';
import BenchmarkPanel from './BenchmarkPanel';

// ─── Backend contract ─────────────────────────────────────────────
// POST {BACKEND_URL}/api/ai/ask
//   Request:  { Message: string, ThreadId: string }
//   Response: { response: string, uiActions: unknown[],
//               terminatedByCircuitBreaker: boolean, iterations: number }
// GET  {BACKEND_URL}/api/ai/benchmark  → see BenchmarkPanel.tsx
// ─────────────────────────────────────────────────────────────────

interface AskRequest {
  Message: string;
  ThreadId: string;
}

interface AskResponse {
  response: string;
  uiActions: unknown[];
  terminatedByCircuitBreaker: boolean;
  iterations: number;
}

type TurnRole = 'user' | 'assistant' | 'system';

interface ChatTurn {
  id: number;
  role: TurnRole;
  text: string;
  meta?: {
    iterations?: number;
    terminatedByCircuitBreaker?: boolean;
    uiActions?: unknown[];
  };
}

const THREAD_KEY = 'aleph.console.threadId';
const BACKEND_URL: string =
  (import.meta.env.VITE_BACKEND_URL as string | undefined) ?? 'http://localhost:5000';

function getOrCreateThreadId(): string {
  try {
    const existing = localStorage.getItem(THREAD_KEY);
    if (existing && existing.length > 0) return existing;
    const fresh = `console-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
    localStorage.setItem(THREAD_KEY, fresh);
    return fresh;
  } catch {
    return `console-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
  }
}

export default function App() {
  const [threadId] = useState<string>(getOrCreateThreadId);
  const [history, setHistory] = useState<ChatTurn[]>([]);
  const [input, setInput] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // Bumped after every chat exchange so the BenchmarkPanel refetches.
  // Starts at 1 so the panel does an initial sync on mount.
  const [benchmarkRefresh, setBenchmarkRefresh] = useState(1);
  const nextIdRef = useRef(1);
  const scrollRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const el = scrollRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [history, loading]);

  function appendTurn(turn: Omit<ChatTurn, 'id'>) {
    setHistory((h) => [...h, { id: nextIdRef.current++, ...turn }]);
  }

  async function send() {
    const message = input.trim();
    if (!message || loading) return;

    setInput('');
    setError(null);
    setLoading(true);
    appendTurn({ role: 'user', text: message });

    try {
      const body: AskRequest = { Message: message, ThreadId: threadId };
      const res = await fetch(`${BACKEND_URL}/api/ai/ask`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      });

      if (!res.ok) {
        const text = await res.text().catch(() => '');
        throw new Error(`Backend returned HTTP ${res.status}${text ? `: ${text}` : ''}`);
      }

      const data = (await res.json()) as AskResponse;

      // eslint-disable-next-line no-console
      console.log('[Aleph console] response payload:', data);
      // eslint-disable-next-line no-console
      console.log('[Aleph console] uiActions:', data.uiActions);
      // eslint-disable-next-line no-console
      console.log('[Aleph console] iterations:', data.iterations);
      // eslint-disable-next-line no-console
      console.log(
        '[Aleph console] terminatedByCircuitBreaker:',
        data.terminatedByCircuitBreaker,
      );

      appendTurn({
        role: 'assistant',
        text: data.response ?? '(empty response)',
        meta: {
          iterations: data.iterations,
          terminatedByCircuitBreaker: data.terminatedByCircuitBreaker,
          uiActions: data.uiActions,
        },
      });
    } catch (err) {
      const msg = err instanceof Error ? err.message : String(err);
      setError(`Request failed: ${msg}`);
      appendTurn({ role: 'system', text: `Request failed: ${msg}` });
    } finally {
      setLoading(false);
      // Trigger a benchmark refetch — happens for both success and failure
      // so the latency of failed exchanges is also visible.
      setBenchmarkRefresh((n) => n + 1);
    }
  }

  function onKeyDown(e: KeyboardEvent<HTMLTextAreaElement>) {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      void send();
    }
  }

  return (
    <div className="h-screen bg-slate-950 text-slate-100 flex flex-col">
      {/* ── Top bar ──────────────────────────────────────────────── */}
      <header className="border-b border-slate-800 px-4 py-3 shrink-0 flex items-baseline justify-between">
        <div>
          <h1 className="text-lg font-semibold">Aleph External Console</h1>
          <p className="text-xs text-slate-400 mt-0.5">
            Thread: <span className="font-mono text-slate-300">{threadId}</span>
            {' · '}
            Backend: <span className="font-mono text-slate-300">{BACKEND_URL}</span>
          </p>
        </div>
        <div className="text-[10px] font-mono text-slate-500 uppercase tracking-widest">
          PoC · Global Compute Arbitrage
        </div>
      </header>

      {/* ── 60/40 split body ─────────────────────────────────────── */}
      <div className="flex-1 min-h-0 grid grid-cols-1 md:grid-cols-5">
        {/* Left 60% — Chat */}
        <div className="md:col-span-3 flex flex-col min-h-0 border-r border-slate-800">
          <main ref={scrollRef} className="flex-1 overflow-y-auto px-4 py-4 space-y-3">
            {history.length === 0 && !loading && (
              <div className="text-slate-500 text-sm">
                Type a message and press Enter. The console will POST to{' '}
                <code className="font-mono text-slate-400">{BACKEND_URL}/api/ai/ask</code>.
              </div>
            )}

            {history.map((turn) => (
              <div
                key={turn.id}
                className={
                  turn.role === 'user'
                    ? 'flex justify-end'
                    : turn.role === 'system'
                      ? 'flex justify-center'
                      : 'flex justify-start'
                }
              >
                <div
                  className={
                    'max-w-2xl rounded-lg px-3 py-2 text-sm whitespace-pre-wrap break-words ' +
                    (turn.role === 'user'
                      ? 'bg-indigo-600 text-white'
                      : turn.role === 'system'
                        ? 'bg-red-900/40 text-red-200 border border-red-700/50'
                        : 'bg-slate-800 text-slate-100')
                  }
                >
                  <div className="text-[10px] uppercase tracking-wide opacity-60 mb-1">
                    {turn.role}
                  </div>
                  <div>{turn.text}</div>
                  {turn.meta && (
                    <div className="mt-2 text-[10px] opacity-60 space-y-0.5 font-mono">
                      <div>iterations: {turn.meta.iterations ?? '?'}</div>
                      <div>
                        circuitBreaker:{' '}
                        {String(turn.meta.terminatedByCircuitBreaker ?? false)}
                      </div>
                      <div>uiActions: {turn.meta.uiActions?.length ?? 0} item(s)</div>
                    </div>
                  )}
                </div>
              </div>
            ))}

            {loading && (
              <div className="flex justify-start">
                <div className="rounded-lg px-3 py-2 text-sm bg-slate-800 text-slate-400 italic">
                  thinking…
                </div>
              </div>
            )}
          </main>

          {error && (
            <div className="border-t border-red-800 bg-red-950/40 text-red-200 px-4 py-2 text-xs shrink-0">
              {error}
            </div>
          )}

          <footer className="border-t border-slate-800 p-3 flex gap-2 shrink-0">
            <textarea
              value={input}
              onChange={(e) => setInput(e.target.value)}
              onKeyDown={onKeyDown}
              rows={2}
              placeholder="Type a message… (Enter to send, Shift+Enter for newline)"
              disabled={loading}
              className="flex-1 bg-slate-900 border border-slate-700 rounded-md px-3 py-2 text-sm resize-none focus:outline-none focus:border-indigo-500 disabled:opacity-50"
            />
            <button
              onClick={() => void send()}
              disabled={loading || input.trim().length === 0}
              className="px-4 py-2 bg-indigo-600 hover:bg-indigo-500 disabled:bg-slate-700 disabled:text-slate-500 rounded-md text-sm font-medium"
            >
              {loading ? 'Sending…' : 'Send'}
            </button>
          </footer>
        </div>

        {/* Right 40% — Benchmark dashboard */}
        <div className="md:col-span-2 min-h-0">
          <BenchmarkPanel backendUrl={BACKEND_URL} refreshSignal={benchmarkRefresh} />
        </div>
      </div>
    </div>
  );
}
