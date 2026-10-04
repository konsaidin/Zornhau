using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Cairo;

namespace WarbandCombat;

internal sealed class CombatClient : IRenderer
{
    private readonly ICoreClientAPI api;
    private readonly IClientNetworkChannel channel;
    private readonly long tick;
    private readonly CameraDiagnostics? cameraDiagnostics;
    internal readonly Fighter Fighter = new();
    private readonly CombatPresentation presentation = new();
    private EntityPlayer? animatedPlayer;
    internal Direction Direction;
    internal bool Enabled = true;
    private bool forcePvpCombat;
    private bool left, right;
    private int sequence;
    private int ownedSlot = -1;
    private ItemStack? ownedStack;
    private long lastHeartbeat, lastAim;
    private string feedback = "";
    private long feedbackUntil;
    private string lastText = "";
    private LoadedTexture texture;
    private readonly LoadedTexture[] directionTextures;
    private readonly CairoFont font;
    public double RenderOrder => 1.01;
    public int RenderRange => 9999;

    internal CombatClient(ICoreClientAPI api)
    {
        this.api = api;
        if (api.Settings.Bool["warbandCameraDiagnostics"]) cameraDiagnostics = new CameraDiagnostics(api);
        // GenOrUpdateTextTexture updates an existing wrapper; it does not allocate one.
        texture = new LoadedTexture(api);
        directionTextures = Enumerable.Range(0, 4).Select(_ => new LoadedTexture(api)).ToArray();
        font = CairoFont.WhiteSmallText().WithFontSize(13);
        font.StrokeWidth = 2;
        font.StrokeColor = new double[] { 0, 0, 0, .9 };
        channel = api.Network.GetChannel("warbandcombat").SetMessageHandler<StatePacket>(OnState);
        api.Input.RegisterHotKey("warband-toggle", Lang.Get("warbandcombat:toggle"), GlKeys.K);
        api.Input.SetHotKeyHandler("warband-toggle", _ =>
        {
            if (forcePvpCombat)
            {
                api.ShowChatMessage(Lang.Get("warbandcombat:pvp-required"));
                return true;
            }
            Cancel(); Enabled = !Enabled;
            api.ShowChatMessage(Lang.Get(Enabled ? "warbandcombat:enabled" : "warbandcombat:disabled"));
            return true;
        });
        api.Event.MouseDown += MouseDown;
        api.Event.MouseUp += MouseUp;
        api.Event.MouseMove += MouseMove;
        api.Event.LeaveWorld += LeaveWorld;
        tick = api.Event.RegisterGameTickListener(Tick, 20);
        api.Event.RegisterRenderer(this, EnumRenderStage.Ortho, "warband-hud");
    }

    internal bool Available => Enabled && api.World.Player?.Entity is { Alive: true } entity
        && CombatSystem.Supported(entity.RightHandItemSlot.Itemstack?.Collectible) && !ToolInteraction;

    private bool ToolInteraction
    {
        get
        {
            var player = api.World.Player;
            var target = player?.CurrentEntitySelection?.Entity;
            var tool = player?.Entity?.RightHandItemSlot.Itemstack?.Collectible.Tool;
            // Decide at the start of a gesture. Moving the crosshair off a live
            // opponent must not turn an ongoing windup or slash into mining.
            return CombatRules.UseToolInteraction(tool is EnumTool.Axe or EnumTool.Knife,
                player?.CurrentBlockSelection != null, target is { Alive: false },
                target is { Alive: true }, left || right || Fighter.Phase != Phase.Idle);
        }
    }

    private bool Active => Available && api.Input.MouseGrabbed && !api.IsGamePaused
        && (!api.World.Player.Entity.Controls.Sneak
            || forcePvpCombat && api.World.Player.CurrentEntitySelection?.Entity is EntityPlayer)
        && channel.Connected;

    private long Now => api.World.ElapsedMilliseconds;

    private void Send(Command command)
    {
        if (!channel.Connected) return;
        var item = api.World.Player?.Entity.RightHandItemSlot.Itemstack?.Collectible;
        if (item != null && Fighter.Apply(command, Direction, Now, CombatSystem.Timing(item)) && command == Command.Begin)
            presentation.Begin();
        channel.SendPacket(new InputPacket { Command = (int)command, Direction = (int)Direction,
            Sequence = ++sequence, SentAt = Now });
    }

    private void MouseDown(MouseEvent e)
    {
        if (e.Handled || !Active || e.Button is not (EnumMouseButton.Left or EnumMouseButton.Right)) return;
        e.Handled = true;
        ownedStack = api.World.Player.Entity.RightHandItemSlot.Itemstack;
        ownedSlot = api.World.Player.InventoryManager.ActiveHotbarSlotNumber;
        if (e.Button == EnumMouseButton.Left)
        {
            left = true;
            if (!right) Send(Command.Begin);
        }
        else
        {
            right = true; Send(Command.Block);
        }
    }

    private void MouseUp(MouseEvent e)
    {
        if (e.Button == EnumMouseButton.Left && left)
        {
            e.Handled = true; left = false;
            if (Active && !right) Send(Command.Release);
            else if (!right) Cancel();
        }
        if (e.Button == EnumMouseButton.Right && right)
        {
            e.Handled = true; right = false; Send(Command.Cancel);
        }
    }

    private void MouseMove(MouseEvent e)
    {
        cameraDiagnostics?.MouseMove(e);
        if (!Active || e.Handled) return;
        Direction next = Fighter.FromMouse(e.DeltaX, e.DeltaY, Direction);
        if (next == Direction) return;
        Direction = next;
        if ((left || right) && Now - lastAim >= 40)
        {
            Send(Command.Aim); lastAim = Now;
        }
    }

    private void Tick(float dt)
    {
        if (!Active || ownedSlot >= 0 && (ownedSlot != api.World.Player.InventoryManager.ActiveHotbarSlotNumber
            || !ReferenceEquals(ownedStack, api.World.Player.Entity.RightHandItemSlot.Itemstack)))
        {
            if (left || right || Fighter.Phase is Phase.Windup or Phase.Block) Cancel();
        }
        if (Active && (left || right) && Now - lastHeartbeat >= 200)
        {
            Send(Command.Aim); Send(Command.KeepAlive); lastHeartbeat = Now;
        }
        Fighter.Advance(Now);
        if (Active && Fighter.Phase is Phase.Windup or Phase.Block
            && Direction != Fighter.Direction && Now - lastAim >= 40)
        {
            Send(Command.Aim); lastAim = Now;
        }
        // Only the server knows whether the current aim contacted a target.
        // Prediction lets a miss run through the window instead of consuming it.
        // Held input resumes when recovery ends; a click during a short lockout
        // must not leave the player holding a button with no action started.
        if (Active && Fighter.Phase == Phase.Idle)
        {
            if (right) Send(Command.Block);
            else if (left) Send(Command.Begin);
        }
        if (api.World.Player?.Entity is { } entity)
        {
            if (!ReferenceEquals(animatedPlayer, entity))
            {
                DetachAnimationHooks();
                animatedPlayer = entity;
                entity.TpAnimManager.OnStartAnimation += CombatAnimations.OnLocalAnimationStart;
                entity.SelfFpAnimManager.OnStartAnimation += CombatAnimations.OnLocalAnimationStart;
            }
            CombatAnimations.Update(entity, Fighter, Now, presentation);
        }
    }

    private void OnState(StatePacket packet)
    {
        if (!Enum.IsDefined((Phase)packet.Phase) || !Enum.IsDefined((Direction)packet.Direction)) return;
        forcePvpCombat = packet.ForcePvpCombat;
        if (forcePvpCombat) Enabled = true;
        if (packet.Sequence == sequence)
        {
            int age = CombatPresentation.SnapshotAge(Now, packet.EchoSentAt, packet.InputReceivedAt, packet.ServerSentAt);
            Fighter.Restore((Phase)packet.Phase, (Direction)packet.Direction, Now,
                packet.Elapsed + age, packet.HitIn - age, packet.EndsIn - age, packet.Charge,
                packet.RecoveryAttackPose, packet.ContactEndsIn - age);
            Fighter.Advance(Now);
        }
        if (!string.IsNullOrEmpty(packet.Feedback))
        {
            feedback = Lang.Get(packet.Feedback); feedbackUntil = Now + 800;
        }
    }

    private void Cancel()
    {
        Send(Command.Cancel); Fighter.Invalidate(Now); left = right = false; ownedSlot = -1; ownedStack = null;
    }

    private void LeaveWorld()
    {
        DetachAnimationHooks(); presentation.Reset();
        cameraDiagnostics?.Reset();
        Fighter.Reset(); left = right = false; ownedSlot = -1; ownedStack = null;
        sequence = 0; feedbackUntil = 0;
        forcePvpCombat = false;
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        cameraDiagnostics?.OnFrame(Now, Fighter);
        if (!Available || !api.Input.MouseGrabbed || api.IsGamePaused) return;
        Direction display = Fighter.Phase == Phase.Idle ? Direction : Fighter.Direction;
        float scale = (float)RuntimeEnv.GUIScale;
        float centerX = api.Render.FrameWidth / 2f;
        float centerY = api.Render.FrameHeight / 2f;
        float radius = 28 * scale, size = 20 * scale;
        var selectedColor = Fighter.CanGuard(Now)
            ? new Vec4f(.35f, 1f, .6f, 1f) : new Vec4f(1f, .78f, .3f, 1f);
        var inactiveColor = new Vec4f(.85f, .9f, .95f, .4f);
        for (int i = 0; i < directionTextures.Length; i++)
        {
            EnsureDirectionTexture(i);
            float dx = i switch { 1 => radius, 3 => -radius, _ => 0 };
            float dy = i switch { 0 => -radius, 2 => radius, _ => 0 };
            api.Render.Render2DTexturePremultipliedAlpha(directionTextures[i].TextureId,
                centerX + dx - size / 2, centerY + dy - size / 2, size, size, 50,
                i == (int)display ? selectedColor : inactiveColor);
        }
        string state = Lang.Get(Fighter.Phase == Phase.Block && !Fighter.CanGuard(Now)
            ? "warbandcombat:guard-raise" : "warbandcombat:phase-" + Fighter.Phase.ToString().ToLowerInvariant());
        string text = Now < feedbackUntil ? state + "\n" + feedback : state;
        if (text != lastText)
        {
            api.Gui.TextTexture.GenOrUpdateTextTexture(text, font, ref texture);
            lastText = text;
        }
        api.Render.Render2DTexturePremultipliedAlpha(texture.TextureId,
            centerX - texture.Width / 2f, centerY + 48 * scale, texture.Width, texture.Height);
    }

    private void EnsureDirectionTexture(int index)
    {
        if (directionTextures[index].TextureId != 0) return;
        // Draw geometry instead of Unicode arrows, which the game's font may lack.
        using var surface = new ImageSurface(Format.Argb32, 32, 32);
        using var context = new Context(surface);
        context.Translate(16, 16);
        context.Rotate(index * Math.PI / 2);
        context.MoveTo(0, -9);
        context.LineTo(10, 1);
        context.LineTo(6, 5);
        context.LineTo(0, -1);
        context.LineTo(-6, 5);
        context.LineTo(-10, 1);
        context.ClosePath();
        context.SetSourceRGBA(0, 0, 0, .9);
        context.LineWidth = 3;
        context.StrokePreserve();
        context.SetSourceRGBA(1, 1, 1, 1);
        context.Fill();
        api.Gui.LoadOrUpdateCairoTexture(surface, true, ref directionTextures[index]);
    }

    public void Dispose()
    {
        DetachAnimationHooks();
        api.Event.MouseDown -= MouseDown;
        api.Event.MouseUp -= MouseUp;
        api.Event.MouseMove -= MouseMove;
        api.Event.LeaveWorld -= LeaveWorld;
        api.Event.UnregisterGameTickListener(tick);
        api.Event.UnregisterRenderer(this, EnumRenderStage.Ortho);
        api.Input.SetHotKeyHandler("warband-toggle", _ => false);
        texture.Dispose(); font.Dispose();
        foreach (var directionTexture in directionTextures) directionTexture.Dispose();
    }

    private void DetachAnimationHooks()
    {
        if (animatedPlayer == null) return;
        animatedPlayer.TpAnimManager.OnStartAnimation -= CombatAnimations.OnLocalAnimationStart;
        animatedPlayer.SelfFpAnimManager.OnStartAnimation -= CombatAnimations.OnLocalAnimationStart;
        animatedPlayer = null;
    }
}
