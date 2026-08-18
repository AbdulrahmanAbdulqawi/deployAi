import { ProjectTarget } from '../models/api.models';

/**
 * The browser-facing part is where a domain belongs. A compose app deploys as one website-role
 * target; a split-origin plan has the site and the API as separate targets and only the site gets
 * the domain here. The role lives inside the target's config blob rather than on the target.
 */
export function pickDomainTargetId(targets: ProjectTarget[]): string | null {
  const website = targets.find((target) => {
    try {
      return target.config ? JSON.parse(target.config)?.role === 'website' : false;
    } catch {
      return false;
    }
  });

  return website?.id ?? targets[0]?.id ?? null;
}
