import { useEffect, useState } from 'react';

// ─── Compute Arbitrage benchmark contract ────────────────────────
// GET {BACKEND_URL}/api/ai/benchmark
//   { totalRequests, averageLatencyMs, latestLatencyMs,
//     totalPromptTokens, totalCompletionTokens, totalTokens }
// ────────────────────────────────────────────────────────────────

export interface BenchmarkSnapshot {
  totalRequests: number;
  averageLatencyMs: number;
  latestLatencyMs: number;
  totalPromptTokens: number;
  totalCompletionTokens: number;
  totalTokens: number;
}

interface BenchmarkPanelProps {
  backendUrl: string;
  /** Increments after every successful chat exchange — triggers a refetch. */
  refreshSignal: number;
}

// Proxy vs official rates in USD per 1M tokens (PoC numbers).
const PROXY_RATE_USD_PER_M_TOKENS = 0.5;
const OPENAI_RATE_USD_PER_M_TOKENS = 2.5;

function fmtMs(ms: number): string {
  if (!Number.isFinite(ms) || ms <= 0) return '—';
  if (ms < 10) return ms.toFixed(2) + ' ms';
  if (ms < 1000) return ms.toFixed(1) + ' ms';
  return (ms / 1000).toFixed(2) + ' s';
}

function fmtInt(n: number): string {
  if (!Number.isFinite(n)) return '—';
  return Math.round(n).toLocaleString('en-US');
}

function fmtUsd(usd: number): string {
  if (!Number.isFinite(usd)) return '—';
  if (usd < 0.01) return '$' + usd.toFixed(6);
  if (usd < 1) return '$' + usd.toFixed(4);
  return '$' + usd.toFixed(2);
}

function costForTokens(tokens: number, ratePerMillion: number): number {
  return (tokens / 1_000_000) * ratePerMillion;
}

export default function BenchmarkPanel({ backendUrl, refreshSignal }: BenchmarkPanelProps) {
  const [snapshot, setSnapshot] = useState<BenchmarkSnapshot | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [lastFetchedAt, setLastFetchedAt] = useState<Date | null>(null);

  useEffect(() => {
    let cancelled = false;

    async function fetchSnapshot() {
      setLoading(true);
      try {
        const res = await fetch(`${backendUrl}/api/ai/benchmark`, {
          method: 'GET',
          headers: { Accept: 'application/json' },
        });
        if (!res.ok) {
          throw new Error(`HTTP ${res.status}`);
        }
        const data = (await res.json()) as BenchmarkSnapshot;
        if (!cancelled) {
          setSnapshot(data);
          setError(null);
          setLastFetchedAt(new Date());
        }
      } catch (err) {
        if (!cancelled) {
          const msg = err instanceof Error ? err.message : String(err);
          setError(msg);
        }
      } finally {
        if (!cancelled) setLoading(false);
      }
    }

    void fetchSnapshot();
    return () => {
      cancelled = true;
    };
  }, [backendUrl, refreshSignal]);

  const totalTokens = snapshot?.totalTokens ?? 0;
  const proxyCostUsd = costForTokens(totalTokens, PROXY_RATE_USD_PER_M_TOKENS);
  const openAiCostUsd = costForTokens(totalTokens, OPENAI_RATE_USD_PER_M_TOKENS);
  const savingsUsd = Math.max(0, openAiCostUsd - proxyCostUsd);
  const savingsPct =
    OPENAI_RATE_USD_PER_M_TOKENS > 0
      ? (1 - PROXY_RATE_USD_PER_M_TOKENS / OPENAI_RATE_USD_PER_M_TOKENS) * 100
      : 0;

  return (
    <aside className="h-full overflow-y-auto bg-slate-950 border-l border-slate-800 text-slate-100">
      {/* ── Header ───────────────────────────────────────────── */}
      <div className="px-4 py-3 border-b border-slate-800 flex items-baseline justify-between">
        <div>
          <h2 className="text-sm font-semibold tracking-wide uppercase text-slate-200">
            Compute Arbitrage Benchmark
          </h2>
          <p className="text-[10px] text-slate-500 mt-0.5">
            Asia proxy vs. Official OpenAI · in-memory · resets on restart
          </p>
        </div>
        <span
          className={
            'text-[10px] font-mono px-2 py-0.5 rounded ' +
            (error
              ? 'bg-red-900/40 text-red-300 border border-red-700/50'
              : loading
                ? 'bg-amber-900/30 text-amber-300 border border-amber-700/40'
                : 'bg-emerald-900/30 text-emerald-300 border border-emerald-700/40')
          }
        >
          {error ? 'OFFLINE' : loading ? 'SYNC' : 'LIVE'}
        </span>
      </div>

      {error && (
        <div className="mx-4 mt-3 rounded-md border border-red-800/50 bg-red-950/40 px-3 py-2 text-[11px] text-red-200">
          Benchmark unreachable: {error}
        </div>
      )}

      <div className="p-4 space-y-4">
        {/* ── Network Latency ────────────────────────────────── */}
        <section className="rounded-lg border border-slate-800 bg-slate-900/60 p-4">
          <div className="flex items-baseline justify-between mb-3">
            <h3 className="text-xs uppercase tracking-widest text-slate-400">
              Network Latency
            </h3>
            <span className="text-[10px] text-slate-500">
              n={fmtInt(snapshot?.totalRequests ?? 0)}
            </span>
          </div>

          <div className="grid grid-cols-2 gap-3">
            <Metric
              label="Latest"
              value={fmtMs(snapshot?.latestLatencyMs ?? 0)}
              accent="emerald"
              big
            />
            <Metric
              label="Average"
              value={fmtMs(snapshot?.averageLatencyMs ?? 0)}
              accent="slate"
              big
            />
          </div>
        </section>

        {/* ── Token Usage ────────────────────────────────────── */}
        <section className="rounded-lg border border-slate-800 bg-slate-900/60 p-4">
          <h3 className="text-xs uppercase tracking-widest text-slate-400 mb-3">
            Token Usage
          </h3>
          <div className="grid grid-cols-3 gap-3">
            <Metric label="Prompt" value={fmtInt(snapshot?.totalPromptTokens ?? 0)} accent="indigo" />
            <Metric
              label="Completion"
              value={fmtInt(snapshot?.totalCompletionTokens ?? 0)}
              accent="violet"
            />
            <Metric label="Total" value={fmtInt(totalTokens)} accent="slate" />
          </div>
        </section>

        {/* ── Estimated Cost / Arbitrage ─────────────────────── */}
        <section className="rounded-lg border border-emerald-800/40 bg-gradient-to-br from-emerald-950/30 to-slate-900/60 p-4">
          <div className="flex items-baseline justify-between mb-3">
            <h3 className="text-xs uppercase tracking-widest text-emerald-300/80">
              Estimated Cost
            </h3>
            <span className="text-[10px] font-mono text-emerald-400/80">
              −{savingsPct.toFixed(0)}% vs OpenAI
            </span>
          </div>

          <div className="space-y-2">
            <CostRow
              label="MuskAI proxy"
              sub={`$${PROXY_RATE_USD_PER_M_TOKENS.toFixed(2)} / 1M tok`}
              value={fmtUsd(proxyCostUsd)}
              accent="emerald"
            />
            <CostRow
              label="OpenAI official"
              sub={`$${OPENAI_RATE_USD_PER_M_TOKENS.toFixed(2)} / 1M tok`}
              value={fmtUsd(openAiCostUsd)}
              accent="slate"
              strike
            />
          </div>

          <div className="mt-4 pt-3 border-t border-emerald-800/40 flex items-baseline justify-between">
            <span className="text-[10px] uppercase tracking-widest text-emerald-300/70">
              Session Savings
            </span>
            <span className="text-2xl font-mono font-semibold text-emerald-300">
              {fmtUsd(savingsUsd)}
            </span>
          </div>
        </section>

        {/* ── Footer ─────────────────────────────────────────── */}
        <div className="text-[10px] text-slate-500 font-mono">
          {lastFetchedAt
            ? `synced ${lastFetchedAt.toLocaleTimeString()}`
            : 'awaiting first sync…'}
        </div>
      </div>
    </aside>
  );
}

// ──────────────────────── sub-components ────────────────────────

type AccentColor = 'emerald' | 'indigo' | 'violet' | 'slate';

const ACCENT: Record<AccentColor, string> = {
  emerald: 'text-emerald-300',
  indigo: 'text-indigo-300',
  violet: 'text-violet-300',
  slate: 'text-slate-200',
};

function Metric({
  label,
  value,
  accent,
  big,
}: {
  label: string;
  value: string;
  accent: AccentColor;
  big?: boolean;
}) {
  return (
    <div>
      <div className="text-[10px] uppercase tracking-wider text-slate-500">{label}</div>
      <div
        className={
          'font-mono font-semibold tabular-nums ' +
          ACCENT[accent] +
          ' ' +
          (big ? 'text-2xl' : 'text-base')
        }
      >
        {value}
      </div>
    </div>
  );
}

function CostRow({
  label,
  sub,
  value,
  accent,
  strike,
}: {
  label: string;
  sub: string;
  value: string;
  accent: AccentColor;
  strike?: boolean;
}) {
  return (
    <div className="flex items-baseline justify-between">
      <div>
        <div className="text-xs text-slate-300">{label}</div>
        <div className="text-[10px] text-slate-500 font-mono">{sub}</div>
      </div>
      <div
        className={
          'font-mono tabular-nums ' +
          ACCENT[accent] +
          ' ' +
          (strike ? 'line-through opacity-70' : '')
        }
      >
        {value}
      </div>
    </div>
  );
}
