using Cairo;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace WarbandCombat;

// Split the game's own plus into four tinted arms and an unchanged center.
// Suppress its original draw only while our combat HUD is actually visible.
internal sealed class CombatReticle : IDisposable
{
    private static CombatReticle? active;
    private readonly Harmony harmony = new("warbandcombat.reticle");
    private readonly ICoreClientAPI api;
    private readonly Func<bool> visible;
    private readonly LoadedTexture[] textures;

    internal CombatReticle(ICoreClientAPI api, Func<bool> visible)
    {
        this.api = api; this.visible = visible;
        textures = Enumerable.Range(0, 5).Select(_ => new LoadedTexture(api)).ToArray();
        active = this;
        harmony.Patch(AccessTools.Method(AccessTools.TypeByName("Vintagestory.Client.NoObf.SystemRenderAim"), "DrawAim"),
            prefix: new HarmonyMethod(typeof(CombatReticle), nameof(DrawNative)));
    }

    private static bool DrawNative() => active == null || !active.visible();

    internal static int Region(int x, int y, int width, int height)
    {
        float dx = (x + .5f) / width - .5f, dy = (y + .5f) / height - .5f;
        if (Math.Abs(dx) <= 2f / width && Math.Abs(dy) <= 2f / height) return 4;
        return Math.Abs(dx) > Math.Abs(dy) ? dx > 0 ? 1 : 3 : dy > 0 ? 2 : 0;
    }

    private void EnsureTextures()
    {
        if (textures[0].TextureId != 0) return;
        using var bitmap = api.Assets.Get(new AssetLocation("game:textures/gui/target.png")).ToBitmap(api);
        var pixels = bitmap.Pixels;
        for (int region = 0; region < textures.Length; region++)
        {
            using var surface = new ImageSurface(Format.Argb32, bitmap.Width, bitmap.Height);
            var data = new byte[surface.Stride * bitmap.Height];
            for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (Region(x, y, bitmap.Width, bitmap.Height) != region) continue;
                int rgba = pixels[y * bitmap.Width + x], alpha = (int)((uint)rgba >> 24), offset = y * surface.Stride + x * 4;
                // Cairo ARGB32 is premultiplied BGRA in little-endian memory.
                data[offset] = (byte)((rgba & 255) * alpha / 255);
                data[offset + 1] = (byte)(((rgba >> 8) & 255) * alpha / 255);
                data[offset + 2] = (byte)(((rgba >> 16) & 255) * alpha / 255);
                data[offset + 3] = (byte)alpha;
            }
            System.Runtime.InteropServices.Marshal.Copy(data, 0, surface.DataPtr, data.Length);
            surface.MarkDirty();
            api.Gui.LoadOrUpdateCairoTexture(surface, true, ref textures[region]);
        }
    }

    internal void Render(float x, float y, Direction direction, bool shield, Fighter fighter, long now,
        long contactAt, ContactKind kind, Direction contactDirection, bool contactOmni)
    {
        EnsureTextures();
        float pulse = CombatHud.Pulse(now - contactAt);
        for (int arm = 0; arm < 4; arm++)
        {
            bool selected = arm == (int)direction || shield;
            var color = selected ? CombatHud.Color(fighter, now) : new Vec4f(.9f, .93f, .96f, .65f);
            if (pulse > 0 && (arm == (int)contactDirection || contactOmni))
            {
                var flash = CombatHud.ContactColor(kind);
                color = new(color.R * (1 - pulse) + flash.R * pulse, color.G * (1 - pulse) + flash.G * pulse,
                    color.B * (1 - pulse) + flash.B * pulse, 1);
                // Soft expanding echo follows the same ray, never an extra arrow.
                float echoSize = 32 + 4 * pulse;
                api.Render.Render2DTexturePremultipliedAlpha(textures[arm].TextureId,
                    x - echoSize / 2, y - echoSize / 2, echoSize, echoSize, 10000, new(flash.R, flash.G, flash.B, .24f * pulse));
            }
            api.Render.Render2DTexturePremultipliedAlpha(textures[arm].TextureId, x - 16, y - 16, 32, 32, 10000, color);
        }
        api.Render.Render2DTexturePremultipliedAlpha(textures[4].TextureId, x - 16, y - 16, 32, 32, 10000);
    }

    public void Dispose()
    {
        harmony.UnpatchAll(harmony.Id);
        if (ReferenceEquals(active, this)) active = null;
        foreach (var texture in textures) texture.Dispose();
    }
}

internal static class CombatHud
{
    internal const int FlashMs = 480;

    internal static string PhaseKey(Fighter fighter, long now) => fighter.Phase switch
    {
        Phase.Windup => "phase-windup-held",
        Phase.Swing when now < fighter.StrikeStarts => "phase-windup",
        Phase.Block when fighter.ShieldGuard => fighter.CanGuard(now) ? "shield-guard" : "shield-raise",
        Phase.Block when !fighter.CanGuard(now) => "guard-raise",
        _ => "phase-" + fighter.Phase.ToString().ToLowerInvariant()
    };

    internal static Vec4f Color(Fighter fighter, long now) => fighter.Phase switch
    {
        Phase.Block => fighter.CanGuard(now) ? new(.35f, 1, .65f, 1) : new(.25f, .65f, .85f, .85f),
        Phase.Swing when now >= fighter.StrikeStarts => new(1, .42f, .18f, 1),
        Phase.Recovery => new(.85f, .7f, .4f, .7f),
        _ => new(1, .8f, .3f, 1)
    };

    internal static Vec4f ContactColor(ContactKind kind) => kind switch
    {
        ContactKind.Hit => new(.25f, 1, .4f, 1),
        ContactKind.ShieldBreak => new(1, .25f, .1f, 1),
        _ => new(.25f, .85f, 1, 1)
    };

    // Three immediate flashes, fading within half a second, independent of FPS.
    internal static float Pulse(long age) => age < 0 || age >= FlashMs ? 0
        : MathF.Exp(-(age % 160) / 40f) * (1 - age / (float)FlashMs);
}
