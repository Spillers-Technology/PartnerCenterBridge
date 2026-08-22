import type { InstancePermission, MeProfile } from "./types";

/** OIDC/Dev callers have no Local profile and retain the trusted-operator behavior. */
export function hasInstancePermission(me: MeProfile | null, permission: InstancePermission): boolean {
  if (me === null) return true;
  if (me.instancePermissions) return me.instancePermissions.includes(permission);
  // Compatibility with a 0.6.x API during a rolling web/API update.
  return me.isSystemAdmin;
}
