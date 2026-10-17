import { useCallback, useEffect, useState } from "react";
import { api, type Answer, type DocumentRecord, type Source } from "../api";

type Turn = { question: string; text: string; answer?: Answer; sources?: Source[]; tools: string[]; streaming: boolean };

export function Chat({ token, refreshKey }: { token: string; refreshKey: number }) {
  const [question, setQuestion] = useState("");
  const [docs, setDocs] = useState<DocumentRecord[]>([]);
  const [scope, setScope] = useState<string>("");
  const [turns, setTurns] = useState<Turn[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    api
      .listDocuments(token)
      .then((d) => setDocs(d.filter((x) => x.status === "Ready")))
      .catch(() => setDocs([]));
  }, [token, refreshKey]);

  const ask = useCallback(async () => {
    const q = question.trim();
    if (!q || busy) return;
    setBusy(true);
    setError(null);
    setQuestion("");
    setTurns((t) => [...t, { question: q, text: "", tools: [], streaming: true }]);
    const update = (fn: (turn: Turn) => Turn) => setTurns((t) => t.map((turn, i) => (i === t.length - 1 ? fn(turn) : turn)));
    try {
      for await (const ev of api.askStream(token, q, scope || undefined)) {
        if (ev.event === "sources") update((t) => ({ ...t, sources: ev.data as Source[] }));
        else if (ev.event === "token") update((t) => ({ ...t, text: t.text + ((ev.data as { text: string }).text ?? "") }));
        else if (ev.event === "tool") update((t) => ({ ...t, tools: [...t.tools, (ev.data as { name: string }).name] }));
        else if (ev.event === "done") update((t) => ({ ...t, answer: ev.data as Answer, text: (ev.data as Answer).answer, streaming: false }));
      }
      update((t) => ({ ...t, streaming: false }));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
      update((t) => ({ ...t, streaming: false }));
    } finally {
      setBusy(false);
    }
  }, [question, busy, token, scope]);

  return (
    <section className="panel" aria-labelledby="chat-h">
      <h2 id="chat-h">Ask</h2>
      <form
        className="ask"
        onSubmit={(e) => {
          e.preventDefault();
          void ask();
        }}
      >
        <input
          value={question}
          onChange={(e) => setQuestion(e.target.value)}
          placeholder="What is the total due on invoice INV-1001?"
          maxLength={2000}
          aria-label="question"
          data-testid="question"
        />
        <select value={scope} onChange={(e) => setScope(e.target.value)} aria-label="limit to document">
          <option value="">All documents</option>
          {docs.map((d) => (
            <option key={d.id} value={d.id}>
              {d.fileName}
            </option>
          ))}
        </select>
        <button type="submit" disabled={busy || !question.trim()} data-testid="ask">
          Ask
        </button>
      </form>
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
      <div className="turns">
        {turns.map((t, i) => (
          <article key={i} className="turn" data-testid="turn">
            <p className="q">{t.question}</p>
            <p className="a" data-testid="answer">
              {t.text || (t.streaming ? "..." : "")}
            </p>
            {t.tools.length > 0 && <p className="muted small">tools: {t.tools.join(", ")}</p>}
            {t.answer && (
              <>
                {t.answer.citations.length > 0 ? (
                  <ul className="citations" data-testid="citations">
                    {t.answer.citations.map((c) => (
                      <li key={c.chunkId}>
                        <span className="marker">[{c.marker}]</span> <strong>{c.fileName}</strong>, page {c.page}: <span className="muted">{c.snippet}</span>
                      </li>
                    ))}
                  </ul>
                ) : (
                  <p className="muted small">No citations.</p>
                )}
                <p className="muted small">
                  {t.answer.model} · {t.answer.latencyMs} ms · {t.answer.usage.calls} model call{t.answer.usage.calls === 1 ? "" : "s"} ·{" "}
                  {t.answer.usage.inputTokens + t.answer.usage.outputTokens} tokens{t.answer.usage.estimated ? " (estimated)" : ""} · $
                  {t.answer.usage.costUsd.toFixed(5)}
                </p>
              </>
            )}
          </article>
        ))}
      </div>
    </section>
  );
}
