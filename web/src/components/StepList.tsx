import type { ProvisioningResult } from "../types";

/** Renders the per-step outcome of a provisioning/offboarding run. */
export function StepList({ result }: { result: ProvisioningResult }) {
  return (
    <div className="plan">
      {result.initialPassword && (
        <p className="password">
          Temporary password: <span className="mono">{result.initialPassword}</span> (shown once)
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
