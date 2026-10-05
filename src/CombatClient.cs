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
    private bool choosingShieldBlock;
    private bool left, right;
    private bool sprintWanted, sprintWasLocked;
    private int sequence;
    internal bool HitDiagnosticsEnabled { get; private set; }
    private readonly HitDiagnostics hitDiagnostics = new();
    private string diagnosticDetail = "";
    private readonly Dictionary<int, long> pendingHitDiagnostics = new();
    private int ownedSlot = -1;
    private ItemStack? ownedStack;
    private ItemStack? ownedOffhand;
    private string ownedMode = "";
    private long lastHeartbeat;
    private string feedback = "";
    private long feedbackUntil;
    private long lastContact, contactUntil;
    private ContactKind contactKind;
    private string lastText = "";
    private LoadedTexture texture;
    private readonly LoadedTexture[] directionTextures;
    private readonly CairoFont font;
    public double RenderOrder => 1.01;
    public int RenderRange => 9999;

    internal CombatClient(ICoreClientAPI api)
    {
        this.api = api;
        HitDiagnosticsEnabled = api.Settings.Bool["warbandHitDiagnostics"];
        if (api.Settings.Bool["warbandCameraDiagnostics"]) cameraDiagnostics = new CameraDiagnostics(api);
        // GenOrUpdateTextTexture updates an existing wrapper; it does not allocate one.
        texture = new LoadedTexture(api);
        directionTextures = Enumerable.Range(0, 4).Select(_ => new LoadedTexture(api)).ToArray();
        font = CairoFont.WhiteSmallText().WithFontSize(13);
        font.StrokeWidth = 2;
        font.StrokeColor = new double[] { 0, 0, 0, .9 };
        channel = api.Network.GetChannel("warbandcombat").SetMessageHandler<StatePacket>(OnState)
            .SetMessageHandler<ContactPacket>(OnContact).SetMessageHandler<HitDiagnosticPacket>(OnHitDiagnostic);
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
        api.Input.RegisterHotKey("warband-hit-diagnostics", Lang.Get("warbandcombat:hit-diagnostics-toggle"), GlKeys.F8);
        api.Input.SetHotKeyHandler("warband-hit-diagnostics", _ =>
        {
            HitDiagnosticsEnabled = !HitDiagnosticsEnabled;
            api.Settings.Bool["warbandHitDiagnostics"] = HitDiagnosticsEnabled;
            hitDiagnostics.Reset(); diagnosticDetail = ""; pendingHitDiagnostics.Clear();
            if (channel.Connected) channel.SendPacket(new DiagnosticsSettingsPacket { Enabled = HitDiagnosticsEnabled });
            api.ShowChatMessage(Lang.Get(HitDiagnosticsEnabled ? "warbandcombat:hit-diagnostics-on" : "warbandcombat:hit-diagnostics-off"));
            return true;
        });
        api.Event.MouseDown += MouseDown;
        api.Event.MouseUp += MouseUp;
        api.Event.MouseMove += MouseMove;
        api.Input.InWorldAction += InWorldAction;
        api.Event.LeaveWorld += LeaveWorld;
        tick = api.Event.RegisterGameTickListener(Tick, 20);
        api.Event.RegisterRenderer(this, EnumRenderStage.Ortho, "warband-hud");
        api.Event.RegisterRenderer(this, EnumRenderStage.AfterOIT, "warband-hit-collider");
    }

    internal bool Available => Enabled && api.World.Player?.Entity is { Alive: true } entity
        && (CombatSystem.Supported(entity.RightHandItemSlot.Itemstack?.Collectible)
            || CombatShields.Equipped(entity)) && !ToolInteraction;

    private bool ToolInteraction
    {
        get
        {
            var player = api.World.Player;
            if ((right || choosingShieldBlock) && player?.Entity is { } equipped
                && CombatShields.Equipped(equipped)) return false;
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
        && (!api.World.Player.Entity.Controls.Sneak || Integration != null
            || forcePvpCombat && api.World.Player.CurrentEntitySelection?.Entity is EntityPlayer)
        && channel.Connected;

    internal bool OwnsShieldInput => Enabled && api.World.Player?.Entity is { Alive: true } entity
        && CombatShields.Equipped(entity) && !entity.Controls.Sneak && api.Input.MouseGrabbed && !api.IsGamePaused && channel.Connected;
    internal bool OwnsMeleeInput => Active
        && (right || Fighter.Phase == Phase.Block || CombatSystem.Supported(api.World.Player.Entity.RightHandItemSlot.Itemstack?.Collectible));
    private ICombatIntegration? Integration => api.ModLoader.GetModSystem<CombatSystem>()?.Integration;
    private StandalonePhysics? Physics => api.ModLoader.GetModSystem<CombatSystem>()?.Physics;
    private StandaloneAnimations? NativeAnimations => api.ModLoader.GetModSystem<CombatSystem>()?.NativeAnimations;
    internal int CurrentAttackId { get; private set; }

    private long Now => api.World.ElapsedMilliseconds;

    private void Send(Command command)
    {
        if (!channel.Connected) return;
        var item = api.World.Player?.Entity.RightHandItemSlot.Itemstack?.Collectible;
        if (api.World.Player?.Entity is { } playerEntity)
            Fighter.SetShield(CombatShields.Equipped(playerEntity), Now);
        bool weapon = CombatSystem.Supported(item);
        bool shield = api.World.Player?.Entity is { } owner && CombatShields.Equipped(owner);
        if (CombatShields.AllowsInput(command, weapon, shield, Fighter.Phase)
            && Fighter.Apply(command, Direction, Now, item != null ? CombatSystem.Timing(item) : WeaponTiming.Sword)
            && command == Command.Begin)
        {
            CurrentAttackId = sequence + 1;
            presentation.Begin();
            if (HitDiagnosticsEnabled)
            {
                hitDiagnostics.Begin(sequence + 1); diagnosticDetail = "";
                api.Logger.Notification("[Warband Hit] client attack={0} begin direction={1} weapon={2} mode={3}",
                    sequence + 1, Direction, item?.Code, api.World.Player?.Entity is { } diagnosticPlayer ? Integration?.WeaponMode(diagnosticPlayer) : "");
            }
        }
        channel.SendPacket(new InputPacket { Command = (int)command, Direction = (int)Direction,
            Sequence = ++sequence, SentAt = Now, HitDiagnostics = HitDiagnosticsEnabled, WeaponMode = api.World.Player?.Entity is { } player
                ? Integration?.WeaponMode(player) ?? "" : "" });
    }

    private void MouseDown(MouseEvent e)
    {
        choosingShieldBlock = e.Button == EnumMouseButton.Right
            && api.World.Player?.Entity is { } player && CombatShields.Equipped(player) && !player.Controls.Sneak;
        bool active = Active && (e.Button != EnumMouseButton.Left
            || CombatSystem.Supported(api.World.Player?.Entity.RightHandItemSlot.Itemstack?.Collectible));
        choosingShieldBlock = false;
        if (e.Handled || !active || e.Button is not (EnumMouseButton.Left or EnumMouseButton.Right)) return;
        e.Handled = true;
        ownedStack = api.World.Player!.Entity.RightHandItemSlot.Itemstack;
        ownedOffhand = api.World.Player.Entity.LeftHandItemSlot.Itemstack;
        ownedSlot = api.World.Player.InventoryManager.ActiveHotbarSlotNumber;
        ownedMode = Integration?.WeaponMode(api.World.Player.Entity) ?? "";
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
        if (!Active || e.Handled || left || right) return;
        Direction next = Fighter.FromMouse(e.DeltaX, e.DeltaY, Direction);
        if (next == Direction) return;
        Direction = next;
    }

    private void InWorldAction(EnumEntityAction action, bool on, ref EnumHandling handled)
    {
        if (action != EnumEntityAction.Sprint) return;
        sprintWanted = on;
        if (on && Fighter.SprintLocked(Now)) handled = EnumHandling.PreventDefault;
    }

    private void Tick(float dt)
    {
        bool changed = ownedSlot >= 0 && (ownedSlot != api.World.Player.InventoryManager.ActiveHotbarSlotNumber
            || !ReferenceEquals(ownedStack, api.World.Player.Entity.RightHandItemSlot.Itemstack)
            || ownedMode != (Integration?.WeaponMode(api.World.Player.Entity) ?? "")
            || !ReferenceEquals(ownedOffhand, api.World.Player.Entity.LeftHandItemSlot.Itemstack));
        if (!Active || changed)
        {
            if (left || right || Fighter.Phase is Phase.Windup or Phase.Block || changed && Fighter.Phase == Phase.Swing) Cancel();
        }
        if (Active && (left || right) && Now - lastHeartbeat >= 200)
        {
            Send(Command.Aim); Send(Command.KeepAlive); lastHeartbeat = Now;
        }
        Fighter.Advance(Now);
        UpdateHitDiagnostics();
        if (api.World.Player?.Entity is { } shieldOwner) Fighter.SetShield(CombatShields.Equipped(shieldOwner), Now);
        // Only the server knows whether the current aim contacted a target.
        // Prediction lets a miss run through the window instead of consuming it.
        // Held input resumes when recovery ends; a click during a short lockout
        // must not leave the player holding a button with no action started.
        if (Active && Fighter.Phase == Phase.Idle)
        {
            if (right) Send(Command.Block);
            else if (left && CombatSystem.Supported(api.World.Player?.Entity.RightHandItemSlot.Itemstack?.Collectible)) Send(Command.Begin);
        }
        if (api.World.Player?.Entity is { } entity)
        {
            if (presentation.PlaySwingSound(Fighter, Now))
                api.World.PlaySoundAt(CombatFeedback.SwingSound, entity.Pos.X, entity.Pos.Y + 1,
                    entity.Pos.Z, api.World.Player, true, 16, .45f);
            CombatMovement.Bind(entity, Fighter);
            bool locked = Fighter.SprintLocked(Now);
            if (locked)
            {
                if (!sprintWasLocked && entity.Controls.Sprint) sprintWanted = true;
                CombatMovement.Enforce(entity, Fighter, Now);
            }
            else if (sprintWasLocked && sprintWanted && api.Input.MouseGrabbed && !api.IsGamePaused)
                entity.Controls.Sprint = true;
            sprintWasLocked = locked;
            if (!ReferenceEquals(animatedPlayer, entity))
            {
                DetachAnimationHooks();
                animatedPlayer = entity;
                entity.TpAnimManager.OnStartAnimation += CombatAnimations.OnLocalAnimationStart;
                entity.SelfFpAnimManager.OnStartAnimation += CombatAnimations.OnLocalAnimationStart;
            }
            CombatAnimations.Update(entity, Fighter, Now, presentation, Integration, NativeAnimations);
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
                packet.RecoveryAttackPose, packet.ContactEndsIn - age, packet.SprintIn - age);
            Fighter.Advance(Now);
        }
        if (!string.IsNullOrEmpty(packet.Feedback))
        {
            feedback = Lang.Get(packet.Feedback); feedbackUntil = Now + 800;
        }
    }

    private void OnContact(ContactPacket packet)
    {
        if (packet.Id <= lastContact || !Enum.IsDefined(packet.Kind) || api.World.Player?.Entity is not { } entity) return;
        bool attacking = entity.EntityId == packet.Attacker;
        if (!attacking && entity.EntityId != packet.Defender) return;
        lastContact = packet.Id;
        float impulse = CombatFeedback.Impulse(packet.Kind, attacking);
        if (impulse > 0) api.World.AddCameraShake(impulse);
        if (packet.Kind != ContactKind.Hit || attacking)
        {
            contactKind = packet.Kind; contactUntil = Now + 180;
        }
    }

    internal void RecordHitFrame(bool terrain, bool entityContact, bool terrainBlockedTarget, string detail, string terrainDetail)
    {
        if (!HitDiagnosticsEnabled) return;
        if (terrain && !hitDiagnostics.Terrain)
        {
            diagnosticDetail = terrainDetail;
            api.Logger.Notification("[Warband Hit] client attack={0} terrain-intersection {1}", hitDiagnostics.AttackId, terrainDetail);
        }
        bool firstBlocked = terrainBlockedTarget && !hitDiagnostics.TerrainBlockedTarget;
        hitDiagnostics.Sample(terrain, entityContact, terrainBlockedTarget);
        if (firstBlocked)
        {
            diagnosticDetail = detail;
            api.Logger.Notification("[Warband Hit] client attack={0} terrain-blocked-target {1}", hitDiagnostics.AttackId, detail);
        }
    }

    internal void RecordHitSent(string detail)
    {
        if (!HitDiagnosticsEnabled) return;
        hitDiagnostics.ContactSent(Now); diagnosticDetail = detail;
        pendingHitDiagnostics[hitDiagnostics.AttackId] = Now;
        api.Logger.Notification("[Warband Hit] client attack={0} contact-sent {1}", hitDiagnostics.AttackId, detail);
    }

    internal void RecordMissingCollider()
    {
        if (HitDiagnosticsEnabled) hitDiagnostics.ServerResult(hitDiagnostics.AttackId, "missing-collider");
    }

    private void UpdateHitDiagnostics()
    {
        if (!HitDiagnosticsEnabled) return;
        if (hitDiagnostics.Finish(Fighter.Phase))
            api.Logger.Notification("[Warband Hit] client attack={0} result={1} frames={2} terrain={3} blockedTarget={4} entityContact={5} sent={6} {7}",
                hitDiagnostics.AttackId, hitDiagnostics.Result, hitDiagnostics.Frames, hitDiagnostics.Terrain,
                hitDiagnostics.TerrainBlockedTarget, hitDiagnostics.EntityContact, hitDiagnostics.Sent, diagnosticDetail);
        foreach (var pending in pendingHitDiagnostics.Where(p => Now - p.Value >= 2000).ToArray())
        {
            pendingHitDiagnostics.Remove(pending.Key);
            if (pending.Key == hitDiagnostics.AttackId) hitDiagnostics.Timeout(Now);
            api.Logger.Notification("[Warband Hit] client attack={0} no-server-reply after 2000ms", pending.Key);
        }
    }

    private void OnHitDiagnostic(HitDiagnosticPacket packet)
    {
        if (!HitDiagnosticsEnabled) return;
        pendingHitDiagnostics.Remove(packet.AttackId);
        api.Logger.Notification("[Warband Hit] server attack={0} target={1} result={2} {3}",
            packet.AttackId, packet.Target, packet.Result, packet.Detail);
        // A delayed answer is logged, but cannot overwrite a newer swing's HUD.
        if (hitDiagnostics.ServerResult(packet.AttackId, packet.Result)) diagnosticDetail = packet.Detail;
    }

    private void Cancel()
    {
        Send(Command.Cancel); Fighter.Invalidate(Now); left = right = false; ownedSlot = -1; ownedStack = null;
    }

    private void LeaveWorld()
    {
        DetachAnimationHooks(); presentation.Reset();
        cameraDiagnostics?.Reset();
        hitDiagnostics.Reset(); diagnosticDetail = ""; pendingHitDiagnostics.Clear();
        Fighter.Reset(clearSprint: true); left = right = false; ownedSlot = -1; ownedStack = null;
        sprintWanted = sprintWasLocked = false;
        sequence = 0; feedbackUntil = 0;
        lastContact = contactUntil = 0;
        forcePvpCombat = false;
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (stage == EnumRenderStage.AfterOIT)
        {
            if (HitDiagnosticsEnabled && Available && api.Input.MouseGrabbed && !api.IsGamePaused)
            {
                Integration?.RenderDiagnostics(api.World.Player.Entity, Now);
                Physics?.RenderDiagnostics(api.World.Player.Entity, Now);
            }
            return;
        }
        cameraDiagnostics?.OnFrame(Now, Fighter);
        if (!Available || !api.Input.MouseGrabbed || api.IsGamePaused) return;
        if (Active)
        {
            Integration?.Frame(api.World.Player.Entity, Fighter, Now);
            Physics?.Frame(api.World.Player.Entity, Fighter, Now, CurrentAttackId);
        }
        UpdateHitDiagnostics();
        Direction display = Fighter.Phase == Phase.Idle ? Direction : Fighter.Direction;
        float scale = (float)RuntimeEnv.GUIScale;
        float centerX = api.Render.FrameWidth / 2f;
        float centerY = api.Render.FrameHeight / 2f;
        float radius = 28 * scale, size = 20 * scale;
        var selectedColor = Fighter.CanGuard(Now)
            ? new Vec4f(.35f, 1f, .6f, 1f) : new Vec4f(1f, .78f, .3f, 1f);
        if (Now < contactUntil)
            selectedColor = contactKind switch
            {
                ContactKind.Hit => new Vec4f(.35f, 1f, .45f, 1f),
                ContactKind.ShieldBreak => new Vec4f(1f, .35f, .15f, 1f),
                _ => new Vec4f(.3f, .85f, 1f, 1f)
            };
        var inactiveColor = new Vec4f(.85f, .9f, .95f, .4f);
        for (int i = 0; i < directionTextures.Length; i++)
        {
            EnsureDirectionTexture(i);
            float dx = i switch { 1 => radius, 3 => -radius, _ => 0 };
            float dy = i switch { 0 => -radius, 2 => radius, _ => 0 };
            api.Render.Render2DTexturePremultipliedAlpha(directionTextures[i].TextureId,
                centerX + dx - size / 2, centerY + dy - size / 2, size, size, 50,
                i == (int)display || Fighter.ShieldGuard && Fighter.Phase == Phase.Block ? selectedColor : inactiveColor);
        }
        string state = Lang.Get(Fighter.Phase == Phase.Block && Fighter.ShieldGuard
            ? Fighter.CanGuard(Now) ? "warbandcombat:shield-guard" : "warbandcombat:shield-raise"
            : Fighter.Phase == Phase.Block && !Fighter.CanGuard(Now)
                ? "warbandcombat:guard-raise" : "warbandcombat:phase-" + Fighter.Phase.ToString().ToLowerInvariant());
        string text = Now < feedbackUntil ? state + "\n" + feedback : state;
        if (HitDiagnosticsEnabled)
            text += "\n" + Lang.Get("warbandcombat:hit-debug-status", hitDiagnostics.AttackId,
                Lang.Get("warbandcombat:hit-debug-" + hitDiagnostics.Result))
                + "\n" + Lang.Get("warbandcombat:hit-debug-legend");
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
        api.Input.InWorldAction -= InWorldAction;
        api.Event.LeaveWorld -= LeaveWorld;
        api.Event.UnregisterGameTickListener(tick);
        api.Event.UnregisterRenderer(this, EnumRenderStage.Ortho);
        api.Event.UnregisterRenderer(this, EnumRenderStage.AfterOIT);
        api.Input.SetHotKeyHandler("warband-toggle", _ => false);
        api.Input.SetHotKeyHandler("warband-hit-diagnostics", _ => false);
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
