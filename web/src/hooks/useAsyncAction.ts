import { useCallback, useRef, useState } from "react";

export type AsyncActionState<T> =
  | { status: "idle"; error: null; result: null }
  | { status: "busy"; error: null; result: null }
  | { status: "error"; error: string; result: null }
  | { status: "success"; error: null; result: T };

export function useAsyncAction<Args extends unknown[], T>(action: (...args: Args) => Promise<T>) {
  const [state, setState] = useState<AsyncActionState<T>>({ status: "idle", error: null, result: null });
  const busyRef = useRef(false);

  const run = useCallback(
    async (...args: Args): Promise<T | undefined> => {
      if (busyRef.current) return undefined;
      busyRef.current = true;
      setState({ status: "busy", error: null, result: null });
      try {
        const result = await action(...args);
        setState({ status: "success", error: null, result });
        return result;
      } catch (e) {
        setState({ status: "error", error: e instanceof Error ? e.message : String(e), result: null });
        return undefined;
      } finally {
        busyRef.current = false;
      }
    },
    [action]
  );

  return { ...state, busy: state.status === "busy", run };
}
