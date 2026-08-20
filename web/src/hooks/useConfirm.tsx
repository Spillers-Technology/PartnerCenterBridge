import { createContext, useCallback, useContext, useState, type ReactNode } from "react";
import Button from "@mui/material/Button";
import Dialog from "@mui/material/Dialog";
import DialogActions from "@mui/material/DialogActions";
import DialogContent from "@mui/material/DialogContent";
import DialogContentText from "@mui/material/DialogContentText";
import DialogTitle from "@mui/material/DialogTitle";
import { useIsPhone } from "./useIsPhone";

export interface ConfirmOptions {
  title: string;
  message: string;
  confirmLabel?: string;
  cancelLabel?: string;
  destructive?: boolean;
}

type ConfirmFn = (options: ConfirmOptions) => Promise<boolean>;

const ConfirmContext = createContext<ConfirmFn | null>(null);

interface PendingConfirm {
  options: ConfirmOptions;
  resolve: (value: boolean) => void;
}

export function ConfirmDialogProvider({ children }: { children: ReactNode }) {
  const [queue, setQueue] = useState<PendingConfirm[]>([]);
  const [closing, setClosing] = useState(false);
  const pending = queue[0] ?? null;
  const isPhone = useIsPhone();

  const confirm = useCallback<ConfirmFn>(
    (options) => new Promise<boolean>((resolve) => setQueue((q) => [...q, { options, resolve }])),
    []
  );

  const close = (value: boolean) => {
    pending?.resolve(value);
    setClosing(true);
  };

  const handleExited = () => {
    setQueue((q) => q.slice(1));
    setClosing(false);
  };

  return (
    <ConfirmContext.Provider value={confirm}>
      {children}
      <Dialog
        open={pending !== null && !closing}
        onClose={() => close(false)}
        fullScreen={isPhone}
        slotProps={{ transition: { onExited: handleExited } }}
      >
        <DialogTitle>{pending?.options.title}</DialogTitle>
        <DialogContent>
          <DialogContentText>{pending?.options.message}</DialogContentText>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => close(false)}>{pending?.options.cancelLabel ?? "Cancel"}</Button>
          <Button
            onClick={() => close(true)}
            color={pending?.options.destructive ? "error" : "primary"}
            variant="contained"
            autoFocus
          >
            {pending?.options.confirmLabel ?? "Confirm"}
          </Button>
        </DialogActions>
      </Dialog>
    </ConfirmContext.Provider>
  );
}

export function useConfirm(): ConfirmFn {
  const ctx = useContext(ConfirmContext);
  if (!ctx) throw new Error("useConfirm must be used within a ConfirmDialogProvider");
  return ctx;
}
