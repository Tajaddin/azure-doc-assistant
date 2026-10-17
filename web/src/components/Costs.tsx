import { useEffect, useState } from "react";
import { api, type CostSummary } from "../api";

export function Costs({ token, refreshKey }: { token: string; refreshKey: number }) {
  const [costs, setCosts] = useState<CostSummary | null>(null);

  useEffect(() => {
    api
      .costs(token, 30)
      .then(setCosts)
      .catch(() => setCosts(null));
  }, [token, refreshKey]);

  if (!costs) return null;
  const s = costs.summary;
  return (
    <section className="panel" aria-labelledby="costs-h">
      <h2 id="costs-h">Model spend, last {costs.days} days</h2>
      <p>
        <strong>${s.totalUsd.toFixed(4)}</strong> over {s.calls} call{s.calls === 1 ? "" : "s"} · {s.inputTokens.toLocaleString()} in /{" "}
        {s.outputTokens.toLocaleString()} out tokens
        <span className="muted small"> (list prices; local models cost nothing)</span>
      </p>
      {s.byOperation.length > 0 && (
        <table>
          <thead>
            <tr>
              <th>Operation</th>
              <th>Calls</th>
              <th>USD</th>
            </tr>
          </thead>
          <tbody>
            {s.byOperation.map((b) => (
              <tr key={b.key}>
                <td>{b.key}</td>
                <td>{b.calls}</td>
                <td>${b.usd.toFixed(4)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}
