import type { InstancePermission, MeProfile, TenantRole } from "./types";

/** OIDC/Dev callers have no Local profile and retain the trusted-operator behavior. */
export function hasInstancePermission(me: MeProfile | null, permission: InstancePermission): boolean {
  if (me === null) return true;
  if (me.instancePermissions) return me.instancePermissions.includes(permission);
  // Compatibility with a 0.6.x API during a rolling web/API update.
  return me.isSystemAdmin;
}

/**
 * The caller's role on one tenant. OIDC/Dev callers (me === null) are trusted operators with no
 * per-tenant grants, so they are treated as Owner everywhere -- the same rule the screens used
 * before routing existed (Tenants' canAssign, ConfigSnapshots' canOperate).
 */
export function tenantRole(me: MeProfile | null, tenantId: string): TenantRole | null {
  if (me === null) return "Owner";
  return me.tenantAccess.find((a) => a.tenantId === tenantId)?.role ?? null;
}

const RANK: Record<TenantRole, number> = { Viewer: 1, Operator: 2, Owner: 3 };

/** Whether the caller holds at least `needed` on the tenant. */
export function hasTenantRole(me: MeProfile | null, tenantId: string, needed: TenantRole): boolean {
  const role = tenantRole(me, tenantId);
  return role !== null && RANK[role] >= RANK[needed];
}

/** Whether the caller can see any tenant at all (Local accounts start with none). */
export function hasAnyTenantAccess(me: MeProfile | null): boolean {
  return me === null || me.tenantAccess.length > 0;
}
