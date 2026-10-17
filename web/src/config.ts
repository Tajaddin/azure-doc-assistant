export const config = {
  apiBase: (import.meta.env.VITE_API_BASE as string | undefined) ?? "",
  authMode: ((import.meta.env.VITE_AUTH_MODE as string | undefined) ?? "dev") as "dev" | "entra",
  entra: {
    clientId: (import.meta.env.VITE_ENTRA_CLIENT_ID as string | undefined) ?? "",
    tenantId: (import.meta.env.VITE_ENTRA_TENANT_ID as string | undefined) ?? "",
    apiScope: (import.meta.env.VITE_ENTRA_API_SCOPE as string | undefined) ?? "",
  },
};
