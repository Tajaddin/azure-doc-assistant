import { useCallback, useEffect, useRef, useState } from "react";
import { api, type DocumentRecord, type Extraction } from "../api";

export function Documents({ token, onChanged }: { token: string; onChanged: () => void }) {
  const [docs, setDocs] = useState<DocumentRecord[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [uploading, setUploading] = useState(false);
  const [extraction, setExtraction] = useState<Extraction | null>(null);
  const fileInput = useRef<HTMLInputElement>(null);

  const refresh = useCallback(async () => {
    try {
      setDocs(await api.listDocuments(token));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }, [token]);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  // poll while anything is still being indexed
  useEffect(() => {
    if (!docs.some((d) => d.status === "Uploaded" || d.status === "Processing")) return;
    const t = setInterval(() => void refresh(), 2000);
    return () => clearInterval(t);
  }, [docs, refresh]);

  const upload = useCallback(
    async (files: FileList | null) => {
      if (!files || files.length === 0) return;
      setUploading(true);
      setError(null);
      try {
        for (const file of Array.from(files)) await api.upload(token, file);
        await refresh();
        onChanged();
      } catch (e) {
        setError(e instanceof Error ? e.message : String(e));
      } finally {
        setUploading(false);
        if (fileInput.current) fileInput.current.value = "";
      }
    },
    [token, refresh, onChanged],
  );

  const remove = useCallback(
    async (id: string) => {
      await api.deleteDocument(token, id);
      await refresh();
      onChanged();
    },
    [token, refresh, onChanged],
  );

  const extract = useCallback(
    async (id: string) => {
      setError(null);
      try {
        setExtraction(await api.extract(token, id));
        onChanged();
      } catch (e) {
        setError(e instanceof Error ? e.message : String(e));
      }
    },
    [token, onChanged],
  );

  return (
    <section className="panel" aria-labelledby="docs-h">
      <h2 id="docs-h">Documents</h2>
      <label className="upload">
        <input
          ref={fileInput}
          type="file"
          accept="application/pdf"
          multiple
          onChange={(e) => void upload(e.target.files)}
          disabled={uploading}
          data-testid="file-input"
        />
        {uploading ? "Uploading..." : "Upload PDF"}
      </label>
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
      <table>
        <thead>
          <tr>
            <th>File</th>
            <th>Kind</th>
            <th>Status</th>
            <th>Pages</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          {docs.length === 0 && (
            <tr>
              <td colSpan={5} className="muted">
                No documents yet.
              </td>
            </tr>
          )}
          {docs.map((d) => (
            <tr key={d.id} data-testid="doc-row" data-status={d.status}>
              <td>{d.fileName}</td>
              <td>{d.kind}</td>
              <td>
                <span className={`status status-${d.status.toLowerCase()}`}>{d.status}</span>
                {d.error && <span className="error small"> {d.error}</span>}
              </td>
              <td>{d.pageCount || ""}</td>
              <td className="actions">
                {d.status === "Ready" && d.kind === "Invoice" && <button onClick={() => void extract(d.id)}>Extract fields</button>}
                <button className="danger" onClick={() => void remove(d.id)} aria-label={`delete ${d.fileName}`}>
                  Delete
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {extraction && (
        <div className="extraction" data-testid="extraction">
          <h3>
            Extracted fields{" "}
            <span className="muted">
              ({extraction.method}, {extraction.elapsedMs} ms)
            </span>
          </h3>
          <dl>
            {Object.entries(extraction.fields)
              .filter(([k]) => k !== "line_items")
              .map(([k, v]) => (
                <div key={k}>
                  <dt>{k.replace(/_/g, " ")}</dt>
                  <dd>{v === null || v === undefined ? <span className="muted">not found</span> : String(v)}</dd>
                </div>
              ))}
          </dl>
        </div>
      )}
    </section>
  );
}
