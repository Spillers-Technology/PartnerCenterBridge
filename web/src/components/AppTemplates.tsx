import { useEffect, useRef, useState } from "react";
import { api } from "../api";
import type { AppTemplate } from "../types";

export function AppTemplates() {
  const [templates, setTemplates] = useState<AppTemplate[]>([]);
  const [form, setForm] = useState({ displayName: "", publisher: "", installCommandLine: "", uninstallCommandLine: "" });
  const [error, setError] = useState<string | null>(null);
  const fileInputs = useRef<Record<string, HTMLInputElement | null>>({});

  const load = () => api.templates.list().then(setTemplates).catch((e) => setError(String(e)));
  useEffect(() => { load(); }, []);

  const create = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!form.displayName || !form.installCommandLine || !form.uninstallCommandLine) return;
    await api.templates.create({ ...form });
    setForm({ displayName: "", publisher: "", installCommandLine: "", uninstallCommandLine: "" });
    await load();
  };

  const upload = async (id: string, file?: File) => {
    if (!file) return;
    setError(null);
    try { await api.templates.uploadPackage(id, file); await load(); }
    catch (e) { setError(String(e)); }
  };

  return (
    <section>
      <h2>App Templates</h2>
      <form className="grid" onSubmit={create}>
        <input placeholder="Display name" value={form.displayName}
          onChange={(e) => setForm({ ...form, displayName: e.target.value })} />
        <input placeholder="Publisher" value={form.publisher}
          onChange={(e) => setForm({ ...form, publisher: e.target.value })} />
        <input placeholder="Install command line" value={form.installCommandLine}
          onChange={(e) => setForm({ ...form, installCommandLine: e.target.value })} />
        <input placeholder="Uninstall command line" value={form.uninstallCommandLine}
          onChange={(e) => setForm({ ...form, uninstallCommandLine: e.target.value })} />
        <button type="submit">Create template</button>
      </form>
      {error && <p className="error">{error}</p>}
      <table>
        <thead><tr><th>Name</th><th>Publisher</th><th>Version</th><th>Package</th><th>.intunewin</th></tr></thead>
        <tbody>
          {templates.map((t) => (
            <tr key={t.id}>
              <td>{t.displayName}</td>
              <td>{t.publisher ?? "—"}</td>
              <td>v{t.contentVersion}</td>
              <td>{t.hasPackage ? <span className="badge succeeded">uploaded</span> : <span className="badge pending">none</span>}</td>
              <td>
                <input type="file" accept=".intunewin"
                  ref={(el) => (fileInputs.current[t.id] = el)}
                  onChange={(e) => upload(t.id, e.target.files?.[0])} />
              </td>
            </tr>
          ))}
          {templates.length === 0 && <tr><td colSpan={5} className="muted">No templates yet.</td></tr>}
        </tbody>
      </table>
    </section>
  );
}
