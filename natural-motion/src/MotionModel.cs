namespace NaturalMotion;

public sealed class MotionConfig
{
    public bool Enabled = true;
    public bool DirectionalSteps = true;
    public bool AirbornePose = true;
    public bool SyncFootsteps = true;
    public float WalkStepMilliseconds = 470;
    public float SprintStepMilliseconds = 340;
    public float WalkStepScale = 1.35f;
    public float JumpLegHeight = .55f;
    public float BodyTurnDegrees = 28;
    public float WalkStride = 1.65f;
    public float SprintStride = 2.3f;
    public float Response = 12;
    internal void Sanitize()
    {
        WalkStepMilliseconds = Finite(WalkStepMilliseconds, 470, 150, 1500);
        SprintStepMilliseconds = Finite(SprintStepMilliseconds, 340, 150, 1500);
        WalkStepScale = Finite(WalkStepScale, 1.35f, 1, 1.65f);
        JumpLegHeight = Finite(JumpLegHeight, .55f, .25f, 1);
        BodyTurnDegrees = Finite(BodyTurnDegrees, 28, 0, 40);
        WalkStride = Finite(WalkStride, 1.65f, .6f, 3);
        SprintStride = Finite(SprintStride, 2.3f, 1, 4);
        Response = Finite(Response, 12, 4, 24);
    }
    private static float Finite(float value, float fallback, float min, float max)
        => float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

internal readonly record struct MotionInput(long At, double X, double Y, double Z, int Dimension,
    double VelocityX, double VelocityY, double VelocityZ, float BodyYaw, bool Ground,
    bool Sneak, bool Sprint, bool Combat, bool Excluded);

internal sealed class MotionState
{
    private MotionInput previous;
    private bool initialized, airborne;
    private double quietVertical;
    private long lastFootstep = -1;
    private double soundPhase, soundInterval;
    private bool soundSprint;
    private double jumpAge = 1, landingAge = 1;
    internal int JumpCount;
    internal double JumpAge => jumpAge;
    internal float AirSide, JumpPulse, LandingPulse;
    internal float WalkStepScale = 1.35f, JumpLegHeight = .55f;
    internal double Cycle, FlightCycle, Speed, RelativeDirection, Turn;
    internal float GroundBlend, AirBlend;
    internal float ForwardWeight = 1, BackWeight, LeftWeight, RightWeight, SprintBlend;
    internal bool Combat, Sneak, Sprint;
    internal bool Active => GroundBlend > .001f || AirBlend > .001f || LandingPulse > .001f || Math.Abs(Turn) > .0001;

    internal void Update(MotionInput input, MotionConfig config)
    {
        if (initialized && input.At == previous.At) return;
        double dt = initialized ? (input.At - previous.At) / 1000d : 1d / 60;
        bool reset = !initialized || dt < 0 || dt > .25 || input.Dimension != previous.Dimension
            || Math.Pow(input.X - previous.X, 2) + Math.Pow(input.Y - previous.Y, 2) + Math.Pow(input.Z - previous.Z, 2) > 4;
        if (reset) { Clear(); dt = 1d / 60; }
        var oldInput = previous;
        previous = input; initialized = true;
        Combat = input.Combat; Sneak = input.Sneak; Sprint = input.Sprint;
        WalkStepScale = config.WalkStepScale; JumpLegHeight = config.JumpLegHeight;
        if (input.Excluded || !double.IsFinite(input.VelocityX) || !double.IsFinite(input.VelocityY)
            || !double.IsFinite(input.VelocityZ) || !float.IsFinite(input.BodyYaw)
            || !double.IsFinite(input.X) || !double.IsFinite(input.Y) || !double.IsFinite(input.Z)) { Clear(); return; }
        double amount = 1 - Math.Exp(-config.Response * dt);
        double speed = Math.Min(12, Math.Sqrt(input.VelocityX * input.VelocityX + input.VelocityZ * input.VelocityZ));
        Speed += (speed - Speed) * amount;
        // EntityControls.CalcMovementVectors: W=(sin(yaw), cos(yaw)),
        // D=(-cos(yaw), sin(yaw)). At yaw zero W is +Z and D is -X.
        double forward = Math.Sin(input.BodyYaw) * input.VelocityX + Math.Cos(input.BodyYaw) * input.VelocityZ;
        double right = -Math.Cos(input.BodyYaw) * input.VelocityX + Math.Sin(input.BodyYaw) * input.VelocityZ;
        if (speed > .08) RelativeDirection = Math.Atan2(right, forward);
        bool wasAirborne = airborne;
        if (input.Ground) { airborne = false; quietVertical = 0; }
        else
        {
            if (Math.Abs(input.VelocityY) > .3) { airborne = true; quietVertical = 0; }
            else quietVertical += dt;
            // Remote physics may briefly leave OnGround stale. A quiet vertical
            // position cannot preserve a jump forever; an apex lasts much less.
            if (quietVertical > .15) airborne = false;
        }
        // A held-space rebound can happen between two rendered frames, with
        // no OnGround frame at all. A descent-to-ascent reversal is a new hop.
        bool launch = airborne && input.VelocityY > .6 && (!wasAirborne
            || !reset && (oldInput.Ground || oldInput.VelocityY < -.3));
        if (launch) { JumpCount++; jumpAge = 0; ForgetFootsteps(); }
        else jumpAge += dt;
        if (wasAirborne && !airborne) landingAge = 0;
        else landingAge += dt;
        if (airborne && !wasAirborne) ForgetFootsteps();
        float side = JumpCount > 0 ? (JumpCount - 1) % 2 : 0;
        AirSide += (side - AirSide) * (float)(1 - Math.Exp(-6 * dt));
        // Both envelopes start and end at zero: even a rebound never resets
        // a live bone or restarts a replicated engine animation.
        JumpPulse = config.AirbornePose && jumpAge < .36 ? (float)Math.Pow(Math.Sin(Math.PI * jumpAge / .36), 2) : 0;
        LandingPulse = config.AirbornePose && landingAge < .24 ? (float)Math.Pow(Math.Sin(Math.PI * landingAge / .24), 2) : 0;
        float air = config.AirbornePose && airborne ? 1 : 0;
        AirBlend += (air - AirBlend) * (float)(1 - Math.Exp(-12 * dt));
        float ground = config.DirectionalSteps && !airborne && input.Ground && !input.Sneak
            ? (float)Math.Clamp(speed / .25, 0, 1) : 0;
        GroundBlend += (ground - GroundBlend) * (float)amount;
        double targetTurn = !airborne && input.Ground && speed > .12 && !input.Combat
            ? -Math.Atan2(right, Math.Abs(forward)) / (Math.PI / 2) * config.BodyTurnDegrees * Math.PI / 180
                * (input.Sneak ? .5 : 1) : 0;
        Turn += (targetTurn - Turn) * amount;
        if (speed > .08)
        {
            double direction = RelativeDirection + Turn;
            double f = Math.Cos(direction), s = Math.Sin(direction), total = Math.Abs(f) + Math.Abs(s);
            ForwardWeight += ((float)(Math.Max(0, f) / total) - ForwardWeight) * (float)amount;
            BackWeight += ((float)(Math.Max(0, -f) / total) - BackWeight) * (float)amount;
            LeftWeight += ((float)(Math.Max(0, -s) / total) - LeftWeight) * (float)amount;
            RightWeight += ((float)(Math.Max(0, s) / total) - RightWeight) * (float)amount;
        }
        SprintBlend += ((input.Sprint ? 1 : 0) - SprintBlend) * (float)amount;
        if (config.SyncFootsteps)
        {
            // A full left+right stride contains TWO audible steps. Freeze this
            // clock off the ground and while stopped; audio never snaps poses.
            if (!reset && input.Ground && !airborne && !input.Sneak && speed > .12)
            {
                double interval = (input.Sprint ? config.SprintStepMilliseconds : config.WalkStepMilliseconds) / 1000d;
                bool recentSound = lastFootstep >= 0 && soundSprint == input.Sprint
                    && input.At >= lastFootstep && input.At - lastFootstep < Math.Max(interval, soundInterval) * 2500;
                if (recentSound && soundInterval > 0) interval = soundInterval;
                Cycle += dt / (2 * interval);
                if (recentSound)
                {
                    double target = soundPhase + (input.At - lastFootstep) / 1000d / (2 * interval);
                    double difference = target - Cycle;
                    difference -= Math.Floor(difference + .5);
                    Cycle += Math.Clamp(difference, -.25 * dt, .25 * dt);
                }
                Cycle -= Math.Floor(Cycle);
            }
        }
        else
        {
            double stride = config.WalkStride + (config.SprintStride - config.WalkStride) * ForwardWeight * SprintBlend;
            Cycle = (Cycle + Speed * dt / stride) % 1;
        }
        if (airborne) FlightCycle = (FlightCycle + dt / 1.3) % 1;
        else if (AirBlend < .001f) FlightCycle = 0;
    }

    internal void ObserveFootstep(long at, bool sprint, MotionConfig config)
    {
        if (!config.SyncFootsteps || at < 0 || at == lastFootstep) return;
        double interval = (at - lastFootstep) / 1000d;
        if (lastFootstep >= 0 && at > lastFootstep && interval is >= .15 and <= 1.5 && sprint == soundSprint)
        {
            soundInterval = soundInterval > 0 ? soundInterval + (interval - soundInterval) * .35 : interval;
            soundPhase = (soundPhase + .5) % 1;
        }
        else
        {
            soundInterval = 0;
            soundPhase = (Math.Round(Cycle * 2) / 2) % 1;
        }
        lastFootstep = at; soundSprint = sprint;
    }

    private void ForgetFootsteps() { lastFootstep = -1; soundPhase = soundInterval = 0; }

    private void Clear()
    {
        initialized = false; airborne = false; quietVertical = 0;
        jumpAge = landingAge = 1; JumpCount = 0; AirSide = JumpPulse = LandingPulse = 0;
        Cycle = FlightCycle = Speed = RelativeDirection = Turn = 0; GroundBlend = AirBlend = 0;
        ForwardWeight = 1; BackWeight = LeftWeight = RightWeight = SprintBlend = 0;
        ForgetFootsteps();
    }
}
