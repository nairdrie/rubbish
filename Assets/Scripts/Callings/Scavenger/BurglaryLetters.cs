using UnityEngine;

/// <summary>
/// The letters a burglary earns, in each district's own voice (the same cast as <see cref="NeighborLetters"/>: a
/// handwritten note in Mildly Pleasant, the HOA in the Meadows, Mr. Pemberton in the Hills, the lawyers on the Bluffs,
/// the swap-meet committee in Coupon Commons, building management in Stackington Heights). Pure text: the host
/// composes it, the burglar's own machine files it in their Head Office inbox.
/// </summary>
public static class BurglaryLetters
{
    public static void Caught(byte district, string who, string address, string time, bool entered, float fine, bool waived, int nth, float mult,
        uint variantSeed, out string subject, out string body)
    {
        who = string.IsNullOrEmpty(who) ? "The Residents" : who;
        string addr = string.IsNullOrEmpty(address) ? "our house" : address;
        string did = entered ? "let yourself in" : "were seen fiddling with the lock";
        int v = (int)(variantSeed % 2u);
        switch (district)
        {
            case 1:
                subject = "HOA VIOLATION NOTICE: UNAUTHORISED ENTRY";
                body = v == 0
                    ? $"At {time} the garbage person {did} at {addr}. Bylaw 31: Doors Are For Residents. The whole cul-de-sac has been emailed.\n- Minivan Meadows HOA"
                    : $"Re: {addr}, {time}. You {did}. Burglary is NOT an approved kerbside service (see Appendix F, \"Things We Did Not Order\").\n- Minivan Meadows HOA";
                break;
            case 2:
                subject = "We are appalled.";
                body = v == 0
                    ? $"At {time} you {did} at {addr}, the residence of {who}. One collects the refuse. One does not collect the silver.\n- Mr. Pemberton, HOA Inspector"
                    : $"{who} of {addr} reports that at {time} you {did}. The Hills have long suspected the binman. The Hills are now certain.\n- Mr. Pemberton, HOA Inspector";
                break;
            case 3:
                subject = "From the desk of Dewey, Cheatham & Howe LLP";
                body = v == 0
                    ? $"Our client, {who} of {addr}, observed that at {time} you {did}. Damages have been deducted. So has this letter."
                    : $"Re: {addr}, {time}. You {did}. Our client's security consultant has been dismissed and you have been billed for his severance.";
                break;
            case 4:
                subject = "Coupon Commons Swap-Meet Committee";
                body = v == 0
                    ? $"At {time} somebody saw you at {addr}. You {did}. We swap things here, friend. Swapping needs TWO people who agree.\n- {who}, Committee Treasurer"
                    : $"{who} at {addr} says that at {time} you {did}. If you wanted the toaster you could have just asked. It was going on the table Saturday.";
                break;
            case 5:
                subject = "Stackington Heights Building Management";
                body = v == 0
                    ? $"Incident report, {time}, {addr}: the waste contractor {did}. The lobby camera got your good side. Please use the tradesman's entrance, for trade.\n- Management"
                    : $"Resident {who} of {addr} reports that at {time} you {did}. Your access fob has been revoked. You never had a fob. That is our point.\n- Management";
                break;
            default:
                subject = "Dear Garbage Person";
                body = v == 0
                    ? $"Dear Garbage Person,\nat {time} you {did} at my house. I was RIGHT THERE. I waved. You did not wave back.\n- {who}, {addr}"
                    : $"To whoever empties the bins,\nat {time} you {did} at {addr}. The bins are OUTSIDE. Everything indoors is not bins.\n- {who}";
                break;
        }
        if (waived) body += "\nFine waived (Town rules). We will remember, though.";
        else if (fine > 0f) body += mult > 1.001f
            ? $"\nFine: {Economy.Money(fine)} (burglar #{nth} caught in this district today, x{mult:0.##})."
            : $"\nFine: {Economy.Money(fine)}.";
        body += "\nP.S. Their bags count as an hour older until midnight, and the whole street trusts you a little less.";
    }

    /// <summary>Nobody saw, but a district that keeps losing things starts a list.</summary>
    public static void WatchBulletin(byte district, string districtName, int suspicion, out string subject, out string body)
    {
        subject = "Neighbourhood Watch bulletin";
        string who = district == 1 ? "Minivan Meadows HOA" : district == 2 ? "Mr. Pemberton, HOA Inspector" : district == 3 ? "Dewey, Cheatham & Howe LLP"
            : district == 4 ? "The Swap-Meet Committee" : district == 5 ? "Building Management" : "Your Neighbours";
        body = $"Things keep going missing in {districtName}. Nobody saw anything. Everybody is now looking.\n" +
               $"(Watchfulness {Mathf.Clamp(suspicion, 0, ScavengerTuning.SuspicionMax)}/{ScavengerTuning.SuspicionMax}: every point makes a doorstep 5% riskier. It fades by one each midnight.)\n- {who}";
    }
}

/// <summary>
/// Where the fence stands: the alley between the two middle shops on Depot Row, a few steps behind the door line. One per
/// Town, the same point on every machine (pure layout data), so the host can check a seller is really there.
/// </summary>
public static class FenceSpot
{
    public static Vector3 Position
    {
        get
        {
            var near = DepotRowLayout.PadRect(1); var far = DepotRowLayout.PadRect(2);
            var door = DepotRowLayout.DoorPoint(1);
            // Pads run along Z; the alley is the gap between pad 1 and pad 2. "Behind the door line" is against StreetFacing.
            Vector3 alley = new Vector3(door.x, door.y, (Mathf.Max(near.yMin, near.yMax) + Mathf.Min(far.yMin, far.yMax)) * 0.5f);
            return alley - DepotRowLayout.StreetFacing * Vector3.forward * 3.35f;
        }
    }
    /// <summary>He faces the street (so you find his eyes first).</summary>
    public static Quaternion Facing => DepotRowLayout.StreetFacing;
}
