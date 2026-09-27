import { useCallback, useEffect, useState } from "react";
import { api, isApiStatus } from "../api";
import type { SystemDiagnostics } from "../types";

export type DiagnosticsState =
  | { status: "loading"; data: null; error: null }
  | { status: "ready"; data: SystemDiagnostics; error: null }
  /** The server predates GET /api/system/diagnostics (404). */
  | { status: "unavailable"; data: null; error: null }
  | { status: "error"; data: null; error: string };

/** Loads GET /api/system/diagnostics, telling "older server" (404) apart from a real failure. */
export function useDiagnostics() {
  const [state, setState] = useState<DiagnosticsState>({ status: "loading", data: null, error: null });

  const load = useCallback(async () => {
    setState({ status: "loading", data: null, error: null });
    try {
      const data = await api.system.diagnostics();
      setState({ status: "ready", data, error: null });
    } catch (e) {
      if (isApiStatus(e, 404)) setState({ status: "unavailable", data: null, error: null });
      else setState({ status: "error", data: null, error: e instanceof Error ? e.message : String(e) });
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  return { ...state, reload: load };
}
