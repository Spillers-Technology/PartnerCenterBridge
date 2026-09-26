import type { ReactElement } from "react";
import HomeOutlined from "@mui/icons-material/HomeOutlined";
import PeopleOutlined from "@mui/icons-material/PeopleOutlined";
import ApartmentOutlined from "@mui/icons-material/ApartmentOutlined";
import HandymanOutlined from "@mui/icons-material/HandymanOutlined";
import HistoryOutlined from "@mui/icons-material/HistoryOutlined";
import SettingsOutlined from "@mui/icons-material/SettingsOutlined";

export type DestinationKey = "home" | "people" | "tenants" | "operations" | "activity" | "settings";

export interface Destination {
  key: DestinationKey;
  label: string;
  path: string;
  icon: ReactElement;
  /** One-line purpose, used by the not-found page and Settings/overview cards. */
  description: string;
}

/**
 * The six top-level destinations. Everything else in the app lives under one of these, so the
 * navigation never grows past six entries -- new screens get a route under an existing area.
 */
export const DESTINATIONS: Destination[] = [
  { key: "home", label: "Home", path: "/", icon: <HomeOutlined />, description: "What needs attention and where to start." },
  { key: "people", label: "People", path: "/people", icon: <PeopleOutlined />, description: "Find a person across every tenant and act on them." },
  { key: "tenants", label: "Tenants", path: "/tenants", icon: <ApartmentOutlined />, description: "Customer tenants, access, contracts and snapshots." },
  { key: "operations", label: "Operations", path: "/operations", icon: <HandymanOutlined />, description: "Onboarding, offboarding, known fixes, app deployment and standards." },
  { key: "activity", label: "Activity", path: "/activity", icon: <HistoryOutlined />, description: "Workflow runs, deployments and approvals." },
  { key: "settings", label: "Settings", path: "/settings", icon: <SettingsOutlined />, description: "Your account, Microsoft connection and workbench health." }
];

/** Which destination a pathname belongs to (Home only matches "/" exactly). */
export function activeDestination(pathname: string): DestinationKey | null {
  if (pathname === "/" || pathname === "") return "home";
  const hit = DESTINATIONS.find((d) => d.path !== "/" && (pathname === d.path || pathname.startsWith(`${d.path}/`)));
  return hit?.key ?? null;
}
