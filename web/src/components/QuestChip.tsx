import { Link as RouterLink } from "react-router";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import IconButton from "@mui/material/IconButton";
import Stack from "@mui/material/Stack";
import Tooltip from "@mui/material/Tooltip";
import ArrowForward from "@mui/icons-material/ArrowForward";
import ContentCopy from "@mui/icons-material/ContentCopy";
import { useToast } from "../hooks/useToast";
import type { DiagnosticFix } from "../types";

/**
 * The actionable half of an incomplete/failing state: instead of only saying what's wrong, it is
 * the fix itself -- a link to the screen that fixes it and/or the exact command to run, with a
 * copy button. Amber (warning) to read as "to do", not as an error.
 */
export function QuestChip({ fix }: { fix: DiagnosticFix }) {
  const toast = useToast();

  const copy = async (value: string) => {
    try {
      await navigator.clipboard.writeText(value);
      toast("Command copied");
    } catch {
      toast("Couldn't copy; select and copy the command manually.", "warning");
    }
  };

  return (
    <Stack spacing={1} sx={{ alignItems: "flex-start", minWidth: 0, maxWidth: "100%" }}>
      {fix.route && (
        <Button
          component={RouterLink}
          to={fix.route}
          size="small"
          variant="outlined"
          color="warning"
          endIcon={<ArrowForward />}
          sx={{ textTransform: "none" }}
        >
          {fix.label}
        </Button>
      )}
      {fix.command && (
        <Box
          sx={{
            display: "flex",
            alignItems: "flex-start",
            gap: 0.5,
            border: 1,
            borderColor: "warning.main",
            borderRadius: 1,
            pl: 1,
            maxWidth: "100%",
            minWidth: 0
          }}
        >
          <Box
            component="code"
            aria-label={fix.route ? "Command" : fix.label}
            sx={{
              fontFamily: "monospace",
              fontSize: "0.8125rem",
              py: 0.75,
              overflowWrap: "anywhere",
              wordBreak: "break-all",
              minWidth: 0
            }}
          >
            {fix.command}
          </Box>
          <Tooltip title="Copy command">
            <IconButton size="small" aria-label={`Copy command: ${fix.label}`} onClick={() => void copy(fix.command!)}>
              <ContentCopy fontSize="small" />
            </IconButton>
          </Tooltip>
        </Box>
      )}
      {!fix.route && !fix.command && (
        <Box component="span" sx={{ color: "warning.main", typography: "body2" }}>
          {fix.label}
        </Box>
      )}
    </Stack>
  );
}
