using UnityEngine;

/// <summary>Keep business ownership, crew access and shared-town simulation distinct. Working for somebody lends
/// access while the job is active, never saves their permit into the employee's business.</summary>
public static class DistrictAccess
{
    public static bool CrewAllowed(bool ownPermit, bool employed, bool employerOnline, bool employerPermit) =>
        ownPermit || (employed && employerOnline && employerPermit);

    public static bool LocalCanEnter(DistrictDef district)
    {
        if (district == null) return false;
        bool employed = Payroll.TryGetLocalJob(out var job);
        bool employerOnline = employed && Businesses.TryGetForClient(job.employerClientId, out _);
        bool employerPermit = employerOnline && Businesses.TryGetForClient(job.employerClientId, out var employer) && BusinessUpgradeNet.HasPermitForBusiness(employer.Id, district);
        // A district won by contract belongs to the whole Town, not to the business that happened to host the win.
        bool wonForTheTown = Campaign.IsGrantedByContract(district.id) && OpenInTown(district);
        return CrewAllowed(Districts.IsUnlocked(district) || wonForTheTown, employed, employerOnline, employerPermit);
    }

    public static bool OpenInTown(DistrictDef district) => BusinessUpgradeNet.TryTownDistrictTrust(district, out _);
    public static float TownTrust(DistrictDef district) => BusinessUpgradeNet.TryTownDistrictTrust(district, out float trust) ? trust : 0f;

    public static int TownMask()
    {
        int mask = 0;
        for (int i = 0; i < Districts.All.Count; i++) if (OpenInTown(Districts.All[i])) mask |= 1 << i;
        return mask;
    }

    /// <summary>One shared curb population, not one duplicate set of bags per business.</summary>
    public static int TownSubscribers(DistrictDef district)
    {
        if (!BusinessUpgradeNet.TryTownDistrictTrust(district, out float trust)) return 0;
        int houses = Progression.Houses(district);
        return Mathf.Clamp(Mathf.RoundToInt(houses * (0.25f + 0.75f * trust)), 0, houses);
    }
}
