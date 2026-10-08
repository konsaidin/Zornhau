using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Common;
using Vintagestory.Server.Systems;
using Zornhau;

internal static class TurnAuthorityChecks
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        Fighter Swing()
        {
            var f = new Fighter(); f.Apply(Command.Begin, Direction.Right, 1000, WeaponTiming.Overhaul("greatsword"));
            f.Apply(Command.Release, Direction.Right, 1001, f.Timing); return f;
        }
        float[] Pose(double yaw, double pitch = 0) => [(float)yaw, (float)(Math.PI + pitch), (float)yaw];
        foreach (int period in new[] { 16, 33, 67, 100 })
        foreach (int latency in new[] { 0, 30, 80, 150, 300 })
        foreach (int jitter in new[] { 0, 30, 75 })
        {
            var f = Swing(); var budget = new CombatTurnAuthority.Budget(f, Pose(0));
            var random = new Random(period + latency * 11 + jitter * 19);
            long delivery = f.StrikeStarts;
            for (int elapsed = 0; elapsed < f.Timing.Strike; elapsed += period)
            {
                // Use the same upstream delay for release and movement; latency
                // shifts the server window too, while jitter batches updates.
                delivery = Math.Max(delivery, f.StrikeStarts + elapsed + random.Next(jitter + 1));
                if (delivery > f.ContactEnds) break;
                double yaw = 2.2 * elapsed / 1000;
                double pitch = .8 * elapsed / 1000;
                var pose = Pose(yaw, pitch);
                Check(budget.Try(pose, delivery, false), "Legitimate continuous turns tolerate native position cadence, latency and jitter");
                Check(budget.Try(pose, delivery, true), "Reliable contact proof does not charge an accepted UDP pose twice");
            }
        }
        {
            var f = Swing(); var b = new CombatTurnAuthority.Budget(f, Pose(6.27));
            Check(b.Try(Pose(.02), f.StrikeStarts + 20, false), "Heading wrap at 2PI follows the short arc");
            Check(!b.Try(Pose(2), f.StrikeStarts + 21, false), "An abrupt multi-radian F5/body spin exceeds the server budget");
        }
        {
            var f = Swing(); var b = new CombatTurnAuthority.Budget(f, Pose(0));
            long t = f.StrikeStarts;
            double allowance = CombatTurnAuthority.PacketAllowanceMs / 1000d * Math.PI;
            Check(b.Try(Pose(allowance), t, false), "One packet batch can spend the single strike allowance");
            Check(!b.Try(Pose(allowance + .01), t, false), "A second message at the same time cannot buy another allowance");
            Check(!b.Try(Pose(allowance + .01), t, true), "Historical proof neighborhood cannot buy a second allowance either");
            int accepted = 0;
            for (int i = 1; i <= 1000; i++)
                if (b.Try(Pose(allowance + i * .0001), t, false)) accepted++;
            Check(accepted <= 2, "Float tolerance is not replenished by packet spam");
        }
        {
            var f = Swing(); var b = new CombatTurnAuthority.Budget(f, Pose(0));
            Check(!b.Try([.2f, (float)Math.PI + .2f, 0], f.StrikeStarts, false), "Yaw and pitch spend one combined angular budget");
            Check(!b.Try([0, (float)Math.PI, 1], f.StrikeStarts, false), "Forging BodyYaw independently of heading is also rejected");
            Check(!b.Try(null, f.StrikeStarts, true) && !b.Try([1, 2], f.StrikeStarts, true)
                && !b.Try([float.NaN, 3, 0], f.StrikeStarts, true), "Missing/malformed/nonfinite proof headings are refused");
        }
        {
            var f = Swing(); var b = new CombatTurnAuthority.Budget(f, Pose(0));
            b.Try(Pose(.1), f.StrikeStarts + 30, false);
            b.Try(Pose(.25), f.StrikeStarts + 90, false);
            Check(b.Try(Pose(.14), f.StrikeStarts + 91, true) && Math.Abs(b.Pose[0] - .25) < .0001,
                "A delayed proof between native samples neither rewinds the avatar nor consumes reverse rotation");
            f.TakeHit(f.StrikeStarts + 92);
            Check(b.Try(Pose(3), f.StrikeStarts + 100, false), "Character rotation is free immediately after confirmed contact");
        }
        // Exercise the actual native movement decoder and production Harmony
        // hook, including a sanity failure after it has already written yaw.
        long now = 1500;
        var world = Stub.Make<IWorldAccessor>((m, a) => m.Name == "get_ElapsedMilliseconds" ? now : Stub.Default(m.ReturnType));
        var api = Stub.Make<ICoreAPI>((m, a) => m.Name switch
        { "get_World" => world, "get_Side" => EnumAppSide.Server, _ => Stub.Default(m.ReturnType) });
        var entity = new Holder { Api = api, World = world, Alive = true, EntityId = 901 };
        entity.Pos.Pitch = (float)Math.PI; entity.Controls.Jump = entity.ServerControls.Jump = true;
        var patch = new Harmony("tests.turn-authority");
        CombatTurnAuthority.Patch(patch);
        var callback = CombatTurnAuthority.Rejected;
        int rejected = 0; CombatTurnAuthority.Rejected = _ => rejected++;
        Packet_EntityPosition Packet(float yaw, int seq, long motion = 0) => new()
        {
            EntityId = entity.EntityId, X = CollectibleNet.SerializeDoublePrecise(entity.Pos.X),
            Y = CollectibleNet.SerializeDoublePrecise(entity.Pos.Y), Z = CollectibleNet.SerializeDoublePrecise(entity.Pos.Z),
            Yaw = CollectibleNet.SerializeFloatPrecise(yaw), Pitch = CollectibleNet.SerializeFloatPrecise((float)Math.PI),
            BodyYaw = CollectibleNet.SerializeFloatPrecise(yaw), Tick = seq, MotionX = motion
        };
        try
        {
            var f = Swing(); CombatTurnAuthority.Bind(entity, f);
            Check(entity.Pos.SetFromPacket(Packet(.1f, 1), entity), "Real native position decoder accepts legitimate movement");
            Check(f.Phase == Phase.Swing && Math.Abs(entity.Pos.Yaw - .1) < .001, "Legitimate movement retains its active strike");
            entity.Pos.SetFromPacket(Packet(3, 1), entity);
            Check(f.Phase == Phase.Swing && Math.Abs(entity.Pos.Yaw - .1) < .001,
                "A duplicate/stale native sequence cannot replay or replace the accepted heading");
            entity.Pos.SetFromPacket(Packet(3, 2), entity);
            Check(f.Phase == Phase.Recovery && !f.CanHit(now) && rejected == 1,
                "Actual movement hook cancels an excessive spin before a later hit can pass");
            Check(Math.Abs(entity.Pos.Yaw - .1) < .001 && entity.Controls.Jump && entity.ServerControls.Jump,
                "Rejected rotation restores heading and preserves jumping");
            entity.Pos.Yaw = entity.BodyYawServer = 0; var g = Swing(); CombatTurnAuthority.Bind(entity, g);
            Check(!entity.Pos.SetFromPacket(Packet(3, 3, CollectibleNet.SerializeDoublePrecise(100)), entity),
                "Native motion sanity failure occurs after writing packet angles");
            Check(g.Phase == Phase.Recovery && Math.Abs(entity.Pos.Yaw) < .001,
                "A failed native decoder cannot leave a forged heading behind");
            entity.Pos.Motion.X = 0; var h = Swing(); CombatTurnAuthority.Bind(entity, h);
            Check(!CombatTurnAuthority.Proof(entity, Pose(3)) && h.Phase == Phase.Recovery,
                "Proof heading is checked even when no forged movement packet arrived first");
            var teleport = Swing(); CombatTurnAuthority.Bind(entity, teleport);
            entity.Pos.Dimension++;
            Check(!CombatTurnAuthority.Proof(entity, Pose(0)) && teleport.Phase == Phase.Recovery,
                "Changing dimension cancels the old rotation window and contact authority");
        }
        finally { patch.UnpatchAll(patch.Id); CombatTurnAuthority.Rejected = callback; }
        Console.WriteLine($"PASS: {checks} server angular budget, native movement decoder and contact-proof checks");
    }
}
