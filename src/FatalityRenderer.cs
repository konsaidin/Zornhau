using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace WarbandCombat;

internal sealed class FatalityRenderer : IRenderer
{
    private readonly ICoreClientAPI api;
    private readonly bool cameraEnabled;
    private readonly Harmony harmony = new("warbandcombat.fatalities");
    private readonly ConditionalWeakTable<EntityAgent, MeshData> snapshots = new();
    private readonly ConditionalWeakTable<EntityAgent, Action> listeners = new();
    private readonly Dictionary<long, Effect> effects = new();
    private readonly HashSet<long> seen = new();
    private static FatalityRenderer? active;
    private Effect? cameraEffect;
    private readonly FieldInfo entityField = AccessTools.Field(typeof(EntityRenderer), "entity");
    private static readonly FieldInfo cameraOutput = AccessTools.Field(AccessTools.TypeByName("Vintagestory.Client.NoObf.Camera"), "camEyePosOutTmp");

    private sealed class Effect(FatalityPacket packet, long at)
    {
        internal readonly FatalityPacket Packet = packet;
        internal readonly long At = at;
        internal readonly Part[] Parts = Enumerable.Range(0, packet.Kind == FatalityKind.Decapitation ? 1 : 3).Select(_ => new Part()).ToArray();
        internal bool Ready, SawDead;
        internal Vec3d Forward = new(0, 0, -1);
        internal bool FacingCaptured;
    }
    private sealed class Part
    {
        internal MultiTextureMeshRef? Mesh;
        internal Vec3d From = new(), To = new(), RotationFrom = new(), RotationTo = new();
        internal long At;
        internal bool Received;
        internal Vec3d Position(long now) => From.AddCopy(To.SubCopy(From).Mul(Math.Clamp((now - At) / 100d, 0, 1)));
        internal Vec3d Rotation(long now) => RotationFrom.AddCopy(RotationTo.SubCopy(RotationFrom).Mul(Math.Clamp((now - At) / 100d, 0, 1)));
    }

    internal FatalityRenderer(ICoreClientAPI api, bool cameraEnabled)
    {
        this.api = api; this.cameraEnabled = cameraEnabled; active = this;
        harmony.Patch(AccessTools.Method(typeof(EntityShapeRenderer), "TesselateShape", [typeof(Action<MeshData>), typeof(string[])]),
            prefix: new HarmonyMethod(typeof(FatalityRenderer), nameof(Tesselate)));
        var camera = AccessTools.TypeByName("Vintagestory.Client.NoObf.Camera");
        harmony.Patch(AccessTools.Method(camera, "GetCameraMatrix"), prefix: new HarmonyMethod(typeof(FatalityRenderer), nameof(Camera)));
        api.Event.RegisterRenderer(this, EnumRenderStage.Opaque, "warband-fatalities");
    }

    private static void Tesselate(EntityShapeRenderer __instance, ref Action<MeshData> onMeshDataReady)
    {
        var manager = active;
        if (manager == null || manager.entityField.GetValue(__instance) is not EntityAgent player || !CombatFatalities.Eligible(player) || onMeshDataReady == null) return;
        manager.Watch(player);
        var original = onMeshDataReady;
        onMeshDataReady = mesh =>
        {
            manager.snapshots.Remove(player); manager.snapshots.Add(player, mesh.Clone());
            var kind = (FatalityKind)player.WatchedAttributes.GetInt(FatalityRules.Attribute);
            if (kind == FatalityKind.Decapitation)
            {
                var ids = FatalityMesh.HeadJoints(player);
                if (ids.Count > 0) mesh = FatalityMesh.Filter(mesh, i => !FatalityMesh.HeadFace(mesh, ids, i));
            }
            else if (kind == FatalityKind.HeadCrush)
            {
                string prefix = player.WatchedAttributes.GetString("warbandCrushedHelmet", "");
                var regions = FatalityMesh.HelmetRegions(manager.api, player, prefix);
                var ids = FatalityMesh.HeadJoints(player);
                var helmetFaces = Enumerable.Range(0, mesh.VerticesCount / 4)
                    .Where(i => FatalityMesh.HeadFace(mesh, ids, i) && FatalityMesh.TextureFace(mesh, i, regions)).ToArray();
                FatalityMesh.Crush(mesh, i => FatalityMesh.HeadFace(mesh, ids, i));
                var flying = helmetFaces.Where((_, ordinal) => FatalityMesh.FragmentPart(ordinal) >= 0).ToHashSet();
                if (flying.Count > 0) mesh = FatalityMesh.Filter(mesh, i => !flying.Contains(i));
            }
            original(mesh);
        };
    }

    private void Watch(EntityAgent player)
    {
        if (listeners.TryGetValue(player, out _)) return;
        Action changed = player.MarkShapeModified;
        listeners.Add(player, changed);
        player.WatchedAttributes.RegisterModifiedListener(FatalityRules.Attribute, changed);
        player.WatchedAttributes.RegisterModifiedListener("warbandCrushedHelmet", changed);
    }

    internal void Receive(FatalityPacket packet)
    {
        if (packet.Id <= 0 || packet.Victim <= 0 || packet.Kind is not (FatalityKind.Decapitation or FatalityKind.HeadCrush)
            || packet.Position?.Length != 3 || packet.Position.Any(v => !double.IsFinite(v))
            || packet.AgeMs < 0 || packet.AgeMs >= FatalityRules.LifetimeMs || packet.HelmetPrefix == null
            || packet.HelmetPrefix.Length > 256 || !seen.Add(packet.Id)) return;
        if (effects.Count >= FatalityRules.MaxEffects) Remove(effects.MinBy(e => e.Value.At).Key);
        var effect = new Effect(packet, api.World.ElapsedMilliseconds - packet.AgeMs); effects.Add(packet.Id, effect);
        if (api.World.GetEntityById(packet.Victim) is EntityAgent victim && CombatFatalities.Eligible(victim))
        {
            Watch(victim);
            // A late join can receive an old detached head after its owner has
            // respawned. It must never remove the living player's head again.
            if (!victim.Alive || packet.AgeMs == 0)
            {
                victim.WatchedAttributes.SetInt(FatalityRules.Attribute, (int)packet.Kind);
                victim.WatchedAttributes.SetString("warbandCrushedHelmet", packet.HelmetPrefix);
            }
            Prepare(effect, victim);
            victim.MarkShapeModified();
        }
        if (cameraEnabled && packet.Kind == FatalityKind.Decapitation && packet.AgeMs < FatalityRules.CameraMs
            && packet.Victim == api.World.Player?.Entity.EntityId) cameraEffect = effect;
    }

    internal void Receive(FatalityFrame packet)
    {
        if (packet.Bodies == null || packet.Bodies.Length > FatalityRules.MaxEffects * 3) return;
        long now = api.World.ElapsedMilliseconds;
        foreach (var motion in packet.Bodies)
        {
            if (!effects.TryGetValue(motion.Id, out var effect) || motion.Part < 0 || motion.Part >= effect.Parts.Length
                || motion.Position?.Length != 3 || motion.Rotation?.Length != 3
                || motion.Position.Concat(motion.Rotation).Any(v => !double.IsFinite(v))) continue;
            var part = effect.Parts[motion.Part];
            var position = new Vec3d(motion.Position[0], motion.Position[1], motion.Position[2]);
            if (position.SquareDistanceTo(effect.Packet.Position[0], effect.Packet.Position[1], effect.Packet.Position[2]) > 64 * 64) continue;
            part.From = part.Received ? part.Position(now) : position.Clone();
            part.RotationFrom = part.Received ? part.Rotation(now) : new Vec3d(motion.Rotation[0], motion.Rotation[1], motion.Rotation[2]);
            part.To = position; part.RotationTo = new(motion.Rotation[0], motion.Rotation[1], motion.Rotation[2]); part.At = now; part.Received = true;
        }
    }

    private void Prepare(Effect effect, EntityAgent player)
    {
        if (effect.Ready || !snapshots.TryGetValue(player, out var source)
            || player.Properties.Client.Renderer is not EntityShapeRenderer renderer
            || FatalityMesh.Animator(player) is not ClientAnimator animator) return;
        var matrices = animator.TransformationMatrices;
        var ids = FatalityMesh.HeadJoints(player); if (ids.Count == 0) return;
        var pivot = new Vec3d(effect.Packet.Position[0], effect.Packet.Position[1], effect.Packet.Position[2]);
        renderer.loadModelMatrix(player, 0, false);
        var model = (float[])renderer.ModelMat.Clone();
        var origin = api.World.Player.Entity.CameraPos.Clone();
        if (effect.Packet.Kind == FatalityKind.Decapitation)
        {
            var head = FatalityMesh.Filter(source, i => FatalityMesh.HeadFace(source, ids, i));
            if (head.VerticesCount == 0) return;
            FatalityMesh.Bake(head, matrices, model, origin, pivot);
            FatalityMesh.Recenter(head);
            effect.Parts[0].Mesh = api.Render.UploadMultiTextureMesh(head);
        }
        else
        {
            var regions = FatalityMesh.HelmetRegions(api, player, effect.Packet.HelmetPrefix);
            var crushed = source.Clone();
            FatalityMesh.Crush(crushed, i => FatalityMesh.HeadFace(crushed, ids, i));
            var helmet = FatalityMesh.Filter(crushed, i => FatalityMesh.HeadFace(crushed, ids, i) && FatalityMesh.TextureFace(crushed, i, regions));
            if (helmet.VerticesCount == 0)
            {
                effect.Ready = true; return;
            }
            for (int part = 0; part < effect.Parts.Length; part++)
            {
                int slice = part;
                var fragment = FatalityMesh.Filter(helmet, face => FatalityMesh.FragmentPart(face) == slice);
                if (fragment.VerticesCount == 0) continue;
                FatalityMesh.Bake(fragment, matrices, model, origin, pivot);
                FatalityMesh.Recenter(fragment);
                effect.Parts[part].Mesh = api.Render.UploadMultiTextureMesh(fragment);
            }
        }
        effect.Ready = true;
    }

    private static bool Camera(object __instance, Vec3d camEyePosIn, Vec3d worldPos, ref double[] __result)
    {
        var manager = active;
        if (manager?.cameraEffect is not { } effect || !effect.Ready || effect.Parts[0].Mesh == null
            || manager.api.World.Player?.Entity is not { } victim) return true;
        if (!victim.Alive) effect.SawDead = true;
        long now = manager.api.World.ElapsedMilliseconds;
        if (now - effect.At >= FatalityRules.CameraMs || effect.SawDead && victim.Alive || victim.EntityId != effect.Packet.Victim)
        { manager.cameraEffect = null; return true; }
        if (victim.Alive || !effect.Parts[0].Received) return true;
        if (!effect.FacingCaptured)
        {
            var forward = (Vec3d)AccessTools.Field(__instance.GetType(), "forwardVec").GetValue(__instance)!;
            if (forward.Length() > .01) effect.Forward = forward.Clone().Normalize();
            effect.FacingCaptured = true;
        }
        var part = effect.Parts[0]; var rotation = part.Rotation(now);
        var matrix = new Matrixf().RotateX((float)rotation.X).RotateY((float)rotation.Y).RotateZ((float)rotation.Z);
        Vec3d Rotate(Vec3d value)
        {
            var m = matrix.Values;
            return new(m[0] * value.X + m[4] * value.Y + m[8] * value.Z,
                m[1] * value.X + m[5] * value.Y + m[9] * value.Z, m[2] * value.X + m[6] * value.Y + m[10] * value.Z);
        }
        var forwardRotated = Rotate(effect.Forward); var up = Rotate(new(0, 1, 0));
        var eye = part.Position(now).AddCopy(Rotate(new(0, .03, 0)));
        // CameraPos is a render origin, not the entity's authoritative position.
        victim.CameraPos.Set(eye.X - victim.LocalEyePos.X, eye.Y - victim.LocalEyePos.Y, eye.Z - victim.LocalEyePos.Z);
        bool absolute = ReferenceEquals(camEyePosIn, worldPos);
        var from = absolute ? eye : victim.LocalEyePos.Clone();
        __result = Mat4d.LookAt(new double[16], from.ToDoubleArray(), from.AddCopy(forwardRotated).ToDoubleArray(), up.ToDoubleArray());
        if (absolute) ((Vec3d)cameraOutput.GetValue(__instance)!).Set(eye);
        return false;
    }

    public double RenderOrder => .65;
    public int RenderRange => 128;
    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (api.World.Player?.Entity is not { } owner) return;
        long now = api.World.ElapsedMilliseconds;
        foreach (var (id, effect) in effects.ToArray())
        {
            if (now - effect.At >= FatalityRules.LifetimeMs) { Remove(id); continue; }
            if (!effect.Ready && api.World.GetEntityById(effect.Packet.Victim) is EntityAgent victim && CombatFatalities.Eligible(victim)) Prepare(effect, victim);
            if (effect.Packet.Dimension != owner.Pos.Dimension) continue;
            foreach (var part in effect.Parts)
            {
                if (part.Mesh == null || !part.Received || ReferenceEquals(cameraEffect, effect) && !owner.Alive) continue;
                var position = part.Position(now); var rotation = part.Rotation(now);
                var previous = api.Render.CurrentActiveShader; previous?.Stop();
                var shader = api.Render.PreparedStandardShader((int)position.X, (int)position.Y, (int)position.Z);
                try
                {
                    shader.ModelMatrix = new Matrixf().Translate((float)(position.X - owner.CameraPos.X),
                        (float)(position.Y - owner.CameraPos.Y), (float)(position.Z - owner.CameraPos.Z))
                        .RotateX((float)rotation.X).RotateY((float)rotation.Y).RotateZ((float)rotation.Z).Values;
                    shader.ViewMatrix = api.Render.CameraMatrixOriginf; shader.ProjectionMatrix = api.Render.CurrentProjectionMatrix;
                    api.Render.RenderMultiTextureMesh(part.Mesh, "tex");
                }
                finally { shader.Stop(); previous?.Use(); }
            }
        }
        if (seen.Count > 4096) { seen.Clear(); foreach (long id in effects.Keys) seen.Add(id); }
    }

    private void Remove(long id)
    {
        if (!effects.Remove(id, out var effect)) return;
        foreach (var part in effect.Parts) part.Mesh?.Dispose();
        if (ReferenceEquals(cameraEffect, effect)) cameraEffect = null;
    }

    public void Dispose()
    {
        api.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
        harmony.UnpatchAll(harmony.Id); if (active == this) active = null;
        foreach (long id in effects.Keys.ToArray()) Remove(id);
        foreach (var listener in listeners) listener.Key.WatchedAttributes.UnregisterListener(listener.Value);
        listeners.Clear();
        snapshots.Clear(); seen.Clear();
    }
}
