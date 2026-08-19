import { useEffect, useRef, useState } from "react";
import { api } from "../api";
import type { AppTemplate, MeProfile } from "../types";

const emptyForm = { displayName: "", description: "", publisher: "", installCommandLine: "", uninstallCommandLine: "" };

export function AppTemplates({ me }: { me: MeProfile | null }) {
  const [templates, setTemplates] = useState<AppTemplate[]>([]);
  const [form, setForm] = useState(emptyForm);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editForm, setEditForm] = useState(emptyForm);
  const [error, setError] = useState<string | null>(null);
  const fileInputs = useRef<Record<string, HTMLInputElement | null>>({});
  const canManage = !me || me.isSystemAdmin;

  const load = () => api.templates.list().then(setTemplates).catch((e) => setError(String(e)));
  useEffect(() => { load(); }, []);

  const create = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!form.displayName || !form.installCommandLine || !form.uninstallCommandLine) return;
    await api.templates.create({ ...form });
    setForm(emptyForm);
    await load();
  };

  const startEdit = (t: AppTemplate) => {
    setError(null);
    setEditingId(t.id);
    setEditForm({
      displayName: t.displayName,
      description: t.description ?? "",
      publisher: t.publisher ?? "",
      installCommandLine: t.installCommandLine,
      uninstallCommandLine: t.uninstallCommandLine
    });
  };

  const saveEdit = async (id: string) => {
    if (!editForm.displayName || !editForm.installCommandLine || !editForm.uninstallCommandLine) return;
    setError(null);
    try { await api.templates.update(id, { ...editForm }); setEditingId(null); await load(); }
    catch (e) { setError(String(e)); }
  };

  const remove = async (id: string) => {
    setError(null);
    try { await api.templates.remove(id); await load(); }
    catch (e) { setError(String(e)); }
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
      {canManage && (
        <form className="grid" onSubmit={create}>
          <input placeholder="Display name" value={form.displayName}
            onChange={(e) => setForm({ ...form, displayName: e.target.value })} />
          <input placeholder="Description" value={form.description}
            onChange={(e) => setForm({ ...form, description: e.target.value })} />
          <input placeholder="Publisher" value={form.publisher}
            onChange={(e) => setForm({ ...form, publisher: e.target.value })} />
          <input placeholder="Install command line" value={form.installCommandLine}
            onChange={(e) => setForm({ ...form, installCommandLine: e.target.value })} />
          <input placeholder="Uninstall command line" value={form.uninstallCommandLine}
            onChange={(e) => setForm({ ...form, uninstallCommandLine: e.target.value })} />
          <button type="submit">Create template</button>
        </form>
      )}
      {error && <p className="error">{error}</p>}
      <table>
        <thead>
          <tr>
            <th>Name</th><th>Description</th><th>Publisher</th><th>Version</th><th>Package</th>
            <th>.intunewin</th>{canManage && <th>Actions</th>}
          </tr>
        </thead>
        <tbody>
          {templates.map((t) => editingId === t.id ? (
            <tr key={t.id}>
              <td><input value={editForm.displayName}
                onChange={(e) => setEditForm({ ...editForm, displayName: e.target.value })} /></td>
              <td><input value={editForm.description}
                onChange={(e) => setEditForm({ ...editForm, description: e.target.value })} /></td>
              <td><input value={editForm.publisher}
                onChange={(e) => setEditForm({ ...editForm, publisher: e.target.value })} /></td>
              <td colSpan={2}>
                <input placeholder="Install command line" value={editForm.installCommandLine}
                  onChange={(e) => setEditForm({ ...editForm, installCommandLine: e.target.value })} />
                <input placeholder="Uninstall command line" value={editForm.uninstallCommandLine}
                  onChange={(e) => setEditForm({ ...editForm, uninstallCommandLine: e.target.value })} />
              </td>
              <td>
                <div className="row-actions">
                  <button onClick={() => saveEdit(t.id)}>Save</button>
                  <button onClick={() => setEditingId(null)}>Cancel</button>
                </div>
              </td>
            </tr>
          ) : (
            <tr key={t.id}>
              <td>{t.displayName}</td>
              <td className="muted">{t.description ?? "—"}</td>
              <td>{t.publisher ?? "—"}</td>
              <td>v{t.contentVersion}</td>
              <td>{t.hasPackage ? <span className="badge succeeded">uploaded</span> : <span className="badge pending">none</span>}</td>
              <td>
                <input type="file" accept=".intunewin"
                  ref={(el) => (fileInputs.current[t.id] = el)}
                  onChange={(e) => upload(t.id, e.target.files?.[0])} />
              </td>
              {canManage && (
                <td>
                  <div className="row-actions">
                    <button onClick={() => startEdit(t)}>Edit</button>
                    <button onClick={() => remove(t.id)}>Delete</button>
                  </div>
                </td>
              )}
            </tr>
          ))}
          {templates.length === 0 && <tr><td colSpan={canManage ? 7 : 6} className="muted">No templates yet.</td></tr>}
        </tbody>
      </table>
    </section>
  );
}
