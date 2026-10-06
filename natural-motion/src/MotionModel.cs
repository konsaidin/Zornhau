namespace NaturalMotion;

public sealed class MotionConfig
{
    public bool Enabled = true;
    public bool DirectionalSteps = true;
    public bool AirbornePose = true;
    public float BodyTurnDegrees = 28;
    public float WalkStride = 1.65f;
    public float SprintStride = 2.3f;
    public float Response = 12;
    internal void Sanitize()
    {
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
    internal double Cycle, FlightCycle, Speed, RelativeDirection, Turn;
    internal float GroundBlend, AirBlend;
    internal float ForwardWeight = 1, BackWeight, LeftWeight, RightWeight, SprintBlend;
    internal bool Combat, Sneak, Sprint;
    internal bool Active => GroundBlend > .001f || AirBlend > .001f || Math.Abs(Turn) > .0001;

    internal void Update(MotionInput input, MotionConfig config)
    {
        if (initialized && input.At == previous.At) return;
        double dt = initialized ? (input.At - previous.At) / 1000d : 1d / 60;
        bool reset = !initialized || dt < 0 || dt > .25 || input.Dimension != previous.Dimension
            || Math.Pow(input.X - previous.X, 2) + Math.Pow(input.Y - previous.Y, 2) + Math.Pow(input.Z - previous.Z, 2) > 4;
        if (reset) { Clear(); dt = 1d / 60; }
        previous = input; initialized = true;
        Combat = input.Combat; Sneak = input.Sneak; Sprint = input.Sprint;
        if (input.Excluded || !double.IsFinite(input.VelocityX) || !double.IsFinite(input.VelocityY)
            || !double.IsFinite(input.VelocityZ) || !float.IsFinite(input.BodyYaw)
            || !double.IsFinite(input.X) || !double.IsFinite(input.Y) || !double.IsFinite(input.Z)) { Clear(); return; }
        double amount = 1 - Math.Exp(-config.Response * dt);
        double speed = Math.Min(12, Math.Sqrt(input.VelocityX * input.VelocityX + input.VelocityZ * input.VelocityZ));
        Speed += (speed - Speed) * amount;
        double forward = -Math.Sin(input.BodyYaw) * input.VelocityX - Math.Cos(input.BodyYaw) * input.VelocityZ;
        double right = Math.Cos(input.BodyYaw) * input.VelocityX - Math.Sin(input.BodyYaw) * input.VelocityZ;
        if (speed > .08) RelativeDirection = Math.Atan2(right, forward);
        if (input.Ground) { airborne = false; quietVertical = 0; }
        else
        {
            if (Math.Abs(input.VelocityY) > .3) { airborne = true; quietVertical = 0; }
            else quietVertical += dt;
            // Remote physics may briefly leave OnGround stale. A quiet vertical
            // position cannot preserve a jump forever; an apex lasts much less.
            if (quietVertical > .15) airborne = false;
        }
        float air = config.AirbornePose && airborne ? 1 : 0;
        AirBlend += (air - AirBlend) * (float)(1 - Math.Exp(-18 * dt));
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
        Cycle = (Cycle + Speed * dt / (input.Sprint ? config.SprintStride : config.WalkStride)) % 1;
        if (airborne) FlightCycle = (FlightCycle + dt / 1.3) % 1;
        else if (AirBlend < .001f) FlightCycle = 0;
    }

    private void Clear()
    {
        initialized = false; airborne = false; quietVertical = 0;
        Cycle = FlightCycle = Speed = RelativeDirection = Turn = 0; GroundBlend = AirBlend = 0;
        ForwardWeight = 1; BackWeight = LeftWeight = RightWeight = SprintBlend = 0;
    }
}
