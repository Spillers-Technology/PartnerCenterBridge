import { createContext, useCallback, useContext, useState, type ReactNode } from "react";
import Alert from "@mui/material/Alert";
import Snackbar from "@mui/material/Snackbar";

export type ToastSeverity = "success" | "error" | "warning" | "info";
interface ToastMessage {
  id: number;
  message: string;
  severity: ToastSeverity;
}
type ToastFn = (message: string, severity?: ToastSeverity) => void;

const ToastContext = createContext<ToastFn | null>(null);

let nextToastId = 1;

export function ToastProvider({ children }: { children: ReactNode }) {
  const [queue, setQueue] = useState<ToastMessage[]>([]);
  const current = queue[0] ?? null;

  const showToast = useCallback<ToastFn>((message, severity = "info") => {
    const id = nextToastId++;
    setQueue((prev) => [...prev, { id, message, severity }]);
  }, []);

  const close = (id: number) => setQueue((prev) => prev.filter((t) => t.id !== id));

  return (
    <ToastContext.Provider value={showToast}>
      {children}
      {current && (
        <Snackbar
          key={current.id}
          open
          autoHideDuration={5000}
          onClose={() => close(current.id)}
          anchorOrigin={{ vertical: "bottom", horizontal: "right" }}
        >
          <Alert onClose={() => close(current.id)} severity={current.severity} variant="filled" sx={{ width: "100%" }}>
            {current.message}
          </Alert>
        </Snackbar>
      )}
    </ToastContext.Provider>
  );
}

export function useToast(): ToastFn {
  const ctx = useContext(ToastContext);
  if (!ctx) throw new Error("useToast must be used within a ToastProvider");
  return ctx;
}
