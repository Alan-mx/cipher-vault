export const isPurgeEligible = (
  eligibleAt: string,
  now: number = Date.now(),
): boolean => new Date(eligibleAt).getTime() <= now;
