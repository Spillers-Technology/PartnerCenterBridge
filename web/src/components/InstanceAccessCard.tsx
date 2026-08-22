import { useEffect, useState } from "react";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Card from "@mui/material/Card";
import CardContent from "@mui/material/CardContent";
import Checkbox from "@mui/material/Checkbox";
import FormControlLabel from "@mui/material/FormControlLabel";
import Stack from "@mui/material/Stack";
import Table from "@mui/material/Table";
import TableBody from "@mui/material/TableBody";
import TableCell from "@mui/material/TableCell";
import TableContainer from "@mui/material/TableContainer";
import TableHead from "@mui/material/TableHead";
import TableRow from "@mui/material/TableRow";
import Typography from "@mui/material/Typography";
import { api } from "../api";
import { useAsyncAction } from "../hooks/useAsyncAction";
import { useToast } from "../hooks/useToast";
import type { InstanceRole, InstanceUser, MeProfile } from "../types";

const ROLES: { value: InstanceRole; label: string }[] = [
  { value: "Administrator", label: "Administrator" },
  { value: "CatalogManager", label: "Catalog" },
  { value: "CredentialManager", label: "SAM credentials" },
  { value: "AutomationPolicyManager", label: "Automation policy" }
];

function RoleEditorRow({ user, currentUserId, onUpdated }: {
  user: InstanceUser;
  currentUserId: string;
  onUpdated: (updated: InstanceUser) => void;
}) {
  const [draft, setDraft] = useState<InstanceRole[]>(user.roles);
  const toast = useToast();
  const saveAction = useAsyncAction(async () =>
    api.instanceAccess.replaceRoles(user.id, draft, user.authorizationVersion));

  useEffect(() => setDraft(user.roles), [user]);

  const toggle = (role: InstanceRole, checked: boolean) => {
    setDraft((current) => {
      if (role === "Administrator") return checked ? ["Administrator"] : [];
      const withoutAdministrator = current.filter((item) => item !== "Administrator");
      return checked
        ? [...withoutAdministrator.filter((item) => item !== role), role]
        : withoutAdministrator.filter((item) => item !== role);
    });
  };

  const isSelf = user.id === currentUserId;
  const changed = [...draft].sort().join("|") !== [...user.roles].sort().join("|");
  const save = async () => {
    const updated = await saveAction.run();
    if (updated) {
      onUpdated(updated);
      toast(`Instance access updated for ${updated.email}`, "success");
    }
  };

  return (
    <TableRow>
      <TableCell>
        <Typography variant="body2">{user.displayName}</Typography>
        <Typography variant="caption" color="text.secondary">{user.email}</Typography>
      </TableCell>
      <TableCell>
        <Stack direction="row" sx={{ flexWrap: "wrap", gap: 0.5 }}>
          {ROLES.map((role) => (
            <FormControlLabel
              key={role.value}
              control={(
                <Checkbox
                  size="small"
                  checked={draft.includes(role.value)}
                  onChange={(event) => toggle(role.value, event.target.checked)}
                  disabled={isSelf || saveAction.busy || !user.isActive}
                />
              )}
              label={role.label}
            />
          ))}
        </Stack>
        {isSelf && (
          <Typography variant="caption" color="text.secondary">
            Another Administrator must change your roles.
          </Typography>
        )}
        {saveAction.error && <Alert severity="error" sx={{ mt: 1 }}>{saveAction.error}</Alert>}
      </TableCell>
      <TableCell align="right">
        <Button
          size="small"
          variant="contained"
          onClick={() => void save()}
          disabled={isSelf || !changed || saveAction.busy || !user.isActive}
        >
          {saveAction.busy ? "Saving..." : "Save"}
        </Button>
      </TableCell>
    </TableRow>
  );
}

export function InstanceAccessCard({ me }: { me: MeProfile }) {
  const [users, setUsers] = useState<InstanceUser[]>([]);
  const loadAction = useAsyncAction(async () => {
    const loaded = await api.instanceAccess.list();
    setUsers(loaded);
    return loaded;
  });

  useEffect(() => {
    void loadAction.run();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <Card variant="outlined" sx={{ mb: 3 }}>
      <CardContent>
        <Typography variant="h6" gutterBottom>Instance access</Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          Delegate catalog, SAM credential, or automation-policy administration without granting
          access to any customer tenant. Tenant Viewer, Operator, and Owner access stays separate.
        </Typography>
        {loadAction.error && <Alert severity="error" sx={{ mb: 2 }}>{loadAction.error}</Alert>}
        <TableContainer sx={{ overflowX: "auto" }}>
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>User</TableCell>
                <TableCell>Instance roles</TableCell>
                <TableCell></TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {users.map((user) => (
                <RoleEditorRow
                  key={`${user.id}:${user.authorizationVersion}`}
                  user={user}
                  currentUserId={me.id}
                  onUpdated={(updated) => setUsers((current) =>
                    current.map((candidate) => candidate.id === updated.id ? updated : candidate))}
                />
              ))}
              {!loadAction.busy && users.length === 0 && !loadAction.error && (
                <TableRow>
                  <TableCell colSpan={3}>No registered Local users.</TableCell>
                </TableRow>
              )}
            </TableBody>
          </Table>
        </TableContainer>
        {loadAction.busy && <Box aria-busy="true">Loading instance access...</Box>}
      </CardContent>
    </Card>
  );
}
