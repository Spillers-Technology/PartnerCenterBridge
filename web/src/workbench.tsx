import { createContext, useContext, type ReactNode } from "react";
import type { AuthMode, MeProfile, SystemStatus } from "./types";

/**
 * Who is signed in and what kind of server this is -- resolved once by App and shared with every
 * routed page, so pages read it from context instead of having it threaded through props.
 */
export interface WorkbenchSession {
  authMode: AuthMode;
  /** The Local-mode profile; null under OIDC/Dev (trusted operator, no per-tenant grants). */
  me: MeProfile | null;
  displayName: string | null;
  /** GET /api/system/status, or null when the server predates it (or it failed). */
  status: SystemStatus | null;
  refreshMe: () => Promise<void>;
  signOut?: () => void;
}

const WorkbenchContext = createContext<WorkbenchSession | null>(null);

export function WorkbenchProvider({ value, children }: { value: WorkbenchSession; children: ReactNode }) {
  return <WorkbenchContext.Provider value={value}>{children}</WorkbenchContext.Provider>;
}

export function useWorkbench(): WorkbenchSession {
  const ctx = useContext(WorkbenchContext);
  if (!ctx) throw new Error("useWorkbench must be used within a WorkbenchProvider");
  return ctx;
}
