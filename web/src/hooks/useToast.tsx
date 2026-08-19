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
  const [toasts, setToasts] = useState<ToastMessage[]>([]);

  const showToast = useCallback<ToastFn>((message, severity = "info") => {
    const id = nextToastId++;
    setToasts((prev) => [...prev, { id, message, severity }]);
  }, []);

  const close = (id: number) => setToasts((prev) => prev.filter((t) => t.id !== id));

  return (
    <ToastContext.Provider value={showToast}>
      {children}
      {toasts.map((t, i) => (
        <Snackbar
          key={t.id}
          open
          autoHideDuration={5000}
          onClose={() => close(t.id)}
          anchorOrigin={{ vertical: "bottom", horizontal: "right" }}
          sx={{ bottom: `${i * 56 + 16}px !important` }}
        >
          <Alert onClose={() => close(t.id)} severity={t.severity} variant="filled" sx={{ width: "100%" }}>
            {t.message}
          </Alert>
        </Snackbar>
      ))}
    </ToastContext.Provider>
  );
}

export function useToast(): ToastFn {
  const ctx = useContext(ToastContext);
  if (!ctx) throw new Error("useToast must be used within a ToastProvider");
  return ctx;
}
