import type { ProvisioningResult } from "../types";
import { useToast } from "../hooks/useToast";

/** Renders the per-step outcome of a provisioning/offboarding run. */
export function StepList({ result }: { result: ProvisioningResult }) {
  const toast = useToast();

  const copy = async (value: string) => {
    try {
      await navigator.clipboard.writeText(value);
      toast("Copied");
    } catch {
      toast("Couldn't copy -- select and copy the text manually.", "warning");
    }
  };

  return (
    <div className="plan">
      {result.initialPassword && (
        <p className="password">
          Temporary password: <span className="mono">{result.initialPassword}</span> (shown once){" "}
          <button type="button" onClick={() => void copy(result.initialPassword!)}>
            Copy
          </button>
        </p>
      )}
      <table>
        <thead><tr><th>Step</th><th>Result</th><th>Detail</th></tr></thead>
        <tbody>
          {result.steps.map((s, i) => (
            <tr key={i}>
              <td>{s.name}</td>
              <td><span className={`badge ${s.success ? "succeeded" : "failed"}`}>{s.success ? "ok" : "failed"}</span></td>
              <td className="mono">{s.detail ?? ""}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
