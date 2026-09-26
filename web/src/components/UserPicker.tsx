import { useEffect, useRef, useState } from "react";
import Autocomplete from "@mui/material/Autocomplete";
import Box from "@mui/material/Box";
import CircularProgress from "@mui/material/CircularProgress";
import TextField from "@mui/material/TextField";
import Typography from "@mui/material/Typography";
import { api } from "../api";
import type { DirectoryObject } from "../types";

const MIN_CHARS = 2;
const DEBOUNCE_MS = 300;

export function userLabel(u: DirectoryObject): string {
  return u.userPrincipalName && u.userPrincipalName !== u.displayName
    ? `${u.displayName} (${u.userPrincipalName})`
    : u.displayName;
}

/**
 * Searches one tenant's directory as you type (name or UPN) and picks a single user. The search
 * runs against the tenant's own directory API, debounced, and ignores responses for a query that
 * is no longer in the box.
 */
export function UserPicker({
  tenantId,
  label,
  value,
  onChange,
  excludeId,
  disabled,
  helperText,
  id
}: {
  tenantId: string;
  label: string;
  value: DirectoryObject | null;
  onChange: (user: DirectoryObject | null) => void;
  /** A user that must not be offered (e.g. the other side of a comparison). */
  excludeId?: string;
  disabled?: boolean;
  helperText?: string;
  id?: string;
}) {
  const [input, setInput] = useState("");
  const [options, setOptions] = useState<DirectoryObject[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const latest = useRef("");

  useEffect(() => {
    const query = input.trim();
    latest.current = query;
    // The box shows the chosen user's label after a pick; that is not a new search.
    if (!tenantId || query.length < MIN_CHARS || (value && query === userLabel(value))) {
      setLoading(false);
      return;
    }
    setLoading(true);
    setError(null);
    const timer = window.setTimeout(() => {
      api.directory.users(tenantId, query)
        .then((users) => { if (latest.current === query) setOptions(users); })
        .catch((e) => { if (latest.current === query) { setOptions([]); setError(e instanceof Error ? e.message : String(e)); } })
        .finally(() => { if (latest.current === query) setLoading(false); });
    }, DEBOUNCE_MS);
    return () => window.clearTimeout(timer);
  }, [input, tenantId, value]);

  // A different tenant means different people.
  useEffect(() => { setOptions([]); setError(null); }, [tenantId]);

  const visible = options.filter((o) => o.id !== excludeId);
  const noOptionsText = error
    ? `Search failed: ${error}`
    : input.trim().length < MIN_CHARS
      ? `Type at least ${MIN_CHARS} characters of a name or UPN`
      : loading ? "Searching..." : "No matching users in this tenant";

  return (
    <Autocomplete
      id={id}
      value={value}
      onChange={(_, v) => onChange(v)}
      inputValue={input}
      onInputChange={(_, v) => setInput(v)}
      options={value && !visible.some((o) => o.id === value.id) ? [value, ...visible] : visible}
      filterOptions={(x) => x}
      getOptionLabel={userLabel}
      isOptionEqualToValue={(a, b) => a.id === b.id}
      loading={loading}
      disabled={disabled || !tenantId}
      noOptionsText={noOptionsText}
      renderOption={(props, option) => {
        const { key, ...rest } = props;
        return (
          <Box component="li" key={key} {...rest} sx={{ display: "block !important" }}>
            <Typography variant="body2">{option.displayName}</Typography>
            {option.userPrincipalName && (
              <Typography variant="caption" color="text.secondary" sx={{ overflowWrap: "anywhere" }}>
                {option.userPrincipalName}
              </Typography>
            )}
          </Box>
        );
      }}
      renderInput={(params) => (
        <TextField
          {...params}
          label={label}
          helperText={helperText}
          slotProps={{
            ...params.slotProps,
            input: {
              ...params.slotProps.input,
              endAdornment: (
                <>
                  {loading ? <CircularProgress color="inherit" size={18} /> : null}
                  {params.slotProps.input.endAdornment}
                </>
              )
            }
          }}
        />
      )}
      sx={{ minWidth: 0, width: "100%" }}
    />
  );
}
