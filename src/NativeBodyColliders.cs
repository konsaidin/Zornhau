using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using System.Text.RegularExpressions;
using OpenTK.Mathematics;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using WarbandCombat.Native.Colliders;

namespace WarbandCombat;

internal sealed class NativeBodyColliders(ICoreAPI api)
{
    private readonly List<(Regex code, HashSet<string> elements)> names = new();
    private readonly ConditionalWeakTable<Entity, Body> bodies = new();
    private sealed record Body(ClientAnimator Animator, object Roots, ShapeElementCollider[] Colliders);

    internal void Load()
    {
        names.Clear(); bodies.Clear();
        var asset = api.Assets.TryGet(new AssetLocation("warbandcombat:config/native-colliders.json"));
        if (asset == null) return;
        var profiles = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string[]>>>(asset.ToText())!;
        foreach (var (file, groups) in profiles.OrderByDescending(p => p.Key.Length))
        {
            // Entity JSON is a server asset and is not sent to connected clients.
            // Match the profile's family/age tokens across optional variants,
            // e.g. bear-adult -> bear-black-adult-male.
            var location = new AssetLocation(file);
            string family = location.Path.Split('/')[^1];
            string pattern = "^" + Regex.Escape(location.Domain + ":")
                + string.Join("(?:-[^-]+)*-", family.Split('-').Select(Regex.Escape)) + "(?:-|$)";
            names.Add((new Regex(pattern, RegexOptions.CultureInvariant), groups.Values.SelectMany(v => v).ToHashSet(StringComparer.Ordinal)));
        }
        api.Logger.Notification("[Warband Combat] Embedded CO body profiles: {0} entity families.", names.Count);
    }

    internal ShapeElementCollider[] For(Entity entity, float[] model, Vector3d origin)
    {
        if (entity.AnimManager?.Animator is not ClientAnimator animator || animator.RootPoses == null) return Array.Empty<ShapeElementCollider>();
        if (!bodies.TryGetValue(entity, out var body) || body.Animator != animator || !ReferenceEquals(body.Roots, animator.RootPoses))
        {
            var all = Elements(animator.RootPoses).Where(BladeGeometry.BodyElement).ToArray();
            var configured = Names(entity.Code);
            var selected = configured != null ? all.Where(e => configured.Contains(e.Name ?? "")).ToArray() : all;
            // Custom models with a different naming scheme still have real body cuboids.
            if (selected.Length == 0) selected = all;
            body = new(animator, animator.RootPoses, selected.Select(e => new ShapeElementCollider(e)).ToArray());
            bodies.Remove(entity); bodies.Add(entity, body);
        }
        foreach (var collider in body.Colliders) collider.Transform(animator.TransformationMatrices, model, origin);
        return body.Colliders;
    }

    internal ISet<string>? Names(AssetLocation code) => names.FirstOrDefault(p => p.code.IsMatch(code.ToString())).elements;

    private static IEnumerable<ShapeElement> Elements(IEnumerable<ElementPose> poses)
    {
        foreach (var pose in poses)
        {
            if (pose.ForElement != null) yield return pose.ForElement;
            if (pose.ChildElementPoses == null) continue;
            foreach (var child in Elements(pose.ChildElementPoses)) yield return child;
        }
    }

    // Adapted from CO CollidersEntityBehavior's moving-blade sweep. Every
    // subdivision tests the blade's full length; include the current endpoint.
    internal static int SweepSteps(BladeSegment before, BladeSegment after, double radius)
        => Math.Clamp(Math.Max(10, (int)Math.Ceiling(Math.Max((after.Tail - before.Tail).Length,
            (after.Tip - before.Tip).Length) / Math.Max(.045, radius * .35))), 10, 128);

    internal static bool Contact(ShapeElementCollider collider, BladeSegment blade, double radius,
        out double parameter, out Vector3d point)
    {
        bool hit = collider.Collide(blade.Tip, blade.Tail, radius, out _, out point, out var bladePoint);
        parameter = StandalonePhysics.PointParameter(blade, bladePoint);
        return hit && BladeGeometry.Finite(point);
    }

    internal void Clear() { bodies.Clear(); names.Clear(); }
}
