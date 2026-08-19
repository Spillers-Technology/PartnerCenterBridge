import { useState, type ReactNode } from "react";
import AppBar from "@mui/material/AppBar";
import Box from "@mui/material/Box";
import Drawer from "@mui/material/Drawer";
import IconButton from "@mui/material/IconButton";
import List from "@mui/material/List";
import ListItemButton from "@mui/material/ListItemButton";
import ListItemText from "@mui/material/ListItemText";
import Menu from "@mui/material/Menu";
import MenuItem from "@mui/material/MenuItem";
import Tab from "@mui/material/Tab";
import Tabs from "@mui/material/Tabs";
import Toolbar from "@mui/material/Toolbar";
import Typography from "@mui/material/Typography";
import AccountCircle from "@mui/icons-material/AccountCircle";
import MenuIcon from "@mui/icons-material/Menu";
import { useIsPhone } from "../hooks/useIsPhone";

export interface ShellTab {
  key: string;
  label: string;
}

export function AppShell({
  tabs,
  activeTab,
  onSelectTab,
  displayName,
  onSignOut,
  children
}: {
  tabs: ShellTab[];
  activeTab: string;
  onSelectTab: (key: string) => void;
  displayName: string | null;
  onSignOut?: () => void;
  children: ReactNode;
}) {
  const isPhone = useIsPhone();
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [menuAnchor, setMenuAnchor] = useState<HTMLElement | null>(null);

  const selectTab = (key: string) => {
    onSelectTab(key);
    setDrawerOpen(false);
  };

  return (
    <Box sx={{ display: "flex", flexDirection: "column", minHeight: "100vh" }}>
      <AppBar position="static" color="default" enableColorOnDark>
        <Toolbar>
          {isPhone && (
            <IconButton aria-label="Open navigation" edge="start" onClick={() => setDrawerOpen(true)} sx={{ mr: 1 }}>
              <MenuIcon />
            </IconButton>
          )}
          <Typography variant="h6" component="h1" sx={{ flexGrow: isPhone ? 1 : 0, mr: 3, whiteSpace: "nowrap" }}>
            Partner Center Bridge
          </Typography>
          {!isPhone && (
            <Tabs
              value={activeTab}
              onChange={(_, key: string) => selectTab(key)}
              variant="scrollable"
              scrollButtons="auto"
              sx={{ flexGrow: 1, minWidth: 0 }}
            >
              {tabs.map((t) => (
                <Tab key={t.key} value={t.key} label={t.label} />
              ))}
            </Tabs>
          )}
          <IconButton aria-label="Account menu" onClick={(e) => setMenuAnchor(e.currentTarget)} sx={{ ml: 1 }}>
            <AccountCircle />
          </IconButton>
          <Menu anchorEl={menuAnchor} open={menuAnchor !== null} onClose={() => setMenuAnchor(null)}>
            <MenuItem disabled>{displayName}</MenuItem>
            {onSignOut && (
              <MenuItem
                onClick={() => {
                  setMenuAnchor(null);
                  onSignOut();
                }}
              >
                Sign out
              </MenuItem>
            )}
          </Menu>
        </Toolbar>
      </AppBar>

      <Drawer anchor="left" open={isPhone && drawerOpen} onClose={() => setDrawerOpen(false)}>
        <List sx={{ width: 260 }}>
          {tabs.map((t) => (
            <ListItemButton key={t.key} selected={t.key === activeTab} onClick={() => selectTab(t.key)}>
              <ListItemText primary={t.label} />
            </ListItemButton>
          ))}
        </List>
      </Drawer>

      <Box component="main" sx={{ p: { xs: 1.5, sm: 2, md: 3 }, maxWidth: 1100, mx: "auto", width: "100%" }}>
        {children}
      </Box>
    </Box>
  );
}
