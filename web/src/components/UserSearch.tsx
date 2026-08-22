import { useRef, useState } from "react";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Stack from "@mui/material/Stack";
import Table from "@mui/material/Table";
import TableBody from "@mui/material/TableBody";
import TableCell from "@mui/material/TableCell";
import TableContainer from "@mui/material/TableContainer";
import TableHead from "@mui/material/TableHead";
import TableRow from "@mui/material/TableRow";
import TextField from "@mui/material/TextField";
import Typography from "@mui/material/Typography";
import { api } from "../api";
import type { GlobalSearchResult } from "../types";
import { useAsyncAction } from "../hooks/useAsyncAction";

export interface WorkflowLaunch {
  workflowId: string;
  tenantId: string;
  inputs: Record<string, string>;
}

/** Person-first workflow shortcuts shown per hit; userUpn is the shared input key. */
const ACTIONS: { workflowId: string; label: string }[] = [
  { workflowId: "mfa-reset", label: "MFA reset" },
  { workflowId: "password-reset", label: "Password reset" },
  { workflowId: "compromised-lockdown", label: "Lockdown" },
  { workflowId: "license-repair", label: "License repair" }
];

export function UserSearch({ onLaunch }: { onLaunch: (launch: WorkflowLaunch) => void }) {
  const [q, setQ] = useState("");
  const [result, setResult] = useState<GlobalSearchResult | null>(null);
  // Always current (updated every render) so a search response can tell, once it resolves,
  // whether the query it was issued for is still what's in the box -- the Search button disables
  // while a search is in flight, but the text field itself doesn't, so the query can still change
  // underneath an in-flight request.
  const currentQueryRef = useRef("");
  currentQueryRef.current = q;

  const searchAction = useAsyncAction(async () => {
    const query = q.trim();
    if (query.length < 3) throw new Error("Type at least 3 characters.");
    // Clear the prior results as soon as a new search starts -- without this, a failed re-search
    // left the previous query's results (and their workflow-launch buttons) sitting on screen
    // next to the new error, looking like they might belong to the query that just failed.
    setResult(null);
    const r = await api.search.users(query);
    // The query field itself stays editable while a search is in flight -- if it's since changed,
    // this response no longer describes what's in the box, so don't display it.
    if (currentQueryRef.current.trim() !== query) return r;
    setResult(r);
    return r;
  });

  return (
    <Box>
      <Typography variant="h5" component="h2" gutterBottom>
        Find user
      </Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        Search every active tenant at once - start from the person, not the portal.
      </Typography>

      <Stack
        component="form"
        direction="row"
        spacing={1}
        sx={{ mb: 2 }}
        onSubmit={(ev) => {
          ev.preventDefault();
          void searchAction.run();
        }}
      >
        <TextField
          label="Name or UPN (min 3 chars)"
          value={q}
          onChange={(e) => {
            setQ(e.target.value);
            // Editing the query invalidates the results shown for the previous one -- leaving
            // them clickable (workflow-launch buttons included) while the field no longer matches
            // what they came from is exactly the kind of stale-but-actionable state this fixes.
            setResult(null);
          }}
          size="small"
          fullWidth
        />
        <Button type="submit" variant="contained" disabled={searchAction.busy}>
          {searchAction.busy ? "Searching..." : "Search"}
        </Button>
      </Stack>

      {searchAction.error && (
        <Alert severity="error" sx={{ mb: 2 }}>
          {searchAction.error}
        </Alert>
      )}

      {result && (
        <>
          <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
            {result.hits.length} match(es) across {result.tenantsSearched} tenant(s)
            {result.errors.length > 0 && ` - ${result.errors.length} tenant(s) unreachable`}
          </Typography>

          {result.hits.length > 0 && (
            <TableContainer sx={{ mb: 3 }}>
              <Table size="small">
                <TableHead>
                  <TableRow>
                    <TableCell>User</TableCell>
                    <TableCell>UPN</TableCell>
                    <TableCell>Tenant</TableCell>
                    <TableCell>Fix something</TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {result.hits.map((h) => (
                    <TableRow key={`${h.tenantId}:${h.id}`}>
                      <TableCell>{h.displayName}</TableCell>
                      <TableCell sx={{ fontFamily: "monospace" }}>{h.userPrincipalName ?? ""}</TableCell>
                      <TableCell>{h.tenantName}</TableCell>
                      <TableCell>
                        <Stack direction="row" spacing={1} useFlexGap sx={{ flexWrap: "wrap" }}>
                          {ACTIONS.map((a) => (
                            <Button
                              key={a.workflowId}
                              size="small"
                              variant="outlined"
                              onClick={() =>
                                onLaunch({
                                  workflowId: a.workflowId,
                                  tenantId: h.tenantId,
                                  inputs: { userUpn: h.userPrincipalName ?? h.id }
                                })
                              }
                            >
                              {a.label}
                            </Button>
                          ))}
                        </Stack>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </TableContainer>
          )}

          {result.errors.length > 0 && (
            <Box>
              <Typography variant="h6" component="h3" gutterBottom>
                Unreachable tenants
              </Typography>
              <TableContainer>
                <Table size="small">
                  <TableHead>
                    <TableRow>
                      <TableCell>Tenant</TableCell>
                      <TableCell>Error</TableCell>
                    </TableRow>
                  </TableHead>
                  <TableBody>
                    {result.errors.map((e) => (
                      <TableRow key={e.tenantId}>
                        <TableCell>{e.tenantName}</TableCell>
                        <TableCell sx={{ color: "text.secondary" }}>{e.message}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </TableContainer>
            </Box>
          )}
        </>
      )}
    </Box>
  );
}
