import { config } from "./config";
import { readSse } from "./sse";

export type DocumentRecord = {
  id: string;
  fileName: string;
  kind: string;
  status: "Uploaded" | "Processing" | "Ready" | "Failed";
  pageCount: number;
  chunkCount: number;
  uploadedAt: string;
  error: string | null;
};

export type Citation = { marker: number; documentId: string; fileName: string; page: number; chunkId: string; snippet: string };
export type Source = { marker: number; documentId: string; fileName: string; page: number; chunkId: string; score: number; snippet: string };
export type Usage = { inputTokens: number; outputTokens: number; calls: number; costUsd: number; estimated: boolean };
export type Answer = { answer: string; citations: Citation[]; sources: Source[]; toolCalls: string[]; usage: Usage; latencyMs: number; model: string };
export type InvoiceFields = Record<string, unknown> & { invoice_number?: string | null; total?: number | null; vendor?: string | null };
export type Extraction = { documentId: string; method: string; fields: InvoiceFields; usage: Usage; elapsedMs: number };
export type CostSummary = {
  days: number;
  summary: {
    totalUsd: number;
    calls: number;
    inputTokens: number;
    outputTokens: number;
    byOperation: { key: string; usd: number; calls: number }[];
    byDay: { key: string; usd: number; calls: number }[];
  };
};

export class ApiError extends Error {
  constructor(
    public status: number,
    message: string,
  ) {
    super(message);
  }
}

async function request<T>(token: string, path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers);
  headers.set("Authorization", `Bearer ${token}`);
  const resp = await fetch(`${config.apiBase}${path}`, { ...init, headers });
  if (resp.status === 204) return undefined as T;
  if (!resp.ok) {
    let detail = resp.statusText;
    try {
      const body = (await resp.json()) as { error?: string; title?: string; errors?: Record<string, string[]> };
      detail = body.error ?? body.title ?? (body.errors ? Object.values(body.errors).flat().join("; ") : detail);
    } catch {
      // keep statusText
    }
    throw new ApiError(resp.status, detail);
  }
  return (await resp.json()) as T;
}

export const api = {
  me: (token: string) => request<{ ownerId: string; name: string }>(token, "/auth/me"),
  listDocuments: (token: string) => request<DocumentRecord[]>(token, "/api/documents"),
  upload: (token: string, file: File) => {
    const form = new FormData();
    form.append("file", file, file.name);
    return request<DocumentRecord>(token, "/api/documents", { method: "POST", body: form });
  },
  deleteDocument: (token: string, id: string) => request<void>(token, `/api/documents/${id}`, { method: "DELETE" }),
  extract: (token: string, id: string) => request<Extraction>(token, `/api/documents/${id}/extract`, { method: "POST" }),
  fields: (token: string, id: string) => request<Extraction>(token, `/api/documents/${id}/fields`),
  ask: (token: string, question: string, documentId?: string) =>
    request<Answer>(token, "/api/ask", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ question, documentId: documentId ?? null }),
    }),
  costs: (token: string, days = 30) => request<CostSummary>(token, `/api/costs?days=${days}`),

  async *askStream(token: string, question: string, documentId?: string): AsyncGenerator<{ event: string; data: unknown }> {
    const resp = await fetch(`${config.apiBase}/api/ask/stream`, {
      method: "POST",
      headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
      body: JSON.stringify({ question, documentId: documentId ?? null }),
    });
    if (!resp.ok || !resp.body) throw new ApiError(resp.status, resp.statusText);
    yield* readSse(resp.body);
  },
};
