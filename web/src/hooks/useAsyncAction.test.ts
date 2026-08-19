import { describe, expect, it } from "vitest";
import { act, renderHook, waitFor } from "@testing-library/react";
import { useAsyncAction } from "./useAsyncAction";

describe("useAsyncAction", () => {
  it("starts idle", () => {
    const { result } = renderHook(() => useAsyncAction(async () => "ok"));
    expect(result.current.status).toBe("idle");
    expect(result.current.busy).toBe(false);
    expect(result.current.error).toBeNull();
  });

  it("goes busy then success on a resolving action, returning the result", async () => {
    const { result } = renderHook(() => useAsyncAction((x: number) => Promise.resolve(x * 2)));

    let pending!: Promise<number | undefined>; // definite-assignment: set inside act() below
    act(() => { pending = result.current.run(21); });
    expect(result.current.busy).toBe(true);

    const returned = await pending!;
    expect(returned).toBe(42);
    await waitFor(() => expect(result.current.status).toBe("success"));
    expect(result.current.result).toBe(42);
    expect(result.current.error).toBeNull();
  });

  it("goes busy then error on a rejecting action, exposing the message", async () => {
    const { result } = renderHook(() => useAsyncAction(async () => { throw new Error("boom"); }));
    await act(async () => { await result.current.run(); });
    expect(result.current.status).toBe("error");
    expect(result.current.error).toBe("boom");
    expect(result.current.result).toBeNull();
  });

  it("stringifies a non-Error throw", async () => {
    // eslint-disable-next-line @typescript-eslint/no-throw-literal
    const { result } = renderHook(() => useAsyncAction(async () => { throw "plain string"; }));
    await act(async () => { await result.current.run(); });
    expect(result.current.error).toBe("plain string");
  });
});
