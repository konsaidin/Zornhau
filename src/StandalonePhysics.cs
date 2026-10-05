using System.Runtime.CompilerServices;
using HarmonyLib;
using OpenTK.Mathematics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using WarbandCombat.Native.Colliders;

namespace WarbandCombat;

// Independent physical contacts. No CO type or library is referenced by this path.
internal sealed class StandalonePhysics : IDisposable
{
    private readonly ICoreAPI api;
    private readonly CombatSystem combat;
    private readonly WeaponGeometry geometry;
    private readonly NativeBodyColliders bodyColliders;
    private readonly ModelContactBounds? bounds;
    private readonly Harmony? renderPatch;
    private readonly long trackingTick;
    private readonly ConditionalWeakTable<Entity, RenderedModel> rendered = new();
    private readonly Dictionary<string, (long at, int count)> packetRates = new();
    private static StandalonePhysics? clientPhysics;
    private RenderedBlade? blade;
    private BladePiece[]? previous;
    private long previousAt, lastRenderedAt = -1, debugUntil;
    private int attackId;
    private bool sent, debugTerrain;
    private ItemStack? attackWeapon, debugWeapon;
    private BladePiece[] debugCurrent = Array.Empty<BladePiece>(), debugPrevious = Array.Empty<BladePiece>();
    private sealed record RenderedModel(float[] Matrix, Vector3d Origin, long At);
    private sealed record RenderedBlade(ItemStack Stack, BladePiece[] Pieces, long At);
    private readonly record struct Candidate(Entity Entity, string Element, Vector3d Point, int Blade, double Fraction, double Parameter);

    internal StandalonePhysics(ICoreAPI api, CombatSystem combat)
    {
        this.api = api; this.combat = combat; geometry = new(api); bodyColliders = new(api);
        if (api is ICoreClientAPI)
        {
            clientPhysics = this;
            renderPatch = new Harmony("warbandcombat.physical-render");
            StandaloneAim.Patch(renderPatch, (ICoreClientAPI)api, combat);
            renderPatch.Patch(AccessTools.Method(typeof(EntityShapeRenderer), "RenderItem"),
                postfix: new HarmonyMethod(typeof(StandalonePhysics), nameof(ItemRendered)));
            renderPatch.Patch(AccessTools.Method(typeof(EntityShapeRenderer), "DoRender3DOpaque"),
                postfix: new HarmonyMethod(typeof(StandalonePhysics), nameof(ModelRendered)));
            renderPatch.Patch(AccessTools.Method(typeof(EntityPlayerShapeRenderer), "DoRender3DOpaque"),
                postfix: new HarmonyMethod(typeof(StandalonePhysics), nameof(ModelRendered)));
        }
        else
        {
            bounds = new(api);
            trackingTick = api.Event.RegisterGameTickListener(TrackTargets, 50);
        }
        api.Logger.Notification("[Warband Combat] Embedded CO animated weapon physics ready ({0}).", api.Side);
    }

    private bool OwnWeapon(CollectibleObject? item)
        => item != null && CombatSystem.Supported(item) && combat.Integration?.UsesPhysicalContacts(item) != true;
    private static Vector3d Origin(ICoreClientAPI client)
    {
        var camera = client.World.Player.Entity.CameraPos;
        return new(camera.X, camera.Y, camera.Z);
    }

    private static void ItemRendered(EntityShapeRenderer __instance, bool isShadowPass, ItemStack stack,
        AttachmentPointAndPose apap, ItemRenderInfo renderInfo)
    {
        var self = clientPhysics;
        if (isShadowPass || self?.api is not ICoreClientAPI client || renderInfo.Transform == null
            || self.combat.Controls?.Fighter.Phase is not (Phase.Windup or Phase.Swing)
            || apap.AttachPoint?.Code != "RightHand" || !ReferenceEquals(stack, client.World.Player.Entity.RightHandItemSlot.Itemstack)
            || !self.OwnWeapon(stack.Collectible)) return;
        self.CaptureBlade(__instance.ModelMat, apap, renderInfo.Transform, stack, Origin(client));
    }

    private void CaptureBlade(float[] model, AttachmentPointAndPose apap, ModelTransform heldTransform, ItemStack stack, Vector3d origin)
    {
        var transform = heldTransform.Clone().EnsureDefaultValues();
        var matrix = ColliderTools.HeldItemMatrix(model, apap, transform);
        double scale = BladeGeometry.Scale(matrix.Values);
        var cameraOrigin = new EntityPos(origin.X, origin.Y, origin.Z);
        var pieces = geometry.For(stack.Collectible).Select(p =>
        {
            var relative = new LineSegmentCollider(p.Segment.Tail, p.Segment.Tip - p.Segment.Tail);
            var world = (LineSegmentCollider)relative.Transform(matrix, cameraOrigin);
            return new BladePiece(new(world.Position, world.Position + world.Direction),
                ContactRadius(p.Radius * scale));
        }).ToArray();
        blade = new(stack, pieces, api.World.ElapsedMilliseconds);
    }

    private static void ModelRendered(EntityShapeRenderer __instance, bool isShadowPass)
    {
        var self = clientPhysics;
        if (isShadowPass || self?.api is not ICoreClientAPI client
            || self.combat.Controls?.Fighter.Phase is not (Phase.Windup or Phase.Swing)
            || !self.OwnWeapon(client.World.Player?.Entity.RightHandItemSlot.Itemstack?.Collectible)) return;
        var entity = __instance.entity;
        self.rendered.Remove(entity);
        self.rendered.Add(entity, new((float[])__instance.ModelMat.Clone(), Origin(client), self.api.World.ElapsedMilliseconds));
    }

    internal static double ContactRadius(double meshRadius) => Math.Max(.1, meshRadius) + CombatRules.WeaponContactPadding;
    internal void AssetsReady() => bodyColliders.Load();

    private (ShapeElementCollider[] parts, PhysicalBox[] fallback) Boxes(Entity entity, long now)
    {
        if (rendered.TryGetValue(entity, out var model) && now - model.At <= 125)
        {
            var parts = bodyColliders.For(entity, model.Matrix, model.Origin);
            if (parts.Length > 0) return (parts, Array.Empty<PhysicalBox>());
        }
        var pos = entity.Pos;
        return (Array.Empty<ShapeElementCollider>(), entity.CollisionBox == null ? Array.Empty<PhysicalBox>()
            : new[] { PhysicalBox.FromCuboid(entity.CollisionBox, new(pos.X, pos.InternalY, pos.Z)) });
    }

    internal void Frame(EntityPlayer player, Fighter fighter, long now, int currentAttackId)
    {
        if (api is not ICoreClientAPI client || !OwnWeapon(player.RightHandItemSlot.Itemstack?.Collectible)) return;
        if (attackId != currentAttackId || !ReferenceEquals(attackWeapon, player.RightHandItemSlot.Itemstack))
        {
            attackId = currentAttackId; attackWeapon = player.RightHandItemSlot.Itemstack; sent = false;
            previous = null; previousAt = 0; lastRenderedAt = -1;
        }
        if (!fighter.CanHit(now) || sent)
        {
            // A new active window starts at the current blade, never the idle/windup arc.
            previous = null; return;
        }
        // Hiding FP hands suppresses RenderItem, but the player model and animator
        // are still updated. Use the same held transform and latest non-shadow matrix.
        if (attackWeapon != null && rendered.TryGetValue(player, out var ownModel) && now - ownModel.At <= 125
            && (blade == null || !ReferenceEquals(blade.Stack, attackWeapon) || blade.At < ownModel.At)
            && player.AnimManager?.Animator?.GetAttachmentPointPose("RightHand") is { } attachment
            && client.Render.GetItemStackRenderInfo(player.RightHandItemSlot, EnumItemRenderTarget.HandTp, 0)?.Transform is { } transform)
            CaptureBlade(ownModel.Matrix, attachment, transform, attackWeapon, ownModel.Origin);
        if (blade == null || !ReferenceEquals(blade.Stack, attackWeapon) || now - blade.At > 125 || blade.Pieces.Length == 0)
        { combat.Controls?.RecordMissingCollider(); previous = null; return; }
        if (lastRenderedAt == blade.At) return;
        lastRenderedAt = blade.At;
        var current = blade.Pieces;
        if (previous == null || previous.Length != current.Length || now - previousAt > 125
            || current.Where((p, i) => Math.Max((p.Segment.Tail - previous[i].Segment.Tail).Length,
                (p.Segment.Tip - previous[i].Segment.Tip).Length) > 2.5).Any()) previous = current;
        double reach = WeaponGeometry.Reach(blade.Stack.Collectible, geometry.For(blade.Stack.Collectible));
        var targets = api.World.GetEntitiesAround(player.Pos.XYZ, (float)(reach + 3), (float)(reach + 3),
            e => e is EntityAgent && e.Alive && e.EntityId != player.EntityId && e.Pos.Dimension == player.Pos.Dimension)
            .Select(e => (entity: e, boxes: Boxes(e, now))).ToArray();
        Candidate? chosen = null; bool terrain = false, blockedTarget = false;
        for (int index = 0; index < current.Length; index++)
        {
            var before = previous[index]; var after = current[index];
            int steps = NativeBodyColliders.SweepSteps(before.Segment, after.Segment, after.Radius);
            var terrainBoxes = Terrain(before.Segment, after.Segment, after.Radius, player.Pos.Dimension);
            for (int step = 0; step <= steps; step++)
            {
                double fraction = step / (double)steps;
                var segment = BladeSegment.Lerp(before.Segment, after.Segment, fraction);
                // Half a sampling step prevents a thin target falling between consecutive slices.
                double radius = after.Radius + BladeGeometry.SweepStep / 2;
                double stop = TerrainStop(segment, radius, terrainBoxes); terrain |= stop <= 1;
                foreach (var (entity, boxes) in targets)
                {
                    void Choose(string name, double parameter, Vector3d point)
                    {
                        if (parameter >= stop) { blockedTarget = true; return; }
                        var hit = new Candidate(entity, name, point, index, fraction, parameter);
                        if (chosen == null || fraction < chosen.Value.Fraction
                            || fraction == chosen.Value.Fraction && parameter < chosen.Value.Parameter) chosen = hit;
                    }
                    foreach (var part in boxes.parts)
                        if (NativeBodyColliders.Contact(part, segment, radius, out double parameter, out var point))
                            Choose(part.ShapeElementName, parameter, point);
                    for (int element = 0; element < boxes.fallback.Length; element++)
                        if (boxes.fallback[element].Contact(segment, radius, out double parameter, out var point))
                            Choose("collision-box", parameter, point);
                }
                if (chosen is { } earliest && fraction > earliest.Fraction) break;
            }
        }
        debugCurrent = current.Select(p => p with { Radius = p.Radius + BladeGeometry.SweepStep / 2 }).ToArray();
        debugPrevious = previous.Select(p => p with { Radius = p.Radius + BladeGeometry.SweepStep / 2 }).ToArray();
        debugWeapon = blade.Stack; debugUntil = now + 350; debugTerrain = blockedTarget;
        combat.Controls?.RecordHitFrame(terrain, chosen != null, blockedTarget, "engine=co-port sweep=blade", "terrain=blade-capsule");
        if (chosen is { } contact)
        {
            sent = true;
            client.Network.GetChannel("warbandcombat").SendPacket(new BladeContactPacket
            {
                AttackId = attackId, Target = contact.Entity.EntityId, Blade = contact.Blade,
                Previous = previous[contact.Blade].Segment.Pack(), Current = current[contact.Blade].Segment.Pack(),
                Fraction = contact.Fraction, Point = new[] { contact.Point.X, contact.Point.Y, contact.Point.Z }, Element = contact.Element
            });
            combat.Controls?.RecordHitSent($"engine=co-port target={contact.Entity.Code} collider={contact.Element} point={contact.Point}");
            fighter.TakeHit(now);
        }
        previous = current; previousAt = now;
    }

    private PhysicalBox[] Terrain(BladeSegment before, BladeSegment after, double radius, int dimension)
    {
        var min = Vector3d.ComponentMin(Vector3d.ComponentMin(before.Tail, before.Tip), Vector3d.ComponentMin(after.Tail, after.Tip)) - new Vector3d(radius);
        var max = Vector3d.ComponentMax(Vector3d.ComponentMax(before.Tail, before.Tip), Vector3d.ComponentMax(after.Tail, after.Tip)) + new Vector3d(radius);
        // All callers validate reach/length first. Bound the broad phase for pathological assets.
        if ((max - min).Length > 16) return Array.Empty<PhysicalBox>();
        var result = new List<PhysicalBox>(); var accessor = api.World.BlockAccessor;
        for (int x = (int)Math.Floor(min.X); x <= (int)Math.Floor(max.X); x++)
        for (int y = (int)Math.Floor(min.Y); y <= (int)Math.Floor(max.Y); y++)
        for (int z = (int)Math.Floor(min.Z); z <= (int)Math.Floor(max.Z); z++)
        {
            var pos = new BlockPos(x, y, z);
            if (pos.dimension != dimension) continue;
            var boxes = accessor.GetBlock(pos, BlockLayersAccess.MostSolid).GetCollisionBoxes(accessor, pos);
            if (boxes != null) result.AddRange(boxes.Select(b => PhysicalBox.FromCuboid(b, new(x, y, z))));
        }
        return result.ToArray();
    }
    internal static double TerrainStop(BladeSegment segment, double radius, PhysicalBox[] terrain)
    {
        double stop = double.PositiveInfinity;
        foreach (var box in terrain) if (box.Contact(segment, radius, out double t, out _)) stop = Math.Min(stop, t);
        return stop;
    }

    internal void OnContact(IServerPlayer player, BladeContactPacket packet)
    {
        if (api is not ICoreServerAPI server) return;
        long now = api.World.ElapsedMilliseconds;
        var rate = packetRates.GetValueOrDefault(player.PlayerUID);
        if (now - rate.at >= 1000) rate = (now, 0);
        packetRates[player.PlayerUID] = (rate.at, rate.count + 1);
        if (rate.count >= 30) return;
        void Reject(string reason, string detail = "") => combat.ReportHitDiagnostic(player, packet.AttackId, packet.Target, reason, detail);
        if (packet.AttackId != combat.PhysicalAttackId(player) || !combat.CanAcceptPhysicalContact(player))
        { Reject("state-or-equipment", combat.PhysicalContactState(player)); return; }
        var stack = player.Entity.RightHandItemSlot.Itemstack;
        if (stack == null || !OwnWeapon(stack.Collectible)) { Reject("not-physical-weapon"); return; }
        if (packet.Previous?.Length != 6 || packet.Current?.Length != 6 || packet.Point?.Length != 3
            || packet.Element == null || packet.Element.Length > 128
            || packet.Previous.Concat(packet.Current).Concat(packet.Point).Any(v => !double.IsFinite(v))
            || !double.IsFinite(packet.Fraction) || packet.Fraction < 0 || packet.Fraction > 1)
        { Reject("invalid-target-or-position"); return; }
        var target = api.World.GetEntityById(packet.Target);
        if (target is not EntityAgent || !target.Alive || target.EntityId == player.Entity.EntityId
            || target.Pos.Dimension != player.Entity.Pos.Dimension) { Reject("invalid-target-or-position"); return; }
        var pieces = geometry.For(stack.Collectible);
        if (packet.Blade < 0 || packet.Blade >= pieces.Length) { Reject("missing-collider"); return; }
        var piece = pieces[packet.Blade]; var before = BladeSegment.Unpack(packet.Previous); var after = BladeSegment.Unpack(packet.Current);
        var point = new Vector3d(packet.Point[0], packet.Point[1], packet.Point[2]);
        var pos = player.Entity.Pos; var origin = new Vector3d(pos.X, pos.InternalY + player.Entity.LocalEyePos.Y * .65, pos.Z);
        double reach = WeaponGeometry.Reach(stack.Collectible, pieces) + .35;
        double scale = WeaponGeometry.HandScale(stack.Collectible) * (player.Entity.Properties.Client?.Size ?? 1);
        double radius = ContactRadius(piece.Radius * scale) + BladeGeometry.SweepStep / 2;
        if (!ValidateBlade(before, after, point, origin, reach, piece.Segment.Length * scale, radius, packet.Fraction))
        { Reject("reach", "Invalid blade extent, sweep or contact proof"); return; }
        var contactSegment = BladeSegment.Lerp(before, after, packet.Fraction);
        if (bounds?.Validate(target, new(point.X, point.Y, point.Z), now, player.Ping, out _) != true)
        { Reject("outside-target-bounds"); return; }
        var terrain = Terrain(contactSegment, contactSegment, radius, pos.Dimension);
        double parameter = PointParameter(contactSegment, point);
        if (TerrainStop(contactSegment, radius, terrain) <= parameter + 1e-5)
        { Reject("co-terrain-occluded", "Blade meets a solid block before the target"); return; }
        // Verify line-of-sight from the attacker's body too: a forged blade cannot start beyond a wall.
        var sight = new BladeSegment(origin, point);
        if (TerrainStop(sight, 0, Terrain(sight, sight, 0, pos.Dimension)) < 1 - 1e-5)
        { Reject("co-terrain-occluded", "Solid block between attacker and contact"); return; }
        if (target is EntityPlayer && (!server.Server.Config.AllowPvP || !player.HasPrivilege("attackplayers")))
        { Reject("pvp-disabled"); return; }
        if (target is not EntityPlayer && !player.HasPrivilege("attackcreatures")) { Reject("no-creature-privilege"); return; }
        if (!combat.AcceptPhysicalContact(player, target)) { Reject("contact-refused"); return; }
        Direction direction = combat.PhysicalDirection(player);
        float multiplier = CombatBalance.DirectionBonus(stack.Collectible.Tool, direction);
        var damage = new DirectionalDamage
        {
            Direction = direction, Source = EnumDamageSource.Player, SourceEntity = player.Entity,
            Type = CombatBalance.StandaloneDamageType(player.Entity, direction),
            DamageTier = stack.Collectible.GetToolTier(player.Entity.RightHandItemSlot),
            HitPosition = new(point.X - target.Pos.X, point.Y - target.Pos.InternalY, point.Z - target.Pos.Z), KnockbackStrength = .6f
        };
        combat.Fatalities?.RecordContact(target, damage, direction, point, packet.Element);
        bool hit = target.ReceiveDamage(damage, CombatBalance.Power(player.Entity, direction,
            stack.Collectible.GetAttackPower(stack)) * multiplier);
        stack.Collectible.OnAttackingWith(api.World, player.Entity, target, player.Entity.RightHandItemSlot);
        if (hit && !damage.Blocked && target is EntityAgent victim) player.Entity.DidAttack(damage, victim);
        combat.PhysicalFeedback(player, target, damage.Blocked, hit);
        Reject(damage.Blocked ? "blocked" : hit ? "hit" : "no-damage", $"engine=co-port target={target.Code} blade={packet.Blade}");
    }

    internal static double PointParameter(BladeSegment segment, Vector3d point)
        => segment.Length < 1e-9 ? 0 : Math.Clamp(Vector3d.Dot(point - segment.Tail, segment.Tip - segment.Tail)
            / (segment.Length * segment.Length), 0, 1);
    internal static bool ValidateBlade(BladeSegment before, BladeSegment after, Vector3d point, Vector3d origin,
        double reach, double length, double radius, double fraction)
    {
        if (!before.Finite || !after.Finite || !BladeGeometry.Finite(point) || !BladeGeometry.Finite(origin)
            || !double.IsFinite(fraction) || fraction < 0 || fraction > 1 || !double.IsFinite(reach) || reach <= 0
            || !double.IsFinite(radius) || radius < 0 || !double.IsFinite(length) || length <= 0) return false;
        if (new[] { before.Tail, before.Tip, after.Tail, after.Tip, point }.Any(v => (v - origin).Length > reach)
            || Math.Max((before.Tail - after.Tail).Length, (before.Tip - after.Tip).Length) > 2.5
            || before.Length > length * 1.2 + .02 || after.Length > length * 1.2 + .02
            || before.Length < length * .3 || after.Length < length * .3) return false;
        var segment = BladeSegment.Lerp(before, after, fraction);
        return (point - (segment.Tail + (segment.Tip - segment.Tail) * PointParameter(segment, point))).Length <= radius + .005;
    }

    private void TrackTargets(float dt)
    {
        if (api is not ICoreServerAPI server || bounds == null) return;
        long now = api.World.ElapsedMilliseconds; var recorded = new HashSet<long>();
        foreach (var player in server.World.AllOnlinePlayers.OfType<IServerPlayer>())
        {
            if (!combat.TracksPhysicalContacts(player) || !OwnWeapon(player.Entity.RightHandItemSlot.Itemstack?.Collectible)) continue;
            foreach (var target in api.World.GetEntitiesAround(player.Entity.Pos.XYZ, 10, 10))
                if (target.Alive && target is EntityAgent && target.Pos.Dimension == player.Entity.Pos.Dimension && recorded.Add(target.EntityId)) bounds.Record(target, now);
        }
        bounds.Prune(now);
        foreach (var uid in packetRates.Where(p => now - p.Value.at > 10000).Select(p => p.Key).ToArray()) packetRates.Remove(uid);
    }

    internal void RenderDiagnostics(EntityPlayer player, long now)
    {
        if (api is not ICoreClientAPI client || now > debugUntil || !ReferenceEquals(debugWeapon, player.RightHandItemSlot.Itemstack)) return;
        var pos = player.Pos.AsBlockPos;
        void Line(Vector3d a, Vector3d b, int color)
            => client.Render.RenderLine(pos, (float)(a.X - pos.X), (float)(a.Y - pos.InternalY), (float)(a.Z - pos.Z),
                (float)(b.X - pos.X), (float)(b.Y - pos.InternalY), (float)(b.Z - pos.Z), color);
        int green = debugTerrain ? ColorUtil.ColorFromRgba(255, 120, 30, 230) : ColorUtil.ColorFromRgba(60, 255, 100, 230);
        int blue = ColorUtil.ColorFromRgba(65, 155, 255, 180);
        for (int i = 0; i < debugCurrent.Length; i++)
        {
            var current = debugCurrent[i]; var old = debugPrevious[i]; Line(current.Segment.Tail, current.Segment.Tip, green);
            Line(old.Segment.Tail, old.Segment.Tip, blue); Line(old.Segment.Tail, current.Segment.Tail, blue); Line(old.Segment.Tip, current.Segment.Tip, blue);
            var direction = current.Segment.Tip - current.Segment.Tail;
            var axis = direction.Length < 1e-8 ? Vector3d.UnitX : direction.Normalized();
            var u = Vector3d.Cross(axis, Math.Abs(axis.Y) < .9 ? Vector3d.UnitY : Vector3d.UnitX).Normalized();
            var v = Vector3d.Cross(axis, u);
            for (int j = 0; j < 8; j++)
            {
                Vector3d Offset(int n) => current.Radius * (u * Math.Cos(n * Math.PI / 4) + v * Math.Sin(n * Math.PI / 4));
                Line(current.Segment.Tail + Offset(j), current.Segment.Tip + Offset(j), green);
                Line(current.Segment.Tail + Offset(j), current.Segment.Tail + Offset(j + 1), green);
                Line(current.Segment.Tip + Offset(j), current.Segment.Tip + Offset(j + 1), green);
            }
        }
    }

    public void Dispose()
    {
        if (renderPatch != null) renderPatch.UnpatchAll(renderPatch.Id);
        if (trackingTick != 0) api.Event.UnregisterGameTickListener(trackingTick);
        if (clientPhysics == this) clientPhysics = null;
        StandaloneAim.Clear(combat);
        geometry.Clear(); bounds?.Clear(); rendered.Clear(); bodyColliders.Clear(); packetRates.Clear();
    }
}
