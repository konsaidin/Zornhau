namespace Zornhau;

// Observes existing collision results. It never advances or consumes a Fighter.
internal sealed class HitDiagnostics
{
    public int AttackId { get; private set; }
    public int Frames { get; private set; }
    public bool Terrain { get; private set; }
    public bool TerrainBlockedTarget { get; private set; }
    public bool EntityContact { get; private set; }
    public bool Sent { get; private set; }
    public bool Finished { get; private set; }
    public string Result { get; private set; } = "ready";
    private bool opened, serverResult, timedOut;
    private long sentAt;

    public void Begin(int id)
    {
        AttackId = id; Frames = 0;
        Terrain = TerrainBlockedTarget = EntityContact = Sent = Finished = opened = serverResult = timedOut = false;
        Result = "windup";
    }

    public void Sample(bool terrain, bool entityContact, bool terrainBlockedTarget)
    {
        opened = true; Frames++;
        Terrain |= terrain; EntityContact |= entityContact; TerrainBlockedTarget |= terrainBlockedTarget;
        if (!Sent && !serverResult) Result = "active";
    }

    public void ContactSent(long now)
    {
        Sent = true; sentAt = now;
        if (!serverResult) Result = "waiting";
    }

    public bool ServerResult(int id, string result)
    {
        if (id != AttackId || AttackId == 0) return false;
        serverResult = true; Result = result;
        return true;
    }

    public bool Finish(Phase phase)
    {
        if (AttackId == 0 || Finished || phase is Phase.Windup or Phase.Swing) return false;
        Finished = true;
        if (!Sent && !serverResult)
            Result = !opened ? "not-sampled" : EntityContact ? "client-no-packet"
                : TerrainBlockedTarget ? "terrain-blocked-target" : Terrain ? "miss-with-terrain" : "no-contact";
        return true;
    }

    public bool Timeout(long now)
    {
        if (!Sent || serverResult || timedOut || now - sentAt < 2000) return false;
        timedOut = true; Result = "no-server-reply";
        return true;
    }

    public void Reset() { AttackId = 0; Finished = true; Result = "ready"; }
}
