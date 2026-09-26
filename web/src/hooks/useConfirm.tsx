import { createContext, useCallback, useContext, useState, type ReactNode } from "react";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Dialog from "@mui/material/Dialog";
import DialogActions from "@mui/material/DialogActions";
import DialogContent from "@mui/material/DialogContent";
import DialogContentText from "@mui/material/DialogContentText";
import DialogTitle from "@mui/material/DialogTitle";
import Typography from "@mui/material/Typography";
import { useIsPhone } from "./useIsPhone";

export interface ConfirmOptions {
  title: string;
  message: string;
  confirmLabel?: string;
  cancelLabel?: string;
  destructive?: boolean;
  /** The exact things that will change, listed under the message (e.g. the groups being added). */
  items?: string[];
  /** Accessible name for the items list. */
  itemsLabel?: string;
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
          {pending?.options.items && pending.options.items.length > 0 && (
            <Box
              component="ul"
              aria-label={pending.options.itemsLabel ?? "Items"}
              sx={{ mt: 1.5, mb: 0, pl: 2.5, maxHeight: 280, overflowY: "auto", overflowWrap: "anywhere" }}
            >
              {pending.options.items.map((item, i) => (
                <Typography component="li" variant="body2" key={i}>{item}</Typography>
              ))}
            </Box>
          )}
        </DialogContent>
        <DialogActions>
          {/* Destructive dialogs autofocus Cancel, not Confirm -- so pressing Enter right after
              the dialog opens (e.g. a stray keypress carried over from what triggered it) lands
              on the safe choice instead of defaulting to the destructive one. */}
          <Button onClick={() => close(false)} autoFocus={pending?.options.destructive}>
            {pending?.options.cancelLabel ?? "Cancel"}
          </Button>
          <Button
            onClick={() => close(true)}
            color={pending?.options.destructive ? "error" : "primary"}
            variant="contained"
            autoFocus={!pending?.options.destructive}
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
