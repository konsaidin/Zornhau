using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace WarbandCombat;

// All emissions are local reactions to the server's verified death and body
// collision. Never spawn network particles, change damage or affect the camera.
internal sealed class FatalityGore(ICoreClientAPI api, bool particles, bool sounds)
{
    private readonly Dictionary<long, Emission> fresh = new();
    private sealed class Emission(FatalityPacket packet, long at)
    {
        internal readonly FatalityPacket Packet = packet;
        internal readonly long At = at;
        internal long LastTrail, LastNeck;
        internal bool Landed;
    }

    internal static bool ValidVector(double[]? values, bool optional = false)
        => values != null && (optional && values.Length == 0
            || values.Length == 3 && values.All(double.IsFinite));

    private bool Visible(FatalityPacket packet, Vec3d point)
        => api.World.Player?.Entity is { } owner && owner.Pos.Dimension == packet.Dimension
            && owner.Pos.XYZ.SquareDistanceTo(point) <= 48 * 48;

    internal void Begin(FatalityPacket packet)
    {
        // Age > 0 is an existing effect being sent to a new observer, not a new
        // death. Do not replay the initial burst, trail or sound in that case.
        if (packet.AgeMs != 0 || (!particles && !sounds) || fresh.ContainsKey(packet.Id)) return;
        var center = new Vec3d(packet.Position[0], packet.Position[1], packet.Position[2]);
        if (!Visible(packet, center)) return;
        if (fresh.Count >= FatalityRules.MaxEffects) fresh.Remove(fresh.MinBy(p => p.Value.At).Key);
        fresh.Add(packet.Id, new(packet, api.World.ElapsedMilliseconds));
        var point = packet.SprayPosition.Length == 3
            ? new Vec3d(packet.SprayPosition[0], packet.SprayPosition[1], packet.SprayPosition[2]) : center;
        var impulse = packet.Impulse.Length == 3
            ? new Vec3f((float)packet.Impulse[0], (float)packet.Impulse[1], (float)packet.Impulse[2]) : new Vec3f(0, 1.7f, 0);
        bool crush = packet.Kind == FatalityKind.HeadCrush;
        if (particles)
        {
            Spawn(point, impulse, crush ? 52 : 40, .7f, 1.1f, .055f, .18f, 146, 9, 17);
            Spawn(point, impulse * .6f, crush ? 8 : 5, .5f, 1.3f, .16f, .28f, 82, 12, 19);
            if (!crush) Spawn(center.AddCopy(0, -.23, 0), new(0, 1.8f, 0), 16, .3f, .85f, .045f, .13f, 166, 11, 19);
        }
        if (sounds)
        {
            Play(crush ? "crush" : "sever", packet.Id, point, 1);
            // The game's recorded squelch adds an organic layer to our original
            // transient/crunch samples; it remains a base-game asset reference.
            api.World.PlaySoundAt(new AssetLocation("game:sounds/effect/squish" + (packet.Id % 2 + 1)),
                point.X, point.Y, point.Z, null, .88f, 20, .65f);
            if (crush && packet.HelmetPrefix.Length > 0)
                api.World.PlaySoundAt(new AssetLocation("game:sounds/held/shieldblock-metal-light"),
                    point.X, point.Y, point.Z, null, .78f, 20, .5f);
        }
    }

    internal void Trail(long id, Vec3d head, Vec3d neck, long now)
    {
        if (!particles || !fresh.TryGetValue(id, out var emission) || emission.Packet.Kind != FatalityKind.Decapitation
            || now - emission.At >= 1200 || now < emission.At || !Visible(emission.Packet, head)) return;
        // Timestamp limits make the emission rate independent of render FPS.
        // No catch-up bursts after a pause or delayed frame.
        if (!emission.Landed && now - emission.LastTrail >= 100)
        {
            emission.LastTrail = now;
            Spawn(head.AddCopy(0, -.10, 0), new(0, -.25f, 0), 3, .08f, .7f, .035f, .10f, 135, 8, 14);
        }
        if (now - emission.At < 550 && now - emission.LastNeck >= 130)
        {
            emission.LastNeck = now;
            Spawn(neck, new(0, 1.4f, 0), 5, .22f, .8f, .045f, .12f, 157, 10, 18);
        }
    }

    internal void Impact(long id, FatalityMotion motion)
    {
        if (motion.Part != 0 || motion.Impact != 1 || !fresh.TryGetValue(id, out var emission) || emission.Landed
            || emission.Packet.Kind != FatalityKind.Decapitation || !ValidVector(motion.ImpactPosition)
            || !float.IsFinite(motion.ImpactSpeed) || motion.ImpactSpeed < 1.2f || motion.ImpactSpeed > 20) return;
        emission.Landed = true;
        var point = new Vec3d(motion.ImpactPosition[0], motion.ImpactPosition[1], motion.ImpactPosition[2]);
        if (!Visible(emission.Packet, point)) return;
        if (particles) Spawn(point.AddCopy(0, .03, 0), new(0, .6f, 0), 12, .8f, .65f, .04f, .13f, 124, 8, 14);
        if (sounds) Play("land", id, point, Math.Clamp(motion.ImpactSpeed / 6, .35f, .75f));
    }

    private void Play(string family, long id, Vec3d point, float volume)
        => api.World.PlaySoundAt(new AssetLocation("warbandcombat:sounds/fatalities/" + family + (id % 2 + 1)),
            point.X, point.Y, point.Z, null, .96f + (id % 5) * .025f, 20, volume);

    private void Spawn(Vec3d position, Vec3f direction, int count, float spread, float life,
        float minSize, float maxSize, int red, int green, int blue)
    {
        // VS particles consume BGRA-packed integers; mesh vertex bytes use RGBA.
        // Quad billboards have softened corners and do not look like voxel debris.
        var properties = new SimpleParticleProperties(count, count, ColorUtil.ColorFromRgba(blue, green, red, 235),
            position.AddCopy(-.035, -.035, -.035), position.AddCopy(.035, .035, .035),
            direction - new Vec3f(spread, spread, spread), direction + new Vec3f(spread, spread, spread),
            life, 1, minSize, maxSize, EnumParticleModel.Quad)
        {
            WithTerrainCollision = true, Bounciness = 0, ShouldDieInLiquid = true,
            WindAffected = false, OpacityEvolve = new(EnumTransformFunction.LINEAR, -170),
            SizeEvolve = new(EnumTransformFunction.LINEAR, -.035f)
        };
        api.World.SpawnParticles(properties);
    }
    internal void Remove(long id) => fresh.Remove(id);
}
